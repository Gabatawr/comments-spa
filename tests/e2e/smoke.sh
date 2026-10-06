#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# End-to-end smoke test for the «Комментарии» stack, v2 (Middle+) contract.
#
# Contract under test: docs/API-v2.md (FROZEN).
#
# Usage:
#   tests/e2e/smoke.sh [BASE_URL]
#
# Environment:
#   BASE            single origin through nginx     (default http://localhost:8080)
#   API_BASE        direct API port for forwarded-header (default http://localhost:8081)
#   PROD_BASE       Production instance for dev-only 404 checks (auto when peek off)
#   WS_URL          override WebSocket URL          (default ws://<BASE host>/ws)
#   CAPTCHA_PEEK    auto|dev|redis                  (default auto)
#   REDIS_CONTAINER redis container name            (default comments-spa-redis)
#
# CAPTCHA solving: in Development with Features:DevCaptchaPeek=true the code comes
# from GET /api/dev/captcha/{id}. Against the default Production compose stack
# (dev peek disabled) the script falls back to reading the one-time code from the
# Redis CAPTCHA store via `docker exec <redis> redis-cli --raw GET captcha:<id>`,
# so the full e2e flow still runs without weakening production hardening.
#
# Requires: curl, python3 (+ Pillow for the image checks), node >= 22 (WebSocket probe).
#
# Prints a PASS/FAIL/SKIP summary and exits non-zero if any check fails.
# ---------------------------------------------------------------------------
set -u

BASE="${1:-${BASE:-http://localhost:8080}}"
BASE="${BASE%/}"
API_BASE="${API_BASE:-http://localhost:8081}"
API_BASE="${API_BASE%/}"
PROD_BASE="${PROD_BASE:-}"
PROD_BASE="${PROD_BASE%/}"
CAPTCHA_PEEK="${CAPTCHA_PEEK:-auto}"
REDIS_CONTAINER="${REDIS_CONTAINER:-comments-spa-redis}"

PASS=0
FAIL=0
SKIP=0
declare -a FAILED=()
declare -a SKIPPED=()

TMPDIR_SMOKE="$(mktemp -d "${TMPDIR:-/tmp}/comments-smoke-v2.XXXXXX")"
cleanup() { rm -rf "$TMPDIR_SMOKE"; }
trap cleanup EXIT

pass() { PASS=$((PASS + 1)); printf '  PASS  %s\n' "$1"; }
skip() { SKIP=$((SKIP + 1)); SKIPPED+=("$1"); printf '  SKIP  %s\n' "$1"; }
fail() { FAIL=$((FAIL + 1)); FAILED+=("$1 :: $2"); printf '  FAIL  %s :: %s\n' "$1" "$2"; }
section() { printf '\n== %s ==\n' "$1"; }

STATUS=""
BODY=""
HEADERS=""

# _jparse <python-expr> : JSON on stdin, object bound to `d`; empty string on error
_jparse() {
  python3 -c '
import json,sys
try: d = json.load(sys.stdin)
except Exception: print(""); sys.exit(0)
try: print(eval(sys.argv[1], {"d": d}))
except Exception: print("")' "$1" 2>/dev/null
}
# jget <python-expr>       -> evaluates against the current response body ($BODY)
jget() { printf '%s' "$BODY" | _jparse "$1"; }
# jb <python-expr> <body>  -> evaluates against an explicit JSON body
jb() { printf '%s' "$2" | _jparse "$1"; }

# req <METHOD> <URL> [curl args...] -> sets STATUS, BODY, HEADERS
req() {
  local method="$1" url="$2"; shift 2
  local out
  out="$(curl -sS --max-time 30 -D "$TMPDIR_SMOKE/headers" -w $'\n%{http_code}' -X "$method" "$@" "$url" 2>"$TMPDIR_SMOKE/curlerr")" || true
  STATUS="${out##*$'\n'}"
  BODY="${out%$'\n'*}"
  HEADERS="$TMPDIR_SMOKE/headers"
}

header_value() { awk -v h="$1" 'BEGIN{IGNORECASE=1} index($0, h":")==1 {v=substr($0,index($0,":")+1); gsub(/^[ \t\r\n]+|[ \t\r\n]+$/,"",v); print v}' "$HEADERS" 2>/dev/null; }

urlenc() { python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"; }

expect_status() { # <name> <want>
  if [ "$STATUS" = "$2" ]; then pass "$1"; else fail "$1" "expected HTTP $2, got $STATUS body=${BODY:0:240}"; fi
}

# --- CAPTCHA solving: dev peek endpoint, else the Redis one-time store --------
captcha_code_from_redis() { # <captchaId> -> 6-char code on stdout
  command -v docker >/dev/null 2>&1 || return 1
  local raw
  raw="$(docker exec "$REDIS_CONTAINER" redis-cli --raw GET "captcha:$1" 2>/dev/null)" || return 1
  [ -n "$raw" ] || return 1
  printf '%s' "$raw" | python3 -c '
import json,re,sys
raw=sys.stdin.read().strip()
cands=[]
def walk(x):
    if isinstance(x,str): cands.append(x)
    elif isinstance(x,dict):
        for v in x.values(): walk(v)
    elif isinstance(x,list):
        for v in x: walk(v)
try: walk(json.loads(raw))
except Exception: cands.append(raw)
six=[c.strip() for c in cands if re.fullmatch(r"[A-Za-z0-9]{6}", c.strip())]
if six:
    print(six[0]); raise SystemExit
for c in cands:
    c=c.strip()
    if re.fullmatch(r"[A-Za-z0-9]{4,12}", c):
        print(c); break
' 2>/dev/null
}

PEEK_MODE=""
detect_peek_mode() {
  case "$CAPTCHA_PEEK" in
    dev|redis) PEEK_MODE="$CAPTCHA_PEEK"; return ;;
  esac
  PEEK_MODE="redis"
  req GET "$BASE/api/captcha"
  if [ "$STATUS" = "200" ]; then
    local id; id="$(jb "d.get('captchaId','')" "$BODY")"
    req GET "$BASE/api/dev/captcha/$id"
    [ "$STATUS" = "200" ] && PEEK_MODE="dev"
  fi
}

# solve_captcha -> CAPTCHA_ID, CAPTCHA_CODE
solve_captcha() {
  req GET "$BASE/api/captcha"
  if [ "$STATUS" != "200" ]; then
    fail "captcha issue" "HTTP $STATUS body=${BODY:0:200}"
    return 1
  fi
  CAPTCHA_ID="$(jb "d['captchaId']" "$BODY")"
  if [ "$PEEK_MODE" = "dev" ]; then
    req GET "$BASE/api/dev/captcha/$CAPTCHA_ID"
    if [ "$STATUS" = "200" ]; then
      CAPTCHA_CODE="$(jb "d['code']" "$BODY")"
      [ -n "$CAPTCHA_CODE" ] && return 0
    fi
  fi
  CAPTCHA_CODE="$(captcha_code_from_redis "$CAPTCHA_ID")"
  if [ -n "$CAPTCHA_CODE" ]; then return 0; fi
  fail "solve captcha" "peek=$PEEK_MODE: no dev peek and no code in Redis container '$REDIS_CONTAINER' (set CAPTCHA_PEEK=dev for a Development stack)"
  return 1
}

# create_comment <userName> <text> [extra -F args...]
create_comment() {
  local user="$1"; local text="$2"; shift 2
  solve_captcha || return 1
  req POST "$BASE/api/comments" \
    --form-string "userName=$user" \
    --form-string "email=${user,,}@example.com" \
    --form-string "text=$text" \
    --form-string "captchaId=$CAPTCHA_ID" \
    --form-string "captchaAnswer=$CAPTCHA_CODE" "$@"
}

# create_comment_at <baseUrl> <userName> <text> [extra curl args...]
# multipart create directly against a given base (used to inject proxy headers such as
# X-Forwarded-For / User-Agent that nginx would otherwise overwrite).
create_comment_at() {
  local abase="$1" user="$2" text="$3"; shift 3
  solve_captcha >/dev/null || return 1
  req POST "$abase/api/comments" \
    --form-string "userName=$user" \
    --form-string "email=${user,,}@example.com" \
    --form-string "text=$text" \
    --form-string "captchaId=$CAPTCHA_ID" \
    --form-string "captchaAnswer=$CAPTCHA_CODE" "$@"
}

# json_create <apiBase> <path> <userName> <text> [parentId] [extra curl args...]
json_create() {
  local abase="$1" apath="$2" user="$3" text="$4"; shift 4
  local parent="${1:-}"; [ $# -gt 0 ] && shift
  solve_captcha >/dev/null || return 1
  local payload
  payload="$(python3 -c 'import json,sys
u,t,pid,cid,ans = sys.argv[1],sys.argv[2],sys.argv[3],sys.argv[4],sys.argv[5]
print(json.dumps({"userName":u,"email":u.lower()+"@example.com","homePage":None,"text":t,"parentId":(int(pid) if pid else None),"captchaId":cid,"captchaAnswer":ans}))' \
    "$user" "$text" "$parent" "$CAPTCHA_ID" "$CAPTCHA_CODE")"
  req POST "$abase$apath" -H 'Content-Type: application/json' "$@" --data "$payload"
}

# ---------------------------------------------------------------- preconditions
command -v curl >/dev/null 2>&1 || { echo "curl is required"; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo "python3 is required"; exit 2; }
HAVE_PIL=0
python3 -c 'import PIL' >/dev/null 2>&1 && HAVE_PIL=1

printf 'Comments SPA smoke test v2 against %s (api %s)\n' "$BASE" "$API_BASE"
detect_peek_mode
printf 'CAPTCHA peek mode: %s%s\n' "$PEEK_MODE" "$([ "$PEEK_MODE" = redis ] && echo " (dev peek disabled -> reading Redis store '$REDIS_CONTAINER')")"
# When dev peek is unavailable the main stack behaves like Production (no dev
# endpoints), so use it for the Production 404 checks unless PROD_BASE is given.
if [ -z "$PROD_BASE" ] && [ "$PEEK_MODE" != "dev" ]; then
  PROD_BASE="$BASE"
  printf 'Production 404 checks will run against %s (override with PROD_BASE)\n' "$PROD_BASE"
fi

# ------------------------------------------------------------------ 1. health
section "health v2"
req GET "$BASE/api/health"
if [ "$STATUS" = "200" ] \
   && [ "$(jget "d.get('status')")" = "ok" ] \
   && [ "$(jget "d.get('database')")" = "ok" ]; then
  pass "GET /api/health -> status=ok database=ok"
  for key in cache queue websocket version; do
    [ "$(jget "'$key' in d")" = "True" ] && pass "health reports legacy '$key'" || fail "health legacy '$key'" "missing from $BODY"
  done
  for key in redis broker search storage; do
    if [ "$(jget "'$key' in d")" = "True" ]; then
      v="$(jget "d['$key']")"
      case "$v" in
        ok) pass "health v2 field $key=$v" ;;
        disabled) skip "health v2 field $key=disabled (feature off in this run)" ;;
        error) fail "health v2 field $key" "value=error" ;;
        *) fail "health v2 field $key" "unexpected value '$v'" ;;
      esac
    else
      fail "health v2 field $key" "missing from $BODY"
    fi
  done
  printf '%s' "$(jget "d.get('version')")" | grep -Eq '^2\.' \
    && pass "health version is 2.x ($(jget "d.get('version')"))" \
    || fail "health version" "got '$(jget "d.get('version')")'"
  # queue must be an object with counters per contract
  [ "$(jget "isinstance(d.get('queue'), dict) and 'pending' in d['queue'] and 'processed' in d['queue']")" = "True" ] \
    && pass "health queue carries pending/processed counters" \
    || fail "health queue shape" "$(jget "d.get('queue')")"
  [ "$(jget "isinstance(d.get('websocket'), dict) and 'clients' in d['websocket']")" = "True" ] \
    && pass "health websocket carries clients counter" \
    || fail "health websocket shape" "$(jget "d.get('websocket')")"
else
  fail "GET /api/health" "HTTP $STATUS body=${BODY:0:300}"
fi

req GET "$BASE/api/health/live"
[ "$STATUS" = "200" ] && [ "$(jget "d.get('status')")" = "ok" ] \
  && pass "GET /api/health/live -> 200 ok" || fail "GET /api/health/live" "HTTP $STATUS body=${BODY:0:200}"

req GET "$BASE/api/health/ready"
[ "$STATUS" = "200" ] && pass "GET /api/health/ready -> 200 (DB up)" || fail "GET /api/health/ready" "HTTP $STATUS"

# ----------------------------------------------------------------- 2. captcha
section "captcha"
req GET "$BASE/api/captcha"
if [ "$STATUS" = "200" ] \
   && printf '%s' "$(jget "d.get('image','')")" | grep -q '^data:image/png;base64,' \
   && [ "$(jget "d.get('expiresInSeconds')")" = "300" ] \
   && [ "$(jget "'code' not in d")" = "True" ]; then
  CAPTCHA_ID="$(jget "d['captchaId']")"
  if solve_captcha && [ "${#CAPTCHA_CODE}" = "6" ]; then
    pass "GET /api/captcha -> PNG data URL, TTL 300s, no code; dev peek returns 6 chars"
    if printf '%s' "$CAPTCHA_CODE" | grep -Eq '^[A-HJ-NP-Z2-9]{6}$'; then
      pass "captcha code uses allowed alphabet (no 0/O/1/I): $CAPTCHA_CODE"
    else
      fail "captcha alphabet" "code '$CAPTCHA_CODE' contains ambiguous chars"
    fi
  else
    fail "dev captcha code" "expected 6 chars, got '${CAPTCHA_CODE:-<none>}'"
  fi
else
  fail "GET /api/captcha" "HTTP $STATUS body=${BODY:0:200}"
fi

# one-time use + wrong answer
solve_captcha >/dev/null 2>&1
CAP_ID="$CAPTCHA_ID"; CAP_CODE="$CAPTCHA_CODE"
req POST "$BASE/api/comments" \
  --form-string "userName=SmkCap$RANDOM" --form-string "email=smkcap@example.com" \
  --form-string "text=captcha one-time" \
  --form-string "captchaId=$CAP_ID" --form-string "captchaAnswer=$CAP_CODE"
[ "$STATUS" = "201" ] && pass "CAPTCHA accepted once (201)" || fail "CAPTCHA first use" "HTTP $STATUS body=${BODY:0:200}"
req POST "$BASE/api/comments" \
  --form-string "userName=SmkCap$RANDOM" --form-string "email=smkcap@example.com" \
  --form-string "text=captcha replay" \
  --form-string "captchaId=$CAP_ID" --form-string "captchaAnswer=$CAP_CODE"
if [ "$STATUS" = "400" ] && [ "$(jget "'captcha' in d.get('errors',{})")" = "True" ]; then
  pass "CAPTCHA replay rejected (400 errors.captcha)"
else
  fail "CAPTCHA replay" "HTTP $STATUS body=${BODY:0:240}"
fi
req POST "$BASE/api/comments" \
  --form-string "userName=SmkCap$RANDOM" --form-string "email=smkcap@example.com" \
  --form-string "text=wrong captcha" \
  --form-string "captchaId=$(python3 -c 'import uuid;print(uuid.uuid4())')" --form-string "captchaAnswer=WRONG1"
if [ "$STATUS" = "400" ] && [ "$(jget "'captcha' in d.get('errors',{})")" = "True" ]; then
  pass "unknown CAPTCHA id rejected (400 errors.captcha)"
else
  fail "unknown CAPTCHA" "HTTP $STATUS body=${BODY:0:240}"
fi

# ------------------------------------------------------------------- 3. create
section "create root + client identity"
CREATE_USER="SmkBase$RANDOM"
create_comment "$CREATE_USER" "smoke <strong>comment</strong>"
if [ "$STATUS" = "201" ]; then
  CREATED_ID="$(jget "d['comment']['id']")"
  LOCATION="$(header_value Location)"
  [ "$(jget "d['comment']['userName']")" = "$CREATE_USER" ] \
    && pass "POST /api/comments -> 201 with correct userName (id=$CREATED_ID)" \
    || fail "POST /api/comments body" "userName mismatch: ${BODY:0:200}"
  echo "$LOCATION" | grep -q "/api/comments/$CREATED_ID" \
    && pass "201 Location header -> $LOCATION" || fail "201 Location" "got '$LOCATION'"
  [ "$(jget "d['comment']['userAgent']")" != "None" ] \
    && pass "client userAgent persisted" || fail "client userAgent" "null"
  [ "$(jget "d['comment']['clientIp']")" != "None" ] \
    && pass "client clientIp persisted ($(jget "d['comment']['clientIp']"))" || fail "client clientIp" "null"
else
  fail "POST /api/comments" "HTTP $STATUS body=${BODY:0:300}"
fi

# X-Forwarded-For -> first hop is stored as clientIp (direct to API)
XFF_USER="SmkXff$RANDOM"
create_comment_at "$API_BASE" "$XFF_USER" "xff check" -H "X-Forwarded-For: 203.0.113.7, 10.0.0.1"
if [ "$STATUS" = "201" ]; then
  CIP="$(jget "d['comment']['clientIp']")"
  [ "$CIP" = "203.0.113.7" ] \
    && pass "X-Forwarded-For first hop stored as clientIp ($CIP)" \
    || fail "X-Forwarded-For" "expected 203.0.113.7, got '$CIP'"
else
  fail "X-Forwarded-For create" "HTTP $STATUS body=${BODY:0:240}"
fi

# userAgent truncation to 512 (server rule §6)
LONG_UA="$(python3 -c 'print("U"*600)')"
create_comment_at "$API_BASE" "SmkUa$RANDOM" "ua check" -A "$LONG_UA"
if [ "$STATUS" = "201" ]; then
  UALEN="$(jget "len(d['comment']['userAgent'] or '')")"
  [ "$UALEN" -le 512 ] && pass "userAgent truncated to <=512 (len=$UALEN)" || fail "userAgent truncation" "len=$UALEN"
else
  fail "userAgent truncation create" "HTTP $STATUS"
fi

# ------------------------------------------------------------ 4. reply cascade
section "reply cascade (deep)"
CASCADE_ROOT_USER="SmkCas$RANDOM"
create_comment "$CASCADE_ROOT_USER" "cascade root"
CASCADE_ROOT=""
if [ "$STATUS" = "201" ]; then
  CASCADE_ROOT="$(jget "d['comment']['id']")"
  pass "cascade root created (id=$CASCADE_ROOT)"
  PARENT="$CASCADE_ROOT"
  CASCADE_OK=1
  for depth in 1 2 3 4 5; do
    create_comment "SmkCas${depth}$RANDOM" "cascade depth $depth" -F "parentId=$PARENT"
    if [ "$STATUS" = "201" ]; then
      PARENT="$(jget "d['comment']['id']")"
    else
      CASCADE_OK=0; fail "cascade depth $depth create" "HTTP $STATUS body=${BODY:0:200}"; break
    fi
  done
  [ "$CASCADE_OK" = "1" ] && pass "created reply chain of depth 5"
  req GET "$BASE/api/comments/$CASCADE_ROOT"
  if [ "$STATUS" = "200" ]; then
    # walk replies[0].replies[0]... 5 levels
    DEPTH_SEEN="$(printf '%s' "$BODY" | python3 -c '
import json,sys
d=json.load(sys.stdin)
node=d["comment"] if "comment" in d else d
n=0
while node and node.get("replies"):
    node=node["replies"][0]; n+=1
print(n)' 2>/dev/null)"
    [ "$DEPTH_SEEN" -ge 5 ] \
      && pass "GET /api/comments/{root} exposes full nested tree (depth=$DEPTH_SEEN)" \
      || fail "nested tree depth" "expected >=5, got $DEPTH_SEEN"
  else
    fail "GET cascade root" "HTTP $STATUS"
  fi
else
  fail "cascade root create" "HTTP $STATUS body=${BODY:0:240}"
fi

# root listing must NOT inline the full tree? contract says items are CommentDto with tree.
# (No assertion here; tree depth is verified above.)

# ---------------------------------------------------------------- 5. sorting
section "sorting (userName / email / createdAt, both directions)"
SORT_TAG="E2e$(date +%s)$RANDOM"
SORT_USERS=("${SORT_TAG}Alpha" "${SORT_TAG}Bravo" "${SORT_TAG}Charlie")
for u in "${SORT_USERS[@]}"; do
  create_comment "$u" "smoke <strong>sort</strong>"
  [ "$STATUS" = "201" ] || fail "create for sort ($u)" "HTTP $STATUS body=${BODY:0:160}"
  sleep 1.1
done

# Collect the tagged usernames in true sorted order, walking pages from the end where
# they are expected (asc -> from page 1, desc -> from the last page). Robust on a shared
# database that already holds many thousands of roots (e.g. perf's seed data).
collect_tagged_order() { # <field> <dir> <tag>
  local field="$1" dir="$2" tag="$3"
  local total p start end step out="" found
  req GET "$BASE/api/comments?pageSize=100&sortBy=$field&sortDir=$dir&page=1"
  [ "$STATUS" = "200" ] || return 1
  total="$(jget "d.get('totalPages')")"; total="${total:-1}"
  [ "$total" -lt 1 ] 2>/dev/null && total=1
  out="$(jget "','.join(str(i['userName']) for i in d['items'] if str(i['userName']).startswith('$tag'))")"
  found="$(printf '%s' "$out" | tr ',' '\n' | grep -c "^${tag}" 2>/dev/null || true)"
  if [ "${found:-0}" -lt 3 ]; then
    local cap=60 from_end=0
    # Our fixtures are the newest and their tag (E2e...) sorts low in lower() order.
    # createdAt: newest -> end for asc, start for desc. userName/email: low value -> start for asc, end for desc.
    if [ "$field" = "createdAt" ]; then
      [ "$dir" = "asc" ] && from_end=1 || from_end=0
    else
      [ "$dir" = "desc" ] && from_end=1 || from_end=0
    fi
    if [ "$from_end" = "1" ]; then
      start="$total"; end=$(( total - cap + 1 )); [ "$end" -lt 2 ] && end=2; step=-1
    else
      start=2; end="$cap"; [ "$end" -gt "$total" ] && end="$total"; step=1
    fi
    p="$start"
    while { [ "$step" -gt 0 ] && [ "$p" -le "$end" ]; } || { [ "$step" -lt 0 ] && [ "$p" -ge "$end" ]; }; do
      req GET "$BASE/api/comments?pageSize=100&sortBy=$field&sortDir=$dir&page=$p"
      out="${out:+$out,}$(jget "','.join(str(i['userName']) for i in d['items'] if str(i['userName']).startswith('$tag'))")"
      found="$(printf '%s' "$out" | tr ',' '\n' | grep -c "^${tag}" 2>/dev/null || true)"
      [ "${found:-0}" -ge 3 ] && break
      p=$(( p + step ))
    done
  fi
  printf '%s' "$out" | sed -e 's/,,*/,/g' -e 's/^,//' -e 's/,$//'
}

check_sort() { # <field> <dir> <expected-joined>
  local field="$1" dir="$2" expected="$3"
  local actual
  actual="$(collect_tagged_order "$field" "$dir" "$SORT_TAG")"
  if [ "$actual" = "$expected" ]; then
    pass "sortBy=$field sortDir=$dir -> $actual"
  else
    fail "sortBy=$field sortDir=$dir" "expected $expected, got '$actual'"
  fi
}
check_sort userName asc  "${SORT_TAG}Alpha,${SORT_TAG}Bravo,${SORT_TAG}Charlie"
check_sort userName desc "${SORT_TAG}Charlie,${SORT_TAG}Bravo,${SORT_TAG}Alpha"
check_sort email asc  "${SORT_TAG}Alpha,${SORT_TAG}Bravo,${SORT_TAG}Charlie"
check_sort email desc "${SORT_TAG}Charlie,${SORT_TAG}Bravo,${SORT_TAG}Alpha"
check_sort createdAt asc  "${SORT_TAG}Alpha,${SORT_TAG}Bravo,${SORT_TAG}Charlie"
check_sort createdAt desc "${SORT_TAG}Charlie,${SORT_TAG}Bravo,${SORT_TAG}Alpha"

# case-insensitive sort: create mixed-case users and verify lower() semantics
CASE_LOWER="${SORT_TAG}aaa$(python3 -c 'import random;print(random.randint(100,999))')"
CASE_UPPER="${SORT_TAG}ZZZ$(python3 -c 'import random;print(random.randint(100,999))')"
create_comment "$CASE_LOWER" "case asc lower" ; CASE1="$STATUS"
sleep 1.1
create_comment "$CASE_UPPER" "case asc upper" ; CASE2="$STATUS"
if [ "$CASE1" = "201" ] && [ "$CASE2" = "201" ]; then
  req GET "$BASE/api/comments?pageSize=100&sortBy=userName&sortDir=asc"
  ORDER="$(jget "'|'.join(str(i['userName']) for i in d['items'] if str(i['userName']).startswith('$SORT_TAG'))")"
  echo "$ORDER" | grep -q "aaa.*ZZZ" \
    && pass "userName sort is case-insensitive (lower())" \
    || fail "case-insensitive sort" "order=$ORDER"
fi

# ------------------------------------------------------------- 6. pagination
section "pagination + keyset"
req GET "$BASE/api/comments"
TOTAL="$(jget "d.get('totalItems')")"; TOTAL="${TOTAL:-0}"
PAGESIZE="$(jget "d.get('pageSize')")"; PAGESIZE="${PAGESIZE:-0}"
TOTALPAGES="$(jget "d.get('totalPages')")"; TOTALPAGES="${TOTALPAGES:-0}"
if [ "$PAGESIZE" = "25" ]; then pass "default pageSize = 25"; else fail "default pageSize" "got '$PAGESIZE' (body=${BODY:0:160})"; fi
if [ "$PAGESIZE" -gt 0 ] 2>/dev/null; then
  EXPECTED_PAGES=$(( (TOTAL + PAGESIZE - 1) / PAGESIZE ))
  [ "$TOTALPAGES" = "$EXPECTED_PAGES" ] \
    && pass "totalPages math ($TOTAL items -> $TOTALPAGES pages)" \
    || fail "totalPages math" "got $TOTALPAGES expected $EXPECTED_PAGES"
else
  fail "totalPages math" "pageSize not numeric ('$PAGESIZE')"
fi
OOR_PAGE=$(( TOTALPAGES + 1000 ))
req GET "$BASE/api/comments?page=$OOR_PAGE"
[ "$STATUS" = "200" ] && [ "$(jget "len(d.get('items',[]))")" = "0" ] \
  && pass "out-of-range page ($OOR_PAGE > $TOTALPAGES) -> 200 empty items" \
  || fail "out-of-range page" "HTTP $STATUS items=$(jget "len(d.get('items',[]))")"
req GET "$BASE/api/comments?page=2000000000"
if [ "$STATUS" = "200" ] && [ "$(jget "len(d.get('items',[]))")" = "0" ]; then
  pass "huge page (2000000000) -> 200 empty (no offset overflow)"
else
  # Open defect QA-302/task-13: (page-1)*pageSize overflows int32 -> negative OFFSET -> 500.
  fail "huge page offset overflow (QA-302 OPEN)" "HTTP $STATUS body=${BODY:0:120}"
fi
req GET "$BASE/api/comments?pageSize=5"
[ "$STATUS" = "200" ] && [ "$(jget "len(d.get('items',[]))")" -le 5 ] \
  && pass "pageSize=5 honoured" || fail "pageSize=5" "HTTP $STATUS"
req GET "$BASE/api/comments?pageSize=100000"
[ "$STATUS" = "200" ] && [ "$(jget "d.get('pageSize')")" = "100" ] \
  && pass "pageSize>100 clamped to 100" || fail "pageSize clamp" "pageSize=$(jget "d.get('pageSize')")"
req GET "$BASE/api/comments?pageSize=-5"
[ "$STATUS" = "200" ] && [ "$(jget "d.get('pageSize')")" = "25" ] \
  && pass "pageSize<1 falls back to 25" || fail "pageSize<1" "pageSize=$(jget "d.get('pageSize')")"

# keyset cursor
req GET "$BASE/api/comments?pageSize=5&sortBy=createdAt&sortDir=desc"
if [ "$STATUS" = "200" ] && [ "$TOTAL" -gt 5 ]; then
  CURSOR="$(jget "d.get('nextCursor')")"
  if [ -n "$CURSOR" ] && [ "$CURSOR" != "None" ]; then
    pass "nextCursor present when more pages exist"
    FIRST_IDS="$(jget "','.join(str(i['id']) for i in d['items'])")"
    req GET "$BASE/api/comments?pageSize=5&sortBy=createdAt&sortDir=desc&cursor=$(urlenc "$CURSOR")"
    if [ "$STATUS" = "200" ]; then
      SECOND_IDS="$(jget "','.join(str(i['id']) for i in d['items'])")"
      if [ -z "$SECOND_IDS" ]; then
        fail "keyset page 2" "cursor returned empty page"
      elif [ "$FIRST_IDS" = "$SECOND_IDS" ]; then
        fail "keyset page 2" "same ids returned (cursor ignored)"
      else
        OVERLAP="$(python3 -c 'import sys
a=set(sys.argv[1].split(",")) if sys.argv[1] else set()
b=set(sys.argv[2].split(",")) if sys.argv[2] else set()
print("yes" if a & b else "no")' "$FIRST_IDS" "$SECOND_IDS")"
        [ "$OVERLAP" = "no" ] && pass "keyset cursor page 2 has no overlap with page 1" \
          || fail "keyset overlap" "page1=$FIRST_IDS page2=$SECOND_IDS"
      fi
    else
      fail "keyset follow" "HTTP $STATUS body=${BODY:0:200}"
    fi
    # cursor/sort mismatch -> 400 per §4.5
    req GET "$BASE/api/comments?pageSize=5&sortBy=userName&sortDir=asc&cursor=$(urlenc "$CURSOR")"
    [ "$STATUS" = "400" ] && pass "cursor with mismatched sort -> 400" \
      || fail "cursor sort mismatch" "HTTP $STATUS body=${BODY:0:200}"
  else
    fail "nextCursor" "missing although totalItems=$TOTAL > 5 (got '$CURSOR')"
  fi
else
  skip "keyset cursor checks (need >5 root comments; total=$TOTAL)"
fi

# ---------------------------------------------------------------- 7. preview
section "preview (AJAX, no navigation)"
req POST "$BASE/api/preview" -H 'Content-Type: application/json' --data '{"text":"<strong>hi</strong> &amp; <i>x</i>"}'
if [ "$STATUS" = "200" ] && [ "$(jget "d.get('valid')")" = "True" ] \
   && printf '%s' "$(jget "d.get('html','')")" | grep -q "<strong>hi</strong>"; then
  pass "POST /api/preview valid -> sanitized html"
else
  fail "POST /api/preview" "HTTP $STATUS body=${BODY:0:240}"
fi
req POST "$BASE/api/preview" -H 'Content-Type: application/json' --data '{"text":"<b>not allowed</b>"}'
if [ "$STATUS" = "200" ] && [ "$(jget "d.get('valid')")" = "False" ] \
   && [ "$(jget "len(d.get('errors',[]))>0")" = "True" ]; then
  pass "POST /api/preview disallowed tag -> valid=false with errors (HTTP 200)"
else
  fail "POST /api/preview invalid" "HTTP $STATUS body=${BODY:0:240}"
fi

# ------------------------------------------------------------------- 8. XSS
section "XSS rejection"
req POST "$BASE/api/preview" -H 'Content-Type: application/json' \
  --data '{"text":"<script>alert(1)</script><i>ok</i>"}'
if printf '%s' "$(jget "d.get('html','')")" | grep -qi '<script'; then
  fail "preview XSS" "html still contains <script>"
else
  pass "preview strips <script>"
fi

check_xss_create() { # <label> <payload> <field>
  local label="$1" payload="$2" field="${3:-text}"
  solve_captcha >/dev/null 2>&1
  req POST "$BASE/api/comments" \
    --form-string "userName=SmkXss$RANDOM" \
    --form-string "email=smkxss@example.com" \
    --form-string "text=$payload" \
    --form-string "captchaId=$CAPTCHA_ID" \
    --form-string "captchaAnswer=$CAPTCHA_CODE"
  if [ "$STATUS" = "400" ] && [ "$(jget "'$field' in d.get('errors',{})")" = "True" ]; then
    pass "$label rejected server-side (400 errors.$field)"
  elif [ "$STATUS" = "201" ]; then
    local stored; stored="$(jget "d['comment']['text']")"
    if printf '%s' "$stored" | grep -qiE '<script|onerror|javascript:|data:text/html'; then
      fail "$label" "201 and stored markup survives: $stored"
    else
      pass "$label sanitized on create (201, no dangerous markup)"
    fi
  else
    fail "$label" "HTTP $STATUS body=${BODY:0:240}"
  fi
}
check_xss_create "stored <script>" '<script>alert(1)</script><i>xsstest</i>'
check_xss_create "onerror attribute" '<img src=x onerror=alert(1)>'
check_xss_create "javascript: href" '<a href="javascript:alert(1)">x</a>'
check_xss_create "data: href" '<a href="data:text/html,<script>alert(1)</script>">x</a>'

# list payload must not contain raw script
req GET "$BASE/api/comments?pageSize=100"
printf '%s' "$BODY" | grep -qi '<script' \
  && fail "stored XSS in list" "list payload contains <script" \
  || pass "list payload contains no raw <script>"

# ------------------------------------------------------------------- 9. SQLi
section "SQL injection"
sqli_get() { # <label> <query>
  req GET "$BASE/api/comments?$2"
  if [ "$STATUS" = "200" ] || [ "$STATUS" = "400" ]; then
    pass "$1 handled (HTTP $STATUS, no 500)"
  else
    fail "$1" "HTTP $STATUS body=${BODY:0:200}"
  fi
}
sqli_get "SQLi sortBy=' OR 1=1--"      "pageSize=100&sortBy=%27%20OR%201%3D1%20--"
sqli_get "SQLi sortByName union"       "pageSize=100&sortBy=userName%29%3BDROP%20TABLE%20comments%3B--"
sqli_get "SQLi sortDir=';DELETE"       "pageSize=100&sortDir=asc%3BDELETE%20FROM%20comments%3B--"
sqli_get "SQLi page=1;DROP TABLE"      "page=1%3BDROP%20TABLE%20comments%3B--"
sqli_get "SQLi pageSize=25;DROP"       "pageSize=25%3BDROP%20TABLE%20comments%3B--"
sqli_get "SQLi cursor"                 "cursor=%27%20OR%201%3D1--"
sqli_get "SQLi q in search"            "q=%27%20OR%201%3D1--"

# SQLi in a form field: stored verbatim as text, table must survive
sqli_create_user="SmkSqli$RANDOM"
create_comment "$sqli_create_user" "'; DROP TABLE comments; --"
[ "$STATUS" = "201" ] && pass "SQLi payload in text accepted as data (201)" \
  || fail "SQLi in text" "HTTP $STATUS body=${BODY:0:200}"
req GET "$BASE/api/health"
[ "$STATUS" = "200" ] && [ "$(jget "d.get('database')")" = "ok" ] \
  && pass "DB alive after SQLi attempts" || fail "DB after SQLi" "health HTTP $STATUS"

# ------------------------------------------------------------- 10. attachments
section "attachments"
python3 -c "open('$TMPDIR_SMOKE/small.txt','w').write('a'*1024)" 2>/dev/null
python3 -c "open('$TMPDIR_SMOKE/big.txt','w').write('a'*(100*1024+1))" 2>/dev/null

create_comment "SmkTxt$RANDOM" "hello txt" -F "attachment=@$TMPDIR_SMOKE/small.txt;type=text/plain"
if [ "$STATUS" = "201" ] && [ "$(jget "d['comment']['attachment']['kind']")" = "text" ]; then
  TXT_ATT_ID="$(jget "d['comment']['attachment']['id']")"
  pass "TXT 1KB accepted (kind=text, id=$TXT_ATT_ID)"
  [ "$(jget "d['comment']['attachment']['width']")" = "None" ] \
    && pass "TXT attachment width=null" || fail "TXT width" "expected null"
  req GET "$BASE/api/attachments/$TXT_ATT_ID"
  if [ "$STATUS" = "200" ] && [ "$(header_value X-Content-Type-Options)" = "nosniff" ] \
     && echo "$(header_value Content-Disposition)" | grep -qi 'inline'; then
    pass "TXT served inline with nosniff"
  else
    fail "TXT headers" "status=$STATUS nosniff=$(header_value X-Content-Type-Options)"
  fi
  req GET "$BASE/api/attachments/$TXT_ATT_ID/thumb"
  [ "$STATUS" = "404" ] && pass "TXT thumb -> 404" || fail "TXT thumb" "HTTP $STATUS"
else
  fail "TXT 1KB accepted" "HTTP $STATUS body=${BODY:0:240}"
fi

create_comment "SmkBig$RANDOM" "big txt" -F "attachment=@$TMPDIR_SMOKE/big.txt;type=text/plain"
if [ "$STATUS" = "400" ] && [ "$(jget "'attachment' in d.get('errors',{})")" = "True" ]; then
  pass "TXT >100KB rejected with errors.attachment"
else
  fail "TXT >100KB rejected" "HTTP $STATUS body=${BODY:0:240}"
fi

if [ "$HAVE_PIL" = "1" ]; then
  python3 - "$TMPDIR_SMOKE/big.png" <<'PY'
import sys
from PIL import Image
Image.new("RGB", (800, 600), (90, 140, 200)).save(sys.argv[1], "PNG")
PY
  create_comment "SmkImg$RANDOM" "image" -F "attachment=@$TMPDIR_SMOKE/big.png;type=image/png"
  if [ "$STATUS" = "201" ]; then
    IMG_ATT_ID="$(jget "d['comment']['attachment']['id']")"
    IMG_W="$(jget "d['comment']['attachment']['width']")"
    IMG_H="$(jget "d['comment']['attachment']['height']")"
    if [ "$IMG_W" -le 320 ] && [ "$IMG_H" -le 240 ] && [ "$IMG_W" -gt 0 ] && [ "$IMG_H" -gt 0 ]; then
      pass "800x600 PNG downscaled to ${IMG_W}x${IMG_H}"
    else
      fail "image downscale" "got ${IMG_W}x${IMG_H}"
    fi
    curl -sS --max-time 30 -o "$TMPDIR_SMOKE/served.png" "$BASE/api/attachments/$IMG_ATT_ID"
    SERVER_DIM="$(python3 - "$TMPDIR_SMOKE/served.png" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1])
print(f"{im.width}x{im.height}")
PY
)"
    [ "$SERVER_DIM" = "${IMG_W}x${IMG_H}" ] \
      && pass "served image is resized (${SERVER_DIM})" \
      || fail "served image size" "reported ${IMG_W}x${IMG_H}, served $SERVER_DIM"
    req GET "$BASE/api/attachments/$IMG_ATT_ID/thumb"
    [ "$STATUS" = "200" ] && pass "image thumb -> 200" || fail "image thumb" "HTTP $STATUS"
  else
    fail "image upload" "HTTP $STATUS body=${BODY:0:240}"
  fi
else
  skip "image attachment checks (python3 Pillow not installed)"
fi

section "attachment errors"
req GET "$BASE/api/attachments/999999999"
[ "$STATUS" = "404" ] && pass "unknown attachment -> 404" || fail "unknown attachment" "HTTP $STATUS"

# ------------------------------------------------------------------ 11. GraphQL
section "GraphQL"
GQL="$BASE/graphql"
gql() { # <json-body>  -> STATUS/BODY
  req POST "$GQL" -H 'Content-Type: application/json' --data "$1"
}
gql '{"query":"{ comments(pageSize: 3, sortBy: CREATED_AT, sortDir: DESC) { items { id userName createdAt clientIp userAgent } page pageSize totalItems totalPages sortBy sortDir nextCursor } }"}'
if [ "$STATUS" = "200" ] && [ "$(jget "'errors' not in d")" = "True" ] \
   && [ "$(jget "len(d['data']['comments']['items'])")" -ge 0 ]; then
  pass "GraphQL query comments(...) -> page data ($(jget "d['data']['comments']['totalItems']") total)"
else
  fail "GraphQL query" "HTTP $STATUS body=${BODY:0:300}"
fi
gql '{"query":"{ comment(id: 1) { id userName text textPlain replies { id } } }"}'
[ "$STATUS" = "200" ] && [ "$(jget "'errors' not in d")" = "True" ] \
  && pass "GraphQL query comment(id:1) accepted" || fail "GraphQL comment(id)" "HTTP $STATUS body=${BODY:0:240}"

# GraphQL mutation with a fresh captcha
solve_captcha >/dev/null 2>&1
GQL_USER="SmkGql$RANDOM"
GQL_PAYLOAD="$(python3 -c 'import json,sys
u,cid,ans=sys.argv[1],sys.argv[2],sys.argv[3]
print(json.dumps({"query":"mutation($i: CreateCommentInput!){ createComment(input:$i){ success errors { field message } comment { id userName parentId } } }","variables":{"i":{"userName":u,"email":u.lower()+"@example.com","homePage":None,"text":"graphql <strong>hello</strong>","parentId":None,"captchaId":cid,"captchaAnswer":ans}}}))' \
  "$GQL_USER" "$CAPTCHA_ID" "$CAPTCHA_CODE")"
gql "$GQL_PAYLOAD"
if [ "$STATUS" = "200" ] && [ "$(jget "d.get('data',{}).get('createComment',{}).get('success')")" = "True" ]; then
  pass "GraphQL mutation createComment -> success=true (id=$(jget "d['data']['createComment']['comment']['id']"))"
  GQL_CREATED_ID="$(jget "d['data']['createComment']['comment']['id']")"
else
  fail "GraphQL mutation" "HTTP $STATUS body=${BODY:0:300}"
fi

# GraphQL mutation with bad captcha -> payload errors, HTTP 200, success=false
GQL_BAD="$(python3 -c 'import json,sys
u=sys.argv[1]
print(json.dumps({"query":"mutation($i: CreateCommentInput!){ createComment(input:$i){ success errors { field message } comment { id } } }","variables":{"i":{"userName":u,"email":u.lower()+"@example.com","homePage":None,"text":"x","parentId":None,"captchaId":"00000000-0000-0000-0000-000000000000","captchaAnswer":"BAD"}}}))' \
  "SmkGqlBad$RANDOM")"
gql "$GQL_BAD"
if [ "$STATUS" = "200" ] && [ "$(jget "d.get('data',{}).get('createComment',{}).get('success')")" = "False" ] \
   && [ "$(jget "len(d['data']['createComment']['errors'])>0")" = "True" ]; then
  pass "GraphQL mutation bad CAPTCHA -> success=false with errors (HTTP 200)"
else
  fail "GraphQL mutation bad CAPTCHA" "HTTP $STATUS body=${BODY:0:300}"
fi

# ------------------------------------------------------------------- 12. search
section "search (/api/search)"
SEARCH_TOKEN="srch$(date +%s)$RANDOM"
create_comment "SmkSrch$RANDOM" "searchable <strong>$SEARCH_TOKEN</strong> token"
if [ "$STATUS" = "201" ]; then
  SEARCH_OK=0
  for i in $(seq 1 15); do
    req GET "$BASE/api/search?q=$SEARCH_TOKEN"
    if [ "$STATUS" = "200" ] && [ "$(jget "d.get('totalItems',0)")" -ge 1 ]; then SEARCH_OK=1; break; fi
    if [ "$STATUS" = "503" ]; then break; fi
    sleep 1
  done
  if [ "$STATUS" = "503" ]; then
    skip "GET /api/search (503 Search unavailable -> Providers:Search=none)"
  elif [ "$SEARCH_OK" = "1" ]; then
    pass "GET /api/search?q=<token> found the indexed comment (totalItems=$(jget "d.get('totalItems')"))"
    [ "$(jget "'query' in d and 'took' in d and 'items' in d")" = "True" ] \
      && pass "search response shape has query/took/items" \
      || fail "search shape" "${BODY:0:200}"
    [ "$(jget "len(d['items'])>0 and 'comment' in d['items'][0]")" = "True" ] \
      && pass "search hit carries comment" || fail "search hit" "${BODY:0:200}"
  else
    fail "GET /api/search" "indexed token not found within 15s (last HTTP $STATUS body=${BODY:0:200})"
  fi
else
  fail "search seed create" "HTTP $STATUS body=${BODY:0:200}"
fi
req GET "$BASE/api/search?q="
[ "$STATUS" = "400" ] && pass "GET /api/search without q -> 400" || fail "search missing q" "HTTP $STATUS body=${BODY:0:200}"

# -------------------------------------------------------------------- 13. stats
section "stats (/api/stats)"
req GET "$BASE/api/stats"
if [ "$STATUS" = "200" ] \
   && [ "$(jget "'totalComments' in d and 'totalRoots' in d and 'totalAttachments' in d and 'cacheHitRate' in d")" = "True" ]; then
  pass "GET /api/stats -> totalComments=$(jget "d['totalComments']"), totalRoots=$(jget "d['totalRoots']"), cacheHitRate=$(jget "d['cacheHitRate']")"
else
  fail "GET /api/stats" "HTTP $STATUS body=${BODY:0:240}"
fi

# ---------------------------------------------------- 14. JSON child creation
section "JSON create /api/comments/{id}/child"
if [ -n "${CASCADE_ROOT:-}" ]; then
  CHILD_USER="SmkChild$RANDOM"
  json_create "$API_BASE" "/api/comments/$CASCADE_ROOT/child" "$CHILD_USER" "json child <i>text</i>"
  if [ "$STATUS" = "201" ]; then
    CHILD_ID="$(jget "d['comment']['id']")"
    CHILD_PARENT="$(jget "d['comment']['parentId']")"
    [ "$CHILD_PARENT" = "$CASCADE_ROOT" ] \
      && pass "POST /api/comments/{id}/child -> 201, parentId=$CASCADE_ROOT (id=$CHILD_ID)" \
      || fail "JSON child parentId" "expected $CASCADE_ROOT got $CHILD_PARENT"
  else
    fail "POST /api/comments/{id}/child" "HTTP $STATUS body=${BODY:0:240}"
  fi
else
  skip "JSON child creation (no cascade root)"
fi

# --------------------------------------------------------------- 15. WebSocket
section "WebSocket hello + comment.created"
WS_SCHEME="ws"; echo "$BASE" | grep -q '^https' && WS_SCHEME="wss"
WS_HOST="$(echo "$BASE" | sed -E 's#^https?://##')"
WS_URL="${WS_URL:-$WS_SCHEME://$WS_HOST/ws}"
if command -v node >/dev/null 2>&1; then
  WS_USER="SmkWs$RANDOM"
  rm -f "$TMPDIR_SMOKE/ws-ready" "$TMPDIR_SMOKE/ws-out.json"
  node "$(dirname "$0")/ws-probe.mjs" "$WS_URL" 20000 "$WS_USER" "$TMPDIR_SMOKE/ws-ready" \
    > "$TMPDIR_SMOKE/ws-out.json" 2>"$TMPDIR_SMOKE/ws-err" &
  WS_PID=$!
  READY=0
  for i in $(seq 1 40); do
    [ -f "$TMPDIR_SMOKE/ws-ready" ] && { READY=1; break; }
    kill -0 "$WS_PID" 2>/dev/null || break
    sleep 0.25
  done
  if [ "$READY" = "1" ]; then
    pass "WS $WS_URL -> hello frame received"
  else
    fail "WS hello" "no hello within 10s (err=$(head -c 200 "$TMPDIR_SMOKE/ws-err" 2>/dev/null))"
  fi
  create_comment "$WS_USER" "ws broadcast test"
  [ "$STATUS" = "201" ] || fail "WS seed create" "HTTP $STATUS body=${BODY:0:200}"
  wait "$WS_PID" 2>/dev/null || true
  WS_JSON="$(cat "$TMPDIR_SMOKE/ws-out.json" 2>/dev/null || echo '{}')"
  if [ "$(jb "d.get('created') is not None" "$WS_JSON")" = "True" ]; then
    pass "WS received comment.created for $WS_USER"
  else
    fail "WS comment.created" "$(printf '%s' "$WS_JSON" | head -c 300)"
  fi
  if [ "$(jb "'eventId' in (d.get('created') or {})" "$WS_JSON")" = "True" ]; then
    pass "WS comment.created carries eventId"
  else
    fail "WS eventId" "missing eventId"
  fi
  # ping/pong
  node -e '
const url=process.argv[1];
const ws=new WebSocket(url);
const t=setTimeout(()=>{console.log("timeout");process.exit(1)},5000);
ws.addEventListener("open",()=>ws.send(JSON.stringify({type:"ping"})));
ws.addEventListener("message",(e)=>{const m=JSON.parse(e.data); if(m.type==="pong"){console.log("pong");clearTimeout(t);ws.close();process.exit(0);}});
ws.addEventListener("error",()=>{console.log("error");process.exit(1)});
' "$WS_URL" > "$TMPDIR_SMOKE/pong.out" 2>/dev/null
  [ "$(cat "$TMPDIR_SMOKE/pong.out" 2>/dev/null)" = "pong" ] \
    && pass "WS ping -> pong" || fail "WS ping" "got '$(cat "$TMPDIR_SMOKE/pong.out" 2>/dev/null)'"
else
  skip "WebSocket checks (node not installed)"
fi

# --------------------------------------------------------- 16. SPA via nginx
section "SPA static hosting (nginx)"
req GET "$BASE/"
if [ "$STATUS" = "200" ] && printf '%s' "$BODY" | grep -qi '<html'; then
  pass "GET / serves index.html"
  printf '%s' "$BODY" | grep -qi 'app-root\|<script' \
    && pass "index.html looks like an app shell" || fail "index shell" "no app-root/script found"
else
  fail "GET /" "HTTP $STATUS"
fi
req GET "$BASE/some/deep/spa/route"
[ "$STATUS" = "200" ] && printf '%s' "$BODY" | grep -qi '<html' \
  && pass "SPA fallback serves index.html for deep route" || fail "SPA fallback" "HTTP $STATUS"
req GET "$BASE/api/does-not-exist"
[ "$STATUS" = "404" ] && pass "unknown /api route -> 404 (not SPA fallback)" || fail "unknown api route" "HTTP $STATUS"

# ------------------------------------------------------------------ 17. prod
section "dev-only endpoints in Production"
if [ -n "$PROD_BASE" ]; then
  req GET "$PROD_BASE/api/dev/captcha/00000000-0000-0000-0000-000000000000"
  [ "$STATUS" = "404" ] && pass "PROD GET /api/dev/captcha/{id} -> 404" \
    || fail "PROD dev captcha" "HTTP $STATUS"
  req POST "$PROD_BASE/api/dev/seed" -H 'Content-Type: application/json' --data '{"count":1,"clear":false}'
  [ "$STATUS" = "404" ] && pass "PROD POST /api/dev/seed -> 404" \
    || fail "PROD dev seed" "HTTP $STATUS"
  req POST "$PROD_BASE/graphql" -H 'Content-Type: application/json' \
    --data '{"query":"{ __schema { types { name } } }"}'
  if [ "$STATUS" = "200" ] && [ "$(jget "'__schema' in (d.get('data') or {})")" = "True" ]; then
    fail "PROD GraphQL introspection" "introspection enabled in Production"
  else
    pass "PROD GraphQL introspection disabled (HTTP $STATUS)"
  fi
  req GET "$PROD_BASE/graphql"
  [ "$STATUS" = "404" ] || [ "$STATUS" = "405" ] \
    && pass "PROD GET /graphql disabled (HTTP $STATUS)" || fail "PROD graphql GET" "HTTP $STATUS"
else
  skip "Production dev-endpoint 404 checks (set PROD_BASE=http://host:port to run)"
fi

# ------------------------------------------------------------------- summary
printf '\n================ SUMMARY ================\n'
printf 'PASS: %d  FAIL: %d  SKIP: %d\n' "$PASS" "$FAIL" "$SKIP"
if [ "${#SKIPPED[@]}" -gt 0 ]; then
  printf 'Skipped:\n'
  for s in "${SKIPPED[@]}"; do printf '  - %s\n' "$s"; done
fi
if [ "$FAIL" -gt 0 ]; then
  printf 'Failures:\n'
  for f in "${FAILED[@]}"; do printf '  - %s\n' "$f"; done
  exit 1
fi
exit 0
