-- receipt.accept: Inbox, receipts, Outbox CONFIRMED. HMAC остаётся generic C# границей.
-- Новые функции; checksum уже применённых миграций не меняется.

CREATE FUNCTION app.receipt_accept(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $accept$
DECLARE
    v_message_id text;
    v_provider_id text;
    v_external_id text;
    v_outcome text;
    v_occurred text;
    v_version integer;
    v_verified boolean;
    v_sig_version integer;
    v_ext delivery.external_requests%ROWTYPE;
    v_inbox delivery.inbox%ROWTYPE;
    v_compact text;
    v_hash text;
    v_result jsonb;
BEGIN
    p_context := COALESCE(p_context, '{}'::jsonb);
    p_payload := COALESCE(p_payload, '{}'::jsonb);

    v_verified := (p_context #>> '{transport,signatureVerified}') = 'true';
    BEGIN
        v_sig_version := (p_context #>> '{transport,signatureVersion}')::integer;
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_sig_version := NULL;
    END;

    IF v_verified IS NOT TRUE OR v_sig_version IS DISTINCT FROM 1 THEN
        RETURN api.error_envelope('receipt.signature_required', 'signed receipt is required');
    END IF;

    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_message_id := p_payload ->> 'messageId';
    v_provider_id := p_payload ->> 'providerPaymentId';
    v_external_id := p_payload ->> 'externalRequestId';
    v_outcome := p_payload ->> 'outcome';
    v_occurred := p_payload ->> 'occurredAt';

    BEGIN
        v_version := (p_payload ->> 'version')::integer;
    EXCEPTION
        WHEN invalid_text_representation THEN
            RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END;

    IF v_message_id IS NULL OR v_message_id = ''
        OR v_provider_id IS NULL OR v_provider_id = ''
        OR v_external_id IS NULL OR v_external_id = ''
        OR v_occurred IS NULL OR v_occurred = ''
        OR v_message_id IS DISTINCT FROM v_provider_id
        OR v_version IS DISTINCT FROM 1
        OR v_outcome NOT IN ('COMPLETED', 'REJECTED')
        OR v_message_id ~ E'[\\r\\n]'
        OR v_provider_id ~ E'[\\r\\n]'
        OR v_external_id ~ E'[\\r\\n]'
        OR v_occurred ~ E'[\\r\\n]'
    THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_ext
    FROM delivery.external_requests
    WHERE external_request_id = v_external_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope(
            'receipt.external_request_not_found',
            'external request does not exist');
    END IF;

    v_compact := '{"externalRequestId":' || to_json(v_external_id)
        || ',"messageId":' || to_json(v_message_id)
        || ',"occurredAt":' || to_json(v_occurred)
        || ',"outcome":' || to_json(v_outcome)
        || ',"providerPaymentId":' || to_json(v_provider_id)
        || ',"version":1}';
    v_hash := encode(sha256(convert_to(v_compact, 'UTF8')), 'hex');

    INSERT INTO delivery.inbox (
        message_id,
        body_hash,
        state,
        external_request_id
    )
    VALUES (
        v_message_id,
        v_hash,
        'RECEIVED',
        v_external_id
    )
    ON CONFLICT (message_id) DO NOTHING
    RETURNING * INTO v_inbox;

    IF v_inbox.message_id IS NULL THEN
        SELECT * INTO v_inbox
        FROM delivery.inbox
        WHERE message_id = v_message_id
        FOR UPDATE;

        IF v_inbox.body_hash IS DISTINCT FROM v_hash THEN
            RETURN api.error_envelope(
                'idempotency.conflict',
                'message was reused with a different payload');
        END IF;

        UPDATE delivery.outbox
        SET state = 'CONFIRMED',
            last_error_code = NULL,
            next_attempt_at = NULL,
            lease_until = NULL,
            lease_owner = NULL
        WHERE external_request_id = v_external_id
          AND state IS DISTINCT FROM 'CONFIRMED';

        UPDATE delivery.external_requests
        SET state = 'CONFIRMED'
        WHERE external_request_id = v_external_id
          AND state IS DISTINCT FROM 'CONFIRMED';

        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'DUPLICATE',
            'result', jsonb_build_object(
                'messageId', v_inbox.message_id,
                'externalRequestId', COALESCE(v_inbox.external_request_id, v_external_id),
                'state', v_inbox.state
            )
        );
    END IF;

    INSERT INTO delivery.receipts (
        message_id,
        external_request_id,
        message_version,
        outcome,
        signature_valid,
        body_hash,
        received_at
    )
    VALUES (
        v_message_id,
        v_external_id,
        1,
        v_outcome,
        true,
        v_hash,
        v_inbox.received_at
    );

    UPDATE delivery.outbox
    SET state = 'CONFIRMED',
        last_error_code = NULL,
        next_attempt_at = NULL,
        lease_until = NULL,
        lease_owner = NULL
    WHERE external_request_id = v_external_id
      AND state IS DISTINCT FROM 'CONFIRMED';

    UPDATE delivery.external_requests
    SET state = 'CONFIRMED'
    WHERE external_request_id = v_external_id
      AND state IS DISTINCT FROM 'CONFIRMED';

    v_result := jsonb_build_object(
        'status', 'ok',
        'outcome', 'RECEIVED',
        'result', jsonb_build_object(
            'messageId', v_message_id,
            'externalRequestId', v_external_id,
            'state', 'RECEIVED'
        )
    );

    RETURN v_result;
END;
$accept$;

ALTER FUNCTION app.receipt_accept(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.receipt_accept(jsonb, jsonb) FROM PUBLIC;

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($manifest$
{
  "module": "receipt",
  "action": "accept",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "receipt_accept",
  "request_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["externalRequestId", "messageId", "occurredAt", "outcome", "providerPaymentId", "version"],
    "properties": {
      "externalRequestId": {"type": "string", "minLength": 1, "maxLength": 128, "not": {"pattern": "[\\r\\n]"}},
      "messageId": {"type": "string", "minLength": 1, "maxLength": 128, "not": {"pattern": "[\\r\\n]"}},
      "occurredAt": {"type": "string", "format": "date-time", "maxLength": 64, "pattern": "Z$", "not": {"pattern": "[\\r\\n]"}},
      "outcome": {"enum": ["COMPLETED", "REJECTED"]},
      "providerPaymentId": {"type": "string", "minLength": 1, "maxLength": 128, "not": {"pattern": "[\\r\\n]"}},
      "version": {"const": 1}
    }
  },
  "response_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["messageId", "externalRequestId", "state"],
    "properties": {
      "messageId": {"type": "string", "minLength": 1, "maxLength": 128},
      "externalRequestId": {"type": "string", "minLength": 1, "maxLength": 128},
      "state": {"enum": ["RECEIVED", "APPLIED"]}
    }
  },
  "outcomes": ["RECEIVED", "DUPLICATE"],
  "required_policy": ["receipt:write"],
  "idempotency_mode": "required",
  "idempotency_scope": "principal_action",
  "timeout_ms": 5000
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish receipt.accept failed: %', v_result;
    END IF;

    v_result := api.activate_action('receipt.accept', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate receipt.accept failed: %', v_result;
    END IF;
END;
$boot$;
