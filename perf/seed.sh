#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# perf/seed.sh — fill the comments DB with realistic data for k6.
#
# Paths:
#   1. API  (preferred): POST /api/dev/seed  (Development/Load, Features:Seed=true)
#   2. DB   (fallback) : docker exec -i <project-postgres> psql <<SQL  (set-based
#      generate_series insert — fast for 1M rows, no row streaming from host)
#
# Idempotent: if the current row count already satisfies --count and --clear was
# not requested, it exits 0 without inserting anything. Re-run with --clear to
# reset. The DB path appends only up to the requested total; the API path is
# all-or-nothing (only skipped when the target is already satisfied).
#
# Examples:
#   perf/seed.sh --count 10000
#   perf/seed.sh --count 100000 --roots 10000 --clear
#   perf/seed.sh --count 1000000 --roots 100000 --via db
#
# Env overrides: BASE_URL, PG_CONTAINER, PGDATABASE, PGUSER
# ---------------------------------------------------------------------------
set -euo pipefail

COUNT=100000
ROOTS=""
DEPTH=3
BATCH=50000
CLEAR=0
VIA="auto"
BASE_URL="${BASE_URL:-http://localhost:8081}"
PG_CONTAINER="${PG_CONTAINER:-}"
PGDATABASE="${PGDATABASE:-comments}"
PGUSER="${PGUSER:-comments}"
PROJECT="${COMPOSE_PROJECT_NAME:-comments-spa}"

usage() {
  sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --count) COUNT="$2"; shift 2 ;;
    --roots) ROOTS="$2"; shift 2 ;;
    --depth) DEPTH="$2"; shift 2 ;;
    --batch) BATCH="$2"; shift 2 ;;
    --clear) CLEAR=1; shift ;;
    --via) VIA="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
    --container) PG_CONTAINER="$2"; shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "unknown arg: $1" >&2; usage 2 ;;
  esac
done

for v in COUNT ROOTS DEPTH BATCH; do
  val="${!v}"
  if [[ -z "$val" ]]; then continue; fi
  [[ "$val" =~ ^[0-9]+$ ]] || { echo "error: $v must be a positive integer, got '$val'" >&2; exit 2; }
done
if [[ -z "$ROOTS" ]]; then ROOTS=$(( COUNT / 10 )); fi
if [[ "$ROOTS" -lt 1 ]]; then ROOTS=1; fi
if [[ "$ROOTS" -gt "$COUNT" ]]; then ROOTS=$COUNT; fi

log() { printf '[seed] %s\n' "$*" >&2; }

# --- helpers ---------------------------------------------------------------
api_alive() {
  curl -fsS --max-time 5 "$BASE_URL/api/health" >/dev/null 2>&1
}

api_count() {
  curl -fsS --max-time 10 "$BASE_URL/api/stats" 2>/dev/null \
    | sed -n 's/.*"totalComments"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' | head -1
}

find_pg_container() {
  if [[ -n "$PG_CONTAINER" ]]; then echo "$PG_CONTAINER"; return; fi
  docker ps --format '{{.Names}}' 2>/dev/null \
    | grep -iE 'postgres' | grep -iE "${PROJECT}|comments|spa" | head -1
}

psql_exec() {
  # stdin -> psql inside the project's postgres container
  docker exec -i "$1" psql -v ON_ERROR_STOP=1 -U "$PGUSER" -d "$PGDATABASE" -q
}

db_count() {
  docker exec "$1" psql -tAq -U "$PGUSER" -d "$PGDATABASE" -c \
    'SELECT count(*) FROM comments;' 2>/dev/null | tr -d '[:space:]'
}

# --- path 1: API seed -------------------------------------------------------
seed_via_api() {
  local current
  current="$(api_count || true)"
  if [[ -z "$current" ]]; then current=0; fi
  log "api path: current totalComments=${current}, target=${COUNT}"

  if [[ "$CLEAR" -eq 0 && "$current" -ge "$COUNT" ]]; then
    log "api path: already satisfied (${current} >= ${COUNT}); nothing to do (use --clear to reset)"
    echo "$current"
    return 0
  fi

  local body
  body=$(printf '{"count":%s,"roots":%s,"depth":%s,"batchSize":%s,"clear":%s}' \
    "$COUNT" "$ROOTS" "$DEPTH" "$BATCH" "$([[ $CLEAR -eq 1 ]] && echo true || echo false)")

  log "api path: POST $BASE_URL/api/dev/seed $body"
  local resp http_code
  # 1M via the API seed takes ~9-10 min on this host; keep a generous timeout.
  resp="$(curl -sS --max-time 1800 -w '\n%{http_code}' \
    -X POST "$BASE_URL/api/dev/seed" \
    -H 'Content-Type: application/json' \
    --data-binary "$body" 2>&1)" || true
  http_code="$(printf '%s' "$resp" | tail -1)"
  resp="$(printf '%s' "$resp" | sed '$d')"

  if [[ "$http_code" != "200" ]]; then
    log "api path FAILED: HTTP ${http_code}: ${resp}"
    return 1
  fi
  log "api path response: ${resp}"

  current="$(api_count || echo 0)"
  log "api path: totalComments now ${current}"
  [[ -n "$current" && "$current" -ge "$COUNT" ]]
}

# --- path 2: direct DB seed -------------------------------------------------
seed_via_db() {
  local pg
  pg="$(find_pg_container)"
  [[ -n "$pg" ]] || { log "db path: no project postgres container found (set --container)"; return 1; }
  log "db path: container=$pg db=$PGDATABASE user=$PGUSER"

  local current
  current="$(db_count "$pg" || true)"
  [[ -z "$current" ]] && current=0
  log "db path: current=${current}, target=${COUNT}"

  if [[ "$CLEAR" -eq 1 ]]; then
    current=0
  elif [[ "$current" -ge "$COUNT" ]]; then
    log "db path: already satisfied (${current} >= ${COUNT}); nothing to do (use --clear to reset)"
    echo "$current"
    return 0
  fi

  local start=$(( current + 1 ))
  local end=$COUNT
  log "db path: inserting rows ${start}..${end} (${ROOTS} roots, flat-1 reply depth)"

  {
    if [[ "$CLEAR" -eq 1 ]]; then
      echo 'TRUNCATE comments, attachments RESTART IDENTITY CASCADE;'
    fi
    cat <<SQL
INSERT INTO comments
  (parent_id, user_name, email, home_page, text_html, text_plain, created_at, client_ip, user_agent)
SELECT
  CASE WHEN i <= ${ROOTS} THEN NULL
       ELSE 1 + ((i - ${ROOTS} - 1) % ${ROOTS}) END,
  'user' || (i % 50000),
  'user' || (i % 50000) || '@example.com',
  CASE WHEN i % 7 = 0 THEN 'https://example.com/u/' || (i % 50000) ELSE NULL END,
  '<strong>seed</strong> <i>' || i || '</i> <code>x</code>',
  'seed comment ' || i || ' ' ||
    (ARRAY['lorem','ipsum','dolor','sit','amet','alpha','beta','gamma','hello','world'])[1 + (i % 10)],
  now() - ((i % 2592000) * interval '1 second'),
  '10.' || (i % 256) || '.' || ((i / 256) % 256) || '.' || ((i / 65536) % 256),
  'seed-agent/' || (i % 100)
FROM generate_series(${start}, ${end}) AS i;
-- Advance the identity sequence only if it is BEHIND max(id). Never rewind:
-- setval(max(id)) while another writer holds a reserved id would make the
-- sequence re-issue that id and cause a PK collision (observed in practice).
DO \$\$
DECLARE
  seq text := pg_get_serial_sequence('comments','id');
  cur bigint;
  mx  bigint;
BEGIN
  IF seq IS NULL THEN RETURN; END IF;
  EXECUTE 'SELECT last_value FROM ' || seq INTO cur;
  SELECT max(id) INTO mx FROM comments;
  IF mx IS NOT NULL AND mx > COALESCE(cur, 0) THEN
    PERFORM setval(seq, mx, true);
  END IF;
END \$\$;
SQL
  } | psql_exec "$pg"

  current="$(db_count "$pg" || echo 0)"
  log "db path: totalComments now ${current}"
  [[ -n "$current" && "$current" -ge "$COUNT" ]]
}

# --- main -------------------------------------------------------------------
case "$VIA" in
  api)
    seed_via_api
    ;;
  db)
    seed_via_db
    ;;
  auto)
    if api_alive && seed_via_api; then
      :
    else
      log "api path unavailable/failed -> falling back to db path"
      seed_via_db
    fi
    ;;
  *) echo "error: --via must be api|db|auto" >&2; exit 2 ;;
esac
