-- Python-роли видят EXECUTE через PUBLIC на trigger-функциях недели 1.
-- checker python-fixed-function-privileges сравнивает has_function_privilege, включая PUBLIC.

REVOKE EXECUTE ON FUNCTION app.operations_protect_identity() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION app.operation_events_insert_only() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION app.operations_protect_identity() FROM outbox_dispatcher;
REVOKE EXECUTE ON FUNCTION app.operations_protect_identity() FROM inbox_reconciler;
REVOKE EXECUTE ON FUNCTION app.operation_events_insert_only() FROM outbox_dispatcher;
REVOKE EXECUTE ON FUNCTION app.operation_events_insert_only() FROM inbox_reconciler;

GRANT EXECUTE ON FUNCTION app.operations_protect_identity() TO course_runtime;
GRANT EXECUTE ON FUNCTION app.operations_protect_identity() TO workflow_worker;
GRANT EXECUTE ON FUNCTION app.operations_protect_identity() TO course_publication;
GRANT EXECUTE ON FUNCTION app.operation_events_insert_only() TO course_runtime;
GRANT EXECUTE ON FUNCTION app.operation_events_insert_only() TO workflow_worker;
GRANT EXECUTE ON FUNCTION app.operation_events_insert_only() TO course_publication;

DO $acl$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT p.oid::regprocedure AS sig
        FROM pg_catalog.pg_proc AS p
        JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
        WHERE n.nspname NOT LIKE 'pg_%'
          AND n.nspname <> 'information_schema'
    LOOP
        EXECUTE format('REVOKE EXECUTE ON FUNCTION %s FROM outbox_dispatcher', r.sig);
        EXECUTE format('REVOKE EXECUTE ON FUNCTION %s FROM inbox_reconciler', r.sig);
    END LOOP;
END;
$acl$;

GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text, integer) TO outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.succeed_outbox(uuid, text, bigint, text) TO outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.fail_outbox(uuid, text, bigint, text) TO outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO inbox_reconciler;
