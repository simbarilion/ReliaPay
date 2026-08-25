-- Предметные operations/events, идемпотентность principal_action и actions payment.request / operation.get.

CREATE TABLE app.operations (
    operation_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id text NOT NULL,
    operation_kind text NOT NULL,
    amount numeric(20, 2) NOT NULL,
    amount_text text NOT NULL,
    currency text NOT NULL,
    status text NOT NULL,
    process_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT operations_kind_chk CHECK (operation_kind IN ('PAYMENT_EXECUTION', 'PAYMENT_APPROVAL')),
    CONSTRAINT operations_currency_chk CHECK (currency = 'RUB'),
    CONSTRAINT operations_status_chk CHECK (status IN ('CREATED', 'PROCESSING', 'COMPLETED', 'REJECTED')),
    CONSTRAINT operations_amount_chk CHECK (amount >= 0.01 AND amount <= 9999999999999999.99)
);

ALTER TABLE app.operations OWNER TO course_owner;

CREATE TABLE app.operation_events (
    event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    operation_id uuid NOT NULL REFERENCES app.operations (operation_id),
    event_type text NOT NULL,
    payload_hash text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT operation_events_type_chk CHECK (event_type = UPPER(event_type))
);

ALTER TABLE app.operation_events OWNER TO course_owner;

CREATE UNIQUE INDEX operation_events_created_once
    ON app.operation_events (operation_id)
    WHERE event_type = 'OPERATION_CREATED';

CREATE TABLE app.idempotency_keys (
    principal text NOT NULL,
    module text NOT NULL,
    action text NOT NULL,
    idempotency_key text NOT NULL,
    payload_hash text NOT NULL,
    result jsonb NOT NULL,
    operation_id uuid REFERENCES app.operations (operation_id),
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT idempotency_keys_pkey PRIMARY KEY (principal, module, action, idempotency_key)
);

ALTER TABLE app.idempotency_keys OWNER TO course_owner;

REVOKE ALL ON TABLE app.operations FROM PUBLIC;
REVOKE ALL ON TABLE app.operation_events FROM PUBLIC;
REVOKE ALL ON TABLE app.idempotency_keys FROM PUBLIC;
GRANT SELECT ON TABLE app.operations TO course_publication;
GRANT SELECT ON TABLE app.operation_events TO course_publication;
GRANT SELECT ON TABLE app.idempotency_keys TO course_publication;

CREATE FUNCTION app.operations_protect_identity()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $protect_op$
BEGIN
    IF NEW.operation_id IS DISTINCT FROM OLD.operation_id
        OR NEW.request_id IS DISTINCT FROM OLD.request_id
        OR NEW.operation_kind IS DISTINCT FROM OLD.operation_kind
        OR NEW.amount IS DISTINCT FROM OLD.amount
        OR NEW.amount_text IS DISTINCT FROM OLD.amount_text
        OR NEW.currency IS DISTINCT FROM OLD.currency
    THEN
        RAISE EXCEPTION 'operation identity fields are immutable';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END;
$protect_op$;

ALTER FUNCTION app.operations_protect_identity() OWNER TO course_owner;

CREATE TRIGGER operations_protect_identity
    BEFORE UPDATE ON app.operations
    FOR EACH ROW
    EXECUTE FUNCTION app.operations_protect_identity();

CREATE FUNCTION app.operation_events_insert_only()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $protect_ev$
BEGIN
    RAISE EXCEPTION 'operation_events is insert-only';
END;
$protect_ev$;

ALTER FUNCTION app.operation_events_insert_only() OWNER TO course_owner;

CREATE TRIGGER operation_events_insert_only
    BEFORE UPDATE OR DELETE ON app.operation_events
    FOR EACH ROW
    EXECUTE FUNCTION app.operation_events_insert_only();

CREATE FUNCTION app.operation_result_json(p_operation app.operations)
RETURNS jsonb
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog, pg_temp
AS $to_json$
    SELECT jsonb_build_object(
        'operationId', p_operation.operation_id,
        'requestId', p_operation.request_id,
        'operationKind', p_operation.operation_kind,
        'amount', p_operation.amount_text,
        'currency', p_operation.currency,
        'status', p_operation.status
    );
$to_json$;

ALTER FUNCTION app.operation_result_json(app.operations) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.operation_result_json(app.operations) FROM PUBLIC;

CREATE FUNCTION app.payment_request(p_context jsonb, p_payload jsonb)
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
    v_payload_hash text;
    v_operation app.operations%ROWTYPE;
    v_result jsonb;
    v_existing_hash text;
    v_existing_result jsonb;
    v_key_count integer;
    v_constraint text;
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

    v_payload_hash := encode(sha256(convert_to(p_payload::text, 'UTF8')), 'hex');

    BEGIN
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
            'payment',
            'request',
            v_request_id,
            v_payload_hash,
            v_result,
            v_operation.operation_id
        );

        RETURN v_result;
    EXCEPTION
        WHEN unique_violation THEN
            GET STACKED DIAGNOSTICS v_constraint = CONSTRAINT_NAME;
            IF v_constraint IS DISTINCT FROM 'idempotency_keys_pkey' THEN
                RETURN api.error_envelope('internal.error', 'action failed');
            END IF;

            SELECT k.payload_hash, k.result
            INTO v_existing_hash, v_existing_result
            FROM app.idempotency_keys AS k
            WHERE k.principal = v_principal
              AND k.module = 'payment'
              AND k.action = 'request'
              AND k.idempotency_key = v_request_id;

            IF NOT FOUND THEN
                RETURN api.error_envelope('internal.error', 'action failed');
            END IF;
            IF v_existing_hash IS DISTINCT FROM v_payload_hash THEN
                RETURN api.error_envelope('idempotency.conflict', 'idempotency key was reused with a different payload');
            END IF;
            RETURN v_existing_result;
    END;
END;
$pay$;

ALTER FUNCTION app.payment_request(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_request(jsonb, jsonb) FROM PUBLIC;

CREATE FUNCTION app.operation_get(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $get$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_key_count integer;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_key_count := (SELECT count(*)::integer FROM jsonb_object_keys(p_payload));
    IF v_key_count <> 1
        OR jsonb_typeof(p_payload -> 'operationId') IS DISTINCT FROM 'string'
    THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    BEGIN
        v_operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION
        WHEN invalid_text_representation THEN
            RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', app.operation_result_json(v_operation)
    );
END;
$get$;

ALTER FUNCTION app.operation_get(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.operation_get(jsonb, jsonb) FROM PUBLIC;

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($manifest$
{
  "module": "payment",
  "action": "request",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "payment_request",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationKind", "amount", "currency"],
    "properties": {
      "operationKind": { "type": "string", "enum": ["PAYMENT_EXECUTION", "PAYMENT_APPROVAL"] },
      "amount": { "type": "string" },
      "currency": { "type": "string", "const": "RUB" }
    }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["operationId", "requestId", "operationKind", "amount", "currency", "status"],
    "properties": {
      "operationId": { "type": "string" },
      "requestId": { "type": "string" },
      "operationKind": { "type": "string" },
      "amount": { "type": "string" },
      "currency": { "type": "string" },
      "status": { "type": "string" }
    }
  },
  "outcomes": ["CREATED"],
  "required_policy": ["payment:write"],
  "idempotency_mode": "required",
  "idempotency_scope": "principal_action",
  "timeout_ms": 5000
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish payment.request failed: %', v_result;
    END IF;

    v_result := api.activate_action('payment.request', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate payment.request failed: %', v_result;
    END IF;

    v_result := api.publish_action($manifest$
{
  "module": "operation",
  "action": "get",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "operation_get",
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
    "required": ["operationId", "requestId", "operationKind", "amount", "currency", "status"],
    "properties": {
      "operationId": { "type": "string" },
      "requestId": { "type": "string" },
      "operationKind": { "type": "string" },
      "amount": { "type": "string" },
      "currency": { "type": "string" },
      "status": { "type": "string" }
    }
  },
  "outcomes": ["FOUND"],
  "required_policy": ["payment:read"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 5000
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish operation.get failed: %', v_result;
    END IF;

    v_result := api.activate_action('operation.get', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate operation.get failed: %', v_result;
    END IF;
END;
$boot$;
