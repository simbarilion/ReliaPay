-- Week 4: login role autocheck_reader — только CONNECT + USAGE schema autocheck + SELECT views.
-- Роль создаётся init/ensure с паролем COURSE_AUTOCHECK_PASSWORD; здесь — гранты и ужесточение ACL.
-- Checksum: новый файл, старые миграции не трогаем.

DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'autocheck_reader') THEN
        CREATE ROLE autocheck_reader LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
    END IF;
END
$role$;

REVOKE ALL ON DATABASE course FROM autocheck_reader;
GRANT CONNECT ON DATABASE course TO autocheck_reader;

REVOKE ALL ON SCHEMA public FROM autocheck_reader;
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

-- Запрет memberships / лишних привилегий на физические отношения и функции.
DO $harden$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT c.oid::regclass AS rel
        FROM pg_catalog.pg_class AS c
        JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('api', 'app', 'workflow', 'delivery', 'autocheck', 'public')
          AND c.relkind IN ('r', 'p', 'm', 'S')
    LOOP
        EXECUTE format('REVOKE ALL ON TABLE %s FROM autocheck_reader', r.rel);
    END LOOP;

    FOR r IN
        SELECT p.oid::regprocedure AS sig
        FROM pg_catalog.pg_proc AS p
        JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
        WHERE n.nspname IN ('api', 'app', 'workflow', 'delivery', 'autocheck', 'public')
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM autocheck_reader', r.sig);
    END LOOP;
END
$harden$;

ALTER ROLE autocheck_reader NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
REVOKE TEMP ON DATABASE course FROM autocheck_reader;
REVOKE CREATE ON SCHEMA public FROM autocheck_reader;
REVOKE CREATE ON SCHEMA autocheck FROM autocheck_reader;

COMMENT ON ROLE autocheck_reader IS 'Week 4 read-only checker: CONNECT + USAGE autocheck + SELECT views only';