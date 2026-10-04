#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# End-to-end smoke test for the «Комментарии» SPA/API.
#
# Usage:  tests/e2e/smoke.sh [BASE_URL]
#         BASE_URL defaults to http://localhost:5080
#
# Requires: curl, python3 (+ Pillow). The app MUST run in Development with
# Features:DevCaptchaPeek=true so GET /api/dev/captcha/{id} can solve CAPTCHAs.
#
# Prints a PASS/FAIL/SKIP summary and exits non-zero if any check fails.
# ---------------------------------------------------------------------------
set -u

BASE="${1:-http://localhost:5080}"
BASE="${BASE%/}"

PASS=0
FAIL=0
SKIP=0
declare -a FAILED=()
declare -a SKIPPED=()

TMPDIR_SMOKE="$(mktemp -d "${TMPDIR:-/tmp}/comments-smoke.XXXXXX")"
cleanup() { rm -rf "$TMPDIR_SMOKE"; }
trap cleanup EXIT

pass() { PASS=$((PASS + 1)); printf '  PASS  %s\n' "$1"; }
skip() { SKIP=$((SKIP + 1)); SKIPPED+=("$1"); printf '  SKIP  %s\n' "$1"; }
fail() { FAIL=$((FAIL + 1)); FAILED+=("$1 :: $2"); printf '  FAIL  %s :: %s\n' "$1" "$2"; }

# jsonget <python-expr>  (JSON on stdin, object bound to `d`)
jsonget() {
  python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1], {"d": d}))' "$1"
}

STATUS=""
BODY=""

# req <METHOD> <URL> [curl args...] -> sets STATUS, BODY, HEADERS
req() {
  local method="$1" url="$2"; shift 2
  local out
  out="$(curl -sS -D "$TMPDIR_SMOKE/headers" -w $'\n%{http_code}' -X "$method" "$@" "$url" 2>"$TMPDIR_SMOKE/curlerr")" || true
  STATUS="${out##*$'\n'}"
  BODY="${out%$'\n'*}"
  HEADERS="$TMPDIR_SMOKE/headers"
}

header_value() { awk -v h="$1" 'BEGIN{IGNORECASE=1} index($0, h":")==1 {v=substr($0,index($0,":")+1); gsub(/^[ \t\r\n]+|[ \t\r\n]+$/,"",v); print v}' "$HEADERS" 2>/dev/null; }

# solve_captcha -> sets CAPTCHA_ID, CAPTCHA_CODE
solve_captcha() {
  req GET "$BASE/api/captcha"
  if [ "$STATUS" != "200" ]; then
    fail "captcha issue" "HTTP $STATUS"
    return 1
  fi
  CAPTCHA_ID="$(printf '%s' "$BODY" | jsonget "d['captchaId']")"
  req GET "$BASE/api/dev/captcha/$CAPTCHA_ID"
  if [ "$STATUS" != "200" ]; then
    fail "dev captcha peek" "HTTP $STATUS (app must run in Development with Features:DevCaptchaPeek=true)"
    return 1
  fi
  CAPTCHA_CODE="$(printf '%s' "$BODY" | jsonget "d['code']")"
  return 0
}

# create_comment <userName> <text> <extra -F args...> -> BODY holds the response
# --form-string is used for text fields so values starting with '<' or '@' are not
# interpreted by curl as file references.
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

section() { printf '\n== %s ==\n' "$1"; }

command -v curl >/dev/null 2>&1 || { echo "curl is required"; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo "python3 is required"; exit 2; }
python3 -c 'import PIL' >/dev/null 2>&1 || { echo "python3 Pillow is required"; exit 2; }

printf 'Comments SPA smoke test against %s\n' "$BASE"

# ------------------------------------------------------------------ 1. health
section "health"
req GET "$BASE/api/health"
if [ "$STATUS" = "200" ] \
   && [ "$(printf '%s' "$BODY" | jsonget "d['status']")" = "ok" ] \
   && [ "$(printf '%s' "$BODY" | jsonget "d['database']")" = "ok" ]; then
  pass "GET /api/health -> status/database ok"
  for key in cache queue websocket version; do
    if [ "$(printf '%s' "$BODY" | jsonget "'$key' in d")" = "True" ]; then
      pass "health reports '$key'"
    else
      fail "health reports '$key'" "missing from $BODY"
    fi
  done
else
  fail "GET /api/health" "HTTP $STATUS body=$BODY"
fi

# ----------------------------------------------------------------- 2. captcha
section "captcha"
req GET "$BASE/api/captcha"
if [ "$STATUS" = "200" ] \
   && printf '%s' "$BODY" | jsonget "d['image']" | grep -q '^data:image/png;base64,' \
   && [ "$(printf '%s' "$BODY" | jsonget "d['expiresInSeconds']")" = "300" ] \
   && [ "$(printf '%s' "$BODY" | jsonget "'code' not in d")" = "True" ]; then
  CAPTCHA_ID="$(printf '%s' "$BODY" | jsonget "d['captchaId']")"
  if solve_captcha && [ "${#CAPTCHA_CODE}" = "6" ]; then
    pass "GET /api/captcha -> PNG data URL, TTL 300s, no code; dev peek returns a 6-char code"
  else
    fail "dev captcha code" "expected 6 chars, got '${CAPTCHA_CODE:-<none>}'"
  fi
else
  fail "GET /api/captcha" "HTTP $STATUS body=${BODY:0:200}"
fi

# ------------------------------------------------------------------- 3. create
section "create comment"
CREATE_USER="SmkBase$RANDOM"
create_comment "$CREATE_USER" "smoke <strong>comment</strong>"
if [ "$STATUS" = "201" ]; then
  CREATED_ID="$(printf '%s' "$BODY" | jsonget "d['comment']['id']")"
  LOCATION="$(header_value Location)"
  if printf '%s' "$BODY" | jsonget "d['comment']['userName']" | grep -q "^$CREATE_USER$"; then
    pass "POST /api/comments -> 201 with correct userName (id=$CREATED_ID)"
  else
    fail "POST /api/comments body" "userName mismatch: $BODY"
  fi
  if echo "$LOCATION" | grep -q "/api/comments/$CREATED_ID"; then
    pass "201 Location header -> $LOCATION"
  else
    fail "201 Location header" "got '$LOCATION'"
  fi
  if [ "$(printf '%s' "$BODY" | jsonget "d['comment']['userAgent']")" != "None" ]; then
    pass "client identity persisted (userAgent)"
  else
    fail "client identity" "userAgent is null"
  fi
  if [ "$(printf '%s' "$BODY" | jsonget "d['comment']['clientIp']")" != "None" ]; then
    pass "client identity persisted (clientIp)"
  else
    fail "client identity" "clientIp is null"
  fi
else
  fail "POST /api/comments" "HTTP $STATUS body=$BODY"
fi

# --------------------------------------------------------------- 4. sort data
section "sorting (both directions, all three fields)"
# Unique per run so the checks stay valid against a non-empty/shared database.
SORT_TAG="E2e$(date +%s)$RANDOM"
SORT_USERS=("${SORT_TAG}A" "${SORT_TAG}B" "${SORT_TAG}C")
for u in "${SORT_USERS[@]}"; do
  create_comment "$u" "smoke <strong>comment</strong>"
  [ "$STATUS" = "201" ] || fail "create for sort ($u)" "HTTP $STATUS"
  sleep 1.1  # ensure a distinct createdAt so date ordering is deterministic
done

check_sort() {
  local field="$1" dir="$2" expected="$3"
  req GET "$BASE/api/comments?pageSize=100&sortBy=$field&sortDir=$dir"
  [ "$STATUS" = "200" ] || { fail "sort $field $dir" "HTTP $STATUS"; return; }
  local actual
  actual="$(printf '%s' "$BODY" | jsonget "','.join(str(i['userName']) for i in d['items'] if str(i['userName']).startswith('$SORT_TAG'))")"
  if [ "$actual" = "$expected" ]; then
    pass "sortBy=$field sortDir=$dir -> $actual"
  else
    fail "sortBy=$field sortDir=$dir" "expected $expected, got $actual"
  fi
}

check_sort userName asc "${SORT_TAG}A,${SORT_TAG}B,${SORT_TAG}C"
check_sort userName desc "${SORT_TAG}C,${SORT_TAG}B,${SORT_TAG}A"
check_sort createdAt asc "${SORT_TAG}A,${SORT_TAG}B,${SORT_TAG}C"
check_sort createdAt desc "${SORT_TAG}C,${SORT_TAG}B,${SORT_TAG}A"

# ----------------------------------------------------------------- 5. default
section "default list is LIFO"
req GET "$BASE/api/comments?pageSize=100"
if [ "$STATUS" = "200" ] \
   && [ "$(printf '%s' "$BODY" | jsonget "d['sortBy']")" = "createdAt" ] \
   && [ "$(printf '%s' "$BODY" | jsonget "d['sortDir']")" = "desc" ]; then
  pass "GET /api/comments defaults to sortBy=createdAt&sortDir=desc (LIFO)"
else
  fail "default sort" "HTTP $STATUS sortBy=$(printf '%s' "$BODY" | jsonget "d['sortBy']") sortDir=$(printf '%s' "$BODY" | jsonget "d['sortDir']")"
fi

# ------------------------------------------------------------- 6. pagination
section "pagination"
req GET "$BASE/api/comments"
TOTAL="$(printf '%s' "$BODY" | jsonget "d['totalItems']")"
PAGESIZE="$(printf '%s' "$BODY" | jsonget "d['pageSize']")"
TOTALPAGES="$(printf '%s' "$BODY" | jsonget "d['totalPages']")"
EXPECTED_PAGES=$(( (TOTAL + PAGESIZE - 1) / PAGESIZE ))
[ "$PAGESIZE" = "25" ] && pass "default pageSize = 25" || fail "default pageSize" "got $PAGESIZE"
[ "$TOTALPAGES" = "$EXPECTED_PAGES" ] && pass "totalPages math ($TOTAL items -> $TOTALPAGES pages)" || fail "totalPages math" "got $TOTALPAGES expected $EXPECTED_PAGES"
req GET "$BASE/api/comments?page=9999"
[ "$STATUS" = "200" ] && [ "$(printf '%s' "$BODY" | jsonget "len(d['items'])")" = "0" ] \
  && pass "out-of-range page -> 200 with empty items" \
  || fail "out-of-range page" "HTTP $STATUS items=$(printf '%s' "$BODY" | jsonget "len(d['items'])")"
req GET "$BASE/api/comments?pageSize=5"
[ "$STATUS" = "200" ] && [ "$(printf '%s' "$BODY" | jsonget "len(d['items'])")" -le 5 ] \
  && pass "pageSize=5 honoured" || fail "pageSize=5" "HTTP $STATUS"

# ---------------------------------------------------------------- 7. preview
section "preview (AJAX, no reload)"
req POST "$BASE/api/preview" -H 'Content-Type: application/json' --data '{"text":"<strong>hi</strong> &amp; <i>x</i>"}'
if [ "$STATUS" = "200" ] \
   && [ "$(printf '%s' "$BODY" | jsonget "d['valid']")" = "True" ] \
   && printf '%s' "$BODY" | jsonget "d['html']" | grep -q "<strong>hi</strong>"; then
  pass "POST /api/preview valid -> sanitized html"
else
  fail "POST /api/preview" "HTTP $STATUS body=$BODY"
fi

# ------------------------------------------------------------------- 8. XSS
section "XSS"
req POST "$BASE/api/preview" -H 'Content-Type: application/json' \
  --data '{"text":"<script>alert(1)</script><i>ok</i>"}'
if printf '%s' "$BODY" | jsonget "d['html']" | grep -qi '<script'; then
  fail "preview XSS" "html still contains <script>: $BODY"
else
  pass "preview strips <script>"
fi

create_comment "SmkXss$RANDOM" '<script>alert(1)</script><i>xsstest</i>'
if [ "$STATUS" = "201" ]; then
  req GET "$BASE/api/comments?pageSize=100"
  if printf '%s' "$BODY" | grep -qi '<script'; then
    fail "stored XSS" "list payload contains <script"
  else
    pass "stored XSS payload is not served as markup"
  fi
elif [ "$STATUS" = "400" ] && [ "$(printf '%s' "$BODY" | jsonget "'text' in d.get('errors',{})")" = "True" ]; then
  pass "comment containing <script> rejected server-side (errors.text)"
else
  fail "XSS comment create" "HTTP $STATUS body=${BODY:0:300}"
fi

req POST "$BASE/api/preview" -H 'Content-Type: application/json' \
  --data '{"text":"<a href=\"javascript:alert(1)\">x</a>"}'
if printf '%s' "$BODY" | jsonget "d['html']" | grep -qi 'javascript:'; then
  fail "attribute XSS" "javascript: survives in href: $BODY"
else
  pass "javascript: href stripped"
fi

# ------------------------------------------------------------------- 9. SQLi
section "SQL injection"
req GET "$BASE/api/comments?pageSize=100&sortBy=%27%20OR%201%3D1%20--"
[ "$STATUS" = "200" ] || [ "$STATUS" = "400" ] && pass "SQLi in sortBy handled (HTTP $STATUS)" || fail "SQLi sortBy" "HTTP $STATUS"
req GET "$BASE/api/comments?page=1%3BDROP%20TABLE%20Comments%3B--"
[ "$STATUS" = "200" ] || [ "$STATUS" = "400" ] && pass "SQLi in page handled (HTTP $STATUS)" || fail "SQLi page" "HTTP $STATUS"
req GET "$BASE/api/health"
[ "$STATUS" = "200" ] && pass "DB alive after SQLi attempts" || fail "DB after SQLi" "health HTTP $STATUS"

# ------------------------------------------------------------------- 10. TXT
section "TXT attachment limits"
python3 -c "open('$TMPDIR_SMOKE/small.txt','w').write('a'*1024)"
python3 -c "open('$TMPDIR_SMOKE/big.txt','w').write('a'*(100*1024+1))"

create_comment "SmkTxt$RANDOM" "hello txt" -F "attachment=@$TMPDIR_SMOKE/small.txt;type=text/plain"
if [ "$STATUS" = "201" ] && [ "$(printf '%s' "$BODY" | jsonget "d['comment']['attachment']['kind']")" = "text" ]; then
  TXT_ATT_ID="$(printf '%s' "$BODY" | jsonget "d['comment']['attachment']['id']")"
  pass "TXT 1KB accepted (kind=text, id=$TXT_ATT_ID)"
  req GET "$BASE/api/attachments/$TXT_ATT_ID"
  if [ "$STATUS" = "200" ] && [ "$(header_value X-Content-Type-Options)" = "nosniff" ] \
     && echo "$(header_value Content-Disposition)" | grep -qi 'inline'; then
    pass "TXT served inline with nosniff"
  else
    fail "TXT headers" "status=$STATUS nosniff=$(header_value X-Content-Type-Options) cd=$(header_value Content-Disposition)"
  fi
  req GET "$BASE/api/attachments/$TXT_ATT_ID/thumb"
  [ "$STATUS" = "404" ] && pass "TXT thumb -> 404" || fail "TXT thumb" "HTTP $STATUS"
else
  fail "TXT 1KB accepted" "HTTP $STATUS body=$BODY"
fi

create_comment "SmkBig$RANDOM" "big txt" -F "attachment=@$TMPDIR_SMOKE/big.txt;type=text/plain"
if [ "$STATUS" = "400" ] && [ "$(printf '%s' "$BODY" | jsonget "'attachment' in d.get('errors',{})")" = "True" ]; then
  pass "TXT >100KB rejected with errors.attachment"
else
  fail "TXT >100KB rejected" "HTTP $STATUS body=$BODY"
fi

# ---------------------------------------------------------------- 11. images
section "image upload / downscale / fetch"
python3 - "$TMPDIR_SMOKE/big.png" <<'PY'
import sys
from PIL import Image
img = Image.new("RGB", (800, 600), (90, 140, 200))
img.save(sys.argv[1], "PNG")
PY

create_comment "SmkImg$RANDOM" "image" -F "attachment=@$TMPDIR_SMOKE/big.png;type=image/png"
if [ "$STATUS" = "201" ]; then
  IMG_ATT_ID="$(printf '%s' "$BODY" | jsonget "d['comment']['attachment']['id']")"
  IMG_W="$(printf '%s' "$BODY" | jsonget "d['comment']['attachment']['width']")"
  IMG_H="$(printf '%s' "$BODY" | jsonget "d['comment']['attachment']['height']")"
  if [ "$IMG_W" -le 320 ] && [ "$IMG_H" -le 240 ] && [ "$IMG_W" -gt 0 ] && [ "$IMG_H" -gt 0 ]; then
    pass "800x600 PNG downscaled to ${IMG_W}x${IMG_H}"
  else
    fail "image downscale" "got ${IMG_W}x${IMG_H}"
  fi
  curl -sS -o "$TMPDIR_SMOKE/served.png" "$BASE/api/attachments/$IMG_ATT_ID"
  SERVER_DIM="$(python3 - "$TMPDIR_SMOKE/served.png" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1])
print(f"{im.width}x{im.height}")
PY
)"
  if [ "$SERVER_DIM" = "${IMG_W}x${IMG_H}" ]; then
    pass "served image is resized (${SERVER_DIM})"
  else
    fail "served image size" "reported ${IMG_W}x${IMG_H}, served $SERVER_DIM"
  fi
  curl -sS -o /dev/null -w '%{http_code}' "$BASE/api/attachments/$IMG_ATT_ID/thumb" > "$TMPDIR_SMOKE/thumb.status"
  [ "$(cat "$TMPDIR_SMOKE/thumb.status")" = "200" ] && pass "image thumb -> 200" || fail "image thumb" "HTTP $(cat "$TMPDIR_SMOKE/thumb.status")"
else
  fail "image upload" "HTTP $STATUS body=$BODY"
fi

# ------------------------------------------------------- 12. attachment 404
section "attachment errors"
req GET "$BASE/api/attachments/999999999"
[ "$STATUS" = "404" ] && pass "unknown attachment -> 404" || fail "unknown attachment" "HTTP $STATUS"

# --------------------------------------------------------- 13. SPA + fallback
section "SPA static hosting"
req GET "$BASE/"
if [ "$STATUS" = "200" ] && printf '%s' "$BODY" | grep -qi '<html'; then
  pass "GET / serves index.html"
else
  fail "GET /" "HTTP $STATUS"
fi
req GET "$BASE/js/app.js"
[ "$STATUS" = "200" ] && pass "GET /js/app.js -> 200" || fail "GET /js/app.js" "HTTP $STATUS"
req GET "$BASE/some/deep/spa/route"
if [ "$STATUS" = "200" ] && printf '%s' "$BODY" | grep -qi '<html'; then
  pass "SPA fallback serves index.html for extensionless route"
else
  fail "SPA fallback" "HTTP $STATUS"
fi
req GET "$BASE/api/does-not-exist"
[ "$STATUS" = "404" ] && pass "unknown /api route -> 404 (not SPA fallback)" || fail "unknown api route" "HTTP $STATUS"

# ----------------------------------------------------------------- summary
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
