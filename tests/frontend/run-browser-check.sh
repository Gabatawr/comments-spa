#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Run the real-browser Angular check (tests/frontend/angular-check.mjs).
#
# Prefers a repo-local Playwright install (PLAYWRIGHT_BROWSERS_PATH=$PWD/.playwright,
# NPM_CONFIG_CACHE=$PWD/.npm-cache). Falls back to the official Playwright
# container when the local package/browser is unavailable.
#
# Usage: tests/frontend/run-browser-check.sh [BASE]
#   BASE default http://localhost:8080
# ---------------------------------------------------------------------------
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
BASE="${1:-${BASE:-http://localhost:8080}}"
export BASE
export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-$ROOT/.playwright}"
export NPM_CONFIG_CACHE="${NPM_CONFIG_CACHE:-$ROOT/.npm-cache}"
export QA_ARTIFACTS="${QA_ARTIFACTS:-$ROOT/tests/.runtime/ui-artifacts}"
mkdir -p "$QA_ARTIFACTS"

command -v node >/dev/null 2>&1 || { echo "node is required"; exit 2; }
cd "$HERE"

if node -e "require.resolve('playwright')" >/dev/null 2>&1; then
  echo "Using repo-local Playwright (browsers: $PLAYWRIGHT_BROWSERS_PATH)"
  exec node angular-check.mjs
fi

if command -v docker >/dev/null 2>&1; then
  echo "Local Playwright not found; using mcr.microsoft.com/playwright:v1.49.0-noble"
  exec docker run --rm --ipc=host --network host \
    -v "$HERE:/work" -w /work \
    -e BASE -e QA_ARTIFACTS=/work/.artifacts \
    mcr.microsoft.com/playwright:v1.49.0-noble node angular-check.mjs
fi

echo "Cannot run browser checks: no local playwright and no docker." >&2
exit 2
