-- Аддитивные поля контракта: attemptId в claim; CLI-конверты дополняет C#.
-- CREATE OR REPLACE не меняет checksum уже применённых файлов.

CREATE OR REPLACE FUNCTION workflow.claim_jobs(p_owner text, p_lease_ms integer DEFAULT 2000)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $claim$
DECLARE
    v_job workflow.jobs%ROWTYPE;
    v_process workflow.processes%ROWTYPE;
    v_def workflow.step_definitions%ROWTYPE;
    v_found boolean := false;
    v_lease integer;
    v_until timestamptz;
    v_payload jsonb;
    v_attempt_id uuid;
BEGIN
    IF p_owner IS NULL OR btrim(p_owner) = '' THEN
        RETURN api.error_envelope('request.invalid', 'lease owner is required');
    END IF;

    v_lease := GREATEST(1, LEAST(COALESCE(p_lease_ms, 2000), 30000));

    FOR v_job IN
        SELECT *
        FROM workflow.jobs
        WHERE state = 'READY'
           OR (state = 'RETRY_WAIT' AND COALESCE(next_attempt_at, clock_timestamp()) <= clock_timestamp())
           OR (state = 'LEASED' AND lease_until IS NOT NULL AND lease_until < clock_timestamp())
        ORDER BY created_at, job_id
        FOR UPDATE SKIP LOCKED
    LOOP
        v_found := true;
        EXIT;
    END LOOP;

    IF NOT v_found THEN
        RETURN jsonb_build_object('status', 'ok', 'jobs', '[]'::jsonb);
    END IF;

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = v_job.process_id
    FOR UPDATE;

    IF v_job.state = 'LEASED' THEN
        UPDATE workflow.attempts
        SET status = 'STALE',
            finished_at = clock_timestamp()
        WHERE job_id = v_job.job_id
          AND status = 'RUNNING';
    END IF;

    v_until := clock_timestamp() + (v_lease * interval '1 millisecond');

    UPDATE workflow.jobs
    SET state = 'LEASED',
        lease_owner = p_owner,
        lease_version = lease_version + 1,
        lease_until = v_until,
        attempt_count = attempt_count + 1,
        next_attempt_at = NULL
    WHERE job_id = v_job.job_id
    RETURNING * INTO v_job;

    INSERT INTO workflow.attempts (
        job_id, execution_id, lease_version, attempt_number, status, started_at
    )
    VALUES (
        v_job.job_id, v_job.execution_id, v_job.lease_version, v_job.attempt_count, 'RUNNING', clock_timestamp()
    )
    RETURNING attempt_id INTO v_attempt_id;

    UPDATE workflow.step_instances
    SET state = 'RUNNING'
    WHERE step_instance_id = v_job.step_instance_id
      AND state IN ('READY', 'RUNNING');

    SELECT d.*
    INTO v_def
    FROM workflow.step_definitions AS d
    JOIN workflow.step_instances AS s
      ON s.step_key = d.step_key
    JOIN workflow.processes AS p
      ON p.process_id = s.process_id
     AND p.flow_name = d.flow_name
     AND p.flow_version = d.flow_version
    WHERE s.step_instance_id = v_job.step_instance_id;

    v_payload := jsonb_build_object(
        'jobId', v_job.job_id,
        'processId', v_job.process_id,
        'stepInstanceId', v_job.step_instance_id,
        'executionId', v_job.execution_id,
        'attemptId', v_attempt_id,
        'leaseOwner', v_job.lease_owner,
        'leaseVersion', v_job.lease_version,
        'leaseUntil', v_job.lease_until,
        'module', v_def.action_module,
        'action', v_def.action_name,
        'actionVersion', v_def.action_version,
        'requestSchema', v_def.request_schema,
        'responseSchema', v_def.response_schema,
        'outcomes', v_def.outcomes,
        'requiredPolicy', v_def.required_policy,
        'timeoutMs', v_def.timeout_ms,
        'inputMapping', v_def.input_mapping,
        'inputConstants', v_def.input_constants,
        'processData', v_process.process_data
    );

    RETURN jsonb_build_object('status', 'ok', 'jobs', jsonb_build_array(v_payload));
END;
$claim$;

ALTER FUNCTION workflow.claim_jobs(text, integer) OWNER TO course_owner;
REVOKE ALL ON FUNCTION workflow.claim_jobs(text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text, integer) TO workflow_worker;
