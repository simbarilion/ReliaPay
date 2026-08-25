-- Неизменяемый каталог действий, api.invoke и служебный probe без публикации в catalog.

ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA api
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA app
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

CREATE TABLE api.actions (
    module text NOT NULL,
    action text NOT NULL,
    version integer NOT NULL,
    http_method text NOT NULL,
    target_schema text NOT NULL,
    target_function text NOT NULL,
    request_schema jsonb NOT NULL,
    response_schema jsonb NOT NULL,
    outcomes jsonb NOT NULL,
    required_policy jsonb NOT NULL,
    idempotency_mode text NOT NULL,
    idempotency_scope text NOT NULL,
    timeout_ms integer NOT NULL,
    manifest_checksum text NOT NULL,
    enabled boolean NOT NULL DEFAULT false,
    is_default boolean NOT NULL DEFAULT false,
    published_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT actions_pkey PRIMARY KEY (module, action, version),
    CONSTRAINT actions_http_method_chk CHECK (http_method = 'POST'),
    CONSTRAINT actions_version_chk CHECK (version >= 1),
    CONSTRAINT actions_timeout_chk CHECK (timeout_ms >= 1),
    CONSTRAINT actions_idempotency_mode_chk CHECK (idempotency_mode IN ('required', 'none')),
    CONSTRAINT actions_idempotency_scope_chk CHECK (idempotency_scope IN ('principal_action', 'none')),
    CONSTRAINT actions_outcomes_chk CHECK (jsonb_typeof(outcomes) = 'array'),
    CONSTRAINT actions_policy_chk CHECK (jsonb_typeof(required_policy) = 'array'),
    CONSTRAINT actions_default_enabled_chk CHECK (NOT is_default OR enabled)
);

ALTER TABLE api.actions OWNER TO course_owner;

CREATE UNIQUE INDEX actions_one_default_per_route
    ON api.actions (module, action)
    WHERE is_default;

CREATE TABLE api.action_dispatches (
    dispatch_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    correlation_id uuid,
    request_id text,
    module text NOT NULL,
    action text NOT NULL,
    version integer,
    principal text,
    payload_hash text NOT NULL,
    status text NOT NULL,
    outcome text,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT action_dispatches_status_chk CHECK (status IN ('OK', 'ERROR'))
);

ALTER TABLE api.action_dispatches OWNER TO course_owner;

CREATE INDEX action_dispatches_correlation_idx
    ON api.action_dispatches (correlation_id);

REVOKE ALL ON TABLE api.actions FROM PUBLIC;
REVOKE ALL ON TABLE api.action_dispatches FROM PUBLIC;
GRANT SELECT ON TABLE api.actions TO course_publication;
GRANT SELECT ON TABLE api.action_dispatches TO course_publication;

CREATE FUNCTION api.actions_protect_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $protect$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'published action version is immutable';
    END IF;

    IF NEW.module IS DISTINCT FROM OLD.module
        OR NEW.action IS DISTINCT FROM OLD.action
        OR NEW.version IS DISTINCT FROM OLD.version
        OR NEW.http_method IS DISTINCT FROM OLD.http_method
        OR NEW.target_schema IS DISTINCT FROM OLD.target_schema
        OR NEW.target_function IS DISTINCT FROM OLD.target_function
        OR NEW.request_schema IS DISTINCT FROM OLD.request_schema
        OR NEW.response_schema IS DISTINCT FROM OLD.response_schema
        OR NEW.outcomes IS DISTINCT FROM OLD.outcomes
        OR NEW.required_policy IS DISTINCT FROM OLD.required_policy
        OR NEW.idempotency_mode IS DISTINCT FROM OLD.idempotency_mode
        OR NEW.idempotency_scope IS DISTINCT FROM OLD.idempotency_scope
        OR NEW.timeout_ms IS DISTINCT FROM OLD.timeout_ms
        OR NEW.manifest_checksum IS DISTINCT FROM OLD.manifest_checksum
        OR NEW.published_at IS DISTINCT FROM OLD.published_at
    THEN
        RAISE EXCEPTION 'published action version is immutable';
    END IF;

    RETURN NEW;
END;
$protect$;

ALTER FUNCTION api.actions_protect_immutable() OWNER TO course_owner;

CREATE TRIGGER actions_protect_immutable
    BEFORE UPDATE OR DELETE ON api.actions
    FOR EACH ROW
    EXECUTE FUNCTION api.actions_protect_immutable();

CREATE FUNCTION api.manifest_checksum(p_manifest jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog, pg_temp
AS $checksum$
    SELECT encode(
        sha256(convert_to(
            jsonb_build_object(
                'module', p_manifest ->> 'module',
                'action', p_manifest ->> 'action',
                'version', p_manifest -> 'version',
                'http_method', p_manifest ->> 'http_method',
                'target_schema', p_manifest ->> 'target_schema',
                'target_function', p_manifest ->> 'target_function',
                'request_schema', p_manifest -> 'request_schema',
                'response_schema', p_manifest -> 'response_schema',
                'outcomes', p_manifest -> 'outcomes',
                'required_policy', p_manifest -> 'required_policy',
                'idempotency_mode', p_manifest ->> 'idempotency_mode',
                'idempotency_scope', p_manifest ->> 'idempotency_scope',
                'timeout_ms', p_manifest -> 'timeout_ms'
            )::text,
            'UTF8')),
        'hex');
$checksum$;

ALTER FUNCTION api.manifest_checksum(jsonb) OWNER TO course_owner;

CREATE FUNCTION api.error_envelope(p_code text, p_message text)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog, pg_temp
AS $err$
    SELECT jsonb_build_object(
        'status', 'error',
        'code', p_code,
        'message', p_message
    );
$err$;

ALTER FUNCTION api.error_envelope(text, text) OWNER TO course_owner;

CREATE FUNCTION api.record_dispatch(
    p_context jsonb,
    p_payload jsonb,
    p_module text,
    p_action text,
    p_version integer,
    p_status text,
    p_outcome text
)
RETURNS void
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $dispatch$
DECLARE
    v_correlation uuid;
BEGIN
    BEGIN
        v_correlation := NULLIF(p_context ->> 'correlationId', '')::uuid;
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_correlation := NULL;
    END;

    INSERT INTO api.action_dispatches (
        correlation_id,
        request_id,
        module,
        action,
        version,
        principal,
        payload_hash,
        status,
        outcome
    )
    VALUES (
        v_correlation,
        NULLIF(p_context ->> 'requestId', ''),
        p_module,
        p_action,
        p_version,
        NULLIF(p_context ->> 'principal', ''),
        encode(sha256(convert_to(COALESCE(p_payload, '{}'::jsonb)::text, 'UTF8')), 'hex'),
        p_status,
        p_outcome
    );
END;
$dispatch$;

ALTER FUNCTION api.record_dispatch(jsonb, jsonb, text, text, integer, text, text) OWNER TO course_owner;

CREATE FUNCTION api.target_exists(p_schema text, p_function text)
RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = pg_catalog, pg_temp
AS $exists$
    SELECT EXISTS (
        SELECT 1
        FROM pg_proc AS proc
        INNER JOIN pg_namespace AS ns ON ns.oid = proc.pronamespace
        WHERE ns.nspname = p_schema
          AND proc.proname = p_function
          AND pg_get_function_identity_arguments(proc.oid) = 'p_context jsonb, p_payload jsonb'
          AND pg_get_function_result(proc.oid) = 'jsonb'
    );
$exists$;

ALTER FUNCTION api.target_exists(text, text) OWNER TO course_owner;

CREATE FUNCTION app.catalog_probe(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $probe$
BEGIN
    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'PROBED',
        'result', jsonb_build_object(
            'accepted', true,
            'payload', COALESCE(p_payload, '{}'::jsonb)
        )
    );
END;
$probe$;

ALTER FUNCTION app.catalog_probe(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.catalog_probe(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION api.publish_action(p_manifest jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $publish$
DECLARE
    v_checksum text;
    v_existing text;
    v_module text;
    v_action text;
    v_version integer;
    v_schema text;
    v_function text;
BEGIN
    IF jsonb_typeof(p_manifest) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('manifest.invalid', 'manifest must be a JSON object');
    END IF;

    v_module := p_manifest ->> 'module';
    v_action := p_manifest ->> 'action';
    v_version := (p_manifest ->> 'version')::integer;
    v_schema := p_manifest ->> 'target_schema';
    v_function := p_manifest ->> 'target_function';

    IF v_module IS NULL OR v_action IS NULL OR v_version IS NULL
        OR v_schema IS NULL OR v_function IS NULL
        OR p_manifest ->> 'http_method' IS NULL
        OR p_manifest -> 'request_schema' IS NULL
        OR p_manifest -> 'response_schema' IS NULL
        OR p_manifest -> 'outcomes' IS NULL
        OR p_manifest -> 'required_policy' IS NULL
        OR p_manifest ->> 'idempotency_mode' IS NULL
        OR p_manifest ->> 'idempotency_scope' IS NULL
        OR p_manifest ->> 'timeout_ms' IS NULL
    THEN
        RETURN api.error_envelope('manifest.invalid', 'manifest is missing required fields');
    END IF;

    IF NOT api.target_exists(v_schema, v_function) THEN
        RETURN api.error_envelope('manifest.invalid', 'target function signature is not registered');
    END IF;

    v_checksum := api.manifest_checksum(p_manifest);

    INSERT INTO api.actions (
        module,
        action,
        version,
        http_method,
        target_schema,
        target_function,
        request_schema,
        response_schema,
        outcomes,
        required_policy,
        idempotency_mode,
        idempotency_scope,
        timeout_ms,
        manifest_checksum,
        enabled,
        is_default
    )
    VALUES (
        v_module,
        v_action,
        v_version,
        p_manifest ->> 'http_method',
        v_schema,
        v_function,
        p_manifest -> 'request_schema',
        p_manifest -> 'response_schema',
        p_manifest -> 'outcomes',
        p_manifest -> 'required_policy',
        p_manifest ->> 'idempotency_mode',
        p_manifest ->> 'idempotency_scope',
        (p_manifest ->> 'timeout_ms')::integer,
        v_checksum,
        false,
        false
    );

    RETURN jsonb_build_object(
        'status', 'ok',
        'resource', 'action',
        'operation', 'published',
        'key', v_module || '.' || v_action,
        'version', v_version
    );
EXCEPTION
    WHEN unique_violation THEN
        SELECT a.manifest_checksum
        INTO v_existing
        FROM api.actions AS a
        WHERE a.module = v_module
          AND a.action = v_action
          AND a.version = v_version;

        IF v_existing = v_checksum THEN
            RETURN jsonb_build_object(
                'status', 'ok',
                'resource', 'action',
                'operation', 'published',
                'key', v_module || '.' || v_action,
                'version', v_version
            );
        END IF;

        RETURN api.error_envelope('manifest.conflict', 'published action version is immutable');
    WHEN check_violation THEN
        RETURN api.error_envelope('manifest.invalid', 'manifest violates catalog constraints');
    WHEN invalid_text_representation THEN
        RETURN api.error_envelope('manifest.invalid', 'manifest field has invalid type');
END;
$publish$;

ALTER FUNCTION api.publish_action(jsonb) OWNER TO course_owner;

CREATE FUNCTION api.activate_action(p_route text, p_version integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $activate$
DECLARE
    v_module text;
    v_action text;
    v_found boolean;
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

    SELECT true
    INTO v_found
    FROM api.actions
    WHERE module = v_module
      AND action = v_action
      AND version = p_version;

    IF v_found IS DISTINCT FROM true THEN
        RETURN api.error_envelope('action.not_found', 'action version is not published');
    END IF;

    UPDATE api.actions
    SET is_default = false
    WHERE module = v_module
      AND action = v_action
      AND is_default
      AND version IS DISTINCT FROM p_version;

    UPDATE api.actions
    SET enabled = true,
        is_default = true
    WHERE module = v_module
      AND action = v_action
      AND version = p_version;

    RETURN jsonb_build_object(
        'status', 'ok',
        'resource', 'action',
        'operation', 'activated',
        'key', p_route,
        'version', p_version
    );
END;
$activate$;

ALTER FUNCTION api.activate_action(text, integer) OWNER TO course_owner;

CREATE FUNCTION api.disable_action(p_route text, p_version integer, p_replacement_version integer)
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

CREATE FUNCTION api.invoke(
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
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    p_context := COALESCE(p_context, '{}'::jsonb);

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
    EXCEPTION
        WHEN OTHERS THEN
            PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
            RETURN api.error_envelope('internal.error', 'action failed');
    END;

    IF v_result IS NULL OR jsonb_typeof(v_result) IS DISTINCT FROM 'object' THEN
        PERFORM api.record_dispatch(p_context, p_payload, p_module, p_action, v_row.version, 'ERROR', NULL);
        RETURN api.error_envelope('action.contract_violation', 'target returned an invalid envelope');
    END IF;

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

REVOKE ALL ON FUNCTION api.publish_action(jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.activate_action(text, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.disable_action(text, integer, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.error_envelope(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.record_dispatch(jsonb, jsonb, text, text, integer, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.manifest_checksum(jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.target_exists(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.actions_protect_immutable() FROM PUBLIC;

GRANT EXECUTE ON FUNCTION api.publish_action(jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION api.activate_action(text, integer) TO course_publication;
GRANT EXECUTE ON FUNCTION api.disable_action(text, integer, integer) TO course_publication;
GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO course_runtime;

