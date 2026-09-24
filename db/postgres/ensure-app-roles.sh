#!/bin/sh
# Досоздаёт роли/схемы на уже существующем томе и синхронизирует пароли.
# На пустом data directory вызывается только после durable marker от 01-roles.sh.
# Все роли создаются/проверяются до ALTER/GRANT.
set -e

if [ -z "${COURSE_PUBLISHER_PASSWORD}" ]; then
  echo "COURSE_PUBLISHER_PASSWORD is required for role bootstrap" >&2
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
  --username "${POSTGRES_USER:-postgres}" \
  --dbname "${POSTGRES_DB:-course}" \
  --set=pub_password="$COURSE_PUBLISHER_PASSWORD" \
  --set=run_password="$COURSE_RUNTIME_PASSWORD" \
  --set=worker_password="$COURSE_WORKER_PASSWORD" \
  --set=outbox_password="$COURSE_OUTBOX_PASSWORD" \
  --set=inbox_password="$COURSE_INBOX_PASSWORD" <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'course_owner') THEN
    CREATE ROLE course_owner NOLOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'course_publication') THEN
    CREATE ROLE course_publication LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'course_runtime') THEN
    CREATE ROLE course_runtime LOGIN NOSUPERUSER INHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'workflow_worker') THEN
    CREATE ROLE workflow_worker LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'outbox_dispatcher') THEN
    CREATE ROLE outbox_dispatcher LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'inbox_reconciler') THEN
    CREATE ROLE inbox_reconciler LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
END
\$\$;

ALTER ROLE course_publication PASSWORD :'pub_password';
ALTER ROLE course_runtime PASSWORD :'run_password';
ALTER ROLE workflow_worker PASSWORD :'worker_password';
ALTER ROLE outbox_dispatcher PASSWORD :'outbox_password';
ALTER ROLE inbox_reconciler PASSWORD :'inbox_password';

GRANT CONNECT ON DATABASE ${POSTGRES_DB:-course} TO workflow_worker;
GRANT CONNECT ON DATABASE ${POSTGRES_DB:-course} TO outbox_dispatcher;
GRANT CONNECT ON DATABASE ${POSTGRES_DB:-course} TO inbox_reconciler;
GRANT CREATE ON DATABASE ${POSTGRES_DB:-course} TO course_owner;

CREATE SCHEMA IF NOT EXISTS workflow AUTHORIZATION course_owner;
CREATE SCHEMA IF NOT EXISTS delivery AUTHORIZATION course_owner;

REVOKE ALL ON SCHEMA workflow FROM PUBLIC;
REVOKE ALL ON SCHEMA delivery FROM PUBLIC;
GRANT USAGE ON SCHEMA workflow TO course_publication;
GRANT USAGE ON SCHEMA workflow TO course_runtime;
GRANT USAGE ON SCHEMA workflow TO workflow_worker;
GRANT USAGE ON SCHEMA api TO workflow_worker;
GRANT USAGE ON SCHEMA delivery TO outbox_dispatcher;
GRANT USAGE ON SCHEMA delivery TO inbox_reconciler;
SQL
