-- Таблицы workflow: неизменяемые версии карт, process/job/attempt, сигналы и события.
-- Повтор с тем же checksum безопасен; правки этого файла после apply дадут conflict.

ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA workflow
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA workflow
    REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA workflow
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

CREATE TABLE workflow.flow_versions (
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    status text NOT NULL,
    is_active boolean NOT NULL DEFAULT false,
    start_step text NOT NULL,
    source_document jsonb NOT NULL,
    source_checksum text NOT NULL,
    published_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT flow_versions_pkey PRIMARY KEY (flow_name, flow_version),
    CONSTRAINT flow_versions_version_chk CHECK (flow_version >= 1),
    CONSTRAINT flow_versions_status_chk CHECK (status = 'PUBLISHED')
);

ALTER TABLE workflow.flow_versions OWNER TO course_owner;

CREATE UNIQUE INDEX flow_versions_one_active
    ON workflow.flow_versions (flow_name)
    WHERE is_active;

CREATE TABLE workflow.step_definitions (
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    step_key text NOT NULL,
    step_type text NOT NULL,
    action_module text,
    action_name text,
    action_version integer,
    request_schema jsonb,
    response_schema jsonb,
    outcomes jsonb,
    required_policy jsonb,
    timeout_ms integer,
    max_attempts integer,
    delays_ms jsonb,
    input_mapping jsonb,
    input_constants jsonb,
    signal_type text,
    step_outcome text,
    allowed_outcomes jsonb,
    CONSTRAINT step_definitions_pkey PRIMARY KEY (flow_name, flow_version, step_key),
    CONSTRAINT step_definitions_flow_fk
        FOREIGN KEY (flow_name, flow_version)
        REFERENCES workflow.flow_versions (flow_name, flow_version),
    CONSTRAINT step_definitions_type_chk
        CHECK (step_type IN ('AUTOMATIC', 'WAIT_SIGNAL', 'MANUAL', 'END'))
);

ALTER TABLE workflow.step_definitions OWNER TO course_owner;

CREATE TABLE workflow.transition_definitions (
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    from_step text NOT NULL,
    outcome text NOT NULL,
    to_step text NOT NULL,
    CONSTRAINT transition_definitions_pkey PRIMARY KEY (flow_name, flow_version, from_step, outcome),
    CONSTRAINT transition_definitions_flow_fk
        FOREIGN KEY (flow_name, flow_version)
        REFERENCES workflow.flow_versions (flow_name, flow_version)
);

ALTER TABLE workflow.transition_definitions OWNER TO course_owner;

CREATE TABLE workflow.processes (
    process_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    business_key text NOT NULL,
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    state text NOT NULL,
    current_step_key text NOT NULL,
    process_data jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT processes_flow_fk
        FOREIGN KEY (flow_name, flow_version)
        REFERENCES workflow.flow_versions (flow_name, flow_version),
    CONSTRAINT processes_state_chk CHECK (state IN (
        'CREATED',
        'RUNNING',
        'WAITING_SIGNAL',
        'WAITING_MANUAL',
        'COMPLETED',
        'FAILED'
    )),
    CONSTRAINT processes_data_chk CHECK (jsonb_typeof(process_data) = 'object')
);

ALTER TABLE workflow.processes OWNER TO course_owner;

CREATE UNIQUE INDEX processes_flow_business_key
    ON workflow.processes (flow_name, business_key);

CREATE TABLE workflow.step_instances (
    step_instance_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.processes (process_id),
    step_key text NOT NULL,
    step_type text NOT NULL,
    state text NOT NULL,
    outcome text,
    entered_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamptz,
    CONSTRAINT step_instances_type_chk
        CHECK (step_type IN ('AUTOMATIC', 'WAIT_SIGNAL', 'MANUAL', 'END')),
    CONSTRAINT step_instances_state_chk
        CHECK (state IN ('READY', 'RUNNING', 'WAITING', 'COMPLETED', 'FAILED')),
    CONSTRAINT step_instances_process_step_uq UNIQUE (process_id, step_key)
);

ALTER TABLE workflow.step_instances OWNER TO course_owner;

CREATE TABLE workflow.jobs (
    job_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.processes (process_id),
    step_instance_id uuid NOT NULL REFERENCES workflow.step_instances (step_instance_id),
    execution_id uuid NOT NULL,
    state text NOT NULL,
    lease_owner text,
    lease_version bigint NOT NULL DEFAULT 0,
    lease_until timestamptz,
    attempt_count integer NOT NULL DEFAULT 0,
    failure_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz,
    max_attempts integer NOT NULL,
    delays_ms jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT jobs_state_chk CHECK (state IN ('READY', 'LEASED', 'RETRY_WAIT', 'SUCCEEDED', 'DEAD')),
    CONSTRAINT jobs_step_uq UNIQUE (step_instance_id),
    CONSTRAINT jobs_attempts_chk CHECK (attempt_count >= 0 AND failure_count >= 0)
);

ALTER TABLE workflow.jobs OWNER TO course_owner;

CREATE INDEX jobs_claim_idx
    ON workflow.jobs (state, next_attempt_at, lease_until, created_at);

CREATE TABLE workflow.attempts (
    attempt_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id uuid NOT NULL REFERENCES workflow.jobs (job_id),
    execution_id uuid NOT NULL,
    lease_version bigint NOT NULL,
    attempt_number integer NOT NULL,
    status text NOT NULL,
    outcome text,
    error_code text,
    started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    finished_at timestamptz,
    CONSTRAINT attempts_status_chk CHECK (status IN ('RUNNING', 'SUCCEEDED', 'FAILED', 'STALE')),
    CONSTRAINT attempts_number_chk CHECK (attempt_number >= 1),
    CONSTRAINT attempts_job_number_uq UNIQUE (job_id, attempt_number),
    CONSTRAINT attempts_job_lease_uq UNIQUE (job_id, lease_version)
);

ALTER TABLE workflow.attempts OWNER TO course_owner;

CREATE TABLE workflow.signals (
    message_id text PRIMARY KEY,
    process_id uuid NOT NULL REFERENCES workflow.processes (process_id),
    signal_type text NOT NULL,
    body jsonb NOT NULL DEFAULT '{}'::jsonb,
    body_hash text NOT NULL,
    status text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at timestamptz,
    CONSTRAINT signals_status_chk CHECK (status IN ('ACCEPTED', 'APPLIED'))
);

ALTER TABLE workflow.signals OWNER TO course_owner;

CREATE INDEX signals_process_idx
    ON workflow.signals (process_id, status, received_at);

CREATE TABLE workflow.events (
    event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.processes (process_id),
    step_instance_id uuid,
    event_type text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT events_type_chk CHECK (event_type ~ '^[A-Z][A-Za-z]+$' AND event_type <> upper(event_type))
);

ALTER TABLE workflow.events OWNER TO course_owner;

CREATE INDEX events_process_idx
    ON workflow.events (process_id, occurred_at, event_id);

CREATE FUNCTION workflow.flow_versions_protect_immutable()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $protect_flow$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'published flow version is immutable';
    END IF;

    IF NEW.flow_name IS DISTINCT FROM OLD.flow_name
        OR NEW.flow_version IS DISTINCT FROM OLD.flow_version
        OR NEW.status IS DISTINCT FROM OLD.status
        OR NEW.start_step IS DISTINCT FROM OLD.start_step
        OR NEW.source_document IS DISTINCT FROM OLD.source_document
        OR NEW.source_checksum IS DISTINCT FROM OLD.source_checksum
        OR NEW.published_at IS DISTINCT FROM OLD.published_at
    THEN
        RAISE EXCEPTION 'published flow version is immutable';
    END IF;

    RETURN NEW;
END;
$protect_flow$;

ALTER FUNCTION workflow.flow_versions_protect_immutable() OWNER TO course_owner;

CREATE TRIGGER flow_versions_protect_immutable
    BEFORE UPDATE OR DELETE ON workflow.flow_versions
    FOR EACH ROW
    EXECUTE FUNCTION workflow.flow_versions_protect_immutable();

CREATE FUNCTION workflow.events_protect_insert_only()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $protect_events$
BEGIN
    RAISE EXCEPTION 'workflow events are insert-only';
END;
$protect_events$;

ALTER FUNCTION workflow.events_protect_insert_only() OWNER TO course_owner;

CREATE TRIGGER events_protect_insert_only
    BEFORE UPDATE OR DELETE ON workflow.events
    FOR EACH ROW
    EXECUTE FUNCTION workflow.events_protect_insert_only();

REVOKE ALL ON TABLE workflow.flow_versions FROM PUBLIC;
REVOKE ALL ON TABLE workflow.step_definitions FROM PUBLIC;
REVOKE ALL ON TABLE workflow.transition_definitions FROM PUBLIC;
REVOKE ALL ON TABLE workflow.processes FROM PUBLIC;
REVOKE ALL ON TABLE workflow.step_instances FROM PUBLIC;
REVOKE ALL ON TABLE workflow.jobs FROM PUBLIC;
REVOKE ALL ON TABLE workflow.attempts FROM PUBLIC;
REVOKE ALL ON TABLE workflow.signals FROM PUBLIC;
REVOKE ALL ON TABLE workflow.events FROM PUBLIC;

COMMENT ON SCHEMA workflow IS 'Карты, process/job/attempt и SECURITY DEFINER-границы worker';
