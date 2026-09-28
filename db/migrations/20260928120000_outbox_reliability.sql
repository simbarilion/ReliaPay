-- Week 4 phase 2: dead_at, COURSE_OUTBOX_* via session GUC, jitter, 4 attempts,
-- extended autocheck.outbox. Fixed claim/succeed/fail signatures preserved.

ALTER TABLE delivery.outbox
    ADD COLUMN IF NOT EXISTS dead_at timestamptz;

COMMENT ON COLUMN delivery.outbox.dead_at IS
    'Set when delivery enters DEAD; kept after late receipt → CONFIRMED.';

CREATE OR REPLACE FUNCTION delivery.claim_outbox(p_owner text, p_limit integer)
RETURNS TABLE (
    outbox_id uuid,
    lease_version bigint,
    external_request_id text,
    correlation_id uuid,
    amount text,
    currency text
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $claim$
DECLARE
    v_limit integer;
    v_n integer := 0;
    v_row delivery.outbox%ROWTYPE;
    v_ext delivery.external_requests%ROWTYPE;
    v_lease_ms integer;
    v_raw text;
BEGIN
    IF p_owner IS NULL OR btrim(p_owner) = '' THEN
        RETURN;
    END IF;

    v_limit := GREATEST(1, LEAST(COALESCE(p_limit, 1), 32));

    v_raw := NULLIF(btrim(current_setting('app.outbox_lease_ms', true)), '');
    BEGIN
        v_lease_ms := GREATEST(1, LEAST(COALESCE(v_raw::integer, 2000), 30000));
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_lease_ms := 2000;
    END;

    FOR v_row IN
        SELECT o.*
        FROM delivery.outbox AS o
        WHERE (
                o.state IN ('PENDING', 'RETRY_WAIT')
                AND COALESCE(o.next_attempt_at, clock_timestamp()) <= clock_timestamp()
            )
           OR (
                o.state = 'LEASED'
                AND o.lease_until IS NOT NULL
                AND o.lease_until < clock_timestamp()
            )
        ORDER BY COALESCE(o.next_attempt_at, o.created_at), o.outbox_id
        FOR UPDATE SKIP LOCKED
    LOOP
        EXIT WHEN v_n >= v_limit;

        UPDATE delivery.outbox AS o
        SET state = 'LEASED',
            lease_owner = btrim(p_owner),
            lease_version = o.lease_version + 1,
            lease_until = clock_timestamp() + (v_lease_ms * interval '1 millisecond'),
            attempt_count = o.attempt_count + 1
        WHERE o.outbox_id = v_row.outbox_id
        RETURNING * INTO v_row;

        SELECT * INTO v_ext
        FROM delivery.external_requests
        WHERE delivery.external_requests.external_request_id = v_row.external_request_id;

        outbox_id := v_row.outbox_id;
        lease_version := v_row.lease_version;
        external_request_id := v_row.external_request_id;
        correlation_id := v_ext.correlation_id;
        amount := v_ext.amount;
        currency := v_ext.currency;
        RETURN NEXT;
        v_n := v_n + 1;
    END LOOP;
END;
$claim$;

ALTER FUNCTION delivery.claim_outbox(text, integer) OWNER TO course_owner;

CREATE OR REPLACE FUNCTION delivery.fail_outbox(
    p_outbox_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_error_code text
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $fail$
DECLARE
    v_row delivery.outbox%ROWTYPE;
    v_retryable boolean;
    v_delay_ms integer;
    v_jitter_ms integer;
    v_next timestamptz;
    v_state text;
    v_max_attempts integer;
    v_base_ms integer;
    v_backoff_max_ms integer;
    v_jitter_max_ms integer;
    v_raw text;
    v_exp numeric;
BEGIN
    IF p_outbox_id IS NULL OR p_owner IS NULL OR btrim(p_owner) = '' OR p_lease_version IS NULL THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'request.invalid');
    END IF;

    SELECT * INTO v_row
    FROM delivery.outbox
    WHERE outbox_id = p_outbox_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.not_found');
    END IF;

    IF v_row.state = 'CONFIRMED' THEN
        RETURN jsonb_build_object('status', 'ok', 'state', 'CONFIRMED');
    END IF;

    IF v_row.state IN ('DELIVERED', 'DEAD') THEN
        RETURN jsonb_build_object('status', 'ok', 'state', v_row.state);
    END IF;

    IF v_row.state IS DISTINCT FROM 'LEASED'
        OR v_row.lease_owner IS DISTINCT FROM btrim(p_owner)
        OR v_row.lease_version IS DISTINCT FROM p_lease_version
    THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.lease_stale');
    END IF;

    v_raw := NULLIF(btrim(current_setting('app.outbox_max_attempts', true)), '');
    BEGIN
        v_max_attempts := GREATEST(1, LEAST(COALESCE(v_raw::integer, 4), 32));
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_max_attempts := 4;
    END;

    v_raw := NULLIF(btrim(current_setting('app.outbox_backoff_base_ms', true)), '');
    BEGIN
        v_base_ms := GREATEST(1, LEAST(COALESCE(v_raw::integer, 200), 60000));
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_base_ms := 200;
    END;

    v_raw := NULLIF(btrim(current_setting('app.outbox_backoff_max_ms', true)), '');
    BEGIN
        v_backoff_max_ms := GREATEST(v_base_ms, LEAST(COALESCE(v_raw::integer, 800), 300000));
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_backoff_max_ms := 800;
    END;

    v_raw := NULLIF(btrim(current_setting('app.outbox_jitter_max_ms', true)), '');
    BEGIN
        v_jitter_max_ms := GREATEST(0, LEAST(COALESCE(v_raw::integer, 100), 60000));
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_jitter_max_ms := 100;
    END;

    v_retryable := COALESCE(p_error_code, '') LIKE '%.retryable';
    IF v_retryable AND v_row.attempt_count < v_max_attempts THEN
        v_exp := power(2::numeric, GREATEST(v_row.attempt_count - 1, 0));
        v_delay_ms := LEAST((v_base_ms * v_exp)::integer, v_backoff_max_ms);
        IF v_jitter_max_ms > 0 THEN
            v_jitter_ms := floor(random() * (v_jitter_max_ms + 1))::integer;
        ELSE
            v_jitter_ms := 0;
        END IF;
        v_next := clock_timestamp()
            + ((v_delay_ms + v_jitter_ms) * interval '1 millisecond');
        v_state := 'RETRY_WAIT';

        UPDATE delivery.outbox
        SET state = v_state,
            last_error_code = NULLIF(btrim(COALESCE(p_error_code, '')), ''),
            next_attempt_at = v_next,
            lease_until = NULL
        WHERE outbox_id = p_outbox_id
          AND state = 'LEASED';
    ELSE
        v_state := 'DEAD';
        UPDATE delivery.outbox
        SET state = v_state,
            last_error_code = NULLIF(btrim(COALESCE(p_error_code, '')), ''),
            next_attempt_at = NULL,
            lease_until = NULL,
            dead_at = COALESCE(dead_at, clock_timestamp())
        WHERE outbox_id = p_outbox_id
          AND state = 'LEASED';
    END IF;

    RETURN jsonb_build_object('status', 'ok', 'state', v_state);
END;
$fail$;

ALTER FUNCTION delivery.fail_outbox(uuid, text, bigint, text) OWNER TO course_owner;

-- Late receipt → CONFIRMED keeps diagnostic dead_at / last_error_code / attempt_count.
CREATE OR REPLACE FUNCTION app.receipt_accept(p_context jsonb, p_payload jsonb)
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

DROP VIEW IF EXISTS autocheck.outbox;

CREATE VIEW autocheck.outbox (
    outbox_id,
    external_request_id,
    state,
    attempt_count,
    next_attempt_at,
    last_error_code,
    created_at,
    delivered_at,
    lease_owner,
    lease_version,
    lease_until,
    dead_at
) AS
SELECT
    o.outbox_id,
    o.external_request_id,
    o.state,
    o.attempt_count,
    o.next_attempt_at,
    o.last_error_code,
    o.created_at,
    o.delivered_at,
    o.lease_owner,
    o.lease_version,
    o.lease_until,
    o.dead_at
FROM delivery.outbox AS o;

ALTER VIEW autocheck.outbox OWNER TO course_owner;
ALTER VIEW autocheck.outbox SET (security_invoker = false);
REVOKE ALL ON autocheck.outbox FROM PUBLIC;
GRANT SELECT ON autocheck.outbox TO course_runtime;
GRANT SELECT ON autocheck.outbox TO course_publication;
REVOKE INSERT, UPDATE, DELETE ON autocheck.outbox FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE ON autocheck.outbox FROM course_publication;

DO $reader$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'autocheck_reader') THEN
        EXECUTE 'GRANT SELECT ON autocheck.outbox TO autocheck_reader';
    END IF;
END;
$reader$;

REVOKE ALL ON FUNCTION delivery.claim_outbox(text, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION delivery.fail_outbox(uuid, text, bigint, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.receipt_accept(jsonb, jsonb) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.fail_outbox(uuid, text, bigint, text) TO outbox_dispatcher;