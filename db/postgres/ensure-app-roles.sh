#!/bin/sh
# Досоздаёт роль workflow_worker и схему workflow на уже существующем томе.
# На пустом data directory 01-roles.sh уже создал те же объекты; команды идемпотентны.
set -e

if [ -z "${COURSE_WORKER_PASSWORD}" ]; then
  echo "COURSE_WORKER_PASSWORD is required for role bootstrap" >&2
  exit 1
fi

psql -v ON_ERROR_STOP=1 \
  --username "${POSTGRES_USER:-postgres}" \
  --dbname "${POSTGRES_DB:-course}" \
  --set=worker_password="$COURSE_WORKER_PASSWORD" <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'workflow_worker') THEN
    CREATE ROLE workflow_worker LOGIN NOSUPERUSER NOINHERIT NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
END
\$\$;

ALTER ROLE workflow_worker PASSWORD :'worker_password';

GRANT CONNECT ON DATABASE ${POSTGRES_DB:-course} TO workflow_worker;
GRANT CREATE ON DATABASE ${POSTGRES_DB:-course} TO course_owner;

CREATE SCHEMA IF NOT EXISTS workflow AUTHORIZATION course_owner;

REVOKE ALL ON SCHEMA workflow FROM PUBLIC;
GRANT USAGE ON SCHEMA workflow TO course_publication;
GRANT USAGE ON SCHEMA workflow TO course_runtime;
GRANT USAGE ON SCHEMA workflow TO workflow_worker;
GRANT USAGE ON SCHEMA api TO workflow_worker;
SQL
