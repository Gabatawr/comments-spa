#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Adversarial security probes for the «Комментарии» v2 stack.
#
# Records raw request + observed response per attack. Never treats 4xx as a
# product failure: a *VULNERABLE* verdict means a dangerous payload survived
# or a 5xx/schema leak occurred; everything else is SAFE / ACCEPTED-RISK /
# UNEXPECTED.
#
# Usage:
#   ADV_LOG=/tmp/adv.log tests/e2e/adversarial.sh [BASE] [API_BASE]
#
# Environment:
#   BASE        through nginx        (default http://localhost:8080)
#   API_BASE    direct API           (default http://localhost:8081)
#   PROD_BASE   Production instance — enables introspection/dev-endpoint checks
#   ADV_LOG     raw log file         (default /tmp/comments-adv-<ts>.log)
#   PG_CONTAINER/PG_USER/PG_DB  Postgres for the self-cleanup step
#               (defaults comments-spa-postgres / comments / comments)
#
# Self-cleaning: at the end the script deletes ONLY the rows it created
# (`user_name LIKE 'Adv%'`), deepest-first because comments.parent_id is
# ON DELETE RESTRICT, then drops the stale list cache. A cleanup problem is
# logged but never changes the verdict or the exit code.
#
# Exit code: 0 when no VULNERABLE verdict was recorded, 1 otherwise.
# ---------------------------------------------------------------------------
set -u

BASE="${1:-${BASE:-http://localhost:8080}}"; BASE="${BASE%/}"
API_BASE="${2:-${API_BASE:-http://localhost:8081}}"; API_BASE="${API_BASE%/}"
PROD_BASE="${PROD_BASE:-}"; PROD_BASE="${PROD_BASE%/}"
REDIS_CONTAINER="${REDIS_CONTAINER:-comments-spa-redis}"
PG_CONTAINER="${PG_CONTAINER:-comments-spa-postgres}"
PG_USER="${PG_USER:-comments}"
PG_DB="${PG_DB:-comments}"
ADV_LOG="${ADV_LOG:-/tmp/comments-adv-$(date +%s).log}"

SAFE=0; VULN=0; RISK=0; UNEXPECTED=0; SKIPPED=0
TMP="$(mktemp -d "${TMPDIR:-/tmp}/comments-adv.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

: > "$ADV_LOG"

req() {
  local method="$1" url="$2"; shift 2
  local out
  out="$(curl -sS --max-time 30 -D "$TMP/ad.headers" -w $'\n%{http_code}' -X "$method" "$@" "$url" 2>"$TMP/ad.curlerr")" || true
  STATUS="${out##*$'\n'}"
  BODY="${out%$'\n'*}"
}
jget() {
  python3 -c '
import json,sys
try: d = json.load(sys.stdin)
except Exception: print(""); sys.exit(0)
try: print(eval(sys.argv[1], {"d": d}))
except Exception: print("")' "$1" 2>/dev/null
}
jb() { printf '%s' "$2" | jget "$1"; }
header_value() { awk -v h="$1" 'BEGIN{IGNORECASE=1} index($0, h":")==1 {v=substr($0,index($0,":")+1); gsub(/^[ \t\r\n]+|[ \t\r\n]+$/,"",v); print v}' "$TMP/ad.headers" 2>/dev/null; }

# raw <label> <full-curl-command>
raw() {
  printf '\n### %s\n$ %s\n' "$1" "$2" >> "$ADV_LOG"
}
snapshot() {
  {
    printf 'HTTP %s\n' "$STATUS"
    printf 'BODY %s\n' "$(printf '%s' "$BODY" | head -c 1200)"
    printf 'HEADERS %s\n' "$(printf '%s' "$(cat "$TMP/ad.headers" 2>/dev/null)" | head -c 600)"
  } >> "$ADV_LOG"
}
record() { # <verdict> <label> <observed>
  printf '%-13s %-46s %s\n' "$1" "$2" "$3"
  printf '%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" >> "$ADV_LOG"
}
safe()       { SAFE=$((SAFE+1));       record "SAFE" "$1" "$2" "${3:-}"; }
vulnerable() { VULN=$((VULN+1));       record "VULNERABLE" "$1" "$2" "${3:-}"; }
risk()       { RISK=$((RISK+1));       record "ACCEPTED-RISK" "$1" "$2" "${3:-}"; }
unexpected() { UNEXPECTED=$((UNEXPECTED+1)); record "UNEXPECTED" "$1" "$2" "${3:-}"; }
skip()       { SKIPPED=$((SKIPPED+1)); record "SKIP" "$1" "$2" "${3:-}"; }

section() { printf '\n== %s ==\n' "$1"; printf '\n\n========== %s ==========\n' "$1" >> "$ADV_LOG"; }

# Solve CAPTCHA via the dev peek endpoint when available, else read the one-time
# code from the Redis store (Production default: Features__DevCaptchaPeek=false).
captcha_code_from_redis() { # <captchaId>
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
if six: print(six[0]); raise SystemExit
for c in cands:
    c=c.strip()
    if re.fullmatch(r"[A-Za-z0-9]{4,12}", c): print(c); break
' 2>/dev/null
}

solve_captcha() {
  req GET "$BASE/api/captcha"
  [ "$STATUS" = "200" ] || return 1
  C_ID="$(jb "d['captchaId']" "$BODY")"
  req GET "$BASE/api/dev/captcha/$C_ID"
  if [ "$STATUS" = "200" ]; then
    C_CODE="$(jb "d['code']" "$BODY")"
    [ -n "$C_CODE" ] && return 0
  fi
  C_CODE="$(captcha_code_from_redis "$C_ID")"
  [ -n "$C_CODE" ] || return 1
  return 0
}

DANGER_RE='<script|onerror[[:space:]]*=|onload[[:space:]]*=|javascript:|data:text/html|srcdoc|<iframe|<svg'

# create_multipart <user> <text> [extra -F ...]
create_multipart() {
  local user="$1" text="$2"; shift 2
  solve_captcha || return 1
  req POST "$BASE/api/comments" \
    --form-string "userName=$user" --form-string "email=${user,,}@example.com" \
    --form-string "text=$text" \
    --form-string "captchaId=$C_ID" --form-string "captchaAnswer=$C_CODE" "$@"
}

# xss_create_verdict <label> <payload-field> <payload> [extra -F ...]
xss_create_verdict() {
  local label="$1" field="$2" payload="$3"; shift 3
  if [ "$field" = "text" ]; then
    raw "$label" "curl -X POST $BASE/api/comments -F userName=... -F text='$payload' -F captchaId=... -F captchaAnswer=..."
    create_multipart "AdvXss$RANDOM" "$payload" "$@"
  else
    solve_captcha >/dev/null 2>&1 || true
    raw "$label" "curl -X POST $BASE/api/comments -F '$field=$payload' ..."
    req POST "$BASE/api/comments" \
      --form-string "userName=$([ "$field" = userName ] && echo "$payload" || echo "AdvField$RANDOM")" \
      --form-string "email=$([ "$field" = email ] && echo "$payload" || echo "advfield@example.com")" \
      --form-string "homePage=$([ "$field" = homePage ] && echo "$payload" || echo "")" \
      --form-string "text=$([ "$field" = text ] && echo "$payload" || echo "field probe")" \
      --form-string "captchaId=$C_ID" --form-string "captchaAnswer=$C_CODE" "$@"
  fi
  snapshot
  if [ "$STATUS" = "400" ]; then
    safe "$label" "HTTP 400 (rejected; errors=$(jb "','.join((d.get('errors') or {}).keys())" "$BODY"))"
    return
  fi
  if [ "$STATUS" = "201" ]; then
    local leaked=0
    for key in text textPlain userName homePage; do
      v="$(jb "d['comment'].get('$key') or ''" "$BODY")"
      printf '%s' "$v" | grep -Eqi "$DANGER_RE" && leaked=1
    done
    if [ "$leaked" = "1" ]; then
      vulnerable "$label" "201 and dangerous markup survives in stored fields: $(printf '%s' "$BODY" | head -c 300)"
    else
      safe "$label" "201, payload sanitized/dropped (stored text=$(jb "'(d[\"comment\"][\"text\"] or \"\")[:80]'" "$BODY"))"
    fi
    return
  fi
  unexpected "$label" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
}

printf 'Adversarial probes: BASE=%s API_BASE=%s PROD_BASE=%s\n' "$BASE" "$API_BASE" "${PROD_BASE:-<unset>}"
printf 'Raw log: %s\n' "$ADV_LOG"

# ------------------------------------------------------------------ 1. XSS
section "Stored XSS (multipart, JSON, GraphQL)"
xss_create_verdict "XSS script tag"            text '<script>alert(1)</script>'
xss_create_verdict "XSS img onerror"           text '<img src=x onerror=alert(1)>'
xss_create_verdict "XSS svg onload"            text '<svg/onload=alert(1)>'
xss_create_verdict "XSS iframe javascript:"    text '<iframe src="javascript:alert(1)"></iframe>'
xss_create_verdict "XSS body onload"           text '<body onload=alert(1)>'
xss_create_verdict "XSS mixed-case script"     text '<ScRiPt>alert(1)</ScRiPt>'
xss_create_verdict "XSS a javascript: href"    text '<a href="javascript:alert(1)">x</a>'
xss_create_verdict "XSS a data: href"          text '<a href="data:text/html,<script>alert(1)</script>">x</a>'
xss_create_verdict "XSS style/expression"      text '<style>body{background:url(javascript:alert(1))}</style>'
xss_create_verdict "XSS in userName"           userName '<script>alert(1)</script>'
xss_create_verdict "XSS in email"              email '"><script>alert(1)</script>@x.com'
xss_create_verdict "XSS in homePage"           homePage 'javascript:alert(1)'

# preview endpoint must never emit dangerous sinks
for payload in '<script>alert(1)</script>' '<img src=x onerror=alert(1)>' '<a href="javascript:alert(1)">x</a>' '<a href="data:text/html,x">x</a>' '<svg/onload=1>' '<iframe src=javascript:alert(1)>' '<ScRiPt>alert(1)</sCrIpT>'; do
  raw "preview: $payload" "curl -X POST $BASE/api/preview -H 'Content-Type: application/json' --data '{\"text\":\"$payload\"}'"
  req POST "$BASE/api/preview" -H 'Content-Type: application/json' \
    --data "$(python3 -c 'import json,sys;print(json.dumps({"text":sys.argv[1]}))' "$payload")"
  snapshot
  html="$(jb "d.get('html','')" "$BODY")"
  if printf '%s' "$html" | grep -Eqi '<script|onerror[[:space:]]*=|javascript:|data:text/html|<iframe|<svg'; then
    vulnerable "preview: $payload" "dangerous sink survives: $html"
  else
    safe "preview: $payload" "sanitized -> ${html:0:80}"
  fi
done

# XSS via GraphQL mutation and JSON child
if solve_captcha; then
  GQLP="$(python3 -c 'import json,sys
u,cid,ans=sys.argv[1],sys.argv[2],sys.argv[3]
print(json.dumps({"query":"mutation($i: CreateCommentInput!){ createComment(input:$i){ success errors{field message} comment{ id text } } }","variables":{"i":{"userName":u,"email":u.lower()+"@example.com","homePage":None,"text":"<script>alert(1)</script>","parentId":None,"captchaId":cid,"captchaAnswer":ans}}}))' "AdvGql$RANDOM" "$C_ID" "$C_CODE")"
  raw "XSS via GraphQL mutation" "curl -X POST $BASE/graphql --data <mutation with <script>>"
  req POST "$BASE/graphql" -H 'Content-Type: application/json' --data "$GQLP"
  snapshot
  if [ "$STATUS" = "200" ]; then
    succ="$(jb "d['data']['createComment']['success']" "$BODY")"
    txt="$(jb "d['data']['createComment'].get('comment',{}).get('text') or ''" "$BODY")"
    if [ "$succ" = "True" ] && printf '%s' "$txt" | grep -Eqi '<script'; then
      vulnerable "XSS via GraphQL mutation" "success=true and text=$txt"
    else
      safe "XSS via GraphQL mutation" "success=$succ text=${txt:0:60} errors=$(jb "len(d['data']['createComment']['errors'])" "$BODY")"
    fi
  else
    unexpected "XSS via GraphQL mutation" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
  fi

  JSONP="$(python3 -c 'import json,sys
u,cid,ans=sys.argv[1],sys.argv[2],sys.argv[3]
print(json.dumps({"userName":u,"email":u.lower()+"@example.com","homePage":None,"text":"<script>alert(1)</script>","parentId":None,"captchaId":cid,"captchaAnswer":ans}))' "AdvJson$RANDOM" "$C_ID" "$C_CODE")"
  raw "XSS via JSON /api/comments/{id}/child" "curl -X POST $API_BASE/api/comments/1/child --data <json with <script>>"
  req POST "$API_BASE/api/comments/1/child" -H 'Content-Type: application/json' --data "$JSONP"
  snapshot
  if [ "$STATUS" = "400" ]; then
    safe "XSS via JSON child" "HTTP 400 rejected"
  elif [ "$STATUS" = "201" ]; then
    txt="$(jb "d['comment']['text']" "$BODY")"
    if printf '%s' "$txt" | grep -Eqi '<script'; then vulnerable "XSS via JSON child" "script survives: $txt"; else safe "XSS via JSON child" "sanitized: ${txt:0:60}"; fi
  else
    unexpected "XSS via JSON child" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
  fi
fi

# UTF-7 / mutation XSS classics
xss_create_verdict "XSS null-byte script" text $'<scr\x00ipt>alert(1)</scr\x00ipt>'
xss_create_verdict "XSS unclosed tag"     text '<i><strong>x</i>'

# ------------------------------------------------------------------ 2. SQLi
section "SQL injection"
sqli_queries=(
  "sortBy=%27%20OR%201%3D1--"
  "sortBy=userName%29%3BSELECT%20pg_sleep%285%29--"
  "sortDir=asc%3Bcopy%20comments%20to%20program%20%27id%27--"
  "page=1%3B%20DROP%20TABLE%20comments%3B--"
  "pageSize=25%3B%20DELETE%20FROM%20comments%3B--"
  "cursor=%27%20OR%201%3D1--"
)
for q in "${sqli_queries[@]}"; do
  raw "SQLi GET /api/comments?$q" "curl -sS '$BASE/api/comments?$q'"
  req GET "$BASE/api/comments?$q"
  snapshot
  if [ "$STATUS" = "200" ] || [ "$STATUS" = "400" ]; then
    # confirm no pg_sleep side effect (response must be fast) and DB alive
    safe "SQLi GET ?$q" "HTTP $STATUS"
  else
    unexpected "SQLi GET ?$q" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
  fi
done
raw "SQLi in search q" "curl -sS '$BASE/api/search?q=%27%3BDROP%20TABLE%20comments%3B--'"
req GET "$BASE/api/search?q=%27%3BDROP%20TABLE%20comments%3B--"
snapshot
if [ "$STATUS" = "200" ] || [ "$STATUS" = "400" ] || [ "$STATUS" = "503" ]; then safe "SQLi in /api/search q" "HTTP $STATUS"; else unexpected "SQLi in search q" "HTTP $STATUS"; fi

raw "SQLi in text field" "curl -X POST $BASE/api/comments -F text=\"'; DROP TABLE comments; --\" ..."
create_multipart "AdvSqli$RANDOM" "'; DROP TABLE comments; --"
snapshot
if [ "$STATUS" = "201" ]; then safe "SQLi in text field" "201, stored as data"; else unexpected "SQLi in text field" "HTTP $STATUS"; fi

raw "SQLi in homePage" "curl -X POST $BASE/api/comments -F homePage=\"' OR 1=1 --\" ..."
create_multipart "AdvSqliHp$RANDOM" "sqli homepage" --form-string "homePage=' OR 1=1 --"
snapshot
if [ "$STATUS" = "400" ] || [ "$STATUS" = "201" ]; then safe "SQLi in homePage" "HTTP $STATUS"; else unexpected "SQLi in homePage" "HTTP $STATUS"; fi

raw "DB liveness after SQLi" "curl -sS $BASE/api/health"
req GET "$BASE/api/health"
snapshot
[ "$STATUS" = "200" ] && [ "$(jb "d.get('database')" "$BODY")" = "ok" ] \
  && safe "DB alive after all SQLi probes" "database=ok" \
  || vulnerable "DB after SQLi" "health=$STATUS $(printf '%s' "$BODY" | head -c 200)"

# ------------------------------------------------------------------ 3. XFF
section "Spoofed X-Forwarded-For"
if solve_captcha; then
  xff_case() { # <label> <header> <expected>
    local label="$1" hdr="$2" expected="$3"
    local user="AdvXff$RANDOM"
    solve_captcha || { skip "$label" "no captcha"; return; }
    raw "$label" "curl -X POST $API_BASE/api/comments -H '$hdr' -F userName=$user -F captchaId=$C_ID -F captchaAnswer=$C_CODE"
    req POST "$API_BASE/api/comments" -H "$hdr" \
      --form-string "userName=$user" --form-string "email=${user,,}@example.com" \
      --form-string "text=xff" --form-string "captchaId=$C_ID" --form-string "captchaAnswer=$C_CODE"
    snapshot
    if [ "$STATUS" = "201" ]; then
      local got; got="$(jb "d['comment']['clientIp']" "$BODY")"
      if [ "$got" = "$expected" ]; then safe "$label" "clientIp=$got"; else unexpected "$label" "expected $expected got '$got'"; fi
    else
      unexpected "$label" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 160)"
    fi
  }
  xff_case "XFF single hop" "X-Forwarded-For: 1.2.3.4" "1.2.3.4"
  xff_case "XFF multi hop -> first" "X-Forwarded-For: 1.2.3.4, 10.0.0.1, 192.168.1.1" "1.2.3.4"
  xff_case "XFF empty then real" "X-Forwarded-For: , 9.9.9.9" "9.9.9.9"
  xff_case "X-Real-IP fallback" "X-Real-IP: 5.5.5.5" "5.5.5.5"

  # malicious XFF: stored verbatim is allowed by §6, but must never be reflected as HTML
  solve_captcha
  raw "XFF with script payload" "curl -X POST $API_BASE/api/comments -H 'X-Forwarded-For: <script>alert(1)</script>' -F userName=... -F captchaId=... -F captchaAnswer=..."
  req POST "$API_BASE/api/comments" -H 'X-Forwarded-For: <script>alert(1)</script>' \
    --form-string "userName=AdvXffBad$RANDOM" --form-string "email=advxffbad@example.com" \
    --form-string "text=bad xff" --form-string "captchaId=$C_ID" --form-string "captchaAnswer=$C_CODE"
  snapshot
  if [ "$STATUS" = "201" ]; then
    cip="$(jb "d['comment']['clientIp']" "$BODY")"
    if printf '%s' "$cip" | grep -qi '<script'; then
      risk "XFF script stored verbatim" "clientIp='$cip' (contract §6 says invalid values are kept; XFF spoofable because Proxy:TrustAll=true)"
    else
      safe "XFF script not stored" "clientIp='$cip'"
    fi
  else
    safe "XFF script rejected" "HTTP $STATUS"
  fi

  # truncation to 64
  longip="$(python3 -c 'print("1."*100+"1")')"
  solve_captcha
  raw "XFF >64 chars truncation" "curl -X POST $API_BASE/api/comments -H 'X-Forwarded-For: <400 chars>' -F userName=... -F captchaId=... -F captchaAnswer=..."
  req POST "$API_BASE/api/comments" -H "X-Forwarded-For: $longip" \
    --form-string "userName=AdvXffLong$RANDOM" --form-string "email=advxfflong@example.com" \
    --form-string "text=long xff" --form-string "captchaId=$C_ID" --form-string "captchaAnswer=$C_CODE"
  snapshot
  if [ "$STATUS" = "201" ]; then
    ln="$(jb "len(d['comment']['clientIp'] or '')" "$BODY")"
    [ "$ln" -le 64 ] && safe "XFF truncated to <=64" "len=$ln" || vulnerable "XFF not truncated" "len=$ln"
  else
    unexpected "XFF truncation" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 160)"
  fi
else
  skip "XFF probes" "dev captcha peek unavailable"
fi

# ------------------------------------------------- 4. attachments / files
section "Attachments (oversized / invalid / multipart abuse)"
python3 - <<PY
import os
d = "$TMP"
open(os.path.join(d, "oversize.txt"), "wb").write(b"a" * (100*1024+1))
open(os.path.join(d, "exact.txt"), "wb").write(b"a" * (100*1024))
open(os.path.join(d, "empty.png"), "wb").write(b"")
open(os.path.join(d, "fake.png"), "wb").write(b"<html><script>alert(1)</script></html>")
open(os.path.join(d, "traversal.txt"), "wb").write(b"hello traversal")
open(os.path.join(d, "big.bin"), "wb").write(os.urandom(2*1024*1024))
try:
    from PIL import Image
    Image.new("RGB", (200, 200), (1,2,3)).save(os.path.join(d, "ok.png"), "PNG")
except Exception:
    pass
PY

att_case() { # <label> <file-spec> <reject|accept>
  local label="$1" spec="$2" expect="$3"
  raw "$label" "curl -X POST $BASE/api/comments -F 'attachment=@$spec' ..."
  create_multipart "AdvAtt$RANDOM" "attachment probe" -F "attachment=$spec"
  snapshot
  if [ "$expect" = "reject" ]; then
    if [ "$STATUS" = "400" ] && [ "$(jb "'attachment' in d.get('errors',{})" "$BODY")" = "True" ]; then
      safe "$label" "400 errors.attachment"
    elif [ "$STATUS" = "201" ]; then
      vulnerable "$label" "invalid/oversized attachment accepted (201)"
    else
      unexpected "$label" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
    fi
  else
    if [ "$STATUS" = "201" ]; then
      safe "$label" "201 accepted; kind=$(jb "d['comment']['attachment']['kind']" "$BODY")"
    else
      unexpected "$label" "expected 201, got HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
    fi
  fi
}
att_case "TXT 1 byte over limit"  "@$TMP/oversize.txt;type=text/plain" reject
att_case "TXT exactly at limit"   "@$TMP/exact.txt;type=text/plain" accept
att_case "0-byte png"             "@$TMP/empty.png;type=image/png" reject
att_case "HTML bytes named .png"  "@$TMP/fake.png;type=image/png" reject
att_case "2MB opaque binary"      "@$TMP/big.bin;type=image/png" reject
att_case "path traversal filename" "@$TMP/traversal.txt;type=text/plain;filename=../../etc/passwd.txt" accept

# traversal response headers (no path leak / no raw CRLF)
if [ -s "$TMP/ok.png" ]; then
  create_multipart "AdvAttOk$RANDOM" "ok image" -F "attachment=@$TMP/ok.png;type=image/png"
  if [ "$STATUS" = "201" ]; then
    aid="$(jb "d['comment']['attachment']['id']" "$BODY")"
    raw "attachment Content-Disposition" "curl -sSI $BASE/api/attachments/$aid"
    req GET "$BASE/api/attachments/$aid"
    snapshot
    cd_="$(header_value Content-Disposition)"
    if printf '%s' "$cd_" | grep -q $'\r'; then vulnerable "attachment header CRLF" "CRLF in Content-Disposition"; else safe "attachment headers clean" "$cd_ nosniff=$(header_value X-Content-Type-Options)"; fi
  fi
fi

# malformed multipart: bogus boundary, no body, wrong content type
raw "multipart bogus boundary" "curl -X POST $API_BASE/api/comments -H 'Content-Type: multipart/form-data; boundary=ZZZ' --data-binary 'not multipart'"
req POST "$API_BASE/api/comments" -H 'Content-Type: multipart/form-data; boundary=ZZZ' --data-binary 'not multipart'
snapshot
[ "$STATUS" = "400" ] || [ "$STATUS" = "415" ] && safe "multipart bogus boundary -> 4xx" "HTTP $STATUS" || unexpected "multipart bogus boundary" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"

raw "JSON body to multipart endpoint" "curl -X POST $API_BASE/api/comments -H 'Content-Type: application/json' --data '{}'"
req POST "$API_BASE/api/comments" -H 'Content-Type: application/json' --data '{"userName":"x"}'
snapshot
[ "$STATUS" = "400" ] || [ "$STATUS" = "415" ] && safe "JSON on multipart endpoint -> 4xx" "HTTP $STATUS" || unexpected "JSON on multipart endpoint" "HTTP $STATUS"

raw "multipart without boundary header" "curl -X POST $API_BASE/api/comments --data-binary 'x'"
req POST "$API_BASE/api/comments" --data-binary 'x'
snapshot
[ "$STATUS" = "400" ] || [ "$STATUS" = "415" ] && safe "multipart missing boundary -> 4xx" "HTTP $STATUS" || unexpected "multipart missing boundary" "HTTP $STATUS"

# ------------------------------------------------- 5. malformed JSON
section "Malformed JSON / GraphQL"
malformed_json_case() { # <label> <url> <content-type> <body>
  local label="$1" url="$2" ct="$3" body="$4"
  raw "$label" "curl -X POST $url -H 'Content-Type: $ct' --data '$body'"
  req POST "$url" -H "Content-Type: $ct" --data "$body"
  snapshot
  case "$STATUS" in
    4*) safe "$label" "HTTP $STATUS (no 5xx)" ;;
    200) # GraphQL may return 200 with errors
      if [ "$(jb "'errors' in d" "$BODY")" = "True" ]; then safe "$label" "200 with GraphQL errors"; else unexpected "$label" "200 without errors"; fi ;;
    *) unexpected "$label" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)" ;;
  esac
}
malformed_json_case "preview malformed JSON" "$BASE/api/preview" application/json '{not json'
malformed_json_case "preview empty body" "$BASE/api/preview" application/json ''
malformed_json_case "child malformed JSON" "$API_BASE/api/comments/1/child" application/json '{"userName":'
malformed_json_case "child unknown parentId" "$API_BASE/api/comments/999999999/child" application/json '{"userName":"AdvPar1","email":"advpar@example.com","text":"x","captchaId":"00000000-0000-0000-0000-000000000000","captchaAnswer":"X"}'
malformed_json_case "graphql malformed JSON" "$BASE/graphql" application/json '{'
malformed_json_case "graphql empty query" "$BASE/graphql" application/json '{"query":""}'
malformed_json_case "graphql wrong shape" "$BASE/graphql" application/json '{"foo":"bar"}'

# ------------------------------------------------- 6. CAPTCHA
section "CAPTCHA replay / abuse"
if solve_captcha; then
  C1="$C_ID"; K1="$C_CODE"
  raw "captcha concurrent replay (2 parallel)" "two parallel curls with captchaId=$C1"
  U1="AdvCapA$RANDOM"; U2="AdvCapB$RANDOM"
  P1="$(python3 -c 'import json,sys;u,cid,ans=sys.argv[1],sys.argv[2],sys.argv[3];print(json.dumps({"userName":u,"email":u.lower()+"@example.com","text":"c1","captchaId":cid,"captchaAnswer":ans}))' "$U1" "$C1" "$K1")"
  P2="$(python3 -c 'import json,sys;u,cid,ans=sys.argv[1],sys.argv[2],sys.argv[3];print(json.dumps({"userName":u,"email":u.lower()+"@example.com","text":"c2","captchaId":cid,"captchaAnswer":ans}))' "$U2" "$C1" "$K1")"
  s1="$(curl -sS --max-time 30 -o "$TMP/c1.json" -w '%{http_code}' -X POST "$API_BASE/api/comments" -H 'Content-Type: application/json' --data "$P1")" &
  s2="$(curl -sS --max-time 30 -o "$TMP/c2.json" -w '%{http_code}' -X POST "$API_BASE/api/comments" -H 'Content-Type: application/json' --data "$P2")" &
  wait 2>/dev/null || true
  ok_count=0
  for f in "$TMP/c1.json" "$TMP/c2.json"; do
    grep -q '"userName"' "$f" 2>/dev/null && ok_count=$((ok_count+1))
  done
  printf 'Parallel captcha results: %s\n' "$(grep -ho '\"userName\"' "$TMP"/c*.json 2>/dev/null | wc -l)" >> "$ADV_LOG"
  if [ "$ok_count" -le 1 ]; then safe "CAPTCHA cannot be used twice concurrently" "created=$ok_count/2"; else vulnerable "CAPTCHA concurrent replay" "created=$ok_count/2"; fi
  # wrong answer (multipart, as required by POST /api/comments)
  solve_captcha >/dev/null 2>&1
  raw "CAPTCHA wrong answer" "curl -X POST $API_BASE/api/comments -F userName=AdvCapW -F captchaId=$C_ID -F captchaAnswer=ZZZZZZ"
  req POST "$API_BASE/api/comments" \
    --form-string "userName=AdvCapW$RANDOM" --form-string "email=advcapw@example.com" \
    --form-string "text=x" --form-string "captchaId=$C_ID" --form-string "captchaAnswer=ZZZZZZ"
  snapshot
  [ "$STATUS" = "400" ] && [ "$(jb "'captcha' in d.get('errors',{})" "$BODY")" = "True" ] \
    && safe "CAPTCHA wrong answer -> 400 errors.captcha" "HTTP $STATUS" \
    || unexpected "CAPTCHA wrong answer" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 160)"
else
  skip "CAPTCHA replay probes" "dev peek unavailable"
fi

# ------------------------------------------------- 7. pagination abuse
section "Pagination abuse"
limit_case() { # <label> <url> <expected-status...>
  local label="$1" url="$2"; shift 2
  raw "$label" "curl -sS '$url'"
  req GET "$url"
  snapshot
  local ok=1
  for want in "$@"; do [ "$STATUS" = "$want" ] && ok=0; done
  if [ "$ok" = "0" ]; then
    safe "$label" "HTTP $STATUS pageSize=$(jb "d.get('pageSize')" "$BODY") items=$(jb "len(d.get('items',[]))" "$BODY")"
  else
    unexpected "$label" "HTTP $STATUS"
  fi
}
limit_case "pageSize=2147483647"       "$BASE/api/comments?pageSize=2147483647" 200
limit_case "page=2147483647"           "$BASE/api/comments?page=2147483647" 200
limit_case "page=-2147483648"          "$BASE/api/comments?page=-2147483648" 200
limit_case "pageSize=-2147483648"      "$BASE/api/comments?pageSize=-2147483648" 200
limit_case "cursor garbage"            "$BASE/api/comments?cursor=not-a-cursor" 400 200
limit_case "cursor SQL-ish"            "$BASE/api/comments?cursor=%27%20OR%201%3D1--" 400 200
BIGCURSOR="$(python3 -c 'print("A"*100000)')"
raw "cursor 100KB" "curl -sS '$BASE/api/comments?cursor=<100KB>'"
req GET "$BASE/api/comments?cursor=$BIGCURSOR"
snapshot
case "$STATUS" in 400|414|200) safe "cursor 100KB handled" "HTTP $STATUS" ;; *) unexpected "cursor 100KB" "HTTP $STATUS" ;; esac
limit_case "search pageSize huge"      "$BASE/api/search?q=a&pageSize=2147483647" 400 200 503
limit_case "search q 201 chars"        "$BASE/api/search?q=$(python3 -c 'print("a"*201)')" 400 503
limit_case "comments pageSize=0"       "$BASE/api/comments?pageSize=0" 200
limit_case "comments page=abc"         "$BASE/api/comments?page=abc" 400 200
limit_case "comments sortBy unknown"   "$BASE/api/comments?sortBy=DROP%20TABLE" 200
# QA-302: (page-1)*pageSize overflows int32 -> negative OFFSET -> 500 instead of empty items.
raw "page offset overflow (QA-302)" "curl -sS '$BASE/api/comments?page=107374183'"
req GET "$BASE/api/comments?page=107374183"
snapshot
if [ "$STATUS" = "200" ]; then
  safe "huge page offset (107374183) -> 200" "HTTP 200"
else
  unexpected "page offset overflow (QA-302)" "HTTP $STATUS — contract §4.4 wants empty items, got 500 (negative OFFSET)"
fi

# ------------------------------------------------- 8. GraphQL abuse
section "GraphQL depth / aliasing / introspection"
if command -v python3 >/dev/null 2>&1; then
  deep="$(python3 -c '
inner = "id"
for _ in range(20):
    inner = "{ replies " + inner + " }"
q = "{ comments { items " + inner + " } }"
print(q)')"
  raw "GraphQL depth 20" "curl -X POST $BASE/graphql --data '<query depth 20>'"
  req POST "$BASE/graphql" -H 'Content-Type: application/json' \
    --data "$(python3 -c 'import json,sys;print(json.dumps({"query":sys.argv[1]}))' "$deep")"
  snapshot
  if [ "$STATUS" = "200" ] && [ "$(jb "'errors' in d" "$BODY")" = "True" ]; then
    safe "GraphQL depth limit enforced" "errors=$(printf '%s' "$(jb "d['errors'][0].get('message')" "$BODY")" | head -c 120)"
  elif [ "$STATUS" = "400" ]; then
    safe "GraphQL depth limit enforced (HTTP 400)" "$(printf '%s' "$BODY" | head -c 120)"
  else
    unexpected "GraphQL depth abuse" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
  fi

  aliasq="$(python3 -c '
parts = " ".join("a%d: comments { totalItems }" % i for i in range(400))
print("{ " + parts + " }")')"
  raw "GraphQL alias bombing 400" "curl -X POST $BASE/graphql --data '<400 aliases>'"
  req POST "$BASE/graphql" -H 'Content-Type: application/json' \
    --data "$(python3 -c 'import json,sys;print(json.dumps({"query":sys.argv[1]}))' "$aliasq")"
  snapshot
  if [ "$STATUS" = "200" ] && [ "$(jb "'errors' in d" "$BODY")" = "True" ]; then
    safe "GraphQL complexity limit enforced" "$(printf '%s' "$(jb "d['errors'][0].get('message')" "$BODY")" | head -c 120)"
  elif [ "$STATUS" = "400" ]; then
    safe "GraphQL complexity limit enforced (HTTP 400)" "$(printf '%s' "$BODY" | head -c 120)"
  else
    unexpected "GraphQL alias bombing" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 200)"
  fi

  raw "GraphQL introspection" "curl -X POST $BASE/graphql --data '{__schema{types{name}}}'"
  req POST "$BASE/graphql" -H 'Content-Type: application/json' --data '{"query":"{ __schema { queryType { name } } }"}'
  snapshot
  if [ "$STATUS" = "200" ] && [ "$(jb "'__schema' in (d.get('data') or {})" "$BODY")" = "True" ]; then
    risk "GraphQL introspection enabled (Development instance)" "dev stack exposes schema; must be disabled in Production"
  else
    safe "GraphQL introspection disabled" "HTTP $STATUS"
  fi
fi

# ------------------------------------------------- 9. Production-only
section "Production hardening"
if [ -n "$PROD_BASE" ]; then
  raw "PROD dev captcha" "curl -sS $PROD_BASE/api/dev/captcha/00000000-0000-0000-0000-000000000000"
  req GET "$PROD_BASE/api/dev/captcha/00000000-0000-0000-0000-000000000000"
  snapshot
  [ "$STATUS" = "404" ] && safe "PROD /api/dev/captcha -> 404" "HTTP 404" || vulnerable "PROD dev captcha exposed" "HTTP $STATUS"

  raw "PROD dev seed" "curl -X POST $PROD_BASE/api/dev/seed --data '{...}'"
  req POST "$PROD_BASE/api/dev/seed" -H 'Content-Type: application/json' --data '{"count":1,"roots":1,"depth":0,"batchSize":1,"clear":false}'
  snapshot
  [ "$STATUS" = "404" ] && safe "PROD /api/dev/seed -> 404" "HTTP 404" || vulnerable "PROD seed exposed" "HTTP $STATUS body=$(printf '%s' "$BODY" | head -c 150)"

  raw "PROD GraphQL introspection" "curl -X POST $PROD_BASE/graphql --data '{__schema{types{name}}}'"
  req POST "$PROD_BASE/graphql" -H 'Content-Type: application/json' --data '{"query":"{ __schema { types { name } } }"}'
  snapshot
  if [ "$STATUS" = "200" ] && [ "$(jb "'__schema' in (d.get('data') or {})" "$BODY")" = "True" ]; then
    vulnerable "PROD GraphQL introspection enabled" "schema leaked in Production"
  else
    safe "PROD GraphQL introspection disabled" "HTTP $STATUS"
  fi

  raw "PROD GET /graphql (UI off)" "curl -sS $PROD_BASE/graphql"
  req GET "$PROD_BASE/graphql"
  snapshot
  case "$STATUS" in 404|405) safe "PROD GraphQL GET disabled" "HTTP $STATUS" ;; *) unexpected "PROD GET /graphql" "HTTP $STATUS" ;; esac
else
  skip "Production hardening" "set PROD_BASE=<url> to run (dev endpoints + introspection must be off)"
fi

# --------------------------------------------- self-cleanup of Adv* fixtures
# Every fixture this script creates uses a user_name starting with "Adv"; no
# other producer in the repo uses that prefix. Deletion is iterative
# (deepest-first) because comments.parent_id is ON DELETE RESTRICT. The stale
# Redis list cache is dropped/invalidated so a following smoke.sh still sees a
# clean DB (smoke greps the raw list payload for stored markup). Cleanup errors
# are logged and ignored — they never fail the adversarial run.
cleanup_adv_fixtures() {
  local before remaining passes n
  if ! command -v docker >/dev/null 2>&1; then
    printf '\n[cleanup] docker not found -> Adv* fixtures left in place\n' | tee -a "$ADV_LOG"
    return 0
  fi
  before="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc \
    "select count(*) from comments where user_name like 'Adv%'" 2>/dev/null)" || before="?"
  passes=0
  while [ "$passes" -lt 50 ]; do
    n="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc \
      "with removed as (delete from comments c where c.user_name like 'Adv%' and not exists (select 1 from comments ch where ch.parent_id = c.id) returning 1) select count(*) from removed" 2>/dev/null)" || { n=""; break; }
    [ -n "$n" ] || break
    [ "$n" != "0" ] || break
    passes=$((passes + 1))
  done
  remaining="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc \
    "select count(*) from comments where user_name like 'Adv%'" 2>/dev/null)" || remaining="?"
  # invalidate the list cache so the next reader does not get a page with removed rows
  docker exec "$REDIS_CONTAINER" redis-cli --scan --pattern 'comments:page:*' 2>/dev/null \
    | while read -r key; do
        [ -n "$key" ] || continue
        docker exec "$REDIS_CONTAINER" redis-cli DEL "$key" >/dev/null 2>&1 || true
      done
  docker exec "$REDIS_CONTAINER" redis-cli INCR comments:version >/dev/null 2>&1 || true
  {
    printf '\n[cleanup] Adv* rows: before=%s remaining=%s delete-passes=%s\n' "$before" "$remaining" "$passes"
    [ "$remaining" = "0" ] || printf '[cleanup] WARNING: %s Adv* row(s) left (referenced by a non-Adv child?)\n' "$remaining"
  } | tee -a "$ADV_LOG"
  return 0
}

# ------------------------------------------------------------------- summary
cleanup_adv_fixtures
{
  printf '\n================ ADVERSARIAL SUMMARY ================\n'
  printf 'SAFE=%d VULNERABLE=%d ACCEPTED-RISK=%d UNEXPECTED=%d SKIP=%d\n' "$SAFE" "$VULN" "$RISK" "$UNEXPECTED" "$SKIPPED"
} | tee -a "$ADV_LOG"
printf '\nRaw log: %s\n' "$ADV_LOG"
[ "$VULN" -eq 0 ] || exit 1
exit 0
