#!/bin/sh
# Wrap the official entrypoint so post-init role repair never races initdb.
# Fresh volume: 01-roles.sh creates the full foundation and writes the durable
# marker; do NOT run ensure-app-roles during the temporary→final postmaster
# transition (psql would hit "database system is shutting down").
# Existing volume: init scripts do not re-run; repair passwords/grants, or
# fail-fast if the data directory looks interrupted/incomplete.
set -eu

if [ "${1:-}" != "postgres" ]; then
  exec docker-entrypoint.sh "$@"
fi

PGDATA="${PGDATA:-/var/lib/postgresql/data}"
INIT_MARKER="${PGDATA}/.reliapay-init-complete"
POSTGRES_USER_NAME="${POSTGRES_USER:-postgres}"
POSTGRES_DB_NAME="${POSTGRES_DB:-course}"
was_empty=0
if [ ! -s "${PGDATA}/PG_VERSION" ]; then
  was_empty=1
fi

docker-entrypoint.sh postgres &
pg_pid=$!

term() {
  kill -TERM "$pg_pid" 2>/dev/null || true
  wait "$pg_pid" || true
  exit 0
}

trap term TERM INT

wait_ready() {
  tries=0
  while [ "$tries" -lt 90 ]; do
    if pg_isready -U "$POSTGRES_USER_NAME" -d "$POSTGRES_DB_NAME" >/dev/null 2>&1; then
      return 0
    fi
    tries=$((tries + 1))
    sleep 1
  done
  return 1
}

foundation_ok() {
  # True when the schema foundation from 01-roles.sh is present.
  psql -v ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER_NAME" \
    --dbname "$POSTGRES_DB_NAME" \
    -Atc "SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'api'" 2>/dev/null | grep -qx 1 \
  && psql -v ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER_NAME" \
    --dbname "$POSTGRES_DB_NAME" \
    -Atc "SELECT 1 FROM information_schema.tables WHERE table_schema = 'api' AND table_name = 'schema_migrations'" 2>/dev/null | grep -qx 1
}

if ! wait_ready; then
  echo "reliapay: timed out waiting for PostgreSQL readiness" >&2
  kill -TERM "$pg_pid" 2>/dev/null || true
  wait "$pg_pid" || true
  exit 1
fi

if [ "$was_empty" -eq 1 ]; then
  # Init scripts still may be running while the temporary server is ready.
  tries=0
  while [ "$tries" -lt 90 ]; do
    if [ -f "$INIT_MARKER" ]; then
      break
    fi
    tries=$((tries + 1))
    sleep 1
  done
  if [ ! -f "$INIT_MARKER" ]; then
    echo "reliapay: timed out waiting for init marker $INIT_MARKER" >&2
    kill -TERM "$pg_pid" 2>/dev/null || true
    wait "$pg_pid" || true
    exit 1
  fi

  # Fresh volume: skip ensure-app-roles.sh. Roles and schemas were created by
  # 01-roles.sh. Running repair here races temporary server shutdown.
  # Wait for temporary→final transition: either observe not-ready then ready,
  # or stable readiness if the brief down window was missed.
  saw_down=0
  consecutive_ready=0
  tries=0
  while [ "$tries" -lt 120 ]; do
    if pg_isready -U "$POSTGRES_USER_NAME" -d "$POSTGRES_DB_NAME" >/dev/null 2>&1; then
      if [ "$saw_down" -eq 1 ]; then
        break
      fi
      consecutive_ready=$((consecutive_ready + 1))
      if [ "$consecutive_ready" -ge 8 ]; then
        break
      fi
    else
      saw_down=1
      consecutive_ready=0
    fi
    tries=$((tries + 1))
    sleep 1
  done
  if ! pg_isready -U "$POSTGRES_USER_NAME" -d "$POSTGRES_DB_NAME" >/dev/null 2>&1; then
    echo "reliapay: timed out waiting for final PostgreSQL postmaster after init" >&2
    kill -TERM "$pg_pid" 2>/dev/null || true
    wait "$pg_pid" || true
    exit 1
  fi
else
  # Existing volume: docker-entrypoint-initdb.d will not run.
  if [ ! -f "$INIT_MARKER" ]; then
    if ! foundation_ok; then
      echo "reliapay: incomplete data directory (missing $INIT_MARKER and schema foundation)." >&2
      echo "reliapay: remove ONLY this candidate volume and recreate (do not reuse a partial contour)." >&2
      echo "reliapay: example: docker compose down && docker volume rm <project>_course_data && docker compose up -d --build" >&2
      kill -TERM "$pg_pid" 2>/dev/null || true
      wait "$pg_pid" || true
      exit 1
    fi
  fi
  /usr/local/bin/ensure-app-roles.sh
  if [ ! -f "$INIT_MARKER" ]; then
    touch "$INIT_MARKER"
  fi
fi

wait "$pg_pid"
