#!/usr/bin/env bash
# Run the API locally in Development mode with the compose data services.
#
#   ./scripts/run-dev.sh [port]        # default 5080
#
# Starts PostgreSQL/Redis/RabbitMQ/Elasticsearch via compose (only if present),
# then runs Comments.Api against them. Cache/messaging/search use the local
# providers, so the API also works if a data service is missing.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PORT="${1:-5080}"
PROJECT="$ROOT/src/Backend/Comments.Api/Comments.Api.csproj"
if [[ ! -f "$PROJECT" ]]; then
    echo "ERROR: API project not found at $PROJECT" >&2
    exit 1
fi

if [[ -f "$ROOT/docker-compose.yml" ]]; then
    echo "==> starting compose data services (postgres/redis/rabbitmq/elasticsearch)"
    docker compose up -d postgres redis rabbitmq elasticsearch >/dev/null 2>&1 || true
fi

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="http://localhost:${PORT}"
export ConnectionStrings__Default="${ConnectionStrings__Default:-Host=localhost;Port=55432;Database=comments;Username=comments;Password=comments}"
export Redis__ConnectionString="${Redis__ConnectionString:-localhost:56379}"
export RabbitMq__ConnectionString="${RabbitMq__ConnectionString:-amqp://guest:guest@localhost:5672/}"
export Elastic__Url="${Elastic__Url:-http://localhost:59200}"
export Features__DevCaptchaPeek="${Features__DevCaptchaPeek:-true}"
export Features__Seed="${Features__Seed:-true}"

echo "==> API + Swagger: http://localhost:${PORT}/swagger (Development)"
echo "==> Health:        http://localhost:${PORT}/api/health"
echo "==> Angular dev UI is served separately by nginx (docker compose up web)."
echo

exec dotnet run --project "$PROJECT" --urls "$ASPNETCORE_URLS"
