# QA checklist — SPA «Комментарии»

Owner: **qa**. Sources: `TASK.md`, `docs/source/task-raw.txt`, `docs/API.md` (FROZEN v1),
`docs/ARCHITECTURE.md`, reference UI `docs/task/page1-X10.png`.

Status legend: `pass` = observed command+output; `fail` = observed defect; `blocked` = dependency
missing; `not verified` = not yet executed / cannot execute (reason given); `pass (part)` = one
layer verified, the other still pending.
**No row is marked `pass` without a real observed command and output excerpt.**

Evidence tokens:
- **R1** = full suite: `dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj` →
  `Passed! - Failed: 0, Passed: 251, Skipped: 0, Total: 251` (see run log).
- **H1** = DOM harness: `cd tests/frontend && BASE=… node dom-harness.mjs` → `PASS: 31, FAIL: 0`.
- **E1** = `tests/e2e/smoke.sh <base>` → `PASS: 38, FAIL: 0, SKIP: 0`.
- **W1** = live WebSocket round-trip → `hello -> comment.created -> pong`.
- **P1** = Production-mode instance → `/api/dev/captcha/{id}` = 404; `/api/health`, `/api/captcha`,
  `/`, SPA fallback = 200.
- Test names are in `tests/CommentsApi.Tests/*.cs`; `S:…` = static audit (grep/read).
- Full evidence: `docs/qa/report.md`.

---

## A. Environment / stack / mandatory tooling

| ID | Requirement (source) | Verification method | Expected result | Status | Evidence | Owner |
|----|----------------------|---------------------|-----------------|--------|----------|-------|
| ENV-01 | .NET latest + EF Core (TASK.md «Обязательный стек») | `dotnet build`; inspect csproj | net10.0, EF Core 10.0.12 Sqlite, ImageSharp 3.1.12 | pass | R1 build succeeded; `CommentsApi.csproj` TargetFramework net10.0 + `Microsoft.EntityFrameworkCore.Sqlite 10.0.12` | backend |
| ENV-02 | SQL relational DB (SQLite/PostgreSQL/MS SQL) (raw p.2) | integration tests on temp SQLite files | SQLite provider, migrations auto-applied | pass | R1 — 251 tests against `Data Source=<temp>/comments.db` | backend |
| ENV-03 | Frontend on a framework choice (TASK.md); team chose static ES-module SPA | static files; live serving + functional checks | SPA served at `/`, no build step | pass | E1 SPA asset sweep: `/`, `/index.html`, `/css/app.css`, `/assets/favicon.svg`, all `/js/*.js` → 200 with correct content types; H1 boots and drives the real modules | frontend |
| ENV-04 | Git repository with branching history (raw p.4) | `git log --oneline --graph` | ≥1 commit, meaningful history | pass | Lead: 4 feature branches (`feature/backend`, `feature/frontend`, `feature/qa`, `feature/infra`) each merged with `--no-ff`; graph in `lead-acceptance.md` §7 | lead |
| ENV-05 | Docker packaging with full environment (raw p.4) | `docker compose build && up -d` | container healthy, app reachable | pass | Lead: `docker compose config --quiet` OK; `down -v` then `up --build -d` → `healthy`, `restarts=0`, `/api/health` 200 | lead |
| ENV-06 | Reproducible from scratch strictly per README (raw p.4) | follow README in clean checkout | app starts, URLs work | pass | Lead: purged `.nuget`+`bin`/`obj`/`wwwroot`, then `dotnet restore && build && test` → 0 errors, **251/251**; `scripts/run-dev.sh` → health 200 + E1 38 PASS | lead |
| ENV-07 | Load/user scale architecture + load test for Middle+ (raw p.6) | read ARCHITECTURE Middle+ section + load test | documented | pass (part) | Lead: `docs/ARCHITECTURE.md` «Middle (заложено)» / Middle+ 1M·100k plan; load test intentionally out of scope per brief | lead |
| ENV-08 | OOP design (raw p.2) | inspect structure/DI | domain/services/validation/infrastructure, DI | pass | S:`ls src/Backend/CommentsApi` → Domain, Services, Validation, Infrastructure, Dtos, Data, Endpoints; Program.cs DI | backend |

## B. API contract (`docs/API.md`, FROZEN)

| ID | Requirement | Verification method | Expected result | Status | Evidence | Owner |
|----|-------------|---------------------|-----------------|--------|----------|-------|
| API-01 | `GET /api/captcha` → `{captchaId,image(data:image/png;base64),expiresInSeconds:300}`; code never returned | `CaptchaTests.Issue_returns_png_data_url_without_the_code`; E1 | 200, PNG data URL, no code field | pass | R1 PNG magic `89 50 4E 47`, TTL 300, no `"code"`; E1 captcha step PASS | backend |
| API-02 | CAPTCHA wrong answer rejected 400 `errors.captcha` | `CaptchaTests.Wrong_answer_is_rejected` | 400 + captcha error | pass | R1 | backend |
| API-03 | CAPTCHA one-time: reuse rejected | `CaptchaTests.Reusing_a_captcha_id_is_rejected` | second use 400 | pass | R1 | backend |
| API-04 | CAPTCHA expiry ≤5 min / TTL honored | `CaptchaServiceUnitTests.Generate_…` + code review | 300 s TTL | pass (part) | R1; TTL constant 300 via IMemoryCache; wall-clock expiry not waited out (documented) | backend |
| API-05 | `GET /api/comments` root-only + full nested tree | `CommentsTests.Nested_reply_tree…` | roots only, full `replies` tree | pass | R1 — totalItems 1, direct replies 2, grandchild nested | backend |
| API-06 | Default list = LIFO (`createdAt desc`) | `CommentsTests.Default_list_is_lifo…`; E1 | newest root first | pass | R1 order [3rd,2nd,1st]; E1 LIFO step PASS | backend |
| API-07 | Sort by `userName`,`email`,`createdAt` × `asc`,`desc` | `CommentsTests.Sort_by_*`; E1 | ordering correct both directions | pass | R1 6 sort assertions; E1 userName+createdAt both directions PASS | backend |
| API-08 | Pagination default 25; `totalItems`/`totalPages` roots only; page OOR → empty items | `CommentsTests.Pagination_*`; E1 | 25/page, math correct | pass | R1 30 roots → 25/page, totalPages 2; E1 default pageSize 25 + totalPages math + OOR empty PASS | backend |
| API-09 | `GET /api/comments/{id}`; 404 if missing | `CommentsTests.Get_by_id…` | 200 tree / 404 | pass | R1 | backend |
| API-10 | `POST /api/comments` multipart, 201 `{comment}` + `Location` | `CommentsTests.Create_returns_201…`; E1 | 201, Location, echo body | pass | R1 + E1 `Location: /api/comments/{id}`, ISO-8601 UTC | backend |
| API-11 | 400 body `{title,status,errors{…}}` | `ValidationTests.*`, `SecurityTests.*` | exact keys, English messages | pass | R1 | backend |
| API-12 | `POST /api/preview` JSON → `{valid,html,plain,errors}`, 200 even invalid | `PreviewTests.*`, `SanitizerTests.*`; E1 | sanitized html + errors | pass | R1 + E1 preview step PASS | backend |
| API-13 | `GET /api/attachments/{id}` inline, `nosniff`, `Content-Disposition`; 404 unknown | `AttachmentTests.*`; E1 | headers present | pass | R1 + E1 TXT inline/nosniff + 404 PASS | backend |
| API-14 | `GET /api/attachments/{id}/thumb` image only; TXT → 404 | `AttachmentTests.Thumb_*`; E1 | thumb ≤320×240, TXT 404 | pass | R1 + E1 image thumb 200 / TXT thumb 404 PASS | backend |
| API-15 | `GET /api/health` all sections | `HealthTests…`; E1 | status ok + all keys | pass | R1 + E1 health step PASS | backend |
| API-16 | `WS /ws` hello, `comment.created`, ping→pong, invalid JSON ignored | `WebSocketTests.*`; W1 | messages as contract | pass | R1 + W1 `hello -> comment.created -> pong` | backend |
| API-17 | `/api/dev/captcha/{id}` only Development + flag; absent in Production | R1 (dev) + P1 | 200 dev, 404 prod | pass | R1 dev peek drives CAPTCHA flows; P1 Production → 404 while health/captcha/SPA 200 | backend |
| API-18 | `clientIp` + `userAgent` persisted (raw p.1) | `CommentsTests.Create_…`; E1 | non-empty | pass | R1 userAgent; E1 `client identity persisted (clientIp)` PASS | backend |
| API-19 | Dates ISO-8601 UTC | `CommentsTests.Create_…` regex | `…Z` | pass | R1 | backend |

## C. Add-comment form

| ID | Requirement | Verification method | Expected result | Status | Evidence | Owner |
|----|-------------|---------------------|-----------------|--------|----------|-------|
| FORM-01 | User Name required, `^[A-Za-z0-9]+$`, 3..50 | `CommentValidatorUnitTests.*`, `ValidationTests.*` | missing/cyrillic/space/symbols rejected | pass | R1 (49/49 validator filter) | both |
| FORM-02 | E-mail required, valid format, ≤100 | `CommentValidatorUnitTests.Email_*`, `ValidationTests.Email_*` | invalid rejected | pass | R1 — includes QA-001 regression | both |
| FORM-03 | Home page optional, absolute http(s), ≤200; empty→null | `CommentValidatorUnitTests.HomePage_*` | empty null, invalid rejected | pass | R1 | both |
| FORM-04 | CAPTCHA image, latin+digits, 6 chars, no 0/O/1/I | `CaptchaServiceUnitTests.*`, `CaptchaTests.*` | `^[A-HJ-NP-Z2-9]{6}$` | pass | R1 | backend |
| FORM-05 | Text required 1..5000 | `CommentValidatorUnitTests.Text_*`, `ValidationTests.Text_*` | 0/5001 rejected, 5000 accepted | pass | R1 | both |
| FORM-06 | Text: only allowlist tags | `HtmlSanitizerUnitTests.*`, `SanitizerTests.*` | `<b>`,`<script>` not preserved | pass | R1 | backend |
| FORM-07 | Client-side validation mirrors server, inline messages | H1 + `S:js/validate.js` | messages under fields | pass | H1: empty User Name / invalid E-mail / empty Text each show an inline error | frontend |
| FORM-08 | `parentId` must reference existing comment | `ValidationTests.Nonexistent_parentId…` | 400 parentId error | pass | R1 | backend |
| FORM-09 | Attachment optional; one image or TXT | `AttachmentTests.*` | accepted/rejected per rules | pass | R1 | both |

## D. Main page / list

| ID | Requirement | Verification method | Expected result | Status | Evidence | Owner |
|----|-------------|---------------------|-----------------|--------|----------|-------|
| LIST-01 | Unlimited cascade replies, indent + left quote bar | `CommentsTests.*tree*` + H1 | deep tree, indent+quote bar | pass | R1 API tree; live 25-level chain; H1 cascade expand → `.comment-children` | both |
| LIST-02 | Root comments in a table | H1 + `S:index.html` | `<table>` with sortable header | pass | H1 rows rendered from `/api/comments`; `S:index.html` `#comments-table` + `th[data-sort]` | frontend |
| LIST-03 | Sortable headers User Name/E-mail/Date both directions + arrows | H1 + `S:comments.js` | toggles asc/desc + arrow | pass | H1 User Name asc→desc (`aria-sort` + request), Date defaults descending; `S:comments.js` ▲▼ | frontend |
| LIST-04 | 25 messages per page | R1 + H1 | pageSize 25 default | pass | R1 API default 25; H1 ≤25 rows and page-2 navigation request | both |
| LIST-05 | Prev/next + page numbers | H1 + `S:comments.js` | pager works | pass | H1 pagination control with page numbers + page=2 request | frontend |
| LIST-06 | Default sort LIFO | R1 + H1 | date desc on load | pass | R1 API; H1 Date header aria-sort=descending on load | both |
| LIST-07 | Reply counts / expand cascade | H1 + R1 `replyCount` | «N ответов» + expand | pass | H1 toggle expands cascade; `replyCount` validated by R1 | frontend |
| LIST-08 | «Ответить» prefills parentId | H1 | parentId set | pass | H1 click → hidden `#parentId` filled + reply banner visible | frontend |
| LIST-09 | CSS design per reference | `S:css/app.css` + H1 DOM | close to reference | pass (part) | DOM structure/classes + 24.9 KB CSS verified; no pixel diff performed | frontend |
| LIST-10 | XSS-safe rendering of stored HTML | `SecurityTests.*` + H1 | no script execution | pass | R1 stored payloads safe; H1 preview never injects `<script>`; `S:sanitize.js` allowlist | both |
| LIST-11 | Empty state / loading / error rendering | `S:index.html`/app.js | graceful | not verified | not exercised in H1; static markup present | frontend |

## E. HTML sanitizer / regex / XHTML

| ID | Requirement (raw p.3) | Verification method | Expected result | Status | Evidence | Owner |
|----|-----------------------|---------------------|-----------------|--------|----------|-------|
| SAN-01 | Allowed tags kept `a[href,title]`, `code`, `i`, `strong` | `HtmlSanitizerUnitTests.Allowed_tags…` | preserved exactly | pass | R1 | backend |
| SAN-02 | Disallowed tags removed/escaped | `HtmlSanitizerUnitTests.Disallowed_elements…` | no executable output; escaped tag name, attrs dropped | pass | R1 — `<img src=x onerror=…>` → `&lt;img&gt;`; live preview same | backend |
| SAN-03 | Only `href`,`title` on `<a>`; strip event/style attrs | `HtmlSanitizerUnitTests.Disallowed_attributes…` | event attrs gone | pass | R1 | backend |
| SAN-04 | `javascript:` href removed (case/whitespace/entities) | `…Dangerous_href_schemes…`, `SanitizerTests.Javascript_href…` | href dropped | pass | R1 — 6 obfuscations; E1 javascript: stripped | backend |
| SAN-05 | `data:` href removed | `…Dangerous_href_schemes…`, `SanitizerTests.Data_href…` | href dropped | pass | R1 | backend |
| SAN-06 | Tag-closing check: unclosed/mismatched reported | `HtmlSanitizerUnitTests.Unclosed…/Mismatched…` | errors non-empty | pass | R1 | backend |
| SAN-07 | Output valid XHTML | `HtmlSanitizerUnitTests.Ampersand…/Every_allowed…` | parseable XML, `&amp;` | pass | R1 — `XDocument.Parse` succeeds | backend |
| SAN-08 | Frontend sanitizer mirrors allowlist | `S:js/sanitize.js` + H1 preview | same allowlist | pass | `S:sanitize.js` ALLOWED_TAGS = a[href,title]/code/i/strong; H1 preview uses it | frontend |
| SAN-09 | `<a href="javascript:…">` neutralized end-to-end | `SecurityTests.Attribute_based_xss…`; E1 | no javascript: stored | pass | R1 + E1 attribute XSS step PASS | backend |
| SAN-10 | Odd tags/attribute quoting safe | `HtmlSanitizerUnitTests.Malformed…` + live | no executable output | pass | R1; live run: `<i><strong>x</i></strong>`, `<A HREF>`, `<svg/onload>`, `</p>` all safe | backend |

## F. Files / attachments

| ID | Requirement (raw p.3) | Verification method | Expected result | Status | Evidence | Owner |
|----|-----------------------|---------------------|-----------------|--------|----------|-------|
| FILE-01 | One image OR one text file | `AttachmentTests.*`; E1 | kind image/text | pass | R1 + E1 TXT/image create | both |
| FILE-02 | JPG, GIF, PNG accepted | `AttachmentTests.Accepted_image_formats` | 201 kind=image | pass | R1 | backend |
| FILE-03 | Other formats rejected (BMP/WEBP/TIFF) | `AttachmentTests.Unsupported…` | 400 attachment | pass | R1 real BMP/TIFF/WebP bytes + declared-extension rejection | backend |
| FILE-04 | Image >320×240 proportionally downscaled; never upscaled | `AttachmentTests.Downscale_*`; E1 | ≤320×240, ratio ±2px | pass | R1 640×480→320×240, 1000×300→320×96, 300×600→120×240; E1 800×600→320×240 and served resized | backend |
| FILE-05 | Small image not upscaled | `AttachmentTests.Small_image…` | 100×80 stays | pass | R1 | backend |
| FILE-06 | Image max 5 MB | `AttachmentTests.Image_over_5mb…` | 400 attachment | pass | R1 — real 7.7 MB PNG rejected | backend |
| FILE-07 | TXT only, ≤100 KB (102400 B) | `AttachmentTests.Txt_*`; E1 | boundary accepted/rejected | pass | R1 exactly-100 KB accepted / +1 B rejected; E1 >100 KB rejected with `errors.attachment` | backend |
| FILE-08 | Non-TXT non-image rejected | `AttachmentTests.Non_txt_non_image…` | 400 attachment | pass | R1 PDF/ZIP/HTML; live 2 MB bin rejected | backend |
| FILE-09 | Magic-byte sniffing (spoofed type) | `AttachmentTests.Spoofed_content_type…` | 400 | pass | R1; live text-as-PNG → 400 | backend |
| FILE-10 | Served inline, `nosniff`, safe filename, TXT utf-8 | `AttachmentTests.*`; live | headers per contract | pass | R1 + E1 `text/plain; charset=utf-8`, `inline`, `nosniff`; traversal filename → `passwd.txt` | backend |
| FILE-11 | Lightbox for images | H1 | lightbox opens image | pass | H1 `openLightbox` → `#lightbox.is-open` aria-hidden=false; close works | frontend |
| FILE-12 | TXT modal uses `textContent` | H1 + `S:attachments.js` | safe text rendering | pass | H1 real TXT attachment opens in modal, body has text and 0 element children; `S` textContent only | frontend |
| FILE-13 | Client checks size/type with feedback | `S:js/attachments.js` | pre-upload validation | pass (part) | static audit of size/type checks; not exercised via a real file picker in H1 | frontend |

## G. JS / AJAX / UI

| ID | Requirement (raw p.3 JS) | Verification method | Expected result | Status | Evidence | Owner |
|----|--------------------------|---------------------|-----------------|--------|----------|-------|
| AJAX-01 | Server-side + client-side validation | R1 + H1 | both exist and agree | pass | R1 server; H1 inline client errors; `S:validate.js` mirrors limits | both |
| AJAX-02 | Preview without reload | R1 + H1 | panel updates, no navigation | pass | R1 endpoint; H1 preview renders `<strong>`, href unchanged, no `<script>` | both |
| AJAX-03 | Toolbar `[i][strong][code][a]` wraps selection | H1 | inserts tags at selection | pass | H1 all four buttons wrap the selected text | frontend |
| AJAX-04 | `[a]` prompts href+title | H1 | href/title inserted | pass | H1 `<a href="https://example.com" title="T">link</a>` | frontend |
| AJAX-05 | Visual effects | H1 + `S:css/app.css` | visible effects | pass (part) | H1 lightbox/`is-open` transitions + toasts; CSS animations static-audited, not visually diffed | frontend |
| AJAX-06 | CAPTCHA refresh | H1 | new captchaId requested | pass | H1 refresh → new id + `data:image/png;base64` image | frontend |
| AJAX-07 | WS toast on `comment.created`, reconnect, ping/pong | H1 + W1 | toast + refresh | pass | H1 server message → toast with userName; W1 real socket ping→pong | both |
| AJAX-08 | Multipart field names exactly per contract | R1 + `S:js/api.js` | exact names | pass | R1 multipart accepted; `S:api.js` FormData fields | both |
| AJAX-09 | 400 `errors` rendered per-field | `S:js/app.js` + H1 | messages mapped | pass (part) | `S:app.js` renderServerErrors; H1 covers client-side inline errors, server 400 mapping static | frontend |

## H. Security

| ID | Requirement (raw p.1/p.4) | Verification method | Expected result | Status | Evidence | Owner |
|----|---------------------------|---------------------|-----------------|--------|----------|-------|
| SEC-01 | Stored XSS in `text` | `SecurityTests.Script_in_text…` + live | no `<script` stored/served | pass | R1 + live: disallowed tag → 400; list payload never contains `<script` | backend |
| SEC-02 | Attribute XSS `href=javascript:` | `SecurityTests.Attribute_based_xss…` | no `javascript:` | pass | R1 + live preview | backend |
| SEC-03 | XSS via every field | `SecurityTests.Xss_in_other_fields…` + live | rejected/escaped | pass | R1 + live 400 per field | backend |
| SEC-04 | SQLi in fields stored, not executed | `SecurityTests.Sqli_in_form_fields…` + live | row intact, list works | pass | R1 + live `'; DROP TABLE Comments; --` stored as text, health ok | backend |
| SEC-05 | SQLi in `sortBy`/`sortDir`/`page`/`pageSize` | `SecurityTests.Sqli_in_query_params…` + live | 400 or clamped, DB intact | pass | R1 + live encoded payloads 200/400, DB alive | backend |
| SEC-06 | SQLi in `parentId` | `SecurityTests.Sqli_or_malformed_parentId…` | 400, table intact | pass | R1 | backend |
| SEC-07 | EF Core LINQ only, no raw/concat SQL | static audit | none | pass | `S:grep FromSqlRaw/ExecuteSqlRaw/SqlQuery/"SELECT …"` → NONE | backend |
| SEC-08 | Malformed/oversized/spoofed uploads safe | `AttachmentTests.*` + live | 400, no 500 | pass | R1 + live 0-byte, 2 MB, spoofed type all 400 | backend |
| SEC-09 | Unicode/BOM/odd input no crash | `SecurityTests.Unicode_bom…` + live | 200/400, no 500 | pass | R1; QA-002 leading-BOM accepted (arbitrated); non-leading BOM/RLM → 400 | backend |

## I. Junior+ (raw p.5)

| ID | Requirement | Verification method | Expected result | Status | Evidence | Owner |
|----|-------------|---------------------|-----------------|--------|----------|-------|
| JR-01 | Queue (`Channel` + BackgroundService), POST does not await | `HealthTests.Queue_processed…` + DI audit | processed increments | pass | R1; live health `queue.processed` grows; `S:Program.cs` | backend |
| JR-02 | Cache IMemoryCache, invalidated on create | `CommentsTests.List_cache_is_invalidated…` | fresh list after create | pass | R1 | backend |
| JR-03 | Events IEventBus + CommentCreatedEvent | DI audit + health | event wired | pass | `S:Program.cs` AddSingleton<IEventBus, InMemoryEventBus>; Infrastructure/Events | backend |
| JR-04 | WebSocket hub, broadcast, health client count | R1 + W1 + health | hello/broadcast/clients | pass | R1 + W1 + live health `websocket.clients` | backend |
| JR-05 | Health reflects db/cache/queue/ws/version | `HealthTests…`; E1 | all sections | pass | R1 + E1 | backend |
| JR-06 | CAPTCHA store in cache, 5-min TTL | `CaptchaServiceUnitTests.*` | 300 s | pass | R1 | backend |

## J. Mandatory artifacts (raw p.4)

| ID | Artifact | Verification method | Expected result | Status | Evidence | Owner |
|----|----------|---------------------|-----------------|--------|----------|-------|
| ART-01 | README.md what/features/quick start/tests | read + run commands | reproducible | pass | Lead: all README commands executed as written (docker + local + tests); `lead-acceptance.md` §5 | lead |
| ART-02 | `db/schema.sql` MySQL Workbench compatible | open file, compare to entities | valid DDL, self-FK, indexes, utf8mb4 | pass | Lead: MySQL 8 DDL, `comments` self-FK + `attachments`, utf8mb4, indexed, column comments; type map in `db/schema.md` | lead |
| ART-03 | Dockerfile multi-stage, non-root, healthcheck | read + build | builds & runs | pass | Lead: sdk→aspnet multi-stage, `USER app`, HEALTHCHECK on `/api/health`; image builds and reports `healthy` | lead |
| ART-04 | docker-compose service/port/volume/env | `docker compose config` + up | healthy | pass | Lead: `config --quiet` OK; port 8080, named data/storage volumes, Production env; container healthy | lead |
| ART-05 | Git history | `git log --oneline --graph` | multi-commit | pass | Lead: 9 commits incl. 4 merge commits; `lead-acceptance.md` §7 | lead |
| ART-06 | Live deployment/hosting | manual | out of automation scope, noted | not verified (out of scope) | hosting/VDS excluded by the task brief; Docker packaging verified instead (`lead-acceptance.md` §3–4) | lead |
| ART-07 | Self-check from scratch per README | clean clone + README | works | pass | Lead: clean-room purge + README path, `docker compose up --build` from empty volumes, e2e 38/38 inside the container | lead |
| ART-08 | `tests/README.md` accurate | commands used during this task | accurate | pass | every command in tests/README.md was used for R1/H1/E1 | qa |

## K. Middle / Middle+ (architecture only, out of implementation scope)

| ID | Requirement (raw p.6) | Verification method | Expected result | Status | Evidence | Owner |
|----|-----------------------|---------------------|-----------------|--------|----------|-------|
| MID-01 | Graph (GraphQL/GraphDb) path | read ARCHITECTURE Middle | documented | pass (documented) | Lead: `docs/ARCHITECTURE.md` «Middle (заложено)» — read-model as GraphQL resolver point | lead |
| MID-02 | Broker path | read ARCHITECTURE Middle | documented | pass (documented) | Lead: `IEventBus` documented as the broker swap point | lead |
| MID-03 | NoSQL path | read ARCHITECTURE Middle | documented | pass (documented) | Lead: `CommentCache` documented as Redis/Elasticsearch swap point | lead |
| MID-04 | Cloud path | read ARCHITECTURE Middle | documented | pass (documented) | Lead: env config + stateless web layer documented | lead |
| MID-05 | 1M/100k architecture + load test | read docs + artifacts | documented | pass (part) | Lead: partitioning/CQRS/rate-limit plan documented; load test not implemented (out of scope) | lead |

---

## Verification runs log

| # | Command | Result |
|---|---------|--------|
| 1 | `dotnet build tests/CommentsApi.Tests/CommentsApi.Tests.csproj` | Build succeeded, 0 errors (early skeleton) |
| 2 | `dotnet test … --filter "FullyQualifiedName~UnitTests"` | 98 passed / 1 failed (QA-001 e-mail laxness found) |
| 3 | `dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj` | 233 / 17 (pre-isolation: shared SQLite DB + over-strict tests) |
| 4 | `dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj` | 250 / 1 (after DB-isolation fix) |
| 5 | **`dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj`** | **Passed: 251, Failed: 0, Skipped: 0** (18 s) |
| 6 | `… --filter "FullyQualifiedName~CommentValidatorUnitTests"` | Passed: 49, Failed: 0 |
| 7 | `… --filter "FullyQualifiedName~HtmlSanitizerUnitTests"` | Passed: 40, Failed: 0 |
| 8 | `… --filter "FullyQualifiedName~CaptchaServiceUnitTests"` | Passed: 10, Failed: 0 |
| 9 | `tests/e2e/smoke.sh http://127.0.0.1:5085` | **PASS: 38, FAIL: 0, SKIP: 0** (exit 0) |
| 10 | `cd tests/frontend && BASE=http://127.0.0.1:5086 node dom-harness.mjs` | **PASS: 31, FAIL: 0** (exit 0) |
| 11 | live WS (`ws-live.mjs`) against :5085 | `hello -> comment.created -> pong` |
| 12 | Production instance :5083 → `GET /api/dev/captcha/{id}` | 404 (dev-only); health/captcha/SPA 200 |
| 13 | live adversarial pass (`curl`, see report.md §3) | no new defects |
| 14 | Lead: `docker compose down -v && docker compose up --build -d`; dev-container E1; clean-room `dotnet test` | **healthy** (0 restarts); E1 **PASS 38/0**; tests **251/0**; prod dev-peek 404 |

### Environment notes (affect reproducibility)

- In this sandbox `~/.nuget/packages` is read-only; root `NuGet.config` points
  `globalPackagesFolder` at `.nuget/packages` (git-ignored with the disposable
  `tests/.nuget-packages/`).
- All integration tests run sequentially (`tests/CommentsApi.Tests/AssemblyInfo.cs`,
  `DisableTestParallelization`) because the temp DB path is passed through process-wide
  environment variables (`ConnectionStrings__Default`), which `Program.cs` reads at
  `WebApplication.CreateBuilder` time — the factory's in-memory configuration is too late.
- Frontend harness deps live in `tests/frontend/node_modules` (git-ignored); `npm install` there
  before running H1.
