-- Idempotency boundary follows manifest scope and includes pinned action version.
-- Existing checksum migrations are not edited.

ALTER TABLE api.actions
    DROP CONSTRAINT IF EXISTS actions_idempotency_mode_chk;
ALTER TABLE api.actions
    ADD CONSTRAINT actions_idempotency_mode_chk
    CHECK (idempotency_mode IN ('none', 'optional', 'required'));

ALTER TABLE api.actions
    DROP CONSTRAINT IF EXISTS actions_idempotency_scope_chk;
ALTER TABLE api.actions
    ADD CONSTRAINT actions_idempotency_scope_chk
    CHECK (idempotency_scope IN ('none', 'principal_action', 'consumer_action', 'global_action'));

ALTER TABLE app.idempotency_keys
    ADD COLUMN IF NOT EXISTS action_version integer NOT NULL DEFAULT 1;

ALTER TABLE app.idempotency_keys
    DROP CONSTRAINT IF EXISTS idempotency_keys_pkey;
ALTER TABLE app.idempotency_keys
    ADD CONSTRAINT idempotency_keys_pkey
    PRIMARY KEY (principal, module, action, action_version, idempotency_key);

CREATE OR REPLACE FUNCTION api.invoke(
    p_module text,
    p_action text,
    p_version integer,
    p_context jsonb,
    p_payload jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $invoke$
DECLARE
    v_row api.actions%ROWTYPE;
    v_scope text;
    v_result jsonb;
    v_call text;
    v_outcome text;
    v_request_id text;
    v_principal text;
    v_scope_subject text;
    v_hash text;
    v_idempotent boolean;
    v_stored_hash text;
    v_stored_result jsonb;
    v_constraint text;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    p_context := COALESCE(p_context, '{}'::jsonb);
    v_request_id := NULLIF(p_context ->> 'requestId', '');
    v_principal := NULLIF(p_context ->> 'principal', '');
    v_hash := encode(sha256(convert_to(p_payload::text, 'UTF8')), 'hex');

    IF p_version IS NULL THEN
        SELECT *
        INTO v_row
        FROM api.actions
        WHERE module = p_module
          AND action = p_action
          AND enabled
          AND is_default;
    ELSE
        SELECT *
        INTO v_row
        FROM api.actions
        WHERE module = p_module
          AND action = p_action
          AND version = p_version
          AND enabled;
    END IF;

    IF NOT FOUND THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, p_version, 'ERROR', NULL);
        RETURN api.error_envelope('action.not_found', 'action is not available');
    END IF;

    IF jsonb_typeof(p_context -> 'scopes') IS DISTINCT FROM 'array' THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
        RETURN api.error_envelope('access.denied', 'required policy is not satisfied');
    END IF;

    FOR v_scope IN
        SELECT jsonb_array_elements_text(v_row.required_policy)
    LOOP
        IF NOT (p_context -> 'scopes') ? v_scope THEN
            PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
            RETURN api.error_envelope('access.denied', 'required policy is not satisfied');
        END IF;
    END LOOP;

    IF v_row.idempotency_mode NOT IN ('none', 'optional') AND v_request_id IS NULL THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
        RETURN api.error_envelope('idempotency.required', 'Idempotency-Key is required');
    END IF;

    v_idempotent :=
        v_row.idempotency_mode IS DISTINCT FROM 'none'
        AND v_row.idempotency_scope IS DISTINCT FROM 'none'
        AND v_request_id IS NOT NULL;

    v_scope_subject := CASE v_row.idempotency_scope
        WHEN 'principal_action' THEN v_principal
        WHEN 'consumer_action' THEN NULLIF(p_context ->> 'consumer', '')
        WHEN 'global_action' THEN '*'
        ELSE NULL
    END;

    IF v_idempotent AND v_scope_subject IS NULL THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
        RETURN api.error_envelope('access.denied', 'required policy is not satisfied');
    END IF;

    IF v_idempotent THEN
        SELECT k.payload_hash, k.result
        INTO v_stored_hash, v_stored_result
        FROM app.idempotency_keys AS k
        WHERE k.principal = v_scope_subject
          AND k.module = v_row.module
          AND k.action = v_row.action
          AND k.action_version = v_row.version
          AND k.idempotency_key = v_request_id
        FOR UPDATE;

        IF FOUND THEN
            IF v_stored_hash IS DISTINCT FROM v_hash THEN
                PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
                RETURN api.error_envelope(
                    'idempotency.conflict',
                    'idempotency key was reused with a different payload');
            END IF;

            v_outcome := v_stored_result ->> 'outcome';
            PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'OK', v_outcome);
            RETURN v_stored_result;
        END IF;
    END IF;

    IF NOT api.target_exists(v_row.target_schema, v_row.target_function) THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
        RETURN api.error_envelope('action.contract_violation', 'registered target is not callable');
    END IF;

    v_call := format(
        'SELECT %I.%I($1, $2)',
        v_row.target_schema,
        v_row.target_function
    );

    BEGIN
        EXECUTE v_call INTO v_result USING p_context, p_payload;

        IF v_result IS NULL OR jsonb_typeof(v_result) IS DISTINCT FROM 'object' THEN
            PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
            RETURN api.error_envelope('action.contract_violation', 'target returned an invalid envelope');
        END IF;

        IF COALESCE(v_result ->> 'status', '') = 'ok' AND v_idempotent THEN
            INSERT INTO app.idempotency_keys (
                principal,
                module,
                action,
                action_version,
                idempotency_key,
                payload_hash,
                result,
                operation_id
            )
            VALUES (
                v_scope_subject,
                v_row.module,
                v_row.action,
                v_row.version,
                v_request_id,
                v_hash,
                v_result,
                NULL
            );
        END IF;
    EXCEPTION
        WHEN unique_violation THEN
            GET STACKED DIAGNOSTICS v_constraint = CONSTRAINT_NAME;
            IF v_constraint IS DISTINCT FROM 'idempotency_keys_pkey' THEN
                PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
                RETURN api.error_envelope('internal.error', 'action failed');
            END IF;

            SELECT k.payload_hash, k.result
            INTO v_stored_hash, v_stored_result
            FROM app.idempotency_keys AS k
            WHERE k.principal = v_scope_subject
              AND k.module = v_row.module
              AND k.action = v_row.action
              AND k.action_version = v_row.version
              AND k.idempotency_key = v_request_id;

            IF NOT FOUND THEN
                PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
                RETURN api.error_envelope('internal.error', 'action failed');
            END IF;

            IF v_stored_hash IS DISTINCT FROM v_hash THEN
                PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
                RETURN api.error_envelope(
                    'idempotency.conflict',
                    'idempotency key was reused with a different payload');
            END IF;

            v_outcome := v_stored_result ->> 'outcome';
            PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'OK', v_outcome);
            RETURN v_stored_result;
        WHEN OTHERS THEN
            PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
            RETURN api.error_envelope('internal.error', 'action failed');
    END;

    v_outcome := v_result ->> 'outcome';
    IF COALESCE(v_result ->> 'status', '') = 'ok' THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'OK', v_outcome);
    ELSE
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', v_outcome);
    END IF;

    RETURN v_result;
END;
$invoke$;

ALTER FUNCTION api.invoke(text, text, integer, jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO course_runtime;
GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO workflow_worker;
