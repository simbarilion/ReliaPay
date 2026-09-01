-- Встроенные training.canary и workflow-smoke v1/v2: локальный automatic-smoke без неизвестных имён checker.

CREATE TABLE app.training_canary_effects (
    execution_id uuid PRIMARY KEY,
    value text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

ALTER TABLE app.training_canary_effects OWNER TO course_owner;
REVOKE ALL ON TABLE app.training_canary_effects FROM PUBLIC;

CREATE FUNCTION app.training_canary(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $canary$
DECLARE
    v_value text;
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
            ON CONFLICT (execution_id) DO UPDATE
            SET value = EXCLUDED.value,
                created_at = clock_timestamp();
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

DO $boot$
DECLARE
    v_result jsonb;
BEGIN
    v_result := api.publish_action($manifest$
{
  "module": "training",
  "action": "canary",
  "version": 1,
  "http_method": "POST",
  "target_schema": "app",
  "target_function": "training_canary",
  "request_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["value"],
    "properties": {
      "value": { "type": "string", "minLength": 1 }
    }
  },
  "response_schema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["stored", "value"],
    "properties": {
      "stored": { "type": "boolean" },
      "value": { "type": "string" }
    }
  },
  "outcomes": ["APPLIED"],
  "required_policy": ["workflow:execute"],
  "idempotency_mode": "none",
  "idempotency_scope": "none",
  "timeout_ms": 2000
}
$manifest$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish training.canary failed: %', v_result;
    END IF;

    v_result := api.activate_action('training.canary', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate training.canary failed: %', v_result;
    END IF;

    v_result := workflow.publish_flow($map1$
{
  "contract_version": "course-1",
  "flow_name": "workflow-smoke",
  "version": 1,
  "start_step": "invoke_canary",
  "steps": [
    {
      "key": "invoke_canary",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "training",
        "action": "canary",
        "action_version": 1,
        "required_policy": ["workflow:execute"],
        "timeout_ms": 2000,
        "retry": {
          "max_attempts": 3,
          "delays_ms": [200, 400]
        },
        "input_mapping": {
          "/value": "/value"
        },
        "input_constants": {}
      }
    },
    {
      "key": "wait_result",
      "type": "wait_signal",
      "signal_type": "training.completed",
      "outcome": "RECEIVED"
    },
    {
      "key": "done",
      "type": "end",
      "outcome": "COMPLETED"
    }
  ],
  "transitions": [
    {
      "from": "invoke_canary",
      "outcome": "APPLIED",
      "to": "wait_result"
    },
    {
      "from": "wait_result",
      "outcome": "RECEIVED",
      "to": "done"
    }
  ]
}
$map1$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish workflow-smoke v1 failed: %', v_result;
    END IF;

    v_result := workflow.publish_flow($map2$
{
  "contract_version": "course-1",
  "flow_name": "workflow-smoke",
  "version": 2,
  "start_step": "invoke_canary",
  "steps": [
    {
      "key": "invoke_canary",
      "type": "automatic",
      "task": {
        "service": "postgres",
        "module": "training",
        "action": "canary",
        "action_version": 1,
        "required_policy": ["workflow:execute"],
        "timeout_ms": 2500,
        "retry": {
          "max_attempts": 3,
          "delays_ms": [300, 600]
        },
        "input_mapping": {
          "/value": "/value"
        },
        "input_constants": {}
      }
    },
    {
      "key": "wait_result",
      "type": "wait_signal",
      "signal_type": "training.completed",
      "outcome": "RECEIVED"
    },
    {
      "key": "done",
      "type": "end",
      "outcome": "COMPLETED"
    }
  ],
  "transitions": [
    {
      "from": "invoke_canary",
      "outcome": "APPLIED",
      "to": "wait_result"
    },
    {
      "from": "wait_result",
      "outcome": "RECEIVED",
      "to": "done"
    }
  ]
}
$map2$::jsonb);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'publish workflow-smoke v2 failed: %', v_result;
    END IF;

    v_result := workflow.activate_flow('workflow-smoke', 1);
    IF COALESCE(v_result ->> 'status', '') IS DISTINCT FROM 'ok' THEN
        RAISE EXCEPTION 'activate workflow-smoke v1 failed: %', v_result;
    END IF;
END;
$boot$;
