#!/bin/sh
# Инициализация пустого тома: роли, схемы и журнал checksum-миграций.
# Образ PostgreSQL выполняет этот файл только при первом создании data directory.
set -e

if [ -z "${COURSE_PUBLISHER_PASSWORD}" ]; then
  echo "COURSE_PUBLISHER_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

if [ -z "${COURSE_MIGRATOR_PASSWORD}" ]; then
  echo "COURSE_MIGRATOR_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

if [ -z "${COURSE_RUNTIME_PASSWORD}" ]; then
  echo "COURSE_RUNTIME_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

if [ -z "${COURSE_WORKER_PASSWORD}" ]; then
  echo "COURSE_WORKER_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

if [ -z "${COURSE_OUTBOX_PASSWORD}" ]; then
  echo "COURSE_OUTBOX_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

if [ -z "${COURSE_INBOX_PASSWORD}" ]; then
  echo "COURSE_INBOX_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

psql -v ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --set=pub_password="$COURSE_PUBLISHER_PASSWORD" \
  --set=run_password="$COURSE_RUNTIME_PASSWORD" \
  --set=worker_password="$COURSE_WORKER_PASSWORD" \
  --set=outbox_password="$COURSE_OUTBOX_PASSWORD" \
  --set=inbox_password="$COURSE_INBOX_PASSWORD" <<SQL
CREATE ROLE course_owner NOLOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE course_publication LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'pub_password';
CREATE ROLE course_runtime LOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'run_password';
CREATE ROLE workflow_worker LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'worker_password';
CREATE ROLE outbox_dispatcher LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'outbox_password';
CREATE ROLE inbox_reconciler LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'inbox_password';

GRANT course_owner TO course_publication WITH INHERIT FALSE;

REVOKE CONNECT ON DATABASE ${POSTGRES_DB} FROM PUBLIC;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO course_publication;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO course_runtime;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO workflow_worker;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO outbox_dispatcher;
GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO inbox_reconciler;
GRANT CREATE ON DATABASE ${POSTGRES_DB} TO course_owner;

CREATE SCHEMA api AUTHORIZATION course_owner;
CREATE SCHEMA app AUTHORIZATION course_owner;
CREATE SCHEMA autocheck AUTHORIZATION course_owner;
CREATE SCHEMA workflow AUTHORIZATION course_owner;
CREATE SCHEMA delivery AUTHORIZATION course_owner;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA workflow FROM PUBLIC;
REVOKE ALL ON SCHEMA delivery FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO course_publication;
GRANT USAGE ON SCHEMA public TO course_runtime;
GRANT USAGE ON SCHEMA api, app, autocheck TO course_publication;
GRANT USAGE ON SCHEMA api, app, autocheck TO course_runtime;
GRANT USAGE ON SCHEMA workflow TO course_publication;
GRANT USAGE ON SCHEMA workflow TO course_runtime;
GRANT USAGE ON SCHEMA api, workflow TO workflow_worker;
GRANT USAGE ON SCHEMA delivery TO outbox_dispatcher;
GRANT USAGE ON SCHEMA delivery TO inbox_reconciler;

CREATE TABLE api.schema_migrations (
    filename text PRIMARY KEY,
    checksum text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE api.schema_migrations OWNER TO course_owner;
GRANT SELECT, INSERT ON TABLE api.schema_migrations TO course_publication;
SQL

# Durable marker: custom entrypoint waits for this before post-init role repair,
# and Compose healthcheck treats bootstrap as finished only after this file exists.
PGDATA="${PGDATA:-/var/lib/postgresql/data}"
touch "${PGDATA}/.reliapay-init-complete"
