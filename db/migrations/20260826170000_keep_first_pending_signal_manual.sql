-- Keep-first canary, PENDING в CHECK шага, CLI-поля сигнала, карта WAITING_MANUAL.
-- CREATE OR REPLACE / ADD CONSTRAINT не меняют checksum уже применённых файлов.

CREATE OR REPLACE FUNCTION app.training_canary(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $canary$
DECLARE
    v_value text;
    v_stored text;
    v_request text;
    v_execution uuid;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_value := NULLIF(p_payload ->> 'value', '');
    IF v_value IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_request := NULLIF(p_context ->> 'requestId', '');
    IF v_request IS NOT NULL THEN
        BEGIN
            v_execution := v_request::uuid;
        EXCEPTION
            WHEN invalid_text_representation THEN
                v_execution := NULL;
        END;

        IF v_execution IS NOT NULL THEN
            INSERT INTO app.training_canary_effects (execution_id, value)
            VALUES (v_execution, v_value)
            ON CONFLICT (execution_id) DO NOTHING;

            SELECT value INTO v_stored
            FROM app.training_canary_effects
            WHERE execution_id = v_execution;

            v_value := COALESCE(v_stored, v_value);
        END IF;
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'APPLIED',
        'result', jsonb_build_object(
            'stored', true,
            'value', v_value
        )
    );
END;
$canary$;

ALTER FUNCTION app.training_canary(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.training_canary(jsonb, jsonb) FROM PUBLIC;

ALTER TABLE workflow.step_instances
    DROP CONSTRAINT IF EXISTS step_instances_state_chk;

ALTER TABLE workflow.step_instances
    ADD CONSTRAINT step_instances_state_chk
    CHECK (state IN ('PENDING', 'READY', 'RUNNING', 'WAITING', 'COMPLETED', 'FAILED'));

CREATE OR REPLACE FUNCTION workflow.signal_process(
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

    IF NOT EXISTS (
        SELECT 1
        FROM workflow.step_definitions
        WHERE flow_name = v_process.flow_name
          AND flow_version = v_process.flow_version
          AND step_type = 'WAIT_SIGNAL'
          AND signal_type = p_type
    ) THEN
        RETURN api.error_envelope('request.invalid', 'signal type is not declared on the pinned flow version');
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
            'messageId', p_message_id,
            'processId', p_process_id,
            'signalType', p_type
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
                    'messageId', p_message_id,
                    'processId', p_process_id,
                    'signalType', p_type
                )
            );
        END IF;

        RETURN api.error_envelope('workflow.conflict', 'signal message id conflicts with a different payload or process');
END;
$signal$;

ALTER FUNCTION workflow.signal_process(uuid, text, text, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION workflow.signal_process(uuid, text, text, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION workflow.signal_process(uuid, text, text, jsonb) TO course_publication;

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := workflow.publish_flow($map$
{
  "contract_version": "course-1",
  "flow_name": "workflow-manual",
  "version": 1,
  "start_step": "review",
  "steps": [
    {
      "key": "review",
      "type": "manual",
      "allowed_outcomes": ["APPROVED"]
    },
    {
      "key": "done",
      "type": "end",
      "outcome": "COMPLETED"
    }
  ],
  "transitions": [
    {
      "from": "review",
      "outcome": "APPROVED",
      "to": "done"
    }
  ]
}
$map$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish workflow-manual v1 failed: %', v_result;
    END IF;

    v_result := workflow.activate_flow('workflow-manual', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate workflow-manual v1 failed: %', v_result;
    END IF;
END;
$boot$;
