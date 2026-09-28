-- Harden database TEMP/CREATE: PUBLIC must not retain TEMP (autocheck_reader inherits it).
-- App principals keep TEMP; read-only autocheck_reader does not.

REVOKE TEMPORARY ON DATABASE course FROM PUBLIC;
REVOKE TEMPORARY ON DATABASE course FROM autocheck_reader;
REVOKE CREATE ON DATABASE course FROM PUBLIC;
REVOKE CREATE ON DATABASE course FROM autocheck_reader;

GRANT TEMPORARY ON DATABASE course TO course_runtime;
GRANT TEMPORARY ON DATABASE course TO workflow_worker;
GRANT TEMPORARY ON DATABASE course TO outbox_dispatcher;
GRANT TEMPORARY ON DATABASE course TO inbox_reconciler;
GRANT TEMPORARY ON DATABASE course TO course_publication;
