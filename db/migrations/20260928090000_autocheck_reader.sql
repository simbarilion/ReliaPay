-- Week 4: гранты autocheck_reader на read-only views schema autocheck.
-- Login role и CONNECT создаются init/ensure (COURSE_AUTOCHECK_PASSWORD).
-- Миграция от course_owner: USAGE autocheck + SELECT views; без DDL на чужих схемах (public).

DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'autocheck_reader') THEN
        RAISE EXCEPTION 'autocheck_reader role missing: ensure COURSE_AUTOCHECK_PASSWORD bootstrap ran';
    END IF;
END
$role$;

REVOKE ALL ON SCHEMA api FROM autocheck_reader;
REVOKE ALL ON SCHEMA app FROM autocheck_reader;
REVOKE ALL ON SCHEMA workflow FROM autocheck_reader;
REVOKE ALL ON SCHEMA delivery FROM autocheck_reader;
REVOKE ALL ON SCHEMA autocheck FROM autocheck_reader;

GRANT USAGE ON SCHEMA autocheck TO autocheck_reader;

DO $views$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT c.relname AS view_name
        FROM pg_catalog.pg_class AS c
        JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname = 'autocheck'
          AND c.relkind = 'v'
    LOOP
        EXECUTE format('REVOKE ALL ON autocheck.%I FROM PUBLIC', r.view_name);
        EXECUTE format('REVOKE ALL ON autocheck.%I FROM autocheck_reader', r.view_name);
        EXECUTE format('GRANT SELECT ON autocheck.%I TO autocheck_reader', r.view_name);
    END LOOP;
END
$views$;

DO $harden$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT c.oid::regclass AS rel
        FROM pg_catalog.pg_class AS c
        JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('api', 'app', 'workflow', 'delivery', 'autocheck')
          AND c.relkind IN ('r', 'p', 'm', 'S')
    LOOP
        EXECUTE format('REVOKE ALL ON TABLE %s FROM autocheck_reader', r.rel);
    END LOOP;

    FOR r IN
        SELECT p.oid::regprocedure AS sig
        FROM pg_catalog.pg_proc AS p
        JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
        WHERE n.nspname IN ('api', 'app', 'workflow', 'delivery', 'autocheck')
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM autocheck_reader', r.sig);
    END LOOP;
END
$harden$;

REVOKE CREATE ON SCHEMA autocheck FROM autocheck_reader;