-- Job fencing: finish_job / fail_job require a non-expired lease_until.
-- Stale completion after lease expiry returns workflow.lease_stale before reclaim.

CREATE OR REPLACE FUNCTION workflow.finish_job(
    p_job_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_outcome text,
    p_result jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $finish$
DECLARE
    v_job workflow.jobs%ROWTYPE;
    v_process workflow.processes%ROWTYPE;
    v_step workflow.step_instances%ROWTYPE;
    v_to text;
    v_result jsonb;
BEGIN
    IF p_job_id IS NULL OR p_owner IS NULL OR p_lease_version IS NULL OR p_outcome IS NULL OR p_outcome = '' THEN
        RETURN api.error_envelope('request.invalid', 'job id, owner, lease version and outcome are required');
    END IF;

    v_result := COALESCE(p_result, '{}'::jsonb);

    SELECT * INTO v_job
    FROM workflow.jobs
    WHERE job_id = p_job_id
    FOR UPDATE;

    IF NOT FOUND
        OR v_job.state IS DISTINCT FROM 'LEASED'
        OR v_job.lease_owner IS DISTINCT FROM p_owner
        OR v_job.lease_version IS DISTINCT FROM p_lease_version
        OR v_job.lease_until IS NULL
        OR v_job.lease_until <= clock_timestamp()
    THEN
        RETURN api.error_envelope('workflow.lease_stale', 'job lease is not held by this owner and version');
    END IF;

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = v_job.process_id
    FOR UPDATE;

    SELECT * INTO v_step
    FROM workflow.step_instances
    WHERE step_instance_id = v_job.step_instance_id
    FOR UPDATE;

    SELECT t.to_step
    INTO v_to
    FROM workflow.transition_definitions AS t
    WHERE t.flow_name = v_process.flow_name
      AND t.flow_version = v_process.flow_version
      AND t.from_step = v_step.step_key
      AND t.outcome = p_outcome;

    IF v_to IS NULL THEN
        RETURN api.error_envelope('request.invalid', 'outcome has no transition from the current step');
    END IF;

    UPDATE workflow.attempts
    SET status = 'SUCCEEDED',
        outcome = p_outcome,
        error_code = NULL,
        finished_at = clock_timestamp()
    WHERE job_id = v_job.job_id
      AND lease_version = p_lease_version
      AND status = 'RUNNING';

    UPDATE workflow.jobs
    SET state = 'SUCCEEDED',
        lease_owner = NULL,
        lease_until = NULL,
        next_attempt_at = NULL
    WHERE job_id = v_job.job_id;

    UPDATE workflow.step_instances
    SET state = 'COMPLETED',
        outcome = p_outcome,
        completed_at = clock_timestamp()
    WHERE step_instance_id = v_job.step_instance_id;

    UPDATE workflow.processes
    SET process_data = COALESCE(process_data, '{}'::jsonb) || v_result,
        updated_at = clock_timestamp()
    WHERE process_id = v_process.process_id;

    PERFORM workflow._append_event(v_process.process_id, v_job.step_instance_id, 'StepCompleted');
    PERFORM workflow._enter_step(v_process.process_id, v_to);

    RETURN jsonb_build_object(
        'status', 'ok',
        'resource', 'job',
        'operation', 'finished',
        'jobId', p_job_id
    );
END;
$finish$;

ALTER FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) OWNER TO course_owner;

CREATE OR REPLACE FUNCTION workflow.fail_job(
    p_job_id uuid,
    p_owner text,
    p_lease_version bigint,
    p_error_code text,
    p_retryable boolean
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $fail$
DECLARE
    v_job workflow.jobs%ROWTYPE;
    v_code text;
    v_failure integer;
    v_delay integer;
    v_retry boolean;
BEGIN
    IF p_job_id IS NULL OR p_owner IS NULL OR p_lease_version IS NULL THEN
        RETURN api.error_envelope('request.invalid', 'job id, owner and lease version are required');
    END IF;

    v_code := NULLIF(btrim(COALESCE(p_error_code, '')), '');
    IF v_code IS NULL THEN
        v_code := 'internal.error';
    END IF;
    v_retry := COALESCE(p_retryable, false);

    SELECT * INTO v_job
    FROM workflow.jobs
    WHERE job_id = p_job_id
    FOR UPDATE;

    IF NOT FOUND
        OR v_job.state IS DISTINCT FROM 'LEASED'
        OR v_job.lease_owner IS DISTINCT FROM p_owner
        OR v_job.lease_version IS DISTINCT FROM p_lease_version
        OR v_job.lease_until IS NULL
        OR v_job.lease_until <= clock_timestamp()
    THEN
        RETURN api.error_envelope('workflow.lease_stale', 'job lease is not held by this owner and version');
    END IF;

    PERFORM 1
    FROM workflow.processes
    WHERE process_id = v_job.process_id
    FOR UPDATE;

    UPDATE workflow.attempts
    SET status = 'FAILED',
        outcome = NULL,
        error_code = v_code,
        finished_at = clock_timestamp()
    WHERE job_id = v_job.job_id
      AND lease_version = p_lease_version
      AND status = 'RUNNING';

    v_failure := v_job.failure_count + 1;

    IF v_retry AND v_failure < v_job.max_attempts THEN
        v_delay := COALESCE((v_job.delays_ms ->> (v_failure - 1))::integer, 0);

        UPDATE workflow.jobs
        SET state = 'RETRY_WAIT',
            failure_count = v_failure,
            lease_owner = NULL,
            lease_until = NULL,
            next_attempt_at = clock_timestamp() + (v_delay * interval '1 millisecond')
        WHERE job_id = v_job.job_id;

        RETURN jsonb_build_object(
            'status', 'ok',
            'resource', 'job',
            'operation', 'retry_scheduled',
            'jobId', p_job_id
        );
    END IF;

    UPDATE workflow.jobs
    SET state = 'DEAD',
        failure_count = v_failure,
        lease_owner = NULL,
        lease_until = NULL,
        next_attempt_at = NULL
    WHERE job_id = v_job.job_id;

    UPDATE workflow.step_instances
    SET state = 'FAILED',
        completed_at = clock_timestamp()
    WHERE step_instance_id = v_job.step_instance_id;

    UPDATE workflow.processes
    SET state = 'FAILED',
        updated_at = clock_timestamp()
    WHERE process_id = v_job.process_id;

    PERFORM workflow._append_event(v_job.process_id, v_job.step_instance_id, 'TaskFailed');
    PERFORM workflow._append_event(v_job.process_id, v_job.step_instance_id, 'ProcessFailed');

    RETURN jsonb_build_object(
        'status', 'ok',
        'resource', 'job',
        'operation', 'failed',
        'jobId', p_job_id
    );
END;
$fail$;

ALTER FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean) OWNER TO course_owner;

REVOKE ALL ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid, text, bigint, text, boolean) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid, text, bigint, text, jsonb) TO course_publication;
