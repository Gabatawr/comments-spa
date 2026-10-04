#!/usr/bin/env bash
# Run the API + SPA locally in Development mode (CAPTCHA peek enabled).
# Usage: ./scripts/run-dev.sh [port]   (default 5080)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${1:-5080}"

PROJECT="$ROOT/src/Backend/CommentsApi/CommentsApi.csproj"
if [[ ! -f "$PROJECT" ]]; then
    echo "ERROR: backend project not found at $PROJECT" >&2
    exit 1
fi

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="http://localhost:${PORT}"

echo "==> SPA:      http://localhost:${PORT}/"
echo "==> API docs: http://localhost:${PORT}/swagger (Development)"
echo "==> Health:   http://localhost:${PORT}/api/health"
echo

exec dotnet run --project "$PROJECT" --urls "$ASPNETCORE_URLS"
