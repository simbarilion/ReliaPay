-- payment.submit (server-side binding), automatic payment-actions и seed карт
-- payment-processing / payment-review v1. Новые функции и индексы; checksum
-- уже применённых миграций не меняется.

ALTER TABLE app.operations
    DROP CONSTRAINT IF EXISTS operations_process_status_chk;

ALTER TABLE app.operations
    ADD CONSTRAINT operations_process_status_chk CHECK (
        (status = 'CREATED' AND process_id IS NULL)
        OR (status IN ('PROCESSING', 'COMPLETED', 'REJECTED') AND process_id IS NOT NULL)
    );

CREATE UNIQUE INDEX IF NOT EXISTS operation_events_submitted_once
    ON app.operation_events (operation_id)
    WHERE event_type = 'OPERATION_SUBMITTED';

CREATE UNIQUE INDEX IF NOT EXISTS operation_events_completed_once
    ON app.operation_events (operation_id)
    WHERE event_type = 'OPERATION_COMPLETED';

CREATE UNIQUE INDEX IF NOT EXISTS operation_events_rejected_once
    ON app.operation_events (operation_id)
    WHERE event_type = 'OPERATION_REJECTED';

CREATE UNIQUE INDEX IF NOT EXISTS external_requests_operation_uq
    ON delivery.external_requests (operation_id);

CREATE UNIQUE INDEX IF NOT EXISTS decisions_limit_rule_once
    ON delivery.decisions (process_id)
    WHERE source = 'LIMIT_RULE';

CREATE FUNCTION app.payload_operation_id(p_payload jsonb)
RETURNS uuid
LANGUAGE plpgsql
IMMUTABLE
SET search_path = pg_catalog, pg_temp
AS $opid$
DECLARE
    v_id uuid;
    v_key_count integer;
BEGIN
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN NULL;
    END IF;

    v_key_count := (SELECT count(*)::integer FROM jsonb_object_keys(p_payload));
    IF v_key_count <> 1
        OR jsonb_typeof(p_payload -> 'operationId') IS DISTINCT FROM 'string'
    THEN
        RETURN NULL;
    END IF;

    BEGIN
        v_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION
        WHEN invalid_text_representation THEN
            RETURN NULL;
    END;

    RETURN v_id;
END;
$opid$;

ALTER FUNCTION app.payload_operation_id(jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payload_operation_id(jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_submit_result(
    p_operation_id uuid,
    p_process_id uuid,
    p_flow_name text,
    p_flow_version integer
)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog, pg_temp
AS $res$
    SELECT jsonb_build_object(
        'status', 'ok',
        'outcome', 'SUBMITTED',
        'result', jsonb_build_object(
            'operationId', p_operation_id,
            'processId', p_process_id,
            'flowName', p_flow_name,
            'flowVersion', p_flow_version,
            'status', 'PROCESSING'
        )
    );
$res$;

ALTER FUNCTION app.payment_submit_result(uuid, uuid, text, integer) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_submit_result(uuid, uuid, text, integer) FROM PUBLIC;

CREATE FUNCTION app.payment_submit(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $submit$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_process workflow.processes%ROWTYPE;
    v_flow text;
    v_started jsonb;
    v_process_id uuid;
    v_result jsonb;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.process_id IS NOT NULL THEN
        SELECT * INTO v_process
        FROM workflow.processes
        WHERE process_id = v_operation.process_id;

        IF NOT FOUND THEN
            RETURN api.error_envelope('internal.error', 'action failed');
        END IF;

        RETURN app.payment_submit_result(
            v_operation.operation_id,
            v_process.process_id,
            v_process.flow_name,
            v_process.flow_version
        );
    END IF;

    IF v_operation.status IS DISTINCT FROM 'CREATED' THEN
        RETURN api.error_envelope('request.invalid', 'operation cannot be submitted');
    END IF;

    v_flow := CASE v_operation.operation_kind
        WHEN 'PAYMENT_EXECUTION' THEN 'payment-processing'
        WHEN 'PAYMENT_APPROVAL' THEN 'payment-review'
        ELSE NULL
    END;
    IF v_flow IS NULL THEN
        RETURN api.error_envelope('internal.error', 'action failed');
    END IF;

    v_started := workflow.start_process(
        v_flow,
        v_operation.operation_id::text,
        jsonb_build_object('operationId', v_operation.operation_id)
    );
    IF COALESCE(v_started ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RETURN v_started;
    END IF;

    v_process_id := (v_started -> 'result' ->> 'processId')::uuid;

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = v_process_id;

    UPDATE app.operations
    SET status = 'PROCESSING',
        process_id = v_process.process_id
    WHERE operation_id = v_operation.operation_id;

    v_result := app.payment_submit_result(
        v_operation.operation_id,
        v_process.process_id,
        v_process.flow_name,
        v_process.flow_version
    );

    INSERT INTO app.operation_events (operation_id, event_type, payload_hash)
    VALUES (
        v_operation.operation_id,
        'OPERATION_SUBMITTED',
        encode(sha256(convert_to(v_result::text, 'UTF8')), 'hex')
    )
    ON CONFLICT DO NOTHING;

    RETURN v_result;
END;
$submit$;

ALTER FUNCTION app.payment_submit(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_submit(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.operation_events(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $events$
DECLARE
    v_operation_id uuid;
    v_exists boolean;
    v_events jsonb;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT EXISTS (
        SELECT 1 FROM app.operations WHERE operation_id = v_operation_id
    ) INTO v_exists;
    IF NOT v_exists THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    SELECT COALESCE(
        jsonb_agg(
            jsonb_build_object(
                'eventType', e.event_type,
                'occurredAt', to_char(e.occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
            )
            ORDER BY e.occurred_at, e.event_id
        ),
        '[]'::jsonb
    )
    INTO v_events
    FROM app.operation_events AS e
    WHERE e.operation_id = v_operation_id;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'LISTED',
        'result', jsonb_build_object(
            'operationId', v_operation_id,
            'events', v_events
        )
    );
END;
$events$;

ALTER FUNCTION app.operation_events(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.operation_events(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_validate(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $validate$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.status IS DISTINCT FROM 'PROCESSING' OR v_operation.process_id IS NULL THEN
        RETURN api.error_envelope('request.invalid', 'operation is not ready');
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'VALID',
        'result', jsonb_build_object('operationId', v_operation.operation_id)
    );
END;
$validate$;

ALTER FUNCTION app.payment_validate(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_validate(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_prepare_external(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $prepare$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_external_id text;
    v_correlation uuid;
    v_body text;
    v_hash text;
    v_ext delivery.external_requests%ROWTYPE;
BEGIN
    p_context := COALESCE(p_context, '{}'::jsonb);
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_external_id := NULLIF(p_context ->> 'executionId', '');
    IF v_external_id IS NULL THEN
        v_external_id := NULLIF(p_context ->> 'requestId', '');
    END IF;
    IF v_external_id IS NULL OR v_external_id = '' THEN
        RETURN api.error_envelope('request.invalid', 'execution identity is required');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.operation_kind IS DISTINCT FROM 'PAYMENT_EXECUTION'
        OR v_operation.status IS DISTINCT FROM 'PROCESSING'
        OR v_operation.process_id IS NULL
    THEN
        RETURN api.error_envelope('request.invalid', 'operation cannot prepare external request');
    END IF;

    BEGIN
        v_correlation := NULLIF(p_context ->> 'correlationId', '')::uuid;
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_correlation := NULL;
    END;
    IF v_correlation IS NULL THEN
        v_correlation := gen_random_uuid();
    END IF;

    v_body := '{"amount":' || to_json(v_operation.amount_text)
        || ',"currency":"RUB","operationId":' || to_json(v_external_id) || '}';
    v_hash := encode(sha256(convert_to(v_body, 'UTF8')), 'hex');

    BEGIN
        INSERT INTO delivery.external_requests (
            external_request_id,
            operation_id,
            process_id,
            state,
            payload_hash,
            amount,
            currency,
            correlation_id
        )
        VALUES (
            v_external_id,
            v_operation.operation_id,
            v_operation.process_id,
            'CREATED',
            v_hash,
            v_operation.amount_text,
            v_operation.currency,
            v_correlation
        )
        ON CONFLICT (external_request_id) DO NOTHING
        RETURNING * INTO v_ext;

        IF v_ext.external_request_id IS NULL THEN
            SELECT * INTO v_ext
            FROM delivery.external_requests
            WHERE external_request_id = v_external_id
               OR operation_id = v_operation.operation_id
            ORDER BY created_at
            LIMIT 1;

            RETURN jsonb_build_object(
                'status', 'ok',
                'outcome', 'PREPARED',
                'result', jsonb_build_object(
                    'operationId', v_operation.operation_id,
                    'externalRequestId', v_ext.external_request_id
                )
            );
        END IF;

        INSERT INTO delivery.outbox (
            external_request_id,
            state,
            attempt_count,
            next_attempt_at
        )
        VALUES (
            v_ext.external_request_id,
            'PENDING',
            0,
            clock_timestamp()
        );
    EXCEPTION
        WHEN unique_violation THEN
            SELECT * INTO v_ext
            FROM delivery.external_requests
            WHERE operation_id = v_operation.operation_id
            ORDER BY created_at
            LIMIT 1;
    END;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'PREPARED',
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'externalRequestId', v_ext.external_request_id
        )
    );
END;
$prepare$;

ALTER FUNCTION app.payment_prepare_external(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_prepare_external(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_apply_receipt(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $apply$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_receipt delivery.receipts%ROWTYPE;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    SELECT r.*
    INTO v_receipt
    FROM delivery.receipts AS r
    INNER JOIN delivery.external_requests AS x
        ON x.external_request_id = r.external_request_id
    WHERE x.operation_id = v_operation.operation_id
    ORDER BY r.received_at
    LIMIT 1;

    IF NOT FOUND THEN
        RETURN api.error_envelope('dependency.unavailable', 'receipt is not available');
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', v_receipt.outcome,
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'outcome', v_receipt.outcome
        )
    );
END;
$apply$;

ALTER FUNCTION app.payment_apply_receipt(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_apply_receipt(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_set_status(
    p_payload jsonb,
    p_status text,
    p_outcome text,
    p_event_type text
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $setst$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_result jsonb;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.status = p_status THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', p_outcome,
            'result', jsonb_build_object(
                'operationId', v_operation.operation_id,
                'status', p_status
            )
        );
    END IF;

    IF v_operation.status IS DISTINCT FROM 'PROCESSING' THEN
        RETURN api.error_envelope('request.invalid', 'operation cannot change status');
    END IF;

    UPDATE app.operations
    SET status = p_status
    WHERE operation_id = v_operation.operation_id;

    v_result := jsonb_build_object(
        'status', 'ok',
        'outcome', p_outcome,
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'status', p_status
        )
    );

    INSERT INTO app.operation_events (operation_id, event_type, payload_hash)
    VALUES (
        v_operation.operation_id,
        p_event_type,
        encode(sha256(convert_to(v_result::text, 'UTF8')), 'hex')
    )
    ON CONFLICT DO NOTHING;

    RETURN v_result;
END;
$setst$;

ALTER FUNCTION app.payment_set_status(jsonb, text, text, text) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_set_status(jsonb, text, text, text) FROM PUBLIC;

CREATE FUNCTION app.payment_complete(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $complete$
BEGIN
    RETURN app.payment_set_status(p_payload, 'COMPLETED', 'COMPLETED', 'OPERATION_COMPLETED');
END;
$complete$;

ALTER FUNCTION app.payment_complete(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_complete(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_reject(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $reject$
BEGIN
    RETURN app.payment_set_status(p_payload, 'REJECTED', 'REJECTED', 'OPERATION_REJECTED');
END;
$reject$;

ALTER FUNCTION app.payment_reject(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_reject(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_approve(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $approve$
BEGIN
    RETURN app.payment_set_status(p_payload, 'COMPLETED', 'APPROVED', 'OPERATION_COMPLETED');
END;
$approve$;

ALTER FUNCTION app.payment_approve(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_approve(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.payment_check_limit(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $limit$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_step_id uuid;
    v_outcome text;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.operation_kind IS DISTINCT FROM 'PAYMENT_APPROVAL'
        OR v_operation.status IS DISTINCT FROM 'PROCESSING'
        OR v_operation.process_id IS NULL
    THEN
        RETURN api.error_envelope('request.invalid', 'operation cannot check limit');
    END IF;

    SELECT s.step_instance_id
    INTO v_step_id
    FROM workflow.step_instances AS s
    WHERE s.process_id = v_operation.process_id
      AND s.step_key = 'check_limit';

    IF v_step_id IS NULL THEN
        RETURN api.error_envelope('internal.error', 'action failed');
    END IF;

    IF v_operation.amount <= 100000.00 THEN
        v_outcome := 'WITHIN_LIMIT';
        INSERT INTO delivery.decisions (
            process_id,
            step_instance_id,
            source,
            principal,
            reason_hash,
            outcome,
            rule_version
        )
        VALUES (
            v_operation.process_id,
            v_step_id,
            'LIMIT_RULE',
            NULL,
            NULL,
            'APPROVED',
            'course-limit-v1'
        )
        ON CONFLICT (process_id) WHERE source = 'LIMIT_RULE' DO NOTHING;
    ELSE
        v_outcome := 'REVIEW_REQUIRED';
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', v_outcome,
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'ruleVersion', 'course-limit-v1'
        )
    );
END;
$limit$;

ALTER FUNCTION app.payment_check_limit(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_check_limit(jsonb, jsonb) FROM PUBLIC;

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($m_submit$
{
  "module": "payment",
  "action": "submit",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_submit",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" }
    }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "processId", "flowName", "flowVersion", "status"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "processId": { "type": "string", "format": "uuid" },
      "flowName": { "enum": ["payment-processing", "payment-review"] },
      "flowVersion": { "type": "integer", "minimum": 1 },
      "status": { "const": "PROCESSING" }
    }
  },
  "outcomes": ["SUBMITTED"],
  "required_policy": ["payment:write"],
  "idempotency_mode": "required",
  "idempotency_scope": "principal_action",
  "timeout_ms": 5000
}
$m_submit$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.submit failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.submit', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.submit failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_events$
{
  "module": "operation",
  "action": "events",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "operation_events",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" }
    }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "events"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "events": {
        "type": "array",
        "items": {
          "type": "object",
          "additionalProperties": false,
          "required": ["eventType", "occurredAt"],
          "properties": {
            "eventType": { "type": "string" },
            "occurredAt": { "type": "string", "format": "date-time" }
          }
        }
      }
    }
  },
  "outcomes": ["LISTED"],
  "required_policy": ["payment:read"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 5000
}
$m_events$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish operation.events failed: %', v_result;
    END IF;
    v_result := api.activate_action('operation.events', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate operation.events failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_validate$
{
  "module": "payment",
  "action": "validate",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_validate",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "outcomes": ["VALID"],
  "required_policy": ["payment:internal"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$m_validate$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.validate failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.validate', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.validate failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_prepare$
{
  "module": "payment",
  "action": "prepare_external",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_prepare_external",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "externalRequestId"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "externalRequestId": { "type": "string", "minLength": 1 }
    }
  },
  "outcomes": ["PREPARED"],
  "required_policy": ["payment:internal"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$m_prepare$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.prepare_external failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.prepare_external', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.prepare_external failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_apply$
{
  "module": "payment",
  "action": "apply_receipt",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_apply_receipt",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "outcome"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "outcome": { "enum": ["COMPLETED", "REJECTED"] }
    }
  },
  "outcomes": ["COMPLETED", "REJECTED"],
  "required_policy": ["payment:internal"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$m_apply$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.apply_receipt failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.apply_receipt', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.apply_receipt failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_complete$
{
  "module": "payment",
  "action": "complete",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_complete",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "status"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "status": { "const": "COMPLETED" }
    }
  },
  "outcomes": ["COMPLETED"],
  "required_policy": ["payment:internal"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$m_complete$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.complete failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.complete', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.complete failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_reject$
{
  "module": "payment",
  "action": "reject",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_reject",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "status"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "status": { "const": "REJECTED" }
    }
  },
  "outcomes": ["REJECTED"],
  "required_policy": ["payment:internal"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$m_reject$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.reject failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.reject', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.reject failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_limit$
{
  "module": "payment",
  "action": "check_limit",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_check_limit",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "ruleVersion"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "ruleVersion": { "const": "course-limit-v1" }
    }
  },
  "outcomes": ["WITHIN_LIMIT", "REVIEW_REQUIRED"],
  "required_policy": ["payment:internal"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$m_limit$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.check_limit failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.check_limit', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.check_limit failed: %', v_result;
    END IF;

    v_result := api.publish_action($m_approve$
{
  "module": "payment",
  "action": "approve",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_approve",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId"],
    "properties": { "operationId": { "type": "string", "format": "uuid" } }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "status"],
    "properties": {
      "operationId": { "type": "string", "format": "uuid" },
      "status": { "const": "COMPLETED" }
    }
  },
  "outcomes": ["APPROVED"],
  "required_policy": ["payment:internal"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$m_approve$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.approve failed: %', v_result;
    END IF;
    v_result := api.activate_action('payment.approve', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.approve failed: %', v_result;
    END IF;

    v_result := workflow.publish_flow($map_processing$
{
  "contract_version": "course-1",
  "flow_name": "payment-processing",
  "version": 1,
  "start_step": "validate_operation",
  "steps": [
    {
      "key": "validate_operation",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "validate",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 1, "delays_ms": [] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    {
      "key": "prepare_external_request",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "prepare_external",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 3, "delays_ms": [200, 400] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    {
      "key": "wait_receipt",
      "type": "wait_signal",
      "signal_type": "payment.receipt",
      "outcome": "RECEIVED"
    },
    {
      "key": "apply_receipt",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "apply_receipt",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 2, "delays_ms": [200] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    {
      "key": "complete_operation",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "complete",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 2, "delays_ms": [200] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    {
      "key": "reject_operation",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "reject",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 2, "delays_ms": [200] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    { "key": "done", "type": "end", "outcome": "FINISHED" }
  ],
  "transitions": [
    { "from": "validate_operation", "outcome": "VALID", "to": "prepare_external_request" },
    { "from": "prepare_external_request", "outcome": "PREPARED", "to": "wait_receipt" },
    { "from": "wait_receipt", "outcome": "RECEIVED", "to": "apply_receipt" },
    { "from": "apply_receipt", "outcome": "COMPLETED", "to": "complete_operation" },
    { "from": "apply_receipt", "outcome": "REJECTED", "to": "reject_operation" },
    { "from": "complete_operation", "outcome": "COMPLETED", "to": "done" },
    { "from": "reject_operation", "outcome": "REJECTED", "to": "done" }
  ]
}
$map_processing$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment-processing v1 failed: %', v_result;
    END IF;

    v_result := workflow.publish_flow($map_review$
{
  "contract_version": "course-1",
  "flow_name": "payment-review",
  "version": 1,
  "start_step": "validate_operation",
  "steps": [
    {
      "key": "validate_operation",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "validate",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 1, "delays_ms": [] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    {
      "key": "check_limit",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "check_limit",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 1, "delays_ms": [] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    {
      "key": "approve_operation",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "approve",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 2, "delays_ms": [200] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    {
      "key": "wait_manual_decision",
      "type": "manual",
      "allowed_outcomes": ["APPROVED", "REJECTED"]
    },
    {
      "key": "reject_operation",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "payment",
        "action": "reject",
        "action_version": 1,
        "required_policy": ["payment:internal"],
        "timeout_ms": 2000,
        "retry": { "max_attempts": 2, "delays_ms": [200] },
        "input_mapping": { "/operationId": "/operationId" },
        "input_constants": {}
      }
    },
    { "key": "done", "type": "end", "outcome": "FINISHED" }
  ],
  "transitions": [
    { "from": "validate_operation", "outcome": "VALID", "to": "check_limit" },
    { "from": "check_limit", "outcome": "WITHIN_LIMIT", "to": "approve_operation" },
    { "from": "check_limit", "outcome": "REVIEW_REQUIRED", "to": "wait_manual_decision" },
    { "from": "wait_manual_decision", "outcome": "APPROVED", "to": "approve_operation" },
    { "from": "wait_manual_decision", "outcome": "REJECTED", "to": "reject_operation" },
    { "from": "approve_operation", "outcome": "APPROVED", "to": "done" },
    { "from": "reject_operation", "outcome": "REJECTED", "to": "done" }
  ]
}
$map_review$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment-review v1 failed: %', v_result;
    END IF;

    v_result := workflow.activate_flow('payment-processing', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment-processing v1 failed: %', v_result;
    END IF;

    v_result := workflow.activate_flow('payment-review', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment-review v1 failed: %', v_result;
    END IF;
END;
$boot$;
