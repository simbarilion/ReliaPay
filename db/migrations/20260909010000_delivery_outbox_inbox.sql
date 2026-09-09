-- Schema delivery: Outbox/Inbox, receipts, decisions и фиксированные SQL-границы Python.
-- Новые таблицы и функции; checksum уже применённых миграций не меняется.

ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA delivery
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA delivery
    REVOKE ALL ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA delivery
    REVOKE ALL ON SEQUENCES FROM PUBLIC;

CREATE TABLE delivery.external_requests (
    external_request_id text PRIMARY KEY,
    operation_id uuid NOT NULL REFERENCES app.operations (operation_id),
    process_id uuid,
    state text NOT NULL,
    payload_hash text NOT NULL,
    amount text NOT NULL,
    currency text NOT NULL,
    correlation_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT external_requests_id_chk CHECK (
        char_length(external_request_id) BETWEEN 1 AND 128
        AND external_request_id !~ E'[\\r\\n]'
    ),
    CONSTRAINT external_requests_state_chk CHECK (state IN ('CREATED', 'SENT', 'CONFIRMED')),
    CONSTRAINT external_requests_currency_chk CHECK (currency = 'RUB'),
    CONSTRAINT external_requests_payload_hash_chk CHECK (payload_hash ~ '^[0-9a-f]{64}$')
);

ALTER TABLE delivery.external_requests OWNER TO course_owner;

CREATE TABLE delivery.outbox (
    outbox_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    external_request_id text NOT NULL REFERENCES delivery.external_requests (external_request_id),
    state text NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz,
    last_error_code text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    delivered_at timestamptz,
    lease_owner text,
    lease_version bigint NOT NULL DEFAULT 0,
    lease_until timestamptz,
    provider_payment_id text,
    CONSTRAINT outbox_external_uq UNIQUE (external_request_id),
    CONSTRAINT outbox_state_chk CHECK (
        state IN ('PENDING', 'LEASED', 'RETRY_WAIT', 'DELIVERED', 'DEAD', 'CONFIRMED')
    ),
    CONSTRAINT outbox_attempt_chk CHECK (attempt_count >= 0)
);

ALTER TABLE delivery.outbox OWNER TO course_owner;

CREATE INDEX outbox_claim_idx
    ON delivery.outbox (state, next_attempt_at, created_at);

CREATE TABLE delivery.inbox (
    message_id text PRIMARY KEY,
    body_hash text NOT NULL,
    state text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at timestamptz,
    external_request_id text REFERENCES delivery.external_requests (external_request_id),
    CONSTRAINT inbox_message_id_chk CHECK (
        char_length(message_id) BETWEEN 1 AND 128
        AND message_id !~ E'[\\r\\n]'
    ),
    CONSTRAINT inbox_state_chk CHECK (state IN ('RECEIVED', 'APPLIED', 'CONFLICT')),
    CONSTRAINT inbox_body_hash_chk CHECK (body_hash ~ '^[0-9a-f]{64}$')
);

ALTER TABLE delivery.inbox OWNER TO course_owner;

CREATE TABLE delivery.receipts (
    message_id text PRIMARY KEY REFERENCES delivery.inbox (message_id),
    external_request_id text NOT NULL REFERENCES delivery.external_requests (external_request_id),
    message_version integer NOT NULL,
    outcome text NOT NULL,
    signature_valid boolean NOT NULL,
    body_hash text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at timestamptz,
    CONSTRAINT receipts_version_chk CHECK (message_version = 1),
    CONSTRAINT receipts_outcome_chk CHECK (outcome IN ('COMPLETED', 'REJECTED')),
    CONSTRAINT receipts_body_hash_chk CHECK (body_hash ~ '^[0-9a-f]{64}$')
);

ALTER TABLE delivery.receipts OWNER TO course_owner;

CREATE TABLE delivery.decisions (
    decision_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.processes (process_id),
    step_instance_id uuid NOT NULL REFERENCES workflow.step_instances (step_instance_id),
    source text NOT NULL,
    principal text,
    reason_hash text,
    outcome text NOT NULL,
    rule_version text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT decisions_source_chk CHECK (source IN ('LIMIT_RULE', 'MANUAL')),
    CONSTRAINT decisions_outcome_chk CHECK (outcome IN ('APPROVED', 'REJECTED')),
    CONSTRAINT decisions_reason_hash_chk CHECK (reason_hash IS NULL OR reason_hash ~ '^[0-9a-f]{64}$')
);

ALTER TABLE delivery.decisions OWNER TO course_owner;

CREATE FUNCTION delivery.claim_outbox(p_owner text, p_limit integer)
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
BEGIN
    IF p_owner IS NULL OR btrim(p_owner) = '' THEN
        RETURN;
    END IF;

    v_limit := GREATEST(1, LEAST(COALESCE(p_limit, 1), 32));

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
            lease_until = clock_timestamp() + interval '2 seconds',
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

CREATE FUNCTION delivery.succeed_outbox(
    p_outbox_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_provider_payment_id text
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $ok$
DECLARE
    v_row delivery.outbox%ROWTYPE;
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

    IF v_row.state = 'DELIVERED' THEN
        RETURN jsonb_build_object('status', 'ok', 'state', 'DELIVERED');
    END IF;

    IF v_row.state IS DISTINCT FROM 'LEASED'
        OR v_row.lease_owner IS DISTINCT FROM btrim(p_owner)
        OR v_row.lease_version IS DISTINCT FROM p_lease_version
    THEN
        RETURN jsonb_build_object('status', 'error', 'code', 'delivery.lease_stale');
    END IF;

    UPDATE delivery.outbox
    SET state = 'DELIVERED',
        delivered_at = clock_timestamp(),
        provider_payment_id = NULLIF(btrim(COALESCE(p_provider_payment_id, '')), ''),
        last_error_code = NULL,
        next_attempt_at = NULL,
        lease_until = NULL
    WHERE outbox_id = p_outbox_id
      AND state = 'LEASED';

    UPDATE delivery.external_requests
    SET state = 'SENT'
    WHERE delivery.external_requests.external_request_id = v_row.external_request_id
      AND state = 'CREATED';

    RETURN jsonb_build_object('status', 'ok', 'state', 'DELIVERED');
END;
$ok$;

ALTER FUNCTION delivery.succeed_outbox(uuid, text, bigint, text) OWNER TO course_owner;

CREATE FUNCTION delivery.fail_outbox(
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
    v_next timestamptz;
    v_state text;
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

    v_retryable := COALESCE(p_error_code, '') LIKE '%.retryable';
    IF v_retryable AND v_row.attempt_count < 3 THEN
        v_delay_ms := (ARRAY[200, 400, 800])[v_row.attempt_count];
        v_next := clock_timestamp() + (COALESCE(v_delay_ms, 200) * interval '1 millisecond');
        v_state := 'RETRY_WAIT';
    ELSE
        v_next := NULL;
        v_state := 'DEAD';
    END IF;

    UPDATE delivery.outbox
    SET state = v_state,
        last_error_code = NULLIF(btrim(COALESCE(p_error_code, '')), ''),
        next_attempt_at = v_next,
        lease_until = NULL
    WHERE outbox_id = p_outbox_id
      AND state = 'LEASED';

    RETURN jsonb_build_object('status', 'ok', 'state', v_state);
END;
$fail$;

ALTER FUNCTION delivery.fail_outbox(uuid, text, bigint, text) OWNER TO course_owner;

CREATE FUNCTION delivery.reconcile_inbox(p_limit integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $recon$
BEGIN
    PERFORM GREATEST(1, LEAST(COALESCE(p_limit, 1), 32));
    RETURN 0;
END;
$recon$;

ALTER FUNCTION delivery.reconcile_inbox(integer) OWNER TO course_owner;

CREATE VIEW autocheck.external_requests (
    external_request_id,
    operation_id,
    state,
    payload_hash,
    created_at
) AS
SELECT
    r.external_request_id,
    r.operation_id,
    r.state,
    r.payload_hash,
    r.created_at
FROM delivery.external_requests AS r;

CREATE VIEW autocheck.receipts (
    message_id,
    external_request_id,
    message_version,
    outcome,
    signature_valid,
    body_hash,
    received_at,
    applied_at
) AS
SELECT
    t.message_id,
    t.external_request_id,
    t.message_version,
    t.outcome,
    t.signature_valid,
    t.body_hash,
    t.received_at,
    t.applied_at
FROM delivery.receipts AS t;

CREATE VIEW autocheck.outbox (
    outbox_id,
    external_request_id,
    state,
    attempt_count,
    next_attempt_at,
    last_error_code,
    created_at,
    delivered_at
) AS
SELECT
    o.outbox_id,
    o.external_request_id,
    o.state,
    o.attempt_count,
    o.next_attempt_at,
    o.last_error_code,
    o.created_at,
    o.delivered_at
FROM delivery.outbox AS o;

CREATE VIEW autocheck.inbox (
    message_id,
    body_hash,
    state,
    received_at,
    applied_at
) AS
SELECT
    i.message_id,
    i.body_hash,
    i.state,
    i.received_at,
    i.applied_at
FROM delivery.inbox AS i;

CREATE VIEW autocheck.decisions (
    decision_id,
    process_id,
    step_instance_id,
    source,
    principal,
    reason_hash,
    outcome,
    rule_version,
    created_at
) AS
SELECT
    d.decision_id,
    d.process_id,
    d.step_instance_id,
    d.source,
    d.principal,
    d.reason_hash,
    d.outcome,
    d.rule_version,
    d.created_at
FROM delivery.decisions AS d;

DO $grants$
DECLARE
    v_view text;
    v_table text;
    r record;
BEGIN
    FOREACH v_view IN ARRAY ARRAY[
        'external_requests',
        'receipts',
        'outbox',
        'inbox',
        'decisions'
    ]
    LOOP
        EXECUTE format('ALTER VIEW autocheck.%I OWNER TO course_owner', v_view);
        EXECUTE format('ALTER VIEW autocheck.%I SET (security_invoker = false)', v_view);
        EXECUTE format('REVOKE ALL ON autocheck.%I FROM PUBLIC', v_view);
        EXECUTE format('GRANT SELECT ON autocheck.%I TO course_runtime', v_view);
        EXECUTE format('GRANT SELECT ON autocheck.%I TO course_publication', v_view);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON autocheck.%I FROM course_runtime', v_view);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON autocheck.%I FROM course_publication', v_view);
    END LOOP;

    FOREACH v_table IN ARRAY ARRAY[
        'external_requests',
        'outbox',
        'inbox',
        'receipts',
        'decisions'
    ]
    LOOP
        EXECUTE format('REVOKE ALL ON TABLE delivery.%I FROM PUBLIC', v_table);
        EXECUTE format('REVOKE ALL ON TABLE delivery.%I FROM course_runtime', v_table);
        EXECUTE format('REVOKE ALL ON TABLE delivery.%I FROM course_publication', v_table);
        EXECUTE format('REVOKE ALL ON TABLE delivery.%I FROM workflow_worker', v_table);
        EXECUTE format('REVOKE ALL ON TABLE delivery.%I FROM outbox_dispatcher', v_table);
        EXECUTE format('REVOKE ALL ON TABLE delivery.%I FROM inbox_reconciler', v_table);
    END LOOP;

    FOR r IN
        SELECT p.oid::regprocedure AS sig
        FROM pg_catalog.pg_proc AS p
        JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
        WHERE n.nspname = 'delivery'
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM outbox_dispatcher', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM inbox_reconciler', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM course_runtime', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM course_publication', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM workflow_worker', r.sig);
    END LOOP;
END;
$grants$;

GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.succeed_outbox(uuid, text, bigint, text) TO outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.fail_outbox(uuid, text, bigint, text) TO outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO inbox_reconciler;

REVOKE ALL ON FUNCTION delivery.claim_outbox(text, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION delivery.succeed_outbox(uuid, text, bigint, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION delivery.fail_outbox(uuid, text, bigint, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION delivery.reconcile_inbox(integer) FROM PUBLIC;
