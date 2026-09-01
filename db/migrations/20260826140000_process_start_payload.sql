-- Неизменяемый снимок входных process data: replay start сравнивает его, а не
-- текущий process_data, который finish_job дополняет result шага.

ALTER TABLE workflow.processes
    ADD COLUMN IF NOT EXISTS start_payload jsonb;

UPDATE workflow.processes
SET start_payload = process_data
WHERE start_payload IS NULL;

ALTER TABLE workflow.processes
    ALTER COLUMN start_payload SET NOT NULL;

ALTER TABLE workflow.processes
    DROP CONSTRAINT IF EXISTS processes_start_payload_chk;

ALTER TABLE workflow.processes
    ADD CONSTRAINT processes_start_payload_chk CHECK (jsonb_typeof(start_payload) = 'object');

CREATE OR REPLACE FUNCTION workflow.start_process(
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
        process_data,
        start_payload
    )
    VALUES (
        v_id,
        p_business_key,
        p_flow_name,
        v_version.flow_version,
        'CREATED',
        v_version.start_step,
        v_data,
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

        IF v_process.start_payload = v_data THEN
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
REVOKE ALL ON FUNCTION workflow.start_process(text, text, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION workflow.start_process(text, text, jsonb) TO course_publication;
