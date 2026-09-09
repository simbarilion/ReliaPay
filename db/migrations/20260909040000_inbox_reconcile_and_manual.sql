-- Inbox → signal payment.receipt и HTTP workflow.manual.
-- CREATE OR REPLACE / новые функции; checksum уже применённых миграций не меняется.

CREATE UNIQUE INDEX IF NOT EXISTS decisions_manual_once
    ON delivery.decisions (step_instance_id)
    WHERE source = 'MANUAL';

CREATE OR REPLACE FUNCTION workflow._apply_pending_signal(p_process_id uuid)
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

    IF NOT FOUND OR v_process.state IS DISTINCT FROM 'WAITING_SIGNAL' THEN
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

    IF v_def.signal_type IS NOT NULL THEN
        INSERT INTO workflow.signals (
            message_id, process_id, signal_type, body, body_hash, status, received_at
        )
        SELECT
            i.message_id,
            x.process_id,
            v_def.signal_type,
            jsonb_build_object(
                'messageId', r.message_id,
                'externalRequestId', r.external_request_id,
                'outcome', r.outcome
            ),
            workflow._json_hash(
                jsonb_build_object(
                    'messageId', r.message_id,
                    'externalRequestId', r.external_request_id,
                    'outcome', r.outcome
                )
            ),
            'ACCEPTED',
            i.received_at
        FROM delivery.inbox AS i
        INNER JOIN delivery.external_requests AS x
            ON x.external_request_id = i.external_request_id
        INNER JOIN delivery.receipts AS r
            ON r.message_id = i.message_id
        WHERE x.process_id = p_process_id
          AND i.state = 'RECEIVED'
          AND v_def.signal_type = 'payment.receipt'
        ON CONFLICT (message_id) DO NOTHING;
    END IF;

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

    UPDATE delivery.inbox
    SET state = 'APPLIED',
        applied_at = clock_timestamp()
    WHERE message_id = v_signal.message_id
      AND state = 'RECEIVED';

    UPDATE delivery.receipts
    SET applied_at = clock_timestamp()
    WHERE message_id = v_signal.message_id
      AND applied_at IS NULL;

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

CREATE OR REPLACE FUNCTION delivery.reconcile_inbox(p_limit integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $recon$
DECLARE
    v_limit integer;
    v_applied integer := 0;
    v_row record;
    v_before text;
BEGIN
    v_limit := GREATEST(1, LEAST(COALESCE(p_limit, 1), 32));

    FOR v_row IN
        SELECT i.message_id, x.process_id
        FROM delivery.inbox AS i
        INNER JOIN delivery.external_requests AS x
            ON x.external_request_id = i.external_request_id
        INNER JOIN workflow.processes AS p
            ON p.process_id = x.process_id
        WHERE i.state = 'RECEIVED'
          AND p.state = 'WAITING_SIGNAL'
        ORDER BY i.received_at, i.message_id
        LIMIT v_limit
    LOOP
        SELECT i.state INTO v_before
        FROM delivery.inbox AS i
        WHERE i.message_id = v_row.message_id;

        PERFORM workflow._apply_pending_signal(v_row.process_id);

        IF v_before = 'RECEIVED'
            AND EXISTS (
                SELECT 1
                FROM delivery.inbox AS i
                WHERE i.message_id = v_row.message_id
                  AND i.state = 'APPLIED'
            )
        THEN
            v_applied := v_applied + 1;
        END IF;
    END LOOP;

    RETURN v_applied;
END;
$recon$;

ALTER FUNCTION delivery.reconcile_inbox(integer) OWNER TO course_owner;

CREATE FUNCTION app.workflow_manual(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $manual$
DECLARE
    v_process_id uuid;
    v_step_id uuid;
    v_decision text;
    v_reason text;
    v_principal text;
    v_reason_hash text;
    v_process workflow.processes%ROWTYPE;
    v_step workflow.step_instances%ROWTYPE;
    v_def workflow.step_definitions%ROWTYPE;
    v_existing delivery.decisions%ROWTYPE;
    v_decision_id uuid;
    v_to text;
    v_result jsonb;
BEGIN
    p_context := COALESCE(p_context, '{}'::jsonb);
    p_payload := COALESCE(p_payload, '{}'::jsonb);

    BEGIN
        v_process_id := (p_payload ->> 'processId')::uuid;
        v_step_id := (p_payload ->> 'stepInstanceId')::uuid;
    EXCEPTION
        WHEN invalid_text_representation THEN
            RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END;

    v_decision := p_payload ->> 'decision';
    v_reason := p_payload ->> 'reason';
    v_principal := NULLIF(p_context ->> 'principal', '');

    IF v_process_id IS NULL
        OR v_step_id IS NULL
        OR v_decision NOT IN ('APPROVED', 'REJECTED')
        OR v_reason IS NULL
        OR char_length(v_reason) < 1
        OR char_length(v_reason) > 500
        OR v_principal IS NULL
        OR char_length(v_principal) > 128
    THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_reason_hash := encode(sha256(convert_to(v_reason, 'UTF8')), 'hex');

    SELECT * INTO v_process
    FROM workflow.processes
    WHERE process_id = v_process_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('request.invalid', 'process does not exist');
    END IF;

    SELECT * INTO v_step
    FROM workflow.step_instances
    WHERE step_instance_id = v_step_id
    FOR UPDATE;

    IF NOT FOUND OR v_step.process_id IS DISTINCT FROM v_process_id THEN
        RETURN api.error_envelope('workflow.decision_conflict', 'manual step is not waiting for a decision');
    END IF;

    SELECT * INTO v_existing
    FROM delivery.decisions
    WHERE step_instance_id = v_step_id
      AND source = 'MANUAL'
    FOR UPDATE;

    IF FOUND THEN
        RETURN api.error_envelope('workflow.decision_conflict', 'manual step is already decided');
    END IF;

    IF v_process.state IS DISTINCT FROM 'WAITING_MANUAL'
        OR v_process.current_step_key IS DISTINCT FROM v_step.step_key
        OR v_step.step_type IS DISTINCT FROM 'MANUAL'
        OR v_step.state IS DISTINCT FROM 'WAITING'
    THEN
        RETURN api.error_envelope('workflow.decision_conflict', 'manual step is not waiting for a decision');
    END IF;

    SELECT * INTO v_def
    FROM workflow.step_definitions
    WHERE flow_name = v_process.flow_name
      AND flow_version = v_process.flow_version
      AND step_key = v_step.step_key;

    IF v_def.allowed_outcomes IS NULL OR NOT (v_def.allowed_outcomes ? v_decision) THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    INSERT INTO delivery.decisions (
        process_id,
        step_instance_id,
        source,
        principal,
        reason_hash,
        outcome
    )
    VALUES (
        v_process_id,
        v_step_id,
        'MANUAL',
        v_principal,
        v_reason_hash,
        v_decision
    )
    RETURNING * INTO v_existing;

    v_decision_id := v_existing.decision_id;

    UPDATE workflow.step_instances
    SET state = 'COMPLETED',
        outcome = v_decision,
        completed_at = clock_timestamp()
    WHERE step_instance_id = v_step_id;

    PERFORM workflow._append_event(v_process_id, v_step_id, 'StepCompleted');

    SELECT t.to_step
    INTO v_to
    FROM workflow.transition_definitions AS t
    WHERE t.flow_name = v_process.flow_name
      AND t.flow_version = v_process.flow_version
      AND t.from_step = v_step.step_key
      AND t.outcome = v_decision;

    IF v_to IS NULL THEN
        RETURN api.error_envelope('internal.error', 'action failed');
    END IF;

    PERFORM workflow._enter_step(v_process_id, v_to);

    v_result := jsonb_build_object(
        'decisionId', v_decision_id,
        'processId', v_process_id,
        'stepInstanceId', v_step_id,
        'decision', v_decision,
        'source', 'MANUAL',
        'principal', v_principal
    );

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', v_decision,
        'result', v_result
    );
EXCEPTION
    WHEN unique_violation THEN
        RETURN api.error_envelope('workflow.decision_conflict', 'manual step is already decided');
END;
$manual$;

ALTER FUNCTION app.workflow_manual(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.workflow_manual(jsonb, jsonb) FROM PUBLIC;

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($manifest$
{
  "module": "workflow",
  "action": "manual",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "workflow_manual",
  "request_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["processId", "stepInstanceId", "decision", "reason"],
    "properties": {
      "processId": {"type": "string", "format": "uuid"},
      "stepInstanceId": {"type": "string", "format": "uuid"},
      "decision": {"enum": ["APPROVED", "REJECTED"]},
      "reason": {"type": "string", "minLength": 1, "maxLength": 500}
    }
  },
  "response_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["decisionId", "processId", "stepInstanceId", "decision", "source", "principal"],
    "properties": {
      "decisionId": {"type": "string", "format": "uuid"},
      "processId": {"type": "string", "format": "uuid"},
      "stepInstanceId": {"type": "string", "format": "uuid"},
      "decision": {"enum": ["APPROVED", "REJECTED"]},
      "source": {"const": "MANUAL"},
      "principal": {"type": "string", "minLength": 1, "maxLength": 128}
    }
  },
  "outcomes": ["APPROVED", "REJECTED"],
  "required_policy": ["workflow:manual"],
  "idempotency_mode": "required",
  "idempotency_scope": "principal_action",
  "timeout_ms": 5000
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish workflow.manual failed: %', v_result;
    END IF;

    v_result := api.activate_action('workflow.manual', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate workflow.manual failed: %', v_result;
    END IF;
END;
$boot$;
