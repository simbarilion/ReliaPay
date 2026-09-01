-- SECURITY DEFINER-движок workflow. Worker вызывает только claim/finish/fail;
-- внутренние _enter_step/_apply_pending_signal исполняются от course_owner.

CREATE FUNCTION workflow._json_hash(p_value jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog, pg_temp
AS $hash$
    SELECT encode(sha256(convert_to(COALESCE(p_value, '{}'::jsonb)::text, 'UTF8')), 'hex');
$hash$;

ALTER FUNCTION workflow._json_hash(jsonb) OWNER TO course_owner;

CREATE FUNCTION workflow._append_event(
    p_process_id uuid,
    p_step_instance_id uuid,
    p_event_type text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $append$
BEGIN
    INSERT INTO workflow.events (process_id, step_instance_id, event_type, occurred_at)
    VALUES (p_process_id, p_step_instance_id, p_event_type, clock_timestamp());
END;
$append$;

ALTER FUNCTION workflow._append_event(uuid, uuid, text) OWNER TO course_owner;

CREATE FUNCTION workflow._enter_step(p_process_id uuid, p_step_key text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $enter$
DECLARE
    v_process workflow.processes%ROWTYPE;
    v_def workflow.step_definitions%ROWTYPE;
    v_step_id uuid;
    v_now timestamptz;
BEGIN
    v_now := clock_timestamp();

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = p_process_id
    FOR UPDATE;

    SELECT * INTO v_def
    FROM workflow.step_definitions
    WHERE flow_name = v_process.flow_name
      AND flow_version = v_process.flow_version
      AND step_key = p_step_key;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow step % is not defined', p_step_key;
    END IF;

    v_step_id := gen_random_uuid();

    IF v_def.step_type = 'AUTOMATIC' THEN
        INSERT INTO workflow.step_instances (
            step_instance_id, process_id, step_key, step_type, state, entered_at
        )
        VALUES (v_step_id, p_process_id, p_step_key, 'AUTOMATIC', 'READY', v_now);

        INSERT INTO workflow.jobs (
            process_id,
            step_instance_id,
            execution_id,
            state,
            lease_version,
            attempt_count,
            failure_count,
            max_attempts,
            delays_ms,
            created_at
        )
        VALUES (
            p_process_id,
            v_step_id,
            gen_random_uuid(),
            'READY',
            0,
            0,
            0,
            COALESCE(v_def.max_attempts, 1),
            COALESCE(v_def.delays_ms, '[]'::jsonb),
            v_now
        );

        UPDATE workflow.processes
        SET state = 'RUNNING',
            current_step_key = p_step_key,
            updated_at = clock_timestamp()
        WHERE process_id = p_process_id;

        PERFORM workflow._append_event(p_process_id, v_step_id, 'StepEntered');
        RETURN;
    END IF;

    IF v_def.step_type = 'WAIT_SIGNAL' THEN
        INSERT INTO workflow.step_instances (
            step_instance_id, process_id, step_key, step_type, state, entered_at
        )
        VALUES (v_step_id, p_process_id, p_step_key, 'WAIT_SIGNAL', 'WAITING', v_now);

        UPDATE workflow.processes
        SET state = 'WAITING_SIGNAL',
            current_step_key = p_step_key,
            updated_at = clock_timestamp()
        WHERE process_id = p_process_id;

        PERFORM workflow._append_event(p_process_id, v_step_id, 'StepEntered');
        PERFORM workflow._apply_pending_signal(p_process_id);
        RETURN;
    END IF;

    IF v_def.step_type = 'MANUAL' THEN
        INSERT INTO workflow.step_instances (
            step_instance_id, process_id, step_key, step_type, state, entered_at
        )
        VALUES (v_step_id, p_process_id, p_step_key, 'MANUAL', 'WAITING', v_now);

        UPDATE workflow.processes
        SET state = 'WAITING_MANUAL',
            current_step_key = p_step_key,
            updated_at = clock_timestamp()
        WHERE process_id = p_process_id;

        PERFORM workflow._append_event(p_process_id, v_step_id, 'StepEntered');
        RETURN;
    END IF;

    IF v_def.step_type IS DISTINCT FROM 'END' THEN
        RAISE EXCEPTION 'unsupported workflow step type %', v_def.step_type;
    END IF;

    INSERT INTO workflow.step_instances (
        step_instance_id, process_id, step_key, step_type, state, outcome, entered_at, completed_at
    )
    VALUES (
        v_step_id, p_process_id, p_step_key, 'END', 'COMPLETED', v_def.step_outcome, v_now, v_now
    );

    UPDATE workflow.processes
    SET state = 'COMPLETED',
        current_step_key = p_step_key,
        updated_at = clock_timestamp()
    WHERE process_id = p_process_id;

    PERFORM workflow._append_event(p_process_id, v_step_id, 'StepEntered');
    PERFORM workflow._append_event(p_process_id, v_step_id, 'StepCompleted');
    PERFORM workflow._append_event(p_process_id, v_step_id, 'ProcessCompleted');
END;
$enter$;

ALTER FUNCTION workflow._enter_step(uuid, text) OWNER TO course_owner;

CREATE FUNCTION workflow._apply_pending_signal(p_process_id uuid)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $apply$
DECLARE
    v_process workflow.processes%ROWTYPE;
    v_step workflow.step_instances%ROWTYPE;
    v_def workflow.step_definitions%ROWTYPE;
    v_signal workflow.signals%ROWTYPE;
    v_to text;
BEGIN
    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = p_process_id
    FOR UPDATE;

    IF v_process.state IS DISTINCT FROM 'WAITING_SIGNAL' THEN
        RETURN;
    END IF;

    SELECT * INTO v_step
    FROM workflow.step_instances
    WHERE process_id = p_process_id
      AND step_key = v_process.current_step_key
      AND step_type = 'WAIT_SIGNAL'
      AND state = 'WAITING'
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN;
    END IF;

    SELECT * INTO v_def
    FROM workflow.step_definitions
    WHERE flow_name = v_process.flow_name
      AND flow_version = v_process.flow_version
      AND step_key = v_step.step_key;

    v_signal.message_id := NULL;

    FOR v_signal IN
        SELECT *
        FROM workflow.signals
        WHERE process_id = p_process_id
          AND status = 'ACCEPTED'
          AND signal_type = v_def.signal_type
        ORDER BY received_at, message_id
        FOR UPDATE
    LOOP
        EXIT;
    END LOOP;

    IF v_signal.message_id IS NULL THEN
        RETURN;
    END IF;

    UPDATE workflow.signals
    SET status = 'APPLIED',
        applied_at = clock_timestamp()
    WHERE message_id = v_signal.message_id;

    UPDATE workflow.step_instances
    SET state = 'COMPLETED',
        outcome = v_def.step_outcome,
        completed_at = clock_timestamp()
    WHERE step_instance_id = v_step.step_instance_id;

    UPDATE workflow.processes
    SET process_data = COALESCE(process_data, '{}'::jsonb) || COALESCE(v_signal.body, '{}'::jsonb),
        updated_at = clock_timestamp()
    WHERE process_id = p_process_id;

    PERFORM workflow._append_event(p_process_id, v_step.step_instance_id, 'SignalApplied');
    PERFORM workflow._append_event(p_process_id, v_step.step_instance_id, 'StepCompleted');

    SELECT t.to_step
    INTO v_to
    FROM workflow.transition_definitions AS t
    WHERE t.flow_name = v_process.flow_name
      AND t.flow_version = v_process.flow_version
      AND t.from_step = v_step.step_key
      AND t.outcome = v_def.step_outcome;

    IF v_to IS NULL THEN
        RAISE EXCEPTION 'workflow transition is missing after signal';
    END IF;

    PERFORM workflow._enter_step(p_process_id, v_to);
END;
$apply$;

ALTER FUNCTION workflow._apply_pending_signal(uuid) OWNER TO course_owner;

CREATE FUNCTION workflow.publish_flow(p_document jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $publish$
DECLARE
    v_checksum text;
    v_existing text;
    v_name text;
    v_version integer;
    v_start text;
    v_step jsonb;
    v_tr jsonb;
    v_type text;
    v_key text;
    v_module text;
    v_action text;
    v_action_version integer;
    v_request jsonb;
    v_response jsonb;
    v_outcomes jsonb;
    v_policy jsonb;
BEGIN
    IF jsonb_typeof(p_document) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('request.invalid', 'flow document must be a JSON object');
    END IF;

    v_name := p_document ->> 'flow_name';
    v_version := (p_document ->> 'version')::integer;
    v_start := p_document ->> 'start_step';

    IF v_name IS NULL OR v_version IS NULL OR v_start IS NULL
        OR jsonb_typeof(p_document -> 'steps') IS DISTINCT FROM 'array'
        OR jsonb_typeof(p_document -> 'transitions') IS DISTINCT FROM 'array'
    THEN
        RETURN api.error_envelope('request.invalid', 'flow document is missing required fields');
    END IF;

    FOR v_step IN
        SELECT value FROM jsonb_array_elements(p_document -> 'steps') AS t(value)
    LOOP
        IF jsonb_typeof(v_step) IS DISTINCT FROM 'object' OR v_step ->> 'key' IS NULL OR v_step ->> 'type' IS NULL THEN
            RETURN api.error_envelope('request.invalid', 'flow step is invalid');
        END IF;

        IF v_step ->> 'type' NOT IN ('automatic', 'wait_signal', 'manual', 'end') THEN
            RETURN api.error_envelope('request.invalid', 'flow step type is not supported');
        END IF;

        IF v_step ->> 'type' = 'automatic' THEN
            v_module := v_step #>> '{task,module}';
            v_action := v_step #>> '{task,action}';
            v_action_version := (v_step #>> '{task,action_version}')::integer;

            SELECT a.request_schema, a.response_schema, a.outcomes, a.required_policy
            INTO v_request, v_response, v_outcomes, v_policy
            FROM api.actions AS a
            WHERE a.module = v_module
              AND a.action = v_action
              AND a.version = v_action_version;

            IF NOT FOUND THEN
                RETURN api.error_envelope('request.invalid', 'automatic step action is not published');
            END IF;
        END IF;
    END LOOP;

    v_checksum := workflow._json_hash(p_document);

    INSERT INTO workflow.flow_versions (
        flow_name,
        flow_version,
        status,
        is_active,
        start_step,
        source_document,
        source_checksum
    )
    VALUES (v_name, v_version, 'PUBLISHED', false, v_start, p_document, v_checksum);

    FOR v_step IN
        SELECT value FROM jsonb_array_elements(p_document -> 'steps') AS t(value)
    LOOP
        v_key := v_step ->> 'key';
        v_type := v_step ->> 'type';
        v_module := NULL;
        v_action := NULL;
        v_action_version := NULL;
        v_request := NULL;
        v_response := NULL;
        v_outcomes := NULL;
        v_policy := NULL;

        IF v_type = 'automatic' THEN
            v_module := v_step #>> '{task,module}';
            v_action := v_step #>> '{task,action}';
            v_action_version := (v_step #>> '{task,action_version}')::integer;

            SELECT a.request_schema, a.response_schema, a.outcomes, a.required_policy
            INTO v_request, v_response, v_outcomes, v_policy
            FROM api.actions AS a
            WHERE a.module = v_module
              AND a.action = v_action
              AND a.version = v_action_version;

            INSERT INTO workflow.step_definitions (
                flow_name, flow_version, step_key, step_type,
                action_module, action_name, action_version,
                request_schema, response_schema, outcomes, required_policy,
                timeout_ms, max_attempts, delays_ms, input_mapping, input_constants
            )
            VALUES (
                v_name, v_version, v_key, 'AUTOMATIC',
                v_module, v_action, v_action_version,
                v_request, v_response, v_outcomes, COALESCE(v_step #> '{task,required_policy}', v_policy),
                (v_step #>> '{task,timeout_ms}')::integer,
                (v_step #>> '{task,retry,max_attempts}')::integer,
                v_step #> '{task,retry,delays_ms}',
                COALESCE(v_step #> '{task,input_mapping}', '{}'::jsonb),
                COALESCE(v_step #> '{task,input_constants}', '{}'::jsonb)
            );
        ELSIF v_type = 'wait_signal' THEN
            INSERT INTO workflow.step_definitions (
                flow_name, flow_version, step_key, step_type, signal_type, step_outcome
            )
            VALUES (v_name, v_version, v_key, 'WAIT_SIGNAL', v_step ->> 'signal_type', v_step ->> 'outcome');
        ELSIF v_type = 'manual' THEN
            INSERT INTO workflow.step_definitions (
                flow_name, flow_version, step_key, step_type, allowed_outcomes
            )
            VALUES (v_name, v_version, v_key, 'MANUAL', v_step -> 'allowed_outcomes');
        ELSIF v_type = 'end' THEN
            INSERT INTO workflow.step_definitions (
                flow_name, flow_version, step_key, step_type, step_outcome
            )
            VALUES (v_name, v_version, v_key, 'END', v_step ->> 'outcome');
        ELSE
            RETURN api.error_envelope('request.invalid', 'flow step type is not supported');
        END IF;
    END LOOP;

    FOR v_tr IN
        SELECT value FROM jsonb_array_elements(p_document -> 'transitions') AS t(value)
    LOOP
        INSERT INTO workflow.transition_definitions (flow_name, flow_version, from_step, outcome, to_step)
        VALUES (v_name, v_version, v_tr ->> 'from', v_tr ->> 'outcome', v_tr ->> 'to');
    END LOOP;

    RETURN jsonb_build_object(
        'status', 'ok',
        'resource', 'flow',
        'operation', 'published',
        'flowName', v_name,
        'version', v_version
    );
EXCEPTION
    WHEN unique_violation THEN
        SELECT f.source_checksum
        INTO v_existing
        FROM workflow.flow_versions AS f
        WHERE f.flow_name = v_name
          AND f.flow_version = v_version;

        IF NOT FOUND THEN
            RETURN api.error_envelope('request.invalid', 'flow document violates uniqueness constraints');
        END IF;

        IF v_existing = v_checksum THEN
            RETURN jsonb_build_object(
                'status', 'ok',
                'resource', 'flow',
                'operation', 'published',
                'flowName', v_name,
                'version', v_version
            );
        END IF;

        RETURN api.error_envelope('workflow.conflict', 'published flow version is immutable');
    WHEN invalid_text_representation THEN
        RETURN api.error_envelope('request.invalid', 'flow document field has invalid type');
END;
$publish$;

ALTER FUNCTION workflow.publish_flow(jsonb) OWNER TO course_owner;

CREATE FUNCTION workflow.activate_flow(p_flow_name text, p_version integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $activate$
DECLARE
    v_found boolean;
BEGIN
    IF p_flow_name IS NULL OR p_flow_name = '' OR p_version IS NULL THEN
        RETURN api.error_envelope('request.invalid', 'flow name and version are required');
    END IF;

    SELECT true
    INTO v_found
    FROM workflow.flow_versions
    WHERE flow_name = p_flow_name
      AND flow_version = p_version;

    IF v_found IS DISTINCT FROM true THEN
        RETURN api.error_envelope('request.invalid', 'flow version is not published');
    END IF;

    UPDATE workflow.flow_versions
    SET is_active = false
    WHERE flow_name = p_flow_name
      AND is_active
      AND flow_version IS DISTINCT FROM p_version;

    UPDATE workflow.flow_versions
    SET is_active = true
    WHERE flow_name = p_flow_name
      AND flow_version = p_version
      AND NOT is_active;

    RETURN jsonb_build_object(
        'status', 'ok',
        'resource', 'flow',
        'operation', 'activated',
        'flowName', p_flow_name,
        'version', p_version
    );
END;
$activate$;

ALTER FUNCTION workflow.activate_flow(text, integer) OWNER TO course_owner;

CREATE FUNCTION workflow.list_flows()
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $list$
DECLARE
    v_items jsonb;
BEGIN
    SELECT COALESCE(
        jsonb_agg(
            jsonb_build_object(
                'flowName', f.flow_name,
                'flowVersion', f.flow_version,
                'status', f.status,
                'isActive', f.is_active,
                'publishedAt', f.published_at
            )
            ORDER BY f.flow_name, f.flow_version
        ),
        '[]'::jsonb
    )
    INTO v_items
    FROM workflow.flow_versions AS f;

    RETURN jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'resource', 'flow',
            'items', v_items
        )
    );
END;
$list$;

ALTER FUNCTION workflow.list_flows() OWNER TO course_owner;

CREATE FUNCTION workflow.get_process(p_process_id uuid)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $get$
DECLARE
    v_process workflow.processes%ROWTYPE;
BEGIN
    IF p_process_id IS NULL THEN
        RETURN api.error_envelope('request.invalid', 'process id is required');
    END IF;

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = p_process_id;

    IF NOT FOUND THEN
        RETURN api.error_envelope('request.invalid', 'process does not exist');
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'resource', 'process',
            'processId', v_process.process_id,
            'businessKey', v_process.business_key,
            'flowName', v_process.flow_name,
            'flowVersion', v_process.flow_version,
            'state', v_process.state,
            'currentStepKey', v_process.current_step_key
        )
    );
END;
$get$;

ALTER FUNCTION workflow.get_process(uuid) OWNER TO course_owner;

CREATE FUNCTION workflow.start_process(
    p_flow_name text,
    p_business_key text,
    p_data jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $start$
DECLARE
    v_version workflow.flow_versions%ROWTYPE;
    v_process workflow.processes%ROWTYPE;
    v_data jsonb;
    v_id uuid;
BEGIN
    IF p_flow_name IS NULL OR p_flow_name = '' OR p_business_key IS NULL OR p_business_key = '' THEN
        RETURN api.error_envelope('request.invalid', 'flow name and business key are required');
    END IF;

    v_data := COALESCE(p_data, '{}'::jsonb);
    IF jsonb_typeof(v_data) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('request.invalid', 'process data must be a JSON object');
    END IF;

    SELECT * INTO v_version
    FROM workflow.flow_versions
    WHERE flow_name = p_flow_name
      AND is_active;

    IF NOT FOUND THEN
        RETURN api.error_envelope('request.invalid', 'flow has no active version');
    END IF;

    v_id := gen_random_uuid();

    INSERT INTO workflow.processes (
        process_id,
        business_key,
        flow_name,
        flow_version,
        state,
        current_step_key,
        process_data
    )
    VALUES (
        v_id,
        p_business_key,
        p_flow_name,
        v_version.flow_version,
        'CREATED',
        v_version.start_step,
        v_data
    );

    PERFORM workflow._append_event(v_id, NULL, 'ProcessStarted');
    PERFORM workflow._enter_step(v_id, v_version.start_step);

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = v_id;

    RETURN jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'resource', 'process',
            'processId', v_process.process_id,
            'businessKey', v_process.business_key,
            'flowName', v_process.flow_name,
            'flowVersion', v_process.flow_version,
            'state', v_process.state,
            'currentStepKey', v_process.current_step_key
        )
    );
EXCEPTION
    WHEN unique_violation THEN
        SELECT * INTO v_process
        FROM workflow.processes
        WHERE flow_name = p_flow_name
          AND business_key = p_business_key;

        IF v_process.process_data = v_data THEN
            RETURN jsonb_build_object(
                'status', 'ok',
                'result', jsonb_build_object(
                    'resource', 'process',
                    'processId', v_process.process_id,
                    'businessKey', v_process.business_key,
                    'flowName', v_process.flow_name,
                    'flowVersion', v_process.flow_version,
                    'state', v_process.state,
                    'currentStepKey', v_process.current_step_key
                )
            );
        END IF;

        RETURN api.error_envelope('workflow.conflict', 'business key was reused with different process data');
END;
$start$;

ALTER FUNCTION workflow.start_process(text, text, jsonb) OWNER TO course_owner;

CREATE FUNCTION workflow.signal_process(
    p_process_id uuid,
    p_type text,
    p_message_id text,
    p_payload jsonb
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $signal$
DECLARE
    v_process workflow.processes%ROWTYPE;
    v_existing workflow.signals%ROWTYPE;
    v_body jsonb;
    v_hash text;
BEGIN
    IF p_process_id IS NULL OR p_type IS NULL OR p_type = '' OR p_message_id IS NULL OR p_message_id = '' THEN
        RETURN api.error_envelope('request.invalid', 'process id, type and message id are required');
    END IF;

    v_body := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(v_body) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('request.invalid', 'signal payload must be a JSON object');
    END IF;

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = p_process_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('request.invalid', 'process does not exist');
    END IF;

    v_hash := workflow._json_hash(v_body);

    INSERT INTO workflow.signals (
        message_id, process_id, signal_type, body, body_hash, status, received_at
    )
    VALUES (p_message_id, p_process_id, p_type, v_body, v_hash, 'ACCEPTED', clock_timestamp());

    PERFORM workflow._append_event(p_process_id, NULL, 'SignalAccepted');
    PERFORM workflow._apply_pending_signal(p_process_id);

    RETURN jsonb_build_object(
        'status', 'ok',
        'result', jsonb_build_object(
            'resource', 'signal',
            'status', 'accepted',
            'messageId', p_message_id
        )
    );
EXCEPTION
    WHEN unique_violation THEN
        SELECT * INTO v_existing
        FROM workflow.signals
        WHERE message_id = p_message_id;

        IF v_existing.process_id = p_process_id
            AND v_existing.signal_type = p_type
            AND v_existing.body_hash = v_hash
        THEN
            RETURN jsonb_build_object(
                'status', 'ok',
                'result', jsonb_build_object(
                    'resource', 'signal',
                    'status', 'duplicate',
                    'messageId', p_message_id
                )
            );
        END IF;

        RETURN api.error_envelope('workflow.conflict', 'signal message id conflicts with a different payload or process');
END;
$signal$;

ALTER FUNCTION workflow.signal_process(uuid, text, text, jsonb) OWNER TO course_owner;

CREATE FUNCTION workflow.claim_jobs(p_owner text, p_lease_ms integer DEFAULT 2000)
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
    );

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

CREATE FUNCTION workflow.finish_job(
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

CREATE FUNCTION workflow.fail_job(
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
