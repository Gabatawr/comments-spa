#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Adversarial verification of the v2.1 functional quotes
# (task-4; docs/DESIGN-v2.1-decisions.md §1, docs/DESIGN-v2.md §4).
#
# Written by the independent verifier, NOT by the api/web authors. The goal is
# to break the feature, not to demonstrate it. Every check records the raw
# command result; a FAIL is reported immediately.
#
# Covers groups 1-3 and 6 of the task; groups 4-5 (voting / author-info in a
# real browser) live in tests/e2e/adversarial-quote.mjs, which is invoked at the
# end of this script when it (and Playwright) is available.
#
# Self-cleaning: every comment this script creates is recorded and removed again
# at the end (section 8, deepest-first, only own rows), and the Redis page cache
# is invalidated. This keeps the shared dev DB free of the deliberately hostile
# `<script` payloads that blunt smoke checks scan for.
#
# Usage:
#   tests/e2e/adversarial-quote.sh [BASE] [API_BASE]
#
# Environment:
#   BASE            through nginx   (default http://localhost:8080)
#   API_BASE        direct API      (default http://localhost:8081)
#   REDIS_CONTAINER redis container (default comments-spa-redis)
#   PG_CONTAINER    postgres cont.  (default comments-spa-postgres)
#   VERIFY_LOG      raw log file    (default tests/.runtime/verify/comments-verify-<ts>.log)
#   SKIP_BROWSER=1  do not invoke the Playwright part
#   KEEP_FIXTURES=1 keep the created rows in the DB (debugging only)
#
# Exit code: 0 when every check passed, 1 otherwise.
# ---------------------------------------------------------------------------
set -u

BASE="${1:-${BASE:-http://localhost:8080}}"; BASE="${BASE%/}"
API_BASE="${2:-${API_BASE:-http://localhost:8081}}"; API_BASE="${API_BASE%/}"
REDIS_CONTAINER="${REDIS_CONTAINER:-comments-spa-redis}"
PG_CONTAINER="${PG_CONTAINER:-comments-spa-postgres}"
PG_USER="${PG_USER:-comments}"
PG_DB="${PG_DB:-comments}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
VERIFY_LOG="${VERIFY_LOG:-$ROOT/tests/.runtime/verify/comments-verify-$(date +%s).log}"
mkdir -p "$(dirname "$VERIFY_LOG")"

PASS=0; FAIL=0
TMP="$(mktemp -d "${TMPDIR:-/tmp}/comments-verify.XXXXXX")"
: > "$VERIFY_LOG"

RESP_FILE="$TMP/resp.json"
HTTP_STATUS=""
C_ID=""; C_CODE=""
TAG="Q$(date +%s | tail -c 7)"
# Every comment created by this script (and only those) is recorded here and
# removed again by cleanup_fixtures(), so the shared dev DB is left without any
# hostile `<script` payload that would break blunt smoke checks.
CREATED_IDS="$TMP/created-ids.txt"
: > "$CREATED_IDS"
CLEANUP_DONE=0

section() { printf '\n========== %s ==========\n' "$1"; printf '\n========== %s ==========\n' "$1" >> "$VERIFY_LOG"; }
check() { # <label> <0|1> <detail>
  if [ "$2" = "1" ]; then
    PASS=$((PASS+1)); printf 'PASS  %-58s %s\n' "$1" "$3"
    printf 'PASS\t%s\t%s\n' "$1" "$3" >> "$VERIFY_LOG"
  else
    FAIL=$((FAIL+1)); printf 'FAIL  %-58s %s\n' "$1" "$3"
    printf 'FAIL\t%s\t%s\n' "$1" "$3" >> "$VERIFY_LOG"
  fi
}
ev() { # <label>
  printf '\n--- %s (HTTP %s)\n' "$1" "$HTTP_STATUS" >> "$VERIFY_LOG"
  head -c 4000 "$RESP_FILE" >> "$VERIFY_LOG" 2>/dev/null || true
  printf '\n' >> "$VERIFY_LOG"
}
log() { printf '%s\n' "$*"; printf '%s\n' "$*" >> "$VERIFY_LOG"; }
json_get() { # <file> <py-expr-with-d>
  python3 - "$1" "$2" <<'PY'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    print("__PARSE_ERR__"); raise SystemExit
try:
    print(eval(sys.argv[2]))
except Exception:
    print("__EVAL_ERR__")
PY
}
body_get() { json_get "$RESP_FILE" "$1"; }

# ------------------------------------------------------------------ self-cleanup
# Deletes only the rows created by this run (recorded in $CREATED_IDS),
# deepest-first because comments.parent_id is ON DELETE RESTRICT, then
# invalidates the Redis page cache so the shared list stops serving the probe
# snapshot. Idempotent and never fails the suite: any problem is logged as WARN.
cleanup_fixtures() {
  [ "${CLEANUP_DONE:-0}" = "1" ] && return 0
  if [ "${KEEP_FIXTURES:-0}" = "1" ]; then
    log "cleanup: KEEP_FIXTURES=1 — fixture rows intentionally left in the database"
    return 0
  fi
  if [ ! -s "$CREATED_IDS" ]; then
    log "cleanup: no fixture rows recorded (nothing to do)"
    CLEANUP_DONE=1
    return 0
  fi
  local ids count
  ids="$(sort -nu "$CREATED_IDS" | paste -sd, -)"
  count="$(sort -nu "$CREATED_IDS" | wc -l | tr -d ' ')"
  if [ -z "$ids" ]; then
    CLEANUP_DONE=1
    return 0
  fi
  if ! command -v docker >/dev/null 2>&1; then
    log "cleanup: WARN docker unavailable — $count fixture rows left in the database (ids: $(printf '%s' "$ids" | head -c 200))"
    CLEANUP_DONE=1
    return 0
  fi

  local out rc remaining
  out="$(
    {
      echo "CREATE TEMP TABLE qprobe(id bigint primary key);"
      printf 'INSERT INTO qprobe(id) SELECT unnest(ARRAY[%s]::bigint[]) ON CONFLICT DO NOTHING;\n' "$ids"
      echo 'DO $$'
      echo 'DECLARE n int := 1;'
      echo 'BEGIN'
      echo '  WHILE n > 0 LOOP'
      echo '    DELETE FROM comments c'
      echo '     WHERE c.id IN (SELECT id FROM qprobe)'
      echo '       AND NOT EXISTS (SELECT 1 FROM comments ch WHERE ch.parent_id = c.id);'
      echo '    GET DIAGNOSTICS n = ROW_COUNT;'
      echo '  END LOOP;'
      echo 'END $$;'
      echo 'SELECT count(*) FROM comments WHERE id IN (SELECT id FROM qprobe);'
    } | docker exec -i "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 -tA 2>&1
  )"
  rc=$?
  remaining="$(printf '%s\n' "$out" | tail -1 | tr -d '[:space:]')"

  # Rows are gone before the cache version bump: a page cached under the old
  # version is then ignored, and new reads hit the database.
  if docker exec "$REDIS_CONTAINER" redis-cli --raw INCR comments:version >/dev/null 2>&1; then
    log "cleanup: bumped comments:version (page cache invalidated)"
  else
    log "cleanup: WARN could not bump comments:version"
  fi

  if [ "$rc" = "0" ] && [ "$remaining" = "0" ]; then
    log "cleanup: removed $count fixture rows (remaining=0)"
  else
    log "cleanup: WARN rc=$rc remaining=${remaining:-?} out=$(printf '%s' "$out" | head -c 300)"
  fi
  CLEANUP_DONE=1
  return 0
}
trap 'cleanup_fixtures; rm -rf "$TMP"' EXIT


captcha_code_from_redis() {
  command -v docker >/dev/null 2>&1 || return 1
  local raw
  raw="$(docker exec "$REDIS_CONTAINER" redis-cli --raw GET "captcha:$1" 2>/dev/null)" || return 1
  [ -n "$raw" ] || return 1
  printf '%s' "$raw" | python3 -c '
import json,re,sys
raw=sys.stdin.read().strip(); cands=[]
def walk(x):
    if isinstance(x,str): cands.append(x)
    elif isinstance(x,dict):
        for v in x.values(): walk(v)
    elif isinstance(x,list):
        for v in x: walk(v)
try: walk(json.loads(raw))
except Exception: cands.append(raw)
six=[c.strip() for c in cands if re.fullmatch(r"[A-Za-z0-9]{6}",c.strip())]
if six: print(six[0]); raise SystemExit
for c in cands:
    c=c.strip()
    if re.fullmatch(r"[A-Za-z0-9]{4,12}",c): print(c); break
' 2>/dev/null
}

solve_captcha() {
  HTTP_STATUS="$(curl -sS -m 15 -o "$RESP_FILE" -w '%{http_code}' "$BASE/api/captcha")"
  [ "$HTTP_STATUS" = "200" ] || return 1
  C_ID="$(body_get "d['captchaId']")"
  HTTP_STATUS="$(curl -sS -m 15 -o "$RESP_FILE" -w '%{http_code}' "$BASE/api/dev/captcha/$C_ID")"
  if [ "$HTTP_STATUS" = "200" ]; then
    C_CODE="$(body_get "d['code']")"
    [ -n "$C_CODE" ] && return 0
  fi
  C_CODE="$(captcha_code_from_redis "$C_ID")"
  [ -n "$C_CODE" ] || return 1
}

# post_comment <user> <text> [parentId] [homePage]
post_comment() {
  local user="$1" text="$2" parent="${3:-}" home="${4:-}"
  solve_captcha || { HTTP_STATUS="000"; return 1; }
  local args=(--form-string "userName=$user" --form-string "email=${user,,}@example.com"
              --form-string "text=$text" --form-string "captchaId=$C_ID" --form-string "captchaAnswer=$C_CODE")
  [ -n "$parent" ] && args+=(--form-string "parentId=$parent")
  [ -n "$home" ] && args+=(--form-string "homePage=$home")
  HTTP_STATUS="$(curl -sS -m 30 -o "$RESP_FILE" -w '%{http_code}' -X POST "$API_BASE/api/comments" "${args[@]}")"
  if [ "$HTTP_STATUS" = "201" ]; then
    local _id
    _id="$(body_get "d['comment']['id']")"
    case "$_id" in
      ''|None|*ERR__*) ;;
      *) printf '%s\n' "$_id" >> "$CREATED_IDS" ;;
    esac
  fi
}

get_api() { HTTP_STATUS="$(curl -sS -m 30 -o "$RESP_FILE" -w '%{http_code}' "$API_BASE$1")"; }

# calc_quote <plain> — exactly the algorithm of QuoteSnapshot.FromPlainText.
calc_quote() {
  printf '%s' "$1" | python3 -c '
import re,sys
s=sys.stdin.read()
s=re.sub(r"\s+"," ",s).strip()
if not s: print("None")
elif len(s)<=160: print(s)
else:
    cut=s[:160]; i=cut.rfind(" ")
    if i>0: cut=cut[:i]
    print(cut+"\u2026")
'
}
strlen_utf8() { python3 -c 'import sys; print(len(sys.argv[1]))' "$1"; }

printf 'Adversarial quote verification: BASE=%s API_BASE=%s TAG=%s\n' "$BASE" "$API_BASE" "$TAG"
printf 'Raw log: %s\n' "$VERIFY_LOG"

FIXTURE="${Q_FIXTURE_OUT:-$ROOT/tests/.runtime/verify/quote-fixture.json}"
mkdir -p "$(dirname "$FIXTURE")"
python3 - "$FIXTURE" "$BASE" <<'PY'
import json,sys
json.dump({"base":sys.argv[2],"cases":[],"author":None,"voteCommentId":None}, open(sys.argv[1],"w"))
PY
fix_add_case() { # <label> <rootId> <replyId> <expectedQuote>
  python3 - "$FIXTURE" "$1" "$2" "$3" "$4" <<'PY'
import json,sys
p=sys.argv[1]; d=json.load(open(p))
d["cases"].append({"label":sys.argv[2],"rootId":int(sys.argv[3]),"replyId":int(sys.argv[4]),"expectedQuote":sys.argv[5]})
json.dump(d,open(p,"w"))
PY
}
fix_set_author() {
  python3 - "$FIXTURE" "$1" "$2" "$3" "$4" <<'PY'
import json,sys
p=sys.argv[1]; d=json.load(open(p))
d["author"]={"rootId":int(sys.argv[2]),"email":sys.argv[3],"homePage":sys.argv[4],"clientIp":sys.argv[5]}
json.dump(d,open(p,"w"))
PY
}
fix_set_vote() {
  python3 - "$FIXTURE" "$1" <<'PY'
import json,sys
p=sys.argv[1]; d=json.load(open(p)); d["voteCommentId"]=int(sys.argv[2]); json.dump(d,open(p,"w"))
PY
}

# ============================================================ 1. XSS via quote
section "1. XSS through the quote snapshot"

# 1.1 entity-encoded <script> is legitimate plain text (§1.1 variant A) -> 201
raw_script='&lt;script&gt;alert(1)&lt;/script&gt;'
post_comment "${TAG}EncScript" "$raw_script"; ev "parent entity-encoded <script>"
check "1.1 entity-encoded <script> accepted" "$([ "$HTTP_STATUS" = 201 ] && echo 1 || echo 0)" "HTTP $HTTP_STATUS"
P_ENCS="$(body_get "d['comment']['id']")"
P_ENCS_PLAIN="$(body_get "d['comment']['textPlain']")"
check "1.1 parent textPlain decodes to literal script text" \
  "$([ "$P_ENCS_PLAIN" = '<script>alert(1)</script>' ] && echo 1 || echo 0)" \
  "textPlain=$(printf '%s' "$P_ENCS_PLAIN" | head -c 120)"

post_comment "${TAG}ReplyEncS" 'benign reply one' "$P_ENCS"
ev "reply to entity-encoded <script> parent"
R_ENCS="$(body_get "d['comment']['id']")"
Q_ENCS="$(body_get "d['comment']['quotedText']")"
R_ENCS_TEXT="$(body_get "d['comment']['text']")"
check "1.2 reply created" "$([ "$HTTP_STATUS" = 201 ] && echo 1 || echo 0)" "HTTP $HTTP_STATUS id=$R_ENCS"
check "1.2 quotedText == parent textPlain (snapshot)" "$([ "$Q_ENCS" = "$P_ENCS_PLAIN" ] && echo 1 || echo 0)" "quotedText=$(printf '%s' "$Q_ENCS" | head -c 120)"
check "1.2 reply body HTML has no script markup" "$(printf '%s' "$R_ENCS_TEXT" | grep -qi '<script' && echo 0 || echo 1)" "text=$(printf '%s' "$R_ENCS_TEXT" | head -c 120)"
check "1.2 quotedText is JSON string, not markup" "$([ -n "$Q_ENCS" ] && [ "$Q_ENCS" != "None" ] && [ "$Q_ENCS" != "__EVAL_ERR__" ] && echo 1 || echo 0)" "type=string"
if [ -n "$P_ENCS" ] && [ -n "$R_ENCS" ]; then fix_add_case "encoded-script" "$P_ENCS" "$R_ENCS" "$Q_ENCS"; fi

# 1.3 raw dangerous markup -> 400 at the sanitizer
for pair in "raw-img|<img src=x onerror=alert(1)>" "raw-script|<script>alert(1)</script>" "raw-svg|<svg/onload=alert(1)>" "raw-iframe|<iframe src=javascript:alert(1)></iframe>" "raw-body|<body onload=alert(1)>"; do
  lbl="${pair%%|*}"; payload="${pair#*|}"
  post_comment "${TAG}${lbl//-/}" "$payload"; ev "raw payload $lbl"
  has_err="$(body_get "'text' in (d.get('errors') or {})")"
  check "1.3 raw $lbl rejected with 400+errors.text" "$([ "$HTTP_STATUS" = 400 ] && [ "$has_err" = "True" ] && echo 1 || echo 0)" "HTTP $HTTP_STATUS errors.text=$has_err"
done

# 1.4 entity-encoded img/onerror is plain text again
raw_img_enc='&lt;img src=x onerror=alert(1)&gt;'
post_comment "${TAG}EncImg" "$raw_img_enc"; ev "parent entity-encoded <img onerror>"
P_EIMG="$(body_get "d['comment']['id']")"
check "1.4 entity-encoded <img onerror> accepted" "$([ "$HTTP_STATUS" = 201 ] && echo 1 || echo 0)" "HTTP $HTTP_STATUS"
post_comment "${TAG}ReplyEncI" 'benign reply two' "$P_EIMG"; ev "reply to entity-encoded <img> parent"
R_EIMG="$(body_get "d['comment']['id']")"
Q_EIMG="$(body_get "d['comment']['quotedText']")"
check "1.4 quotedText == literal <img onerror> plain text" "$([ "$Q_EIMG" = '<img src=x onerror=alert(1)>' ] && echo 1 || echo 0)" "quotedText=$(printf '%s' "$Q_EIMG" | head -c 120)"
if [ -n "$P_EIMG" ] && [ -n "$R_EIMG" ]; then fix_add_case "encoded-img" "$P_EIMG" "$R_EIMG" "$Q_EIMG"; fi

# 1.5 attribute-breakout text and hex-entity text are plain text too
post_comment "${TAG}AttrBreak" '" onmouseover=alert(1) x="'; ev "parent attribute-breakout text"
P_ATTR="$(body_get "d['comment']['id']")"
post_comment "${TAG}ReplyAttr" 'benign reply three' "$P_ATTR"
Q_ATTR="$(body_get "d['comment']['quotedText']")"
check "1.5 attribute-breakout quote is inert text" "$([ "$Q_ATTR" = '" onmouseover=alert(1) x="' ] && echo 1 || echo 0)" "quotedText=$(printf '%s' "$Q_ATTR" | head -c 120)"

hex_script='&#x3c;script&#x3e;alert(1)&#x3c;/script&#x3e;'
post_comment "${TAG}HexScript" "$hex_script"; ev "parent hex-encoded <script>"
P_HEX="$(body_get "d['comment']['id']")"
post_comment "${TAG}ReplyHex" 'benign reply four' "$P_HEX"
Q_HEX="$(body_get "d['comment']['quotedText']")"
check "1.5 hex-encoded <script> decodes to plain text" "$([ "$Q_HEX" = '<script>alert(1)</script>' ] && echo 1 || echo 0)" "quotedText=$(printf '%s' "$Q_HEX" | head -c 120)"

# 1.6 allowed inline markup is flattened out of the quote
post_comment "${TAG}MarkupParent" 'hello <strong>bold</strong> <a href="https://example.com/">link</a> world'
ev "parent with allowed inline markup"
P_MARK="$(body_get "d['comment']['id']")"
P_MARK_PLAIN="$(body_get "d['comment']['textPlain']")"
post_comment "${TAG}ReplyMarkup" 'reply to markup' "$P_MARK"
Q_MARK="$(body_get "d['comment']['quotedText']")"
check "1.6 quote carries no tags" "$(printf '%s' "$Q_MARK" | grep -qE '<[a-zA-Z/]' && echo 0 || echo 1)" "quotedText=$(printf '%s' "$Q_MARK" | head -c 160)"
check "1.6 quote == parent textPlain" "$([ "$Q_MARK" = "$P_MARK_PLAIN" ] && echo 1 || echo 0)" "plain=$(printf '%s' "$P_MARK_PLAIN" | head -c 160)"

# ==================================================== 2. contract additivity
section "2. Contract additivity"

DIFF_FROZEN="$(cd "$ROOT" && git diff -- docs/API-v2.md docs/ARCHITECTURE-v2.md 2>&1)"
check "2.1 git diff frozen docs is empty" "$([ -z "$DIFF_FROZEN" ] && echo 1 || echo 0)" "bytes=$(printf '%s' "$DIFF_FROZEN" | wc -c)"

get_api "/api/comments?page=1&pageSize=100"; ev "GET root comments"
ROOT_STATUS="$HTTP_STATUS"
NON_NULL_ROOTS="$(body_get "sum(1 for i in d['items'] if i.get('quotedText') is not None)")"
N_ITEMS="$(body_get "len(d['items'])")"
check "2.2 GET /api/comments alive" "$([ "$ROOT_STATUS" = 200 ] && echo 1 || echo 0)" "HTTP $ROOT_STATUS items=$N_ITEMS"
check "2.2 all root comments quotedText == null" "$([ "$NON_NULL_ROOTS" = "0" ] && echo 1 || echo 0)" "checked=$N_ITEMS non-null=$NON_NULL_ROOTS"

get_api "/api/comments/$P_ENCS"; ev "GET single root with replies"
G_ROOT_Q="$(body_get "d.get('quotedText')")"
G_REPLY_Q="$(body_get "(d.get('replies') or [{}])[0].get('quotedText')")"
check "2.2 GET /{id}: root quotedText null" "$([ "$G_ROOT_Q" = "None" ] && echo 1 || echo 0)" "root quotedText=$G_ROOT_Q"
check "2.2 GET /{id}: reply quotedText present" "$([ "$G_REPLY_Q" = "$Q_ENCS" ] && echo 1 || echo 0)" "reply quotedText=$(printf '%s' "$G_REPLY_Q" | head -c 120)"

# GraphQL: field appears via the DTO, no explicit Field needed
gql() { # <query-json>
  HTTP_STATUS="$(curl -sS -m 30 -o "$RESP_FILE" -w '%{http_code}' -X POST "$API_BASE/graphql" -H 'Content-Type: application/json' --data-binary "$1")"
}
gql "$(python3 -c 'import json,sys;print(json.dumps({"query":"query($id:Long!){comment(id:$id){id quotedText textPlain}}","variables":{"id":int(sys.argv[1])}}))' "$R_ENCS")"
ev "GraphQL comment(id) quotedText for reply"
check "2.3 GraphQL quotedText on reply" "$([ "$HTTP_STATUS" = 200 ] && [ "$(body_get "d['data']['comment']['quotedText']")" = "$Q_ENCS" ] && echo 1 || echo 0)" "HTTP $HTTP_STATUS value=$(body_get "d['data']['comment']['quotedText']" | head -c 120)"

gql "$(python3 -c 'import json,sys;print(json.dumps({"query":"query($id:Long!){comment(id:$id){id quotedText}}","variables":{"id":int(sys.argv[1])}}))' "$P_ENCS")"
ev "GraphQL comment(id) quotedText for root"
check "2.3 GraphQL quotedText null on root" "$([ "$(body_get "d['data']['comment']['quotedText']")" = "None" ] && echo 1 || echo 0)" "value=$(body_get "d['data']['comment']['quotedText']")"

gql '{"query":"{ comment(id: 2147483000){ id quotedText } }"}'
ev "GraphQL unknown id"
check "2.3 GraphQL unknown id -> null, no 5xx" "$([ "$HTTP_STATUS" = 200 ] && [ "$(body_get "d['data']['comment']")" = "None" ] && echo 1 || echo 0)" "HTTP $HTTP_STATUS"

# ============================================================ 3. truncation
section "3. Quote truncation"

quote_case() { # <label> <parentText> [expect-ellipsis 0|1|auto]
  local label="$1" text="$2" want="${3:-auto}"
  post_comment "${TAG}P${label}" "$text"
  local pid plain expected actual plen
  if [ "$HTTP_STATUS" != "201" ]; then
    check "3.x $label parent created" 0 "HTTP $HTTP_STATUS $(head -c 160 "$RESP_FILE")"
    return
  fi
  pid="$(body_get "d['comment']['id']")"
  plain="$(body_get "d['comment']['textPlain']")"
  post_comment "${TAG}R${label}" "reply for $label" "$pid"
  actual="$(body_get "d['comment']['quotedText']")"
  expected="$(calc_quote "$plain")"
  plen="$(strlen_utf8 "$actual")"
  local ends_ellipsis=0
  case "$actual" in *"…") ends_ellipsis=1;; esac
  check "3.x $label snapshot == algorithm" "$([ "$actual" = "$expected" ] && echo 1 || echo 0)" \
    "len=$plen ends_ellipsis=$ends_ellipsis actual=$(printf '%s' "$actual" | head -c 100)"
  check "3.x $label length <= 161" "$([ "$plen" -le 161 ] && echo 1 || echo 0)" "len=$plen"
  check "3.x $label ends-with-… == $want" "$([ "$want" = "auto" ] || [ "$ends_ellipsis" = "$want" ] && echo 1 || echo 0)" "ends_ellipsis=$ends_ellipsis want=$want"
}

LONG_WORD="$(python3 -c "print('x'*200)")"
quote_case "LongNoSpace" "$LONG_WORD" 1
LONG_WORDS="$(python3 -c "print(('alpha beta gamma delta epsilon zeta eta theta '*6).strip())")"
quote_case "LongWords" "$LONG_WORDS" 1
quote_case "Exactly160" "$(python3 -c "print('w'*160)")" 0
quote_case "Exactly161" "$(python3 -c "print('a'*159+' '+'b')")" 1
quote_case "Short150" "$(python3 -c "print('s'*150)")" 0
quote_case "CollapseSpaces" 'one     two       three' 0
quote_case "WordBoundary161" "$(python3 -c "print('z'*156+' '+'tail')")" 1

# explicit length/boundary assertions on the long-word case
post_comment "${TAG}EdgeLong" "$LONG_WORD"
P_EDGE="$(body_get "d['comment']['id']")"
post_comment "${TAG}REdgeLong" 'edge reply' "$P_EDGE"
Q_EDGE="$(body_get "d['comment']['quotedText']")"
check "3.5 hard cut keeps exactly 160 source chars + …" \
  "$([ "$Q_EDGE" = "$(python3 -c "print('x'*160)")…" ] && echo 1 || echo 0)" \
  "len=$(strlen_utf8 "$Q_EDGE") head=$(printf '%s' "$Q_EDGE" | head -c 20) tail=$(printf '%s' "$Q_EDGE" | tail -c 5)"

# ==================================== 4. no vote backend (static + DB)
section "4. Voting has no backend"

VOTE_SRC="$(grep -rniE '\b(upvote|downvote|vote_count|voteCount|comment_votes|vote_score)\b' "$ROOT/src/Backend" "$ROOT/db" --exclude-dir=.nuget --exclude-dir=bin --exclude-dir=obj --exclude-dir=node_modules 2>/dev/null | head -20)"
check "4.1 no vote fields/tables in src/Backend+db" "$([ -z "$VOTE_SRC" ] && echo 1 || echo 0)" "$(printf '%s' "$VOTE_SRC" | head -c 200)"

if command -v docker >/dev/null 2>&1; then
  DB_COLS="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc \
    "select column_name from information_schema.columns where table_name='comments' and column_name ~* 'vote|rating|score'" 2>/dev/null)"
  check "4.1 DB comments has no vote/rating/score columns" "$([ -z "$DB_COLS" ] && echo 1 || echo 0)" "cols=${DB_COLS:-<none>}"
  DB_QT="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc \
    "select column_name from information_schema.columns where table_name='comments' and column_name='quoted_text'" 2>/dev/null)"
  check "4.1 DB comments.quoted_text exists" "$([ "$DB_QT" = "quoted_text" ] && echo 1 || echo 0)" "col=${DB_QT:-<none>}"
  DB_VOTETABLES="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc \
    "select table_name from information_schema.tables where table_schema='public' and table_name ~* 'vote|rating'" 2>/dev/null)"
  check "4.1 no vote/rating tables in DB" "$([ -z "$DB_VOTETABLES" ] && echo 1 || echo 0)" "tables=${DB_VOTETABLES:-<none>}"
else
  check "4.1 postgres reachable" 0 "docker not available"
fi

# ============================================ 5. author-info payload fixture
section "5. Author-info fixture for the browser part"

# Root shown in a cascade, carries e-mail/IP/homePage for the popover checks.
post_comment "${TAG}AuthorCard" 'author info fixture comment' "" "https://example.com/${TAG,,}"
ev "author fixture"
P_AUTH="$(body_get "d['comment']['id']")"
AUTH_EMAIL="$(body_get "d['comment']['email']")"
AUTH_HOME="$(body_get "d['comment']['homePage']")"
AUTH_IP="$(body_get "d['comment']['clientIp']")"
check "5.1 author fixture created with homePage" "$([ "$HTTP_STATUS" = 201 ] && [ "$AUTH_HOME" = "https://example.com/${TAG,,}" ] && echo 1 || echo 0)" "HTTP $HTTP_STATUS home=$AUTH_HOME ip=$AUTH_IP"
# give it a reply so the root cascade is worth expanding in the browser too
post_comment "${TAG}AuthorReply" 'author fixture reply' "$P_AUTH"
AUTH_REPLY="$(body_get "d['comment']['id']")"
if [ -n "$P_AUTH" ] && [ "$P_AUTH" != "__EVAL_ERR__" ]; then
  fix_set_author "$P_AUTH" "$AUTH_EMAIL" "$AUTH_HOME" "$AUTH_IP"
  fix_set_vote "$P_AUTH"
fi

# ==================================================== 6. repo invariants
section "6. Repository / stack invariants"

if command -v docker >/dev/null 2>&1; then
  COMPOSE_PS="$(cd "$ROOT" && docker compose ps --format '{{.Service}} {{.Status}}' 2>/dev/null)"
  HEALTHY="$(printf '%s\n' "$COMPOSE_PS" | grep -c 'healthy')"
  RUNNING="$(printf '%s\n' "$COMPOSE_PS" | grep -cE ' (Up|running)')"
  check "6.1 six containers running" "$([ "$RUNNING" = "6" ] && echo 1 || echo 0)" "$(printf '%s' "$COMPOSE_PS" | tr '\n' ';' | head -c 400)"
  check "6.1 six containers healthy" "$([ "$HEALTHY" = "6" ] && echo 1 || echo 0)" "healthy=$HEALTHY"
  VOLS="$(docker volume ls --format '{{.Name}}' | grep -c 'comments-spa')"
  check "6.2 project volumes present" "$([ "$VOLS" -ge 6 ] && echo 1 || echo 0)" "count=$VOLS"
else
  check "6.1 docker available" 0 "docker not available"
fi

SECRET_RE='(password|passwd|secret|api[_-]?key|access[_-]?token|private[_-]?key|connection[_-]?string)[[:space:]]*[:=][[:space:]]*["'"'"'][^"'"'"']{6,}'
DIFF_SECRETS="$(cd "$ROOT" && {
  git diff | grep -IniE "$SECRET_RE"
  git status --porcelain | awk '$1=="??"{print $2}' | while read -r f; do
    [ -f "$f" ] && grep -IHniE "$SECRET_RE" "$f" 2>/dev/null
  done
} 2>/dev/null | grep -vE 'POSTGRES_PASSWORD|\.env\.example|example\.com' | head -20)"
check "6.3 no secrets in diff/new files" "$([ -z "$DIFF_SECRETS" ] && echo 1 || echo 0)" "$(printf '%s' "$DIFF_SECRETS" | head -c 300)"

WORK_DIR="$ROOT/src/Frontend/.work"
if [ -e "$WORK_DIR" ]; then
  IGNORED="$(cd "$ROOT" && git check-ignore -v src/Frontend/.work >/dev/null 2>&1 && echo 1 || echo 0)"
  check "6.4 src/Frontend/.work removed or gitignored" "$IGNORED" "present, check-ignore=$IGNORED (web scratch zone; must be gone before final)"
else
  check "6.4 src/Frontend/.work removed or gitignored" 1 "absent"
fi

# ============================================ browser part (groups 4-5)
if [ "${SKIP_BROWSER:-0}" = "1" ]; then
  section "7. browser part skipped (SKIP_BROWSER=1)"
elif [ -f "$HERE/adversarial-quote.mjs" ] && command -v node >/dev/null 2>&1; then
  section "7. real-browser part (Playwright) — voting + author-info + quote DOM"
  export Q_FIXTURE="$FIXTURE"
  export Q_TMP="$TMP"
  export QROOT="$ROOT"
  export Q_REQUIRE_BROWSER=1
  export BASE API_BASE
  export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-$ROOT/.playwright}"
  if node "$HERE/adversarial-quote.mjs"; then
    check "7.1 Playwright DOM suite green" 1 "adversarial-quote.mjs exit 0"
  else
    check "7.1 Playwright DOM suite green" 0 "adversarial-quote.mjs non-zero exit (see output)"
  fi
else
  check "7.1 Playwright DOM suite" 0 "node or adversarial-quote.mjs unavailable"
fi

# ============================================ 8. self-cleanup of fixtures
section "8. Self-cleanup of fixtures (shared DB hygiene)"

CREATED_COUNT="$(sort -nu "$CREATED_IDS" | wc -l | tr -d ' ')"
cleanup_fixtures

if command -v docker >/dev/null 2>&1 && [ -s "$CREATED_IDS" ]; then
  IDS_CSV="$(sort -nu "$CREATED_IDS" | paste -sd, -)"
  REMAINING="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc \
    "select count(*) from comments where id in ($IDS_CSV)" 2>/dev/null | tr -d '[:space:]')"
else
  REMAINING="n/a"
fi
check "8.1 all created fixture rows removed (created=$CREATED_COUNT)" \
  "$([ "$REMAINING" = "0" ] && echo 1 || echo 0)" "remaining=${REMAINING:-?}"

# The shared list (through the API + its Redis cache) must no longer serve any
# `<script` payload. Leftovers are attributed to their owner: only rows from
# this run count against us; foreign probe rows are reported as WARN.
get_api "/api/comments?pageSize=100"
SCAN_OUT="$(python3 - "$RESP_FILE" "$TAG" <<'PY'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    print("0\t0\t"); raise SystemExit
tag = sys.argv[2]; bad = []
def walk(c):
    for k in ("text", "textPlain", "quotedText", "clientIp", "userName", "email", "homePage"):
        v = c.get(k)
        if isinstance(v, str) and "<script" in v:
            bad.append((c.get("id"), c.get("userName"), k))
    for r in c.get("replies") or []:
        walk(r)
for i in d.get("items", []) or []:
    walk(i)
mine = [b for b in bad if str(b[1]).startswith(tag)]
print(f"{len(bad)}\t{len(mine)}\t" + ";".join(f"{b[0]}:{b[1]}:{b[2]}" for b in bad[:10]))
PY
)"
TOTAL_BAD="$(printf '%s' "$SCAN_OUT" | cut -f1)"
MINE_BAD="$(printf '%s' "$SCAN_OUT" | cut -f2)"
BAD_WHO="$(printf '%s' "$SCAN_OUT" | cut -f3)"
if [ "$TOTAL_BAD" = "0" ]; then
  check "8.2 shared /api/comments has no raw <script payload" 1 "scan-clean"
elif [ "$MINE_BAD" = "0" ]; then
  log "WARN 8.2 foreign probe rows still carry <script (not from this run): $BAD_WHO"
  check "8.2 shared /api/comments has no raw <script payload" 1 "only foreign leftovers: $BAD_WHO"
else
  check "8.2 shared /api/comments has no raw <script payload" 0 "rows from this run still present: $BAD_WHO"
fi

printf '\n================ SUMMARY ================\n'
printf 'PASS=%d FAIL=%d (log: %s)\n' "$PASS" "$FAIL" "$VERIFY_LOG"
printf 'PASS=%d FAIL=%d\n' "$PASS" "$FAIL" >> "$VERIFY_LOG"
[ "$FAIL" = "0" ]
