#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# perf/run-load.sh — orchestrate one measured k6 run against the compose stack.
#
#   ./perf/run-load.sh --label 100k-full --count 100000 --scale 1
#
# Steps:
#   1. record environment (host CPU/RAM, git SHA, dataset size) -> results/env-<label>.txt
#   2. seed the dataset (idempotent; skip with --no-seed)
#   3. capture `docker stats --no-stream` samples during the run -> results/stats-<label>.log
#   4. run k6 through the compose `load` profile -> results/k6-<label>.log
#
# Only this project's containers are touched. No docker prune anywhere.
# ---------------------------------------------------------------------------
set -euo pipefail

LABEL="run-$(date +%Y%m%d-%H%M%S)"
COUNT=100000
ROOTS=""
SCALE=1
RAMP_S=15
STEADY_S=60
SEED=1
CLEAR=0
VIA="auto"
SEARCH_ENABLED="${SEARCH_ENABLED:-true}"
CREATE_ENABLED="${CREATE_ENABLED:-true}"
WS_ENABLED="${WS_ENABLED:-true}"
BASE_URL="${BASE_URL:-http://localhost:8081}"
PERF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS="$PERF_DIR/results"
COMPOSE=(docker compose -f "$PERF_DIR/../docker-compose.yml")
# IMPORTANT: `compose run` reconciles dependencies from the config it is given.
# Without the dev override it silently recreates api in Production (losing
# Features:Seed/DevCaptchaPeek mid-run). Always include the dev override for
# load runs unless PERF_PROD=1 explicitly asks for Production.
if [[ -f "$PERF_DIR/../infra/compose.dev.yml" && "${PERF_PROD:-0}" != "1" ]]; then
  COMPOSE+=(-f "$PERF_DIR/../infra/compose.dev.yml")
fi

usage() {
  sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --label) LABEL="$2"; shift 2 ;;
    --count) COUNT="$2"; shift 2 ;;
    --roots) ROOTS="$2"; shift 2 ;;
    --scale) SCALE="$2"; shift 2 ;;
    --ramp) RAMP_S="$2"; shift 2 ;;
    --steady) STEADY_S="$2"; shift 2 ;;
    --no-seed) SEED=0; shift ;;
    --clear) CLEAR=1; shift ;;
    --via) VIA="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "unknown arg: $1" >&2; usage 2 ;;
  esac
done

mkdir -p "$RESULTS"
chmod 777 "$RESULTS" 2>/dev/null || true

wait_health() {
  local i
  for i in $(seq 1 60); do
    if curl -fsS --max-time 3 "$BASE_URL/api/health" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

echo "[run] label=$LABEL count=$COUNT scale=$SCALE steady=${STEADY_S}s base=$BASE_URL"

if wait_health; then
  echo "[run] api healthy at $BASE_URL"
else
  echo "[run] WARNING: api not healthy at $BASE_URL yet; continuing (k6 setup will gate)" >&2
fi

# 1. environment / dataset bookkeeping
{
  echo "label:            $LABEL"
  echo "date_utc:         $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "git_sha:          $(cd "$PERF_DIR/.." && git rev-parse --short HEAD 2>/dev/null || echo n/a)"
  echo "host_nproc:       $(nproc 2>/dev/null || echo n/a)"
  echo "host_mem_total:   $(free -h 2>/dev/null | awk '/^Mem:/{print $2}' || echo n/a)"
  echo "target_rows:      $COUNT"
  echo "base_url:         $BASE_URL"
  echo "scenarios:        SEARCH_ENABLED=$SEARCH_ENABLED CREATE_ENABLED=$CREATE_ENABLED WS_ENABLED=$WS_ENABLED"
  echo "actual_total:     $(curl -fsS --max-time 10 "$BASE_URL/api/stats" 2>/dev/null || echo n/a)"
} | tee "$RESULTS/env-$LABEL.txt"

# 2. seed
if [[ "$SEED" -eq 1 ]]; then
  seed_args=(--count "$COUNT" --base-url "$BASE_URL" --via "$VIA")
  if [[ -n "$ROOTS" ]]; then seed_args+=(--roots "$ROOTS"); fi
  if [[ "$CLEAR" -eq 1 ]]; then seed_args+=(--clear); fi
  echo "[run] seeding: perf/seed.sh ${seed_args[*]}"
  bash "$PERF_DIR/seed.sh" "${seed_args[@]}"
else
  echo "[run] --no-seed: reusing existing dataset"
fi

# 3. docker stats sampler in background
( echo "t_seconds,container,cpu_perc,mem_usage,mem_perc,net_io,block_io,pids";
  start=$(date +%s)
  while true; do
    now=$(date +%s)
    docker stats --no-stream --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}},{{.NetIO}},{{.BlockIO}},{{.PIDs}}' 2>/dev/null \
      | grep -E 'comments-spa|comments-spa-' \
      | sed "s/^/$((now-start)),/" || true
    sleep 5
  done
) > "$RESULTS/stats-$LABEL.log" 2>&1 &
STATS_PID=$!
trap 'kill $STATS_PID 2>/dev/null || true' EXIT

# 4. k6 through compose load profile
echo "[run] k6: docker compose --profile load run --rm k6 run /scripts/scenarios.js"
set +e
"${COMPOSE[@]}" --profile load run --rm \
  -e "RUN_LABEL=$LABEL" \
  -e "SUMMARY_DIR=/results" \
  -e "SEARCH_ENABLED=$SEARCH_ENABLED" \
  -e "CREATE_ENABLED=$CREATE_ENABLED" \
  -e "WS_ENABLED=$WS_ENABLED" \
  -e "SCALE=$SCALE" \
  -e "RAMP_S=$RAMP_S" \
  -e "STEADY_S=$STEADY_S" \
  k6 run /scripts/scenarios.js 2>&1 | tee "$RESULTS/k6-$LABEL.log"
K6_EXIT=${PIPESTATUS[0]}
set -e

kill $STATS_PID 2>/dev/null || true
trap - EXIT

echo "[run] k6 exit=$K6_EXIT"
echo "[run] raw results:"
ls -la "$RESULTS"/*"$LABEL"* 2>/dev/null || true
exit "$K6_EXIT"
