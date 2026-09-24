-- Final actions require durable evidence under one lock.
-- complete/reject: matching APPLIED receipt; approve/reject-review: decision for process.
-- Checksum уже применённых миграций не меняется — только новый файл.

CREATE OR REPLACE FUNCTION app.payment_apply_receipt(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $apply$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_receipt delivery.receipts%ROWTYPE;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    SELECT r.*
    INTO v_receipt
    FROM delivery.receipts AS r
    INNER JOIN delivery.external_requests AS x
        ON x.external_request_id = r.external_request_id
    WHERE x.operation_id = v_operation.operation_id
      AND r.applied_at IS NOT NULL
    ORDER BY r.applied_at, r.received_at
    LIMIT 1;

    IF NOT FOUND THEN
        RETURN api.error_envelope('dependency.unavailable', 'receipt is not available');
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', v_receipt.outcome,
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'outcome', v_receipt.outcome
        )
    );
END;
$apply$;

ALTER FUNCTION app.payment_apply_receipt(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_apply_receipt(jsonb, jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION app.payment_complete(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $complete$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_receipt delivery.receipts%ROWTYPE;
    v_result jsonb;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.status = 'COMPLETED' THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'COMPLETED',
            'result', jsonb_build_object(
                'operationId', v_operation.operation_id,
                'status', 'COMPLETED'
            )
        );
    END IF;

    IF v_operation.status IS DISTINCT FROM 'PROCESSING' THEN
        RETURN api.error_envelope('request.invalid', 'operation cannot change status');
    END IF;

    SELECT r.*
    INTO v_receipt
    FROM delivery.receipts AS r
    INNER JOIN delivery.external_requests AS x
        ON x.external_request_id = r.external_request_id
    WHERE x.operation_id = v_operation.operation_id
      AND r.applied_at IS NOT NULL
      AND r.outcome = 'COMPLETED'
    ORDER BY r.applied_at, r.received_at
    LIMIT 1
    FOR UPDATE OF r;

    IF NOT FOUND THEN
        RETURN api.error_envelope('request.invalid', 'completed receipt evidence is required');
    END IF;

    UPDATE app.operations
    SET status = 'COMPLETED'
    WHERE operation_id = v_operation.operation_id;

    v_result := jsonb_build_object(
        'status', 'ok',
        'outcome', 'COMPLETED',
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'status', 'COMPLETED'
        )
    );

    INSERT INTO app.operation_events (operation_id, event_type, payload_hash)
    VALUES (
        v_operation.operation_id,
        'OPERATION_COMPLETED',
        encode(sha256(convert_to(v_result::text, 'UTF8')), 'hex')
    )
    ON CONFLICT DO NOTHING;

    RETURN v_result;
END;
$complete$;

ALTER FUNCTION app.payment_complete(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_complete(jsonb, jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION app.payment_reject(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $reject$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_receipt delivery.receipts%ROWTYPE;
    v_decision delivery.decisions%ROWTYPE;
    v_has_evidence boolean := false;
    v_result jsonb;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.status = 'REJECTED' THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'REJECTED',
            'result', jsonb_build_object(
                'operationId', v_operation.operation_id,
                'status', 'REJECTED'
            )
        );
    END IF;

    IF v_operation.status IS DISTINCT FROM 'PROCESSING' THEN
        RETURN api.error_envelope('request.invalid', 'operation cannot change status');
    END IF;

    SELECT r.*
    INTO v_receipt
    FROM delivery.receipts AS r
    INNER JOIN delivery.external_requests AS x
        ON x.external_request_id = r.external_request_id
    WHERE x.operation_id = v_operation.operation_id
      AND r.applied_at IS NOT NULL
      AND r.outcome = 'REJECTED'
    ORDER BY r.applied_at, r.received_at
    LIMIT 1
    FOR UPDATE OF r;

    IF FOUND THEN
        v_has_evidence := true;
    ELSIF v_operation.process_id IS NOT NULL THEN
        SELECT d.*
        INTO v_decision
        FROM delivery.decisions AS d
        WHERE d.process_id = v_operation.process_id
          AND d.outcome = 'REJECTED'
          AND d.source = 'MANUAL'
        ORDER BY d.created_at DESC
        LIMIT 1
        FOR UPDATE OF d;

        IF FOUND THEN
            v_has_evidence := true;
        END IF;
    END IF;

    IF NOT v_has_evidence THEN
        RETURN api.error_envelope('request.invalid', 'rejected evidence is required');
    END IF;

    UPDATE app.operations
    SET status = 'REJECTED'
    WHERE operation_id = v_operation.operation_id;

    v_result := jsonb_build_object(
        'status', 'ok',
        'outcome', 'REJECTED',
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'status', 'REJECTED'
        )
    );

    INSERT INTO app.operation_events (operation_id, event_type, payload_hash)
    VALUES (
        v_operation.operation_id,
        'OPERATION_REJECTED',
        encode(sha256(convert_to(v_result::text, 'UTF8')), 'hex')
    )
    ON CONFLICT DO NOTHING;

    RETURN v_result;
END;
$reject$;

ALTER FUNCTION app.payment_reject(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_reject(jsonb, jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION app.payment_approve(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $approve$
DECLARE
    v_operation_id uuid;
    v_operation app.operations%ROWTYPE;
    v_decision delivery.decisions%ROWTYPE;
    v_result jsonb;
BEGIN
    p_payload := COALESCE(p_payload, '{}'::jsonb);
    v_operation_id := app.payload_operation_id(p_payload);
    IF v_operation_id IS NULL THEN
        RETURN api.error_envelope('payload.invalid', 'payload does not match schema');
    END IF;

    SELECT * INTO v_operation
    FROM app.operations
    WHERE operation_id = v_operation_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN api.error_envelope('operation.not_found', 'operation does not exist');
    END IF;

    IF v_operation.status = 'COMPLETED' THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'APPROVED',
            'result', jsonb_build_object(
                'operationId', v_operation.operation_id,
                'status', 'COMPLETED'
            )
        );
    END IF;

    IF v_operation.status IS DISTINCT FROM 'PROCESSING'
        OR v_operation.process_id IS NULL
    THEN
        RETURN api.error_envelope('request.invalid', 'operation cannot change status');
    END IF;

    SELECT d.*
    INTO v_decision
    FROM delivery.decisions AS d
    WHERE d.process_id = v_operation.process_id
      AND d.outcome = 'APPROVED'
      AND d.source IN ('LIMIT_RULE', 'MANUAL')
    ORDER BY d.created_at DESC
    LIMIT 1
    FOR UPDATE OF d;

    IF NOT FOUND THEN
        RETURN api.error_envelope('request.invalid', 'approved decision evidence is required');
    END IF;

    UPDATE app.operations
    SET status = 'COMPLETED'
    WHERE operation_id = v_operation.operation_id;

    v_result := jsonb_build_object(
        'status', 'ok',
        'outcome', 'APPROVED',
        'result', jsonb_build_object(
            'operationId', v_operation.operation_id,
            'status', 'COMPLETED'
        )
    );

    INSERT INTO app.operation_events (operation_id, event_type, payload_hash)
    VALUES (
        v_operation.operation_id,
        'OPERATION_COMPLETED',
        encode(sha256(convert_to(v_result::text, 'UTF8')), 'hex')
    )
    ON CONFLICT DO NOTHING;

    RETURN v_result;
END;
$approve$;

ALTER FUNCTION app.payment_approve(jsonb, jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION app.payment_approve(jsonb, jsonb) FROM PUBLIC;
