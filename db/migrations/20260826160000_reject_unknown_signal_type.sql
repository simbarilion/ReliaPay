-- Сигнал принимается только если type объявлен на wait_signal pinned версии.
-- CREATE OR REPLACE не меняет checksum уже применённых файлов.

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
REVOKE ALL ON FUNCTION workflow.signal_process(uuid, text, text, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION workflow.signal_process(uuid, text, text, jsonb) TO course_publication;
