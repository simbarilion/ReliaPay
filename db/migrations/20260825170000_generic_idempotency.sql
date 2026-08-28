-- Generic idempotency in api.invoke before target, disable self-replacement guard,
-- and a non-payment probe action so replay is not payment-only.

ALTER TABLE api.actions
    DROP CONSTRAINT IF EXISTS actions_idempotency_mode_chk;
ALTER TABLE api.actions
    ADD CONSTRAINT actions_idempotency_mode_chk
    CHECK (char_length(idempotency_mode) >= 1 AND char_length(idempotency_mode) <= 64);

ALTER TABLE api.actions
    DROP CONSTRAINT IF EXISTS actions_idempotency_scope_chk;
ALTER TABLE api.actions
    ADD CONSTRAINT actions_idempotency_scope_chk
    CHECK (char_length(idempotency_scope) >= 1 AND char_length(idempotency_scope) <= 64);

CREATE TABLE IF NOT EXISTS app.runtime_records (
    seq bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    token text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE app.runtime_records OWNER TO course_owner;
REVOKE ALL ON TABLE app.runtime_records FROM PUBLIC;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.runtime_records FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.runtime_records FROM course_publication;
GRANT SELECT ON app.runtime_records TO course_publication;

CREATE OR REPLACE FUNCTION app.runtime_record(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $record$
DECLARE
    v_token text;
    v_seq bigint;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object'
        OR jsonb_typeof(p_payload -> 'token') IS DISTINCT FROM 'string'
    THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_token := p_payload ->> 'token';
    IF v_token IS NULL OR v_token = '' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    INSERT INTO app.runtime_records (token)
    VALUES (v_token)
    RETURNING seq INTO v_seq;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'RECORDED',
        'result', jsonb_build_object(
            'token', v_token,
            'seq', v_seq
        )
    );
END;
$record$;

ALTER FUNCTION app.runtime_record(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.runtime_record(jsonb, jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION app.payment_request(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $pay$
DECLARE
    v_principal text;
    v_request_id text;
    v_kind text;
    v_amount_text text;
    v_currency text;
    v_operation app.operations%ROWTYPE;
    v_result jsonb;
    v_key_count integer;
BEGIN
    p_context := COALESCE(p_context, '{}'::jsonb);
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_principal := NULLIF(p_context ->> 'principal', '');
    v_request_id := NULLIF(p_context ->> 'requestId', '');

    IF v_request_id IS NULL THEN
        RETURN api.error_envelope('idempotency.required', 'Idempotency-Key is required');
    END IF;
    IF v_principal IS NULL THEN
        RETURN api.error_envelope('access.denied', 'required policy is not satisfied');
    END IF;

    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_key_count := (SELECT count(*)::integer FROM jsonb_object_keys(p_payload));
    v_kind := p_payload ->> 'operationKind';
    v_amount_text := p_payload ->> 'amount';
    v_currency := p_payload ->> 'currency';

    IF v_key_count <> 3
        OR jsonb_typeof(p_payload -> 'operationKind') IS DISTINCT FROM 'string'
        OR jsonb_typeof(p_payload -> 'amount') IS DISTINCT FROM 'string'
        OR jsonb_typeof(p_payload -> 'currency') IS DISTINCT FROM 'string'
        OR v_kind NOT IN ('PAYMENT_EXECUTION', 'PAYMENT_APPROVAL')
        OR v_currency IS DISTINCT FROM 'RUB'
        OR v_amount_text IS NULL
        OR v_amount_text !~ '^(?:0\.(?:0[1-9]|[1-9][0-9]?)|[1-9][0-9]{0,15}(?:\.[0-9]{1,2})?)$'
    THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    INSERT INTO app.operations (
        request_id,
        operation_kind,
        amount,
        amount_text,
        currency,
        status
    )
    VALUES (
        v_request_id,
        v_kind,
        v_amount_text::numeric,
        v_amount_text,
        v_currency,
        'CREATED'
    )
    RETURNING * INTO v_operation;

    v_result := jsonb_build_object(
        'status', 'ok',
        'outcome', 'CREATED',
        'result', app.operation_result_json(v_operation)
    );

    INSERT INTO app.operation_events (operation_id, event_type, payload_hash)
    VALUES (
        v_operation.operation_id,
        'OPERATION_CREATED',
        encode(sha256(convert_to(v_result::text, 'UTF8')), 'hex')
    );

    RETURN v_result;
END;
$pay$;

ALTER FUNCTION app.payment_request(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_request(jsonb, jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION api.disable_action(p_route text, p_version integer, p_replacement_version integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $disable$
DECLARE
    v_module text;
    v_action text;
    v_current api.actions%ROWTYPE;
    v_remaining integer;
    v_enabled_count integer;
    v_default_count integer;
BEGIN
    v_module := split_part(p_route, '.', 1);
    v_action := split_part(p_route, '.', 2);
    IF v_module = '' OR v_action = '' OR p_route <> (v_module || '.' || v_action) THEN
        RETURN api.error_envelope('manifest.invalid', 'action key must be module.action');
    END IF;

    PERFORM 1
    FROM api.actions
    WHERE module = v_module
      AND action = v_action
    FOR UPDATE;

    SELECT *
    INTO v_current
    FROM api.actions
    WHERE module = v_module
      AND action = v_action
      AND version = p_version;

    IF NOT FOUND THEN
        RETURN api.error_envelope('action.not_found', 'action version is not published');
    END IF;

    IF p_replacement_version IS NOT NULL AND p_replacement_version = p_version THEN
        RETURN api.error_envelope('manifest.invalid', 'replacement-version must differ from the disabled version');
    END IF;

    IF v_current.is_default THEN
        SELECT count(*)::integer
        INTO v_remaining
        FROM api.actions
        WHERE module = v_module
          AND action = v_action
          AND enabled
          AND version IS DISTINCT FROM p_version;

        IF v_remaining > 0 AND p_replacement_version IS NULL THEN
            RETURN api.error_envelope('manifest.invalid', 'disable requires replacement-version');
        END IF;

        IF p_replacement_version IS NOT NULL THEN
            IF NOT EXISTS (
                SELECT 1
                FROM api.actions
                WHERE module = v_module
                  AND action = v_action
                  AND version = p_replacement_version
            ) THEN
                RETURN api.error_envelope('action.not_found', 'replacement version is not published');
            END IF;

            UPDATE api.actions
            SET is_default = false
            WHERE module = v_module
              AND action = v_action
              AND is_default;

            UPDATE api.actions
            SET enabled = true,
                is_default = true
            WHERE module = v_module
              AND action = v_action
              AND version = p_replacement_version;
        END IF;
    END IF;

    UPDATE api.actions
    SET enabled = false,
        is_default = false
    WHERE module = v_module
      AND action = v_action
      AND version = p_version;

    SELECT count(*) FILTER (WHERE enabled)::integer,
           count(*) FILTER (WHERE is_default)::integer
    INTO v_enabled_count, v_default_count
    FROM api.actions
    WHERE module = v_module
      AND action = v_action;

    IF v_enabled_count > 0 AND v_default_count IS DISTINCT FROM 1 THEN
        RAISE EXCEPTION 'enabled action must have exactly one default version';
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'resource', 'action',
        'operation', 'disabled',
        'key', p_route,
        'version', p_version
    );
END;
$disable$;

ALTER FUNCTION api.disable_action(text, integer, integer) OWNER TO course_owner;
REVOKE ALL ON FUNCTION api.disable_action(text, integer, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.disable_action(text, integer, integer) TO course_publication;

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

    IF v_idempotent AND v_principal IS NULL THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
        RETURN api.error_envelope('access.denied', 'required policy is not satisfied');
    END IF;

    IF v_idempotent THEN
        SELECT k.payload_hash, k.result
        INTO v_stored_hash, v_stored_result
        FROM app.idempotency_keys AS k
        WHERE k.principal = v_principal
          AND k.module = v_row.module
          AND k.action = v_row.action
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
                idempotency_key,
                payload_hash,
                result,
                operation_id
            )
            VALUES (
                v_principal,
                v_row.module,
                v_row.action,
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
            WHERE k.principal = v_principal
              AND k.module = v_row.module
              AND k.action = v_row.action
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

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($manifest$
{
  "contract_version": "course-1",
  "module": "check",
  "action": "record",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "runtime_record",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["token"],
    "properties": {
      "token": { "type": "string", "minLength": 1 }
    }
  },
  "response_schema": {
    "type": "object",
    "required": ["token", "seq"],
    "properties": {
      "token": { "type": "string" },
      "seq": { "type": "integer" }
    }
  },
  "outcomes": ["RECORDED"],
  "required_policy": ["payment:read"],
  "idempotency_mode": "required",
  "idempotency_scope": "principal_action",
  "timeout_ms": 2000,
  "enabled": false,
  "is_default": false
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish check.record failed: %', v_result;
    END IF;

    v_result := api.activate_action('check.record', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate check.record failed: %', v_result;
    END IF;
END;
$boot$;
