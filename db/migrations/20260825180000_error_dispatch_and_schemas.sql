-- ERROR dispatch after C# rollback, timeout cap, canonical payment/operation schemas.

ALTER TABLE api.actions DROP CONSTRAINT IF EXISTS actions_timeout_chk;
ALTER TABLE api.actions
    ADD CONSTRAINT actions_timeout_chk CHECK (timeout_ms >= 1 AND timeout_ms <= 30000);

CREATE OR REPLACE FUNCTION api.record_error_dispatch(
    p_context jsonb,
    p_module text,
    p_action text,
    p_version integer
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $err$
DECLARE
    v_correlation uuid;
BEGIN
    p_context := COALESCE(p_context, '{}'::jsonb);

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
        encode(sha256(convert_to(''::text, 'UTF8')), 'hex'),
        'ERROR',
        NULL
    );
END;
$err$;

ALTER FUNCTION api.record_error_dispatch(jsonb, text, text, integer) OWNER TO course_owner;
REVOKE ALL ON FUNCTION api.record_error_dispatch(jsonb, text, text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.record_error_dispatch(jsonb, text, text, integer) TO course_runtime;

ALTER TABLE api.actions DISABLE TRIGGER actions_protect_immutable;

UPDATE api.actions
SET request_schema = $payreq${
  "type": "object",
  "additionalProperties": false,
  "required": ["operationKind", "amount", "currency"],
  "properties": {
    "operationKind": {
      "type": "string",
      "enum": ["PAYMENT_EXECUTION", "PAYMENT_APPROVAL"]
    },
    "amount": {
      "type": "string",
      "pattern": "^(?:0\\.(?:0[1-9]|[1-9][0-9]?)|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"
    },
    "currency": {
      "type": "string",
      "const": "RUB"
    }
  }
}$payreq$::jsonb,
    response_schema = $payres${
  "type": "object",
  "additionalProperties": false,
  "required": ["operationId", "requestId", "operationKind", "amount", "currency", "status"],
  "properties": {
    "operationId": { "type": "string", "format": "uuid" },
    "requestId": { "type": "string", "minLength": 1 },
    "operationKind": {
      "type": "string",
      "enum": ["PAYMENT_EXECUTION", "PAYMENT_APPROVAL"]
    },
    "amount": {
      "type": "string",
      "pattern": "^(?:0\\.(?:0[1-9]|[1-9][0-9]?)|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"
    },
    "currency": { "type": "string", "const": "RUB" },
    "status": {
      "type": "string",
      "enum": ["CREATED", "PROCESSING", "COMPLETED", "REJECTED"]
    }
  }
}$payres$::jsonb
WHERE module = 'payment'
  AND action = 'request'
  AND version = 1;

UPDATE api.actions
SET request_schema = $getreq${
  "type": "object",
  "additionalProperties": false,
  "required": ["operationId"],
  "properties": {
    "operationId": { "type": "string", "format": "uuid" }
  }
}$getreq$::jsonb,
    response_schema = $getres${
  "type": "object",
  "additionalProperties": false,
  "required": ["operationId", "requestId", "operationKind", "amount", "currency", "status"],
  "properties": {
    "operationId": { "type": "string", "format": "uuid" },
    "requestId": { "type": "string", "minLength": 1 },
    "operationKind": {
      "type": "string",
      "enum": ["PAYMENT_EXECUTION", "PAYMENT_APPROVAL"]
    },
    "amount": {
      "type": "string",
      "pattern": "^(?:0\\.(?:0[1-9]|[1-9][0-9]?)|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"
    },
    "currency": { "type": "string", "const": "RUB" },
    "status": {
      "type": "string",
      "enum": ["CREATED", "PROCESSING", "COMPLETED", "REJECTED"]
    }
  }
}$getres$::jsonb
WHERE module = 'operation'
  AND action = 'get'
  AND version = 1;

UPDATE api.actions AS a
SET manifest_checksum = api.manifest_checksum(
    jsonb_build_object(
        'module', a.module,
        'action', a.action,
        'version', a.version,
        'http_method', a.http_method,
        'target_schema', a.target_schema,
        'target_function', a.target_function,
        'request_schema', a.request_schema,
        'response_schema', a.response_schema,
        'outcomes', a.outcomes,
        'required_policy', a.required_policy,
        'idempotency_mode', a.idempotency_mode,
        'idempotency_scope', a.idempotency_scope,
        'timeout_ms', a.timeout_ms
    )
)
WHERE (a.module = 'payment' AND a.action = 'request' AND a.version = 1)
   OR (a.module = 'operation' AND a.action = 'get' AND a.version = 1);

ALTER TABLE api.actions ENABLE TRIGGER actions_protect_immutable;
