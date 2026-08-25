-- Read-only проекции autocheck и запрет DML для прикладных ролей.

DO $info$
BEGIN
    EXECUTE format(
        $sql$
        CREATE VIEW autocheck.contract_info AS
        SELECT %L::text AS contract_version, %L::timestamptz AS generated_at
        $sql$,
        'course-1',
        clock_timestamp());
END;
$info$;

CREATE VIEW autocheck.action_definitions (
    module,
    action,
    version,
    http_method,
    target_schema,
    target_function,
    outcomes,
    enabled,
    is_default
) AS
SELECT
    a.module,
    a.action,
    a.version,
    a.http_method,
    a.target_schema,
    a.target_function,
    a.outcomes,
    a.enabled,
    a.is_default
FROM api.actions AS a;

CREATE VIEW autocheck.action_dispatches (
    correlation_id,
    request_id,
    module,
    action,
    version,
    principal,
    payload_hash,
    status,
    outcome,
    occurred_at
) AS
SELECT
    d.correlation_id,
    d.request_id,
    d.module,
    d.action,
    d.version,
    d.principal,
    d.payload_hash,
    d.status,
    d.outcome,
    d.occurred_at
FROM api.action_dispatches AS d;

CREATE VIEW autocheck.operations (
    operation_id,
    request_id,
    operation_kind,
    amount,
    currency,
    status,
    process_id,
    created_at,
    updated_at
) AS
SELECT
    o.operation_id,
    o.request_id,
    o.operation_kind,
    o.amount,
    o.currency,
    o.status,
    o.process_id,
    o.created_at,
    o.updated_at
FROM app.operations AS o;

CREATE VIEW autocheck.operation_events (
    event_id,
    operation_id,
    event_type,
    payload_hash,
    occurred_at
) AS
SELECT
    e.event_id,
    e.operation_id,
    e.event_type,
    e.payload_hash,
    e.occurred_at
FROM app.operation_events AS e;

DO $grants$
DECLARE
    v_view text;
BEGIN
    FOREACH v_view IN ARRAY ARRAY[
        'contract_info',
        'action_definitions',
        'action_dispatches',
        'operations',
        'operation_events'
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
END;
$grants$;

REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.operations FROM PUBLIC;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.operations FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.operations FROM course_publication;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.operation_events FROM PUBLIC;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.operation_events FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.operation_events FROM course_publication;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.idempotency_keys FROM PUBLIC;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.idempotency_keys FROM course_runtime;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.idempotency_keys FROM course_publication;

GRANT SELECT ON app.operations TO course_publication;
GRANT SELECT ON app.operation_events TO course_publication;
GRANT SELECT ON app.idempotency_keys TO course_publication;
