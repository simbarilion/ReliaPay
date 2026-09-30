-- diagnostics.stalled: only include DEAD deliveries older than a documented threshold
-- measured from delivery.outbox.dead_at (set when attempts are exhausted).
-- Default threshold: 60 seconds. Override via GUC app.diagnostics_stalled_after
-- (PostgreSQL interval text, e.g. '10 seconds') for local/test contours.

CREATE OR REPLACE FUNCTION app.diagnostics_stalled(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $stalled$
DECLARE
    v_items jsonb;
    v_key_count integer;
    v_threshold interval;
    v_raw text;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_key_count := (SELECT count(*)::integer FROM jsonb_object_keys(p_payload));
    IF v_key_count <> 0 THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    v_raw := NULLIF(btrim(current_setting('app.diagnostics_stalled_after', true)), '');
    BEGIN
        v_threshold := COALESCE(v_raw::interval, interval '60 seconds');
    EXCEPTION
        WHEN invalid_text_representation OR datetime_field_overflow THEN
            v_threshold := interval '60 seconds';
    END;

    -- Guard against zero/negative thresholds from a mis-set GUC.
    IF v_threshold IS NULL OR v_threshold <= interval '0 seconds' THEN
        v_threshold := interval '60 seconds';
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
          AND o.dead_at IS NOT NULL
          AND o.dead_at <= clock_timestamp() - v_threshold
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
GRANT EXECUTE ON FUNCTION app.diagnostics_stalled(jsonb, jsonb) TO course_runtime;
