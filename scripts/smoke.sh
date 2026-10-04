#!/usr/bin/env bash
# Quick smoke check of a running instance.
# Delegates to the full QA suite (tests/e2e/smoke.sh) when present.
# Usage: ./scripts/smoke.sh [base-url]   (default http://localhost:5080)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE="${1:-http://localhost:5080}"

if [[ -x "$ROOT/tests/e2e/smoke.sh" ]]; then
    exec "$ROOT/tests/e2e/smoke.sh" "$BASE"
fi

echo "==> Basic smoke against $BASE"
fail=0
check() {
    local name="$1" url="$2" expect="$3"
    code="$(curl -s -o /tmp/smoke_body.$$ -w '%{http_code}' "$url" || echo 000)"
    if [[ "$code" == "$expect" ]]; then
        echo "PASS  $name ($code)"
    else
        echo "FAIL  $name (got $code, want $expect)"
        fail=1
    fi
}

check "health"   "$BASE/api/health"   200
check "captcha"  "$BASE/api/captcha"  200
check "comments" "$BASE/api/comments" 200
check "spa"      "$BASE/"             200

if grep -qi 'ok' /tmp/smoke_body.$$ 2>/dev/null; then :; fi
rm -f /tmp/smoke_body.$$

if [[ "$fail" -eq 0 ]]; then
    echo "==> SMOKE: PASS"
else
    echo "==> SMOKE: FAIL"
    exit 1
fi
