-- Read-only проекции workflow и EXECUTE-граница workflow_worker.

CREATE VIEW autocheck.flow_versions (
    flow_name,
    flow_version,
    status,
    is_active,
    published_at
) AS
SELECT
    f.flow_name,
    f.flow_version,
    f.status,
    f.is_active,
    f.published_at
FROM workflow.flow_versions AS f;

CREATE VIEW autocheck.processes (
    process_id,
    business_key,
    flow_name,
    flow_version,
    state,
    current_step_key,
    created_at,
    updated_at
) AS
SELECT
    p.process_id,
    p.business_key,
    p.flow_name,
    p.flow_version,
    p.state,
    p.current_step_key,
    p.created_at,
    p.updated_at
FROM workflow.processes AS p;

CREATE VIEW autocheck.steps (
    step_instance_id,
    process_id,
    step_key,
    step_type,
    state,
    outcome,
    entered_at,
    completed_at
) AS
SELECT
    s.step_instance_id,
    s.process_id,
    s.step_key,
    s.step_type,
    s.state,
    s.outcome,
    s.entered_at,
    s.completed_at
FROM workflow.step_instances AS s;

CREATE VIEW autocheck.jobs (
    job_id,
    process_id,
    step_instance_id,
    execution_id,
    state,
    lease_owner,
    lease_version,
    lease_until,
    attempt_count,
    next_attempt_at
) AS
SELECT
    j.job_id,
    j.process_id,
    j.step_instance_id,
    j.execution_id,
    j.state,
    j.lease_owner,
    j.lease_version,
    j.lease_until,
    j.attempt_count,
    j.next_attempt_at
FROM workflow.jobs AS j;

CREATE VIEW autocheck.attempts (
    attempt_id,
    job_id,
    execution_id,
    lease_version,
    attempt_number,
    status,
    outcome,
    error_code,
    started_at,
    finished_at
) AS
SELECT
    a.attempt_id,
    a.job_id,
    a.execution_id,
    a.lease_version,
    a.attempt_number,
    a.status,
    a.outcome,
    a.error_code,
    a.started_at,
    a.finished_at
FROM workflow.attempts AS a;

CREATE VIEW autocheck.signals (
    message_id,
    process_id,
    signal_type,
    body_hash,
    status,
    received_at
) AS
SELECT
    g.message_id,
    g.process_id,
    g.signal_type,
    g.body_hash,
    g.status,
    g.received_at
FROM workflow.signals AS g;

CREATE VIEW autocheck.workflow_events (
    event_id,
    process_id,
    step_instance_id,
    event_type,
    occurred_at
) AS
SELECT
    e.event_id,
    e.process_id,
    e.step_instance_id,
    e.event_type,
    e.occurred_at
FROM workflow.events AS e;

DO $grants$
DECLARE
    v_view text;
BEGIN
    FOREACH v_view IN ARRAY ARRAY[
        'flow_versions',
        'processes',
        'steps',
        'jobs',
        'attempts',
        'signals',
        'workflow_events'
    ]
    LOOP
        EXECUTE format('ALTER VIEW autocheck.%I OWNER TO course_owner', v_view);
        EXECUTE format('ALTER VIEW autocheck.%I SET (security_invoker = false)', v_view);
        EXECUTE format('REVOKE ALL ON autocheck.%I FROM PUBLIC', v_view);
        EXECUTE format('GRANT SELECT ON autocheck.%I TO course_runtime', v_view);
        EXECUTE format('GRANT SELECT ON autocheck.%I TO course_publication', v_view);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON autocheck.%I FROM course_runtime', v_view);
        EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON autocheck.%I FROM course_publication', v_view);
        EXECUTE format('REVOKE ALL ON autocheck.%I FROM workflow_worker', v_view);
    END LOOP;
END;
$grants$;

DO $tables$
DECLARE
    v_table text;
BEGIN
    FOREACH v_table IN ARRAY ARRAY[
        'flow_versions',
        'step_definitions',
        'transition_definitions',
        'processes',
        'step_instances',
        'jobs',
        'attempts',
        'signals',
        'events'
    ]
    LOOP
        EXECUTE format('REVOKE ALL ON TABLE workflow.%I FROM PUBLIC', v_table);
        EXECUTE format('REVOKE ALL ON TABLE workflow.%I FROM course_runtime', v_table);
        EXECUTE format('REVOKE ALL ON TABLE workflow.%I FROM course_publication', v_table);
        EXECUTE format('REVOKE ALL ON TABLE workflow.%I FROM workflow_worker', v_table);
    END LOOP;
END;
$tables$;

DO $functions$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT p.oid::regprocedure AS sig
        FROM pg_catalog.pg_proc AS p
        JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
        WHERE n.nspname = 'workflow'
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM workflow_worker', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM course_runtime', r.sig);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM course_publication', r.sig);
    END LOOP;
END;
$functions$;

GRANT EXECUTE ON FUNCTION api.invoke(text, text, integer, jsonb, jsonb) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text, integer) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean) TO workflow_worker;

GRANT EXECUTE ON FUNCTION workflow.publish_flow(jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.activate_flow(text, integer) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.list_flows() TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.get_process(uuid) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.start_process(text, text, jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.signal_process(uuid, text, text, jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO course_publication;
