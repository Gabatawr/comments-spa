# QA verification report — SPA «Комментарии»

> **Исторический документ (этап 1: SQLite + SPA на ES-модулях).** Актуальный отчёт —
> [`report-v2.1.md`](report-v2.1.md).

- **Task**: task-5 (QA verification, adversarial pass, final report)
- **Owner**: qa
- **Verdict**: **PASS** for the scope verified here (backend API, security, attachments,
  WebSocket, static SPA hosting, SPA frontend behaviour). Mandatory-artifact/Docker/git items
  remain with the Lead (task-6/task-7) and are listed as unverified below.
- **Companion documents**: requirement matrix `docs/qa/checklist.md`; suite docs `tests/README.md`.

## 1. Environment

| Item | Value |
|---|---|
| OS / shell | Linux, bash |
| .NET SDK | 10.0.112 (net10.0) |
| Node.js / npm | v24.21.0 / 11.19.0 |
| Python | python3 + Pillow 12.3.0 (smoke fixtures) |
| DB | SQLite via EF Core 10.0.12; temp file per test; controlled files for live runs |
| Backend | `src/Backend/CommentsApi` (Debug DLL run for live checks) |
| Frontend | static ES modules from `src/Frontend`, copied to `wwwroot` at build |
| Live ports used by QA | 5081–5085 (controlled instances; runtime data under `tests/.runtime`, deleted after) |
| NuGet note | sandbox `~/.nuget/packages` is read-only; root `NuGet.config` points `globalPackagesFolder` at `.nuget/packages`. Tests run sequentially (`tests/CommentsApi.Tests/AssemblyInfo.cs`) because the temp DB is injected through process-wide env vars read at `WebApplication.CreateBuilder` time. |

## 2. Commands and results

All commands below were run from the repo root unless stated otherwise.

| # | Command | Result |
|---|---------|--------|
| 1 | `dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj` | **Passed: 251, Failed: 0, Skipped: 0, Total: 251 (18 s)** |
| 2 | `… --filter "FullyQualifiedName~CommentValidatorUnitTests"` | Passed: 49, Failed: 0 |
| 3 | `… --filter "FullyQualifiedName~HtmlSanitizerUnitTests"` | Passed: 40, Failed: 0 |
| 4 | `… --filter "FullyQualifiedName~CaptchaServiceUnitTests"` | Passed: 10, Failed: 0 |
| 5 | `tests/e2e/smoke.sh http://127.0.0.1:5085` | **PASS: 38, FAIL: 0, SKIP: 0** (exit 0) |
| 6 | `cd tests/frontend && BASE=http://127.0.0.1:5086 node dom-harness.mjs` | **PASS: 31, FAIL: 0** (exit 0) |
| 7 | live WebSocket round-trip (`tests/.runtime/ws-live.mjs`) | `hello -> comment.created -> pong` |
| 8 | Production-mode instance: `curl /api/dev/captcha/{id}` | **404** (dev-only endpoint absent), `/api/health`, `/api/captcha`, `/`, SPA fallback all 200 |
| 9 | SPA asset sweep (`curl` on every `index.html` asset + module graph) | all 200 with correct `Content-Type` (`text/css`, `text/javascript`, `image/svg+xml`) |
| 10 | static audit: `grep -rnE "FromSqlRaw\|ExecuteSqlRaw\|SqlQuery" src/Backend` | NONE — EF Core LINQ only |

Live SPA asset sweep output:

```
200 text/html; charset=utf-8  /                200 text/javascript  /js/app.js
200 text/html                 /index.html      200 text/javascript  /js/api.js
200 text/css                  /css/app.css     200 text/javascript  /js/comments.js
200 image/svg+xml             /assets/favicon.svg   … all module files 200
200 SPA fallback /comments/123
```

### DOM harness (frontend behaviour, jsdom boots the real modules)

29/29 checks passed in the first run; with the reply-prefill and pagination-navigation checks
added the final run is **31/31**: app boot; health badge; root table rendered; ≤25 rows/page;
pagination control and page-2 navigation; User Name header sorts ascending then descending;
Date header defaults to descending (LIFO); reply thread expands to a nested cascade with an
indented children container; «Ответить» prefills the hidden `parentId` and shows the reply
banner; toolbar `[i] [strong] [code] [a]` wrap the selection (`[a]` inserts href+title);
client validation shows inline errors for empty User Name, invalid E-mail, empty Text; AJAX
preview renders sanitized HTML without navigation and never injects `<script>`; CAPTCHA refresh
fetches a new id + PNG; lightbox opens/closes; TXT attachment opens in the modal rendered via
`textContent`; WebSocket client connects to `/ws` and a `comment.created` message raises a toast.

## 3. Live adversarial pass

Raw commands were `curl` against a controlled Development instance; a representative script is
reproduced in `tests/.runtime/adv.sh` (deleted with the runtime dir; findings summarised here).

| Attack | Payload / command | Observed | Verdict |
|---|---|---|---|
| Stored XSS in `text` | `<script>alert(1)</script>` | 400 `errors.text = "Tag <script> is not allowed."`; nothing stored | safe |
| Attribute XSS | `<img src=x onerror=alert(1)>` | 400 `errors.text`; via `/api/preview` → `&lt;img&gt;` (attributes dropped) | safe |
| `javascript:`/`data:` href | `<a href="javascript:alert(1)">`, `<a href="data:text/html,x">`, entities/case/whitespace | 400 `errors.text`; preview drops the href | safe |
| XSS in userName/email/homePage | `<script>…`, `"><script>…`, `javascript:` | 400 per-field errors | safe |
| SQLi in form fields | `'; DROP TABLE Comments; --` in text | 201, stored verbatim as text; table intact; health ok | safe |
| SQLi in query params | encoded `sortBy=' OR 1=1--`, `sortBy=userName);DROP TABLE…`, `sortDir=asc;DELETE…`, `page=1 UNION SELECT 1--`, `pageSize=25;DROP TABLE…` | 200 (unknown sort falls back) or 400 for non-integer page/pageSize; DB alive | safe |
| Malformed/odd tags | `<i><strong>x</i></strong>`, `<!-- -->`, `</p>`, `<A HREF="HTTPS://X">`, `<svg/onload=1>`, `<i style="x">` | 200 preview, output well-formed/escaped, attributes dropped | safe |
| Malformed JSON / non-multipart / empty multipart | `{not json`, JSON on `/api/comments`, `-F x=1` | 400 (no 500) | safe |
| Garbage CAPTCHA id | `/api/dev/captcha/not-a-guid` | 404 | safe |
| Spoofed extension/content-type | text bytes named `evil.png` `type=image/png` | 400, "File content does not match an allowed image format" | safe |
| Actual unsupported formats | real BMP/TIFF/WebP | 400 attachment error | safe |
| 0-byte upload | empty `.png` | 400 "Uploaded file is empty." | safe |
| 2 MB binary | `big.bin` | 400 attachment error | safe |
| Oversized TXT | 102 401 bytes | 400 `errors.attachment` | safe |
| TXT boundary | exactly 102 400 bytes | 201 | safe |
| Path traversal filename | `../../etc/passwd.txt` | 201, stored/served `filename="passwd.txt"`, `nosniff`, inline | safe |
| Header injection filename | `evil\".txt` | 201; `Content-Disposition` contains no raw quote/CRLF | safe |
| Deep reply nesting | 25-level reply chain | created 25/25; `GET /api/comments/{root}` serves depth 25 | safe |
| CAPTCHA reuse / wrong | same id twice, wrong answer | first 201, reuse 400, wrong 400 `errors.captcha` | safe |
| Unicode/control chars | BOM + emoji + Arabic + RTL + `\x01\x02` | 201, no crash | accepted |
| Huge/negative paging | `pageSize=100000` → 200 clamped to 100; `pageSize=-5` → default 25; `page=-1` → clamped to 1 | safe (contract allows) | accepted |
| Production dev endpoint | `ASPNETCORE_ENVIRONMENT=Production`, `GET /api/dev/captcha/{id}` | 404 | safe |

**No new security defects were found in the live adversarial pass.**

## 4. Defects

| ID | Severity | Status | Description / repro |
|----|----------|--------|---------------------|
| QA-001 | Medium | **Fixed** (backend) | `CommentValidator.ValidateEmail` used `EmailAddressAttribute`, which only requires one `@`; emails with spaces/angle brackets/quotes were accepted. Repro: `dotnet test … --filter "FullyQualifiedName~CommentValidatorUnitTests.Email_invalid"` → `"spaces in@example.com"` returned `null` (valid). Now a strict anchored regex + whitespace trim + ≤100; regression covered and green. |
| QA-002 | Info (accepted) | **Accepted by Lead** | A leading UTF-8 BOM in a multipart field is consumed by ASP.NET Core's form reader, so `"\uFEFFUniUser02"` decodes to `UniUser02` and is accepted (201). Only position 0 is affected; `"\uFEFF UniUser02"`, `"Uni\uFEFFUser03"`, `"Uni\u200FUser03"` are all 400. Test documents this and is green. |

Test-suite issues found and fixed during verification (not product defects): shared SQLite DB
across test hosts (fixed with env-var injection + sequential collections), too-strict
unsupported-format fixtures (now real BMP/TIFF/WebP bytes), and a non-idempotent smoke script
on a shared database (now uses a unique per-run sort tag).

## 5. Residual risks / unverified items

- **Docker** (`ENV-05`, `ART-03`, `ART-04`): `docker compose build/up` not run by QA — Lead's
  task-6/task-7.
- **README reproducibility, git history, db/schema.sql, deployment** (`ENV-04`, `ENV-06`,
  `ENV-07`, `ART-01`, `ART-02`, `ART-05`–`ART-07`): Lead scope.
- **Captcha wall-clock expiry**: TTL constant (300 s) and one-time consumption are verified; a
  real 5-minute wait was not performed (documented, low risk).
- **Visual fidelity to `docs/task/page1-X10.png`**: DOM structure, cascade indent/quote-bar
  containers and CSS are verified by static audit + jsdom; no pixel diff was performed.
- **Middle / Middle+ architecture** (`MID-01`…`MID-05`): architecture-only, out of
  implementation scope; not reviewed by QA.
- **Stack deviation**: the task prefers Angular but allows a free frontend choice; the team used
  a no-build vanilla ES-module SPA (documented in `docs/ARCHITECTURE.md`). SQLite chosen over
  MS SQL/PostgreSQL (allowed).
- The live checks used a controlled instance and a shared instance on :5080; all `tests/.runtime`
  data (DBs, storage, logs) is scratch and was removed before hand-off.

## 6. Checklist reconciliation

Every row in `docs/qa/checklist.md` carries `pass` / `pass (part)` / `not verified` with a real
command/output reference. Rows marked `not verified` are exclusively the Lead-owned artifact
items and the out-of-scope Middle section listed in §5.
