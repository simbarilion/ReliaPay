-- Claim ceiling: expired/reclaimable outbox at max attempts becomes DEAD
-- without returning a row for a new provider HTTP attempt. Late receipt still
-- confirms DEAD → CONFIRMED via app.receipt_accept (unchanged).

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
    v_max_attempts integer;
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

    v_raw := NULLIF(btrim(current_setting('app.outbox_max_attempts', true)), '');
    BEGIN
        v_max_attempts := GREATEST(1, LEAST(COALESCE(v_raw::integer, 4), 32));
    EXCEPTION
        WHEN invalid_text_representation THEN
            v_max_attempts := 4;
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

        -- Budget already spent (typical: lease expired on the last attempt).
        -- Mark DEAD without incrementing attempt_count and without HTTP return.
        IF v_row.attempt_count >= v_max_attempts THEN
            UPDATE delivery.outbox AS o
            SET state = 'DEAD',
                last_error_code = COALESCE(
                    o.last_error_code,
                    'delivery.attempts_exhausted'),
                next_attempt_at = NULL,
                lease_until = NULL,
                lease_owner = NULL,
                dead_at = COALESCE(o.dead_at, clock_timestamp())
            WHERE o.outbox_id = v_row.outbox_id
              AND o.state IN ('PENDING', 'RETRY_WAIT', 'LEASED');
            CONTINUE;
        END IF;

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

REVOKE ALL ON FUNCTION delivery.claim_outbox(text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO outbox_dispatcher;
