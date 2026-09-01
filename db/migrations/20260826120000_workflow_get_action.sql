-- HTTP action workflow.get: снимок process, steps, jobs и attempts для scope workflow:read.

CREATE FUNCTION app.workflow_get(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $get$
DECLARE
    v_process_id uuid;
    v_process workflow.processes%ROWTYPE;
    v_steps jsonb;
    v_jobs jsonb;
    v_attempts jsonb;
    v_key_count integer;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_key_count := (SELECT count(*)::integer FROM jsonb_object_keys(p_payload));
    IF v_key_count <> 1
        OR jsonb_typeof(p_payload -> 'processId') IS DISTINCT FROM 'string'
    THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    BEGIN
        v_process_id := (p_payload ->> 'processId')::uuid;
    EXCEPTION
        WHEN invalid_text_representation THEN
            RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END;

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = v_process_id;

    IF NOT FOUND THEN
        RETURN api.error_envelope('request.invalid', 'process does not exist');
    END IF;

    SELECT COALESCE(
        jsonb_agg(
            jsonb_build_object(
                'stepInstanceId', s.step_instance_id,
                'processId', s.process_id,
                'stepKey', s.step_key,
                'stepType', s.step_type,
                'state', s.state,
                'outcome', s.outcome,
                'enteredAt', s.entered_at,
                'completedAt', s.completed_at
            )
            ORDER BY s.entered_at, s.step_instance_id
        ),
        '[]'::jsonb
    )
    INTO v_steps
    FROM workflow.step_instances AS s
    WHERE s.process_id = v_process_id;

    SELECT COALESCE(
        jsonb_agg(
            jsonb_build_object(
                'jobId', j.job_id,
                'processId', j.process_id,
                'stepInstanceId', j.step_instance_id,
                'executionId', j.execution_id,
                'state', j.state,
                'leaseOwner', j.lease_owner,
                'leaseVersion', j.lease_version,
                'leaseUntil', j.lease_until,
                'attemptCount', j.attempt_count,
                'nextAttemptAt', j.next_attempt_at
            )
            ORDER BY j.created_at, j.job_id
        ),
        '[]'::jsonb
    )
    INTO v_jobs
    FROM workflow.jobs AS j
    WHERE j.process_id = v_process_id;

    SELECT COALESCE(
        jsonb_agg(
            jsonb_build_object(
                'attemptId', a.attempt_id,
                'jobId', a.job_id,
                'executionId', a.execution_id,
                'leaseVersion', a.lease_version,
                'attemptNumber', a.attempt_number,
                'status', a.status,
                'outcome', a.outcome,
                'errorCode', a.error_code,
                'startedAt', a.started_at,
                'finishedAt', a.finished_at
            )
            ORDER BY a.started_at, a.attempt_id
        ),
        '[]'::jsonb
    )
    INTO v_attempts
    FROM workflow.attempts AS a
    INNER JOIN workflow.jobs AS j ON j.job_id = a.job_id
    WHERE j.process_id = v_process_id;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', jsonb_build_object(
            'process', jsonb_build_object(
                'processId', v_process.process_id,
                'businessKey', v_process.business_key,
                'flowName', v_process.flow_name,
                'flowVersion', v_process.flow_version,
                'state', v_process.state,
                'currentStepKey', v_process.current_step_key
            ),
            'steps', v_steps,
            'jobs', v_jobs,
            'attempts', v_attempts
        )
    );
END;
$get$;

ALTER FUNCTION app.workflow_get(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.workflow_get(jsonb, jsonb) FROM PUBLIC;

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($manifest$
{
  "module": "workflow",
  "action": "get",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "workflow_get",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["processId"],
    "properties": {
      "processId": { "type": "string", "format": "uuid" }
    }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["process", "steps", "jobs", "attempts"],
    "properties": {
      "process": { "type": "object" },
      "steps": { "type": "array" },
      "jobs": { "type": "array" },
      "attempts": { "type": "array" }
    }
  },
  "outcomes": ["FOUND"],
  "required_policy": ["workflow:read"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 5000
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish workflow.get failed: %', v_result;
    END IF;

    v_result := api.activate_action('workflow.get', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate workflow.get failed: %', v_result;
    END IF;
END;
$boot$;
