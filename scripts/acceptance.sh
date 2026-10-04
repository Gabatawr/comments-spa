#!/usr/bin/env bash
# ===========================================================================
# Full acceptance pipeline for stage 2 (Middle+).
#
#   scripts/acceptance.sh            # clean build + tests + compose + smoke + k6
#   SKIP_K6=1 scripts/acceptance.sh  # skip the load profile
#   SKIP_COMPOSE=1 ...               # only clean build + tests
#
# Real commands only; every step prints its own PASS/FAIL. The whole run is
# teed to docs/qa/acceptance-logs/ so the final report can cite raw output.
# ===========================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

LOG_DIR="$ROOT/docs/qa/acceptance-logs"
mkdir -p "$LOG_DIR"

# Sandbox workaround: Buildx wants to write ~/.docker/buildx, which may be
# read-only. Keep its state inside the repo (.buildx/ is git-ignored).
export BUILDX_CONFIG="${BUILDX_CONFIG:-$ROOT/.buildx}"
mkdir -p "$BUILDX_CONFIG"

STAMP="$(date +%Y%m%d-%H%M%S)"
LOG="$LOG_DIR/acceptance-$STAMP.log"

FAILED=()

step() { echo; echo "=================================================================="; echo "== $*"; echo "=================================================================="; }

run_step() {
  local name="$1"; shift
  step "$name"
  if "$@"; then
    echo ">>> PASS: $name"
  else
    echo ">>> FAIL: $name"
    FAILED+=("$name")
  fi
}

resolved() { realpath -m "$1"; }
[ "$(resolved "$ROOT")" = "$ROOT" ] || { echo "unexpected root $ROOT"; exit 1; }

exec > >(tee -a "$LOG") 2>&1
echo "acceptance log: $LOG"
echo "root: $ROOT"
echo "date: $(date -Is)"
echo "host: $(uname -a)"
echo "cpus: $(nproc)  mem: $(free -h | awk '/Mem:/{print $2}')"
echo "dotnet: $(dotnet --version)  node: $(node --version 2>/dev/null || echo n/a)"
echo "docker: $(docker --version)  compose: $(docker compose version --short)"

# 1 -------------------------------------------------- clean room build + tests
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  run_step "clean-room restore+build+tests on PostgreSQL" \
    env CLEAN=1 bash "$ROOT/scripts/test.sh"
fi

# 2 -------------------------------------------------- compose from scratch
if [[ "${SKIP_COMPOSE:-0}" != "1" ]]; then
  run_step "docker compose down -v (project only)" \
    docker compose down -v --remove-orphans

  run_step "docker compose up --build -d" \
    docker compose up --build -d

  step "wait for all services to become healthy (timeout 480s)"
  deadline=$((SECONDS + 480))
  healthy=0
  while (( SECONDS < deadline )); do
    total=0; ok=0
    while IFS=$'\t' read -r svc state; do
      [[ -z "$svc" ]] && continue
      total=$((total + 1))
      [[ "$state" == "running" && "$svc" != "k6" ]] && ok=$((ok + 1))
    done < <(docker compose ps --format '{{.Service}}\t{{.State}}' 2>/dev/null)
    # A container counts as ready when running AND (healthy or no healthcheck).
    ready=0; seen=0
    for cid in $(docker compose ps -q 2>/dev/null); do
      seen=$((seen + 1))
      st="$(docker inspect --format '{{.State.Status}}' "$cid" 2>/dev/null)"
      hs="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$cid" 2>/dev/null)"
      if [[ "$st" == "running" && ( "$hs" == "healthy" || "$hs" == "none" ) ]]; then
        ready=$((ready + 1))
      fi
    done
    echo "  services running=$ok/$total  ready=$ready/$seen"
    if (( seen > 0 && ready == seen )); then healthy=1; break; fi
    sleep 5
  done

  if (( healthy == 1 )); then
    echo ">>> PASS: all services healthy"
    step "docker compose ps"
    docker compose ps
  else
    echo ">>> FAIL: services did not become healthy"
    docker compose ps
    docker compose logs --tail=120 api web postgres redis rabbitmq elasticsearch 2>/dev/null || true
    FAILED+=("compose healthy")
  fi

  # 3 ------------------------------------------------ HTTP checks
  step "HTTP checks (nginx :8080 and API :8081)"
  for url in \
      "http://localhost:8080/" \
      "http://localhost:8080/api/health" \
      "http://localhost:8081/api/health" \
      "http://localhost:8081/api/health/live" \
      "http://localhost:8081/api/comments" \
      "http://localhost:8081/api/captcha"; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$url" || echo 000)"
    echo "  $code  $url"
    [[ "$code" == "200" ]] || FAILED+=("HTTP 200 $url (got $code)")
  done
  echo "--- /api/health body (nginx) ---"
  curl -s --max-time 15 http://localhost:8080/api/health || true
  echo

  # 4 ------------------------------------------------ e2e smoke
  if [[ -f "$ROOT/tests/e2e/smoke.sh" ]]; then
    run_step "tests/e2e/smoke.sh http://localhost:8080" \
      bash "$ROOT/tests/e2e/smoke.sh" http://localhost:8080
  fi

  # 5 ------------------------------------------------ k6 load
  if [[ "${SKIP_K6:-0}" != "1" ]]; then
    K6_SCRIPT="${K6_SCRIPT:-/scripts/scenarios.js}"
    if [[ -d "$ROOT/perf/k6" ]] || [[ -d "$ROOT/perf" ]]; then
      step "docker compose --profile load run --rm k6 run $K6_SCRIPT"
      if docker compose --profile load run --rm k6 run "$K6_SCRIPT"; then
        echo ">>> PASS: k6 load run"
      else
        echo ">>> FAIL: k6 load run"
        FAILED+=("k6")
      fi
    else
      echo ">>> SKIP: perf scripts not present"
    fi
  fi
fi

# ---------------------------------------------------- summary
step "ACCEPTANCE SUMMARY"
if (( ${#FAILED[@]} == 0 )); then
  echo "ALL STEPS PASSED"
  # Record a completion marker with the timestamp for the report.
  {
    echo "completed: $(date -Is)"
    echo "log: $LOG"
    echo "result: PASS"
  } > "$LOG.result"
  exit 0
else
  echo "FAILED STEPS:"
  printf '  - %s\n' "${FAILED[@]}"
  {
    echo "completed: $(date -Is)"
    echo "log: $LOG"
    echo "result: FAIL"
    printf 'failed: %s\n' "${FAILED[@]}"
  } > "$LOG.result"
  exit 1
fi
