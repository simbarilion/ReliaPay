#!/usr/bin/env bash
# ReliaPay course-1: Compose + unit/integration/stress checks from a clone-equivalent tree.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required" >&2
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "docker compose v2 is required" >&2
  exit 1
fi

if [ ! -f .env ]; then
  echo "missing .env: copy .env.example and fill in local secrets" >&2
  exit 1
fi

ENV_FILE="$(mktemp)"
tr -d '\r' < .env > "$ENV_FILE"
set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a
rm -f "$ENV_FILE"

if [ -z "${COURSE_JWT_SIGNING_KEY:-}" ]; then
  echo "COURSE_JWT_SIGNING_KEY is missing in .env" >&2
  exit 1
fi

SDK_IMAGE="${COURSE_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c}"
GATEWAY_URL_HOST="${COURSE_GATEWAY_HOST_URL:-http://127.0.0.1:8080}"

wait_http() {
  local url="$1"
  local expect="$2"
  local attempts="${3:-90}"
  local code="000"
  local i
  for i in $(seq 1 "$attempts"); do
    code="$(curl -sS -o /dev/null -w '%{http_code}' "$url" 2>/dev/null || true)"
    if [ "$code" = "$expect" ]; then
      return 0
    fi
    sleep 2
  done
  echo "timeout waiting HTTP ${expect} from ${url} (last=${code})" >&2
  return 1
}

run_sdk_tests() {
  local project="$1"
  local network="${2:-}"
  local args=(
    run --rm
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1
    -e DOTNET_NOLOGO=1
    -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    -v "${ROOT}:/src"
    -w /src
  )
  if [ -n "$network" ]; then
    args+=(--network "$network")
    args+=(-e COURSE_GATEWAY_BASE_URL=http://gateway:8080)
    args+=(-e COURSE_JWT_SIGNING_KEY)
    args+=(-e COURSE_JWT_ISSUER="${COURSE_JWT_ISSUER:-moduledev-course}")
    args+=(-e COURSE_JWT_AUDIENCE="${COURSE_JWT_AUDIENCE:-moduledev-api}")
  fi
  docker "${args[@]}" "$SDK_IMAGE" \
    dotnet test "$project" \
      --nologo \
      --verbosity minimal \
      --artifacts-path /tmp/reliapay-test-artifacts
}

echo "==> compose up"
docker compose up -d --build

echo "==> wait gateway ready"
wait_http "${GATEWAY_URL_HOST}/health/ready" 200 120

echo "==> unit tests"
run_sdk_tests tests/ReliaPay.UnitTests/ReliaPay.UnitTests.csproj

echo "==> recreate api and gateway (same images, no volume wipe)"
docker compose up -d --force-recreate --no-deps api gateway
wait_http "${GATEWAY_URL_HOST}/health/ready" 200 90

echo "==> postgres stop/start without recreating api"
api_id_before="$(docker compose ps -q api)"
docker compose stop postgres
wait_http "${GATEWAY_URL_HOST}/health/ready" 503 60
docker compose start postgres
wait_http "${GATEWAY_URL_HOST}/health/ready" 200 90
api_id_after="$(docker compose ps -q api)"
if [ "$api_id_before" != "$api_id_after" ]; then
  echo "api container was recreated during postgres restart" >&2
  exit 1
fi

echo "==> publish check.ping without rebuilding images"
docker compose run --rm --no-deps -T \
  -v "${ROOT}/db/manifests:/manifests:ro" \
  cli action validate /manifests/check.ping.v1.json >/dev/null
docker compose run --rm --no-deps -T \
  -v "${ROOT}/db/manifests:/manifests:ro" \
  cli action publish /manifests/check.ping.v1.json >/dev/null
docker compose run --rm --no-deps -T \
  -v "${ROOT}/db/manifests:/manifests:ro" \
  cli action activate check.ping --version 1 >/dev/null

network="$(docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}}{{end}}' "$(docker compose ps -q gateway)")"
if [ -z "$network" ]; then
  echo "could not resolve compose network of gateway" >&2
  exit 1
fi

echo "==> integration tests"
run_sdk_tests tests/ReliaPay.IntegrationTests/ReliaPay.IntegrationTests.csproj "$network"

echo "==> stress tests"
run_sdk_tests tests/ReliaPay.StressTests/ReliaPay.StressTests.csproj "$network"

echo "check.sh: ok"
