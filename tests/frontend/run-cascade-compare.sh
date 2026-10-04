#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# cascade-compare.sh — deterministic cascade screenshot vs the reference
# docs/task/page1-X10.png, backend not required (all /api/** are stubbed).
#
# Builds nothing: it serves the already-built Angular app from
# src/Frontend/dist/comments-spa/browser. Run `cd src/Frontend && npm run build`
# first when the sources changed.
#
# Usage: tests/frontend/run-cascade-compare.sh
# Artefacts: docs/qa/design-v2/{cascade,side-by-side,diff,metrics}-<ts>.*
# ---------------------------------------------------------------------------
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-$ROOT/.playwright}"
export NPM_CONFIG_CACHE="${NPM_CONFIG_CACHE:-$ROOT/.npm-cache}"
export OUT_DIR="${OUT_DIR:-$ROOT/docs/qa/design-v2}"
cd "$HERE"
exec node cascade-compare.mjs "$@"
