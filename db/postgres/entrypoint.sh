#!/bin/sh
# Официальный entrypoint остаётся источником initdb. После готовности postgres
# досоздаём роль worker на томе недели 1, где docker-entrypoint-initdb.d уже не выполняется.
set -eu

if [ "${1:-}" != "postgres" ]; then
  exec docker-entrypoint.sh "$@"
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
    /usr/local/bin/ensure-app-roles.sh
    break
  fi
  tries=$((tries + 1))
  sleep 1
done

wait "$pg_pid"
