#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Run the full xUnit suite against a real PostgreSQL instance.
#
# Usage:
#   scripts/test.sh                     # start postgres if needed, run all tests
#   CLEAN=1 scripts/test.sh             # also remove bin/obj first (clean room)
#   scripts/test.sh "FullyQualifiedName~CommentsTests"   # extra dotnet test args
#
# The suite creates a throw-away database per test factory
# (`comments_test_<guid>`), so the server only needs an admin connection.
# ---------------------------------------------------------------------------
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.dotnet-home}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$ROOT/.nuget/http-cache}"

PG_SERVICE="${PG_SERVICE:-postgres}"
PG_HOST_PORT="${PG_HOST_PORT:-55432}"
PG_USER="${PG_USER:-comments}"
PG_PASSWORD="${PG_PASSWORD:-comments}"
PG_ADMIN_DB="${PG_ADMIN_DB:-postgres}"

if [[ "${CLEAN:-0}" == "1" ]]; then
  echo ">> cleaning bin/obj"
  find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
fi

echo ">> ensuring PostgreSQL ($PG_SERVICE) is running"
docker compose up -d "$PG_SERVICE" >/dev/null

echo ">> waiting for PostgreSQL to become healthy"
cid=""
for _ in $(seq 1 90); do
  cid="$(docker compose ps -q "$PG_SERVICE" 2>/dev/null || true)"
  if [[ -n "$cid" ]]; then
    state="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$cid" 2>/dev/null || echo unknown)"
    if [[ "$state" == "healthy" || "$state" == "running" ]]; then
      break
    fi
  fi
  sleep 1
done

if [[ -z "$cid" ]]; then
  echo "!! could not find the $PG_SERVICE container; is docker-compose.yml present?" >&2
  exit 1
fi

export COMMENTS_TEST_POSTGRES="Host=localhost;Port=${PG_HOST_PORT};Username=${PG_USER};Password=${PG_PASSWORD};Database=${PG_ADMIN_DB};Include Error Detail=true"
echo ">> COMMENTS_TEST_POSTGRES=$COMMENTS_TEST_POSTGRES"

echo ">> dotnet test"
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj "$@"
