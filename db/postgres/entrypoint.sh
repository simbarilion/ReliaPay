#!/bin/sh
# Официальный entrypoint остаётся источником initdb. Post-init repair ролей
# не запускается параллельно с init scripts: на пустом volume ждём durable
# marker от 01-roles.sh; на старом volume repair только после готовности.
set -eu

if [ "${1:-}" != "postgres" ]; then
  exec docker-entrypoint.sh "$@"
fi

PGDATA="${PGDATA:-/var/lib/postgresql/data}"
INIT_MARKER="${PGDATA}/.reliapay-init-complete"
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

tries=0
while [ "$tries" -lt 90 ]; do
  if pg_isready -U "${POSTGRES_USER:-postgres}" -d "${POSTGRES_DB:-course}" >/dev/null 2>&1; then
    break
  fi
  tries=$((tries + 1))
  sleep 1
done

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
  # Fresh volume: 01-roles.sh already created roles. Optional idempotent repair
  # only after the marker so ALTER/GRANT cannot race CREATE ROLE.
  /usr/local/bin/ensure-app-roles.sh
else
  # Existing volume: docker-entrypoint-initdb.d will not run; repair roles, then
  # write the durable marker so healthchecks and restarts see a finished bootstrap.
  /usr/local/bin/ensure-app-roles.sh
  if [ ! -f "$INIT_MARKER" ]; then
    touch "$INIT_MARKER"
  fi
fi

wait "$pg_pid"
