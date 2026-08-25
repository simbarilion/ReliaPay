#!/bin/sh
# Инициализация пустого тома: роли, схемы и журнал checksum-миграций.
# Образ PostgreSQL выполняет этот файл только при первом создании data directory.
set -e

if [ -z "${COURSE_PUBLICATION_PASSWORD}" ]; then
  echo "COURSE_PUBLICATION_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

if [ -z "${COURSE_RUNTIME_PASSWORD}" ]; then
  echo "COURSE_RUNTIME_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

psql -v ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --set=pub_password="$COURSE_PUBLICATION_PASSWORD" \
  --set=run_password="$COURSE_RUNTIME_PASSWORD" <<SQL
CREATE ROLE course_owner NOLOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE course_publication LOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'pub_password';
CREATE ROLE course_runtime LOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'run_password';

GRANT course_owner TO course_publication;

REVOKE CONNECT ON DATABASE ${POSTGRES_DB} FROM PUBLIC;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO course_publication;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO course_runtime;

CREATE SCHEMA api AUTHORIZATION course_owner;
CREATE SCHEMA app AUTHORIZATION course_owner;
CREATE SCHEMA autocheck AUTHORIZATION course_owner;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO course_publication;
GRANT USAGE ON SCHEMA public TO course_runtime;
GRANT USAGE ON SCHEMA api, app, autocheck TO course_publication;
GRANT USAGE ON SCHEMA api, app, autocheck TO course_runtime;

CREATE TABLE api.schema_migrations (
    filename text PRIMARY KEY,
    checksum text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE api.schema_migrations OWNER TO course_owner;
GRANT SELECT, INSERT ON TABLE api.schema_migrations TO course_publication;
SQL
