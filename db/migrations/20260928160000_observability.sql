-- Observability: course metrics + diagnostics.trace / diagnostics.stalled.

CREATE OR REPLACE FUNCTION app.course_metrics()
RETURNS TABLE (
    workflow_jobs_ready double precision,
    workflow_job_oldest_age_seconds double precision,
    workflow_processes_waiting double precision,
    outbox_pending double precision,
    outbox_oldest_age_seconds double precision,
    workflow_failures_total double precision
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $metrics$
    WITH claimable AS (
        SELECT j.created_at
        FROM workflow.jobs AS j
        WHERE j.state = 'READY'
           OR (j.state = 'RETRY_WAIT' AND COALESCE(j.next_attempt_at, clock_timestamp()) <= clock_timestamp())
           OR (j.state = 'LEASED' AND j.lease_until IS NOT NULL AND j.lease_until < clock_timestamp())
    ),
    outbox_open AS (
        SELECT o.created_at
        FROM delivery.outbox AS o
        WHERE o.state IN ('PENDING', 'LEASED', 'RETRY_WAIT')
    )
    SELECT
        (SELECT count(*)::double precision FROM claimable),
        COALESCE(
            (SELECT EXTRACT(EPOCH FROM (clock_timestamp() - min(created_at)))::double precision FROM claimable),
            0::double precision
        ),
        (
            SELECT count(*)::double precision
            FROM workflow.processes AS p
            WHERE p.state IN ('WAITING_SIGNAL', 'WAITING_MANUAL')
        ),
        (SELECT count(*)::double precision FROM outbox_open),
        COALESCE(
            (SELECT EXTRACT(EPOCH FROM (clock_timestamp() - min(created_at)))::double precision FROM outbox_open),
            0::double precision
        ),
        (
            SELECT count(*)::double precision
            FROM workflow.processes AS p
            WHERE p.state = 'FAILED'
        );
$metrics$;

ALTER FUNCTION app.course_metrics() OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.course_metrics() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.course_metrics() TO course_runtime;

CREATE OR REPLACE FUNCTION app.diagnostics_stalled(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $stalled$
DECLARE
    v_items jsonb;
    v_key_count integer;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_key_count := (SELECT count(*)::integer FROM jsonb_object_keys(p_payload));
    IF v_key_count <> 0 THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT COALESCE(jsonb_agg(item ORDER BY item ->> 'operationId'), '[]'::jsonb)
    INTO v_items
    FROM (
        SELECT jsonb_build_object(
            'operationId', er.operation_id,
            'processId', er.process_id,
            'externalRequestId', er.external_request_id
        ) AS item
        FROM delivery.outbox AS o
        INNER JOIN delivery.external_requests AS er
            ON er.external_request_id = o.external_request_id
        INNER JOIN workflow.processes AS p
            ON p.process_id = er.process_id
        INNER JOIN app.operations AS op
            ON op.operation_id = er.operation_id
        WHERE o.state = 'DEAD'
          AND p.state = 'WAITING_SIGNAL'
          AND op.status = 'PROCESSING'
          AND er.process_id IS NOT NULL
        ORDER BY er.operation_id
    ) AS stalled;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', jsonb_build_object('items', v_items)
    );
END;
$stalled$;

ALTER FUNCTION app.diagnostics_stalled(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.diagnostics_stalled(jsonb, jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION app._diag_ts(p_ts timestamptz)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $ts$
    SELECT CASE
        WHEN p_ts IS NULL THEN NULL
        ELSE to_char(p_ts AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
    END;
$ts$;

ALTER FUNCTION app._diag_ts(timestamptz) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app._diag_ts(timestamptz) FROM PUBLIC;

CREATE OR REPLACE FUNCTION app.diagnostics_trace(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $trace$
DECLARE
    v_identifier text;
    v_key_count integer;
    v_operation_id uuid;
    v_process_id uuid;
    v_matched text[] := ARRAY[]::text[];
    v_tmp uuid;
    v_tmp_text text;
    v_operation jsonb;
    v_process jsonb;
    v_dispatches jsonb;
    v_events jsonb;
    v_steps jsonb;
    v_jobs jsonb;
    v_attempts jsonb;
    v_outbox jsonb;
    v_inbox jsonb;
    v_receipts jsonb;
    v_decisions jsonb;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_key_count := (SELECT count(*)::integer FROM jsonb_object_keys(p_payload));
    IF v_key_count <> 1 OR jsonb_typeof(p_payload -> 'identifier') IS DISTINCT FROM 'string' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_identifier := p_payload ->> 'identifier';
    IF v_identifier IS NULL OR length(v_identifier) < 1 OR length(v_identifier) > 200
        OR v_identifier ~ E'[\r\n]' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    -- Resolve identifier into operation_id / process_id (chain root).
    BEGIN
        v_tmp := v_identifier::uuid;
    EXCEPTION WHEN invalid_text_representation THEN
        v_tmp := NULL;
    END;

    IF v_tmp IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM api.action_dispatches d WHERE d.correlation_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'correlationId');
            SELECT o.operation_id, o.process_id INTO v_operation_id, v_process_id
            FROM app.operations o
            WHERE o.request_id IN (
                SELECT d.request_id FROM api.action_dispatches d
                WHERE d.correlation_id = v_tmp AND d.request_id IS NOT NULL
            )
            ORDER BY o.created_at
            LIMIT 1;
            IF v_operation_id IS NULL THEN
                SELECT er.operation_id, er.process_id INTO v_operation_id, v_process_id
                FROM delivery.external_requests er
                WHERE er.correlation_id = v_tmp
                LIMIT 1;
            END IF;
        END IF;

        IF EXISTS (SELECT 1 FROM app.operations o WHERE o.operation_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'operationId');
            SELECT o.operation_id, o.process_id INTO v_operation_id, v_process_id
            FROM app.operations o WHERE o.operation_id = v_tmp;
        END IF;

        IF EXISTS (SELECT 1 FROM workflow.processes p WHERE p.process_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'processId');
            v_process_id := v_tmp;
            SELECT o.operation_id INTO v_operation_id
            FROM app.operations o WHERE o.process_id = v_tmp
            ORDER BY o.created_at LIMIT 1;
        END IF;

        IF EXISTS (SELECT 1 FROM workflow.step_instances s WHERE s.step_instance_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'stepInstanceId');
            SELECT s.process_id INTO v_process_id FROM workflow.step_instances s WHERE s.step_instance_id = v_tmp;
            SELECT o.operation_id INTO v_operation_id FROM app.operations o WHERE o.process_id = v_process_id LIMIT 1;
        END IF;

        IF EXISTS (SELECT 1 FROM workflow.jobs j WHERE j.job_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'jobId');
            SELECT j.process_id INTO v_process_id FROM workflow.jobs j WHERE j.job_id = v_tmp;
            SELECT o.operation_id INTO v_operation_id FROM app.operations o WHERE o.process_id = v_process_id LIMIT 1;
        END IF;

        IF EXISTS (SELECT 1 FROM workflow.jobs j WHERE j.execution_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'executionId');
            SELECT j.process_id INTO v_process_id FROM workflow.jobs j WHERE j.execution_id = v_tmp LIMIT 1;
            SELECT o.operation_id INTO v_operation_id FROM app.operations o WHERE o.process_id = v_process_id LIMIT 1;
        END IF;

        IF EXISTS (SELECT 1 FROM workflow.attempts a WHERE a.attempt_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'attemptId');
            SELECT j.process_id INTO v_process_id
            FROM workflow.attempts a
            INNER JOIN workflow.jobs j ON j.job_id = a.job_id
            WHERE a.attempt_id = v_tmp;
            SELECT o.operation_id INTO v_operation_id FROM app.operations o WHERE o.process_id = v_process_id LIMIT 1;
        END IF;

        IF EXISTS (SELECT 1 FROM delivery.decisions d WHERE d.decision_id = v_tmp) THEN
            v_matched := array_append(v_matched, 'decisionId');
            SELECT d.process_id INTO v_process_id FROM delivery.decisions d WHERE d.decision_id = v_tmp;
            SELECT o.operation_id INTO v_operation_id FROM app.operations o WHERE o.process_id = v_process_id LIMIT 1;
        END IF;
    END IF;

    IF EXISTS (SELECT 1 FROM app.operations o WHERE o.request_id = v_identifier) THEN
        v_matched := array_append(v_matched, 'requestId');
        SELECT o.operation_id, o.process_id INTO v_operation_id, v_process_id
        FROM app.operations o WHERE o.request_id = v_identifier;
    END IF;

    IF EXISTS (SELECT 1 FROM delivery.external_requests er WHERE er.external_request_id = v_identifier) THEN
        v_matched := array_append(v_matched, 'externalRequestId');
        SELECT er.operation_id, er.process_id INTO v_operation_id, v_process_id
        FROM delivery.external_requests er WHERE er.external_request_id = v_identifier;
    END IF;

    IF EXISTS (SELECT 1 FROM delivery.inbox i WHERE i.message_id = v_identifier)
       OR EXISTS (SELECT 1 FROM delivery.receipts r WHERE r.message_id = v_identifier) THEN
        v_matched := array_append(v_matched, 'messageId');
        SELECT er.operation_id, er.process_id INTO v_operation_id, v_process_id
        FROM delivery.inbox i
        INNER JOIN delivery.external_requests er ON er.external_request_id = i.external_request_id
        WHERE i.message_id = v_identifier
        LIMIT 1;
        IF v_operation_id IS NULL THEN
            SELECT er.operation_id, er.process_id INTO v_operation_id, v_process_id
            FROM delivery.receipts r
            INNER JOIN delivery.external_requests er ON er.external_request_id = r.external_request_id
            WHERE r.message_id = v_identifier
            LIMIT 1;
        END IF;
    END IF;

    -- Deduplicate matchedBy, keep stable order of known kinds.
    SELECT COALESCE(array_agg(x ORDER BY ordinality), ARRAY[]::text[])
    INTO v_matched
    FROM (
        SELECT DISTINCT x, ordinality
        FROM unnest(ARRAY[
            'correlationId','requestId','operationId','processId','stepInstanceId',
            'jobId','executionId','attemptId','externalRequestId','messageId','decisionId'
        ]) WITH ORDINALITY AS u(x, ordinality)
        WHERE x = ANY (v_matched)
    ) AS d;

    IF v_operation_id IS NULL AND v_process_id IS NULL THEN
        RETURN api.error_envelope('diagnostics.trace_not_found', 'trace not found');
    END IF;

    IF v_operation_id IS NULL AND v_process_id IS NOT NULL THEN
        SELECT o.operation_id INTO v_operation_id
        FROM app.operations o WHERE o.process_id = v_process_id
        ORDER BY o.created_at LIMIT 1;
    END IF;

    IF v_process_id IS NULL AND v_operation_id IS NOT NULL THEN
        SELECT o.process_id INTO v_process_id FROM app.operations o WHERE o.operation_id = v_operation_id;
    END IF;

    IF cardinality(v_matched) IS NULL OR cardinality(v_matched) = 0 THEN
        v_matched := ARRAY['operationId'];
    END IF;

    SELECT CASE WHEN o.operation_id IS NULL THEN NULL ELSE jsonb_build_object(
        'operationId', o.operation_id,
        'requestId', o.request_id,
        'operationKind', o.operation_kind,
        'amount', o.amount_text,
        'currency', o.currency,
        'status', o.status,
        'processId', o.process_id,
        'createdAt', app._diag_ts(o.created_at),
        'updatedAt', app._diag_ts(o.updated_at)
    ) END
    INTO v_operation
    FROM app.operations o WHERE o.operation_id = v_operation_id;

    SELECT CASE WHEN p.process_id IS NULL THEN NULL ELSE jsonb_build_object(
        'processId', p.process_id,
        'flowName', p.flow_name,
        'flowVersion', p.flow_version,
        'state', p.state,
        'currentStepKey', NULLIF(p.current_step_key, ''),
        'createdAt', app._diag_ts(p.created_at),
        'updatedAt', app._diag_ts(p.updated_at)
    ) END
    INTO v_process
    FROM workflow.processes p WHERE p.process_id = v_process_id;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'occurredAt', x ->> 'correlationId'), '[]'::jsonb)
    INTO v_dispatches
    FROM (
        SELECT jsonb_build_object(
            'correlationId', d.correlation_id,
            'requestId', COALESCE(d.request_id, ''),
            'module', d.module,
            'action', d.action,
            'version', COALESCE(d.version, 1),
            'status', d.status,
            'outcome', d.outcome,
            'occurredAt', app._diag_ts(d.occurred_at)
        ) AS x
        FROM api.action_dispatches d
        WHERE d.correlation_id IS NOT NULL
          AND (
                (v_operation_id IS NOT NULL AND d.request_id = (SELECT request_id FROM app.operations WHERE operation_id = v_operation_id))
             OR (v_process_id IS NOT NULL AND d.correlation_id IN (
                    SELECT er.correlation_id FROM delivery.external_requests er WHERE er.process_id = v_process_id
               ))
             OR (v_operation_id IS NOT NULL AND d.correlation_id IN (
                    SELECT er.correlation_id FROM delivery.external_requests er WHERE er.operation_id = v_operation_id
               ))
          )
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'occurredAt', x ->> 'eventId'), '[]'::jsonb)
    INTO v_events
    FROM (
        SELECT jsonb_build_object(
            'eventId', e.event_id,
            'eventType', e.event_type,
            'occurredAt', app._diag_ts(e.occurred_at)
        ) AS x
        FROM app.operation_events e
        WHERE e.operation_id = v_operation_id
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'enteredAt', x ->> 'stepInstanceId'), '[]'::jsonb)
    INTO v_steps
    FROM (
        SELECT jsonb_build_object(
            'stepInstanceId', s.step_instance_id,
            'stepKey', s.step_key,
            'stepType', s.step_type,
            'state', s.state,
            'outcome', s.outcome,
            'enteredAt', app._diag_ts(s.entered_at),
            'completedAt', app._diag_ts(s.completed_at)
        ) AS x
        FROM workflow.step_instances s
        WHERE s.process_id = v_process_id
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'jobId'), '[]'::jsonb)
    INTO v_jobs
    FROM (
        SELECT jsonb_build_object(
            'jobId', j.job_id,
            'stepInstanceId', j.step_instance_id,
            'executionId', j.execution_id,
            'state', j.state,
            'leaseVersion', j.lease_version,
            'attemptCount', j.attempt_count,
            'nextAttemptAt', app._diag_ts(j.next_attempt_at)
        ) AS x
        FROM workflow.jobs j
        WHERE j.process_id = v_process_id
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'startedAt', x ->> 'attemptId'), '[]'::jsonb)
    INTO v_attempts
    FROM (
        SELECT jsonb_build_object(
            'attemptId', a.attempt_id,
            'jobId', a.job_id,
            'executionId', a.execution_id,
            'leaseVersion', a.lease_version,
            'attemptNumber', a.attempt_number,
            'status', a.status,
            'outcome', a.outcome,
            'errorCode', a.error_code,
            'startedAt', app._diag_ts(a.started_at),
            'finishedAt', app._diag_ts(a.finished_at)
        ) AS x
        FROM workflow.attempts a
        INNER JOIN workflow.jobs j ON j.job_id = a.job_id
        WHERE j.process_id = v_process_id
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'createdAt', x ->> 'outboxId'), '[]'::jsonb)
    INTO v_outbox
    FROM (
        SELECT jsonb_build_object(
            'outboxId', o.outbox_id,
            'externalRequestId', o.external_request_id,
            'state', o.state,
            'attemptCount', o.attempt_count,
            'leaseVersion', o.lease_version,
            'nextAttemptAt', app._diag_ts(o.next_attempt_at),
            'lastErrorCode', o.last_error_code,
            'createdAt', app._diag_ts(o.created_at),
            'deliveredAt', app._diag_ts(o.delivered_at),
            'deadAt', app._diag_ts(o.dead_at)
        ) AS x
        FROM delivery.outbox o
        INNER JOIN delivery.external_requests er ON er.external_request_id = o.external_request_id
        WHERE er.operation_id = v_operation_id
           OR er.process_id = v_process_id
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'receivedAt', x ->> 'messageId'), '[]'::jsonb)
    INTO v_inbox
    FROM (
        SELECT jsonb_build_object(
            'messageId', i.message_id,
            'state', i.state,
            'receivedAt', app._diag_ts(i.received_at),
            'appliedAt', app._diag_ts(i.applied_at)
        ) AS x
        FROM delivery.inbox i
        INNER JOIN delivery.external_requests er ON er.external_request_id = i.external_request_id
        WHERE er.operation_id = v_operation_id
           OR er.process_id = v_process_id
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'receivedAt', x ->> 'messageId'), '[]'::jsonb)
    INTO v_receipts
    FROM (
        SELECT jsonb_build_object(
            'messageId', r.message_id,
            'externalRequestId', r.external_request_id,
            'outcome', r.outcome,
            'signatureValid', true,
            'receivedAt', app._diag_ts(r.received_at),
            'appliedAt', app._diag_ts(r.applied_at)
        ) AS x
        FROM delivery.receipts r
        INNER JOIN delivery.external_requests er ON er.external_request_id = r.external_request_id
        WHERE (er.operation_id = v_operation_id OR er.process_id = v_process_id)
          AND r.signature_valid IS TRUE
    ) q;

    SELECT COALESCE(jsonb_agg(x ORDER BY x ->> 'createdAt', x ->> 'decisionId'), '[]'::jsonb)
    INTO v_decisions
    FROM (
        SELECT jsonb_build_object(
            'decisionId', d.decision_id,
            'stepInstanceId', d.step_instance_id,
            'source', d.source,
            'principal', COALESCE(d.principal, ''),
            'outcome', d.outcome,
            'ruleVersion', d.rule_version,
            'createdAt', app._diag_ts(d.created_at)
        ) AS x
        FROM delivery.decisions d
        WHERE d.process_id = v_process_id
    ) q;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', jsonb_build_object(
            'query', jsonb_build_object(
                'identifier', v_identifier,
                'matchedBy', to_jsonb(v_matched)
            ),
            'dispatches', COALESCE(v_dispatches, '[]'::jsonb),
            'operation', v_operation,
            'operationEvents', COALESCE(v_events, '[]'::jsonb),
            'process', v_process,
            'steps', COALESCE(v_steps, '[]'::jsonb),
            'jobs', COALESCE(v_jobs, '[]'::jsonb),
            'attempts', COALESCE(v_attempts, '[]'::jsonb),
            'outbox', COALESCE(v_outbox, '[]'::jsonb),
            'inbox', COALESCE(v_inbox, '[]'::jsonb),
            'receipts', COALESCE(v_receipts, '[]'::jsonb),
            'decisions', COALESCE(v_decisions, '[]'::jsonb)
        )
    );
END;
$trace$;

ALTER FUNCTION app.diagnostics_trace(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.diagnostics_trace(jsonb, jsonb) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION app.diagnostics_trace(jsonb, jsonb) TO course_runtime;
GRANT EXECUTE ON FUNCTION app.diagnostics_stalled(jsonb, jsonb) TO course_runtime;

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($manifest$
{
  "contract_version": "course-1",
  "module": "diagnostics",
  "action": "trace",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "diagnostics_trace",
  "request_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "required": ["identifier"],
    "properties": {
      "identifier": {
        "type": "string",
        "minLength": 1,
        "maxLength": 200,
        "pattern": "^[^\\r\\n]+$"
      }
    }
  },
  "response_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": true
  },
  "outcomes": ["FOUND"],
  "required_policy": ["diagnostics:read"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 10000,
  "enabled": false,
  "is_default": false
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish diagnostics.trace failed: %', v_result;
    END IF;

    v_result := api.activate_action('diagnostics.trace', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate diagnostics.trace failed: %', v_result;
    END IF;

    v_result := api.publish_action($manifest$
{
  "contract_version": "course-1",
  "module": "diagnostics",
  "action": "stalled",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "diagnostics_stalled",
  "request_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": false,
    "properties": {}
  },
  "response_schema": {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": true
  },
  "outcomes": ["FOUND"],
  "required_policy": ["diagnostics:read"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 10000,
  "enabled": false,
  "is_default": false
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish diagnostics.stalled failed: %', v_result;
    END IF;

    v_result := api.activate_action('diagnostics.stalled', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate diagnostics.stalled failed: %', v_result;
    END IF;
END;
$boot$;
