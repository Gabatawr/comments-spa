# QA Checklist v2 — SPA «Комментарии» (целевой уровень Middle+)

- **Task**: task-10 (QA: e2e + adversarial verification + checklist v2)
- **Owner**: `qa` (independent/adversarial verification)
- **Contract under test**: `docs/API-v2.md` (FROZEN), `docs/ARCHITECTURE-v2.md`, `docs/target-middle-plus.md`
- **Status legend**: `VERIFIED` — executed by QA with raw evidence below · `NOT VERIFIED` — not executed by QA (owner/scope) or not exercised · `FAILED` — contract violated, defect recorded · `FIXED` — defect confirmed fixed with new repro/evidence.

> Honesty rule: every `VERIFIED` row cites a real command or a captured log under
> `tests/.runtime/qa-logs/`. Rows QA did not run are explicitly `NOT VERIFIED` — no assumptions.

## 0. Verification runs (final, raw)

**Definitive clean-start run (Lead `docker compose down -v && up --build -d`, fresh volumes, Production, code frozen):**

| Suite | Result | Log |
|---|---|---|
| `tests/e2e/smoke.sh` | **PASS 93 / FAIL 0 / SKIP 0**, exit 0 | `tests/.runtime/qa-logs/clean-smoke.log` |
| `tests/e2e/adversarial.sh` | **SAFE 77 / VULNERABLE 0 / ACCEPTED-RISK 1 / UNEXPECTED 0 / SKIP 0**, exit 0 | `tests/.runtime/qa-logs/clean-adversarial.log` |

Earlier runs (kept for traceability):

| Suite | Result | Log |
|---|---|---|
| xUnit (PostgreSQL) | **Passed 258 / Failed 0 / Skipped 0**, 22 m 54 s | `tests/.runtime/qa-logs/xunit-pg.log` |
| `tests/e2e/smoke.sh` (pre-task-13 image) | PASS 92 / FAIL 1 / SKIP 0 (the FAIL was QA-302, now fixed) | `tests/.runtime/qa-logs/final-smoke3.log` |
| `tests/e2e/smoke.sh` (Production image) | PASS 92 / FAIL 0 / SKIP 0, exit 0 | `tests/.runtime/qa-logs/smoke-v2-run5.log` |
| `tests/e2e/adversarial.sh` (pre-fix) | SAFE 75 / VULNERABLE 0 / ACCEPTED-RISK 2 / UNEXPECTED 1 (QA-302) | `tests/.runtime/qa-logs/final-adversarial.log` |
| Playwright real-browser check | **PASS 23 / FAIL 0 / SKIP 0**, exit 0 | `tests/.runtime/qa-logs/final-browser2.log` |

Reproduce:

```bash
bash tests/e2e/smoke.sh http://localhost:8080                         # + PROD_BASE=<prod-url>
ADV_LOG=/tmp/adv.log PROD_BASE=<prod-url> bash tests/e2e/adversarial.sh http://localhost:8080 http://localhost:8081
BASE=http://localhost:8080 bash tests/frontend/run-browser-check.sh http://localhost:8080
COMMENTS_TEST_POSTGRES="Host=localhost;Port=55432;Username=comments;Password=comments;Database=postgres" \
  dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj -c Debug
```

CAPTCHA solving in the e2e scripts: dev peek when the stack is Development, otherwise the
one-time code is read from the Redis store (`docker exec comments-spa-redis redis-cli GET captcha:<id>`),
so the default Production compose stack is fully testable without weakening hardening.

---

## 1. Environment / infrastructure

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| ENV-1 | One command `docker compose up --build -d` brings up API + nginx + PostgreSQL + Redis + RabbitMQ + Elasticsearch | VERIFIED | `docker compose ps`: 6/6 `healthy`; api `:8081`, web `:8080`, pg `:55432`, redis `:56379`, rabbit `:5672/15672`, ES `:59200` |
| ENV-2 | One origin: nginx serves SPA and proxies `/api/*`, `/graphql`, `/ws` | VERIFIED | `GET http://localhost:8080/` → 200 HTML; `/api/health` via nginx → 200; WS test via `ws://localhost:8080/ws` → hello + comment.created |
| ENV-3 | Healthchecks + `depends_on: condition: service_healthy` | VERIFIED | `docker compose ps` all healthy; api container health `healthy` |
| ENV-4 | Clean-room restart from empty volumes | NOT VERIFIED (qa) | Lead's final acceptance owns `down -v && up --build` |
| ENV-5 | Management ports exposed (RabbitMQ, ES) | VERIFIED | `:15672`, `:59200` mapped in `docker compose ps` |
| ENV-6 | No cloud calls / no external network in mandatory path | VERIFIED | no cloud SDK packages in `src/**/*.csproj`; all endpoints local |

## 2. Architecture / static verification

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| ARC-1 | Separate `Domain`/`Application`/`Infrastructure`/`Api` projects, dependencies point inward | VERIFIED | `Comments.Domain.csproj` has 0 refs; `Application` → Domain only; `Infrastructure` → Application; `Api` → Application + Infrastructure |
| ARC-2 | No EF/ASP.NET/Redis/RabbitMQ/Elastic packages in Domain/Application | VERIFIED | `grep -E 'EntityFrameworkCore|StackExchange|RabbitMQ|Elastic|AspNetCore|Npgsql' …Domain.csproj …Application.csproj` → no matches |
| ARC-3 | Ports/interfaces in Application | VERIFIED | `Abstractions/`: `ICacheService`, `ICaptchaStore`, `IClock`, `IDatabaseHealthCheck`, `IEventBus`+`IEventPublisher`+`IEventConsumer`, `ICommentRepository`, `IAttachmentRepository`, `IUnitOfWork`, `ICommentSeeder`, `ICommentSearchIndex`, `IFileStorage` |
| ARC-4 | No raw SQL concatenation | VERIFIED | `grep -rnE 'FromSqlRaw|ExecuteSqlRaw|SqlQuery|ExecuteSqlInterpolated' src/Backend` → NONE (EF LINQ only; seed uses parameterised binary COPY) |
| ARC-5 | PostgreSQL provider + migrations in repo + idempotent start | VERIFIED | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3; `Persistence/Migrations/20261004144633_InitialCreate.*`; `MigrationRunner` uses `pg_advisory_lock`; app start runs `ApplyMigrationsAsync` |
| ARC-6 | `db/schema-postgres.sql` + legacy MySQL `db/schema.sql` | VERIFIED | both files present |
| ARC-7 | No `node_modules`/`dist` committed | VERIFIED | `git ls-files src/Frontend/node_modules src/Frontend/dist` → empty |
| ARC-8 | Angular built multi-stage (node → nginx) | VERIFIED | `src/Frontend/Dockerfile` stage 1 `node:22-alpine` `ng build --production`, stage 2 `nginx:alpine`; image `comments-spa-web:local` served on :8080 |

## 3. Base (v1) requirements

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| BASE-1 | Create comment + reply, cascade of any depth | VERIFIED | smoke: 5-level chain created, `GET /api/comments/{root}` depth=5; browser: depth-1 nested reply rendered |
| BASE-2 | Sort roots by User Name / E-mail / date, both directions; LIFO default | VERIFIED | smoke 7 checks: 6 directions + case-insensitive `lower()`; browser: aria-sort ascending→descending + monotonic page |
| BASE-3 | Pagination 25, out-of-range page empty | VERIFIED | smoke: pageSize=25, totalPages math, page `totalPages+1000` → 200 empty; clamping 100 / 25 |
| BASE-4 | Form validation (userName latin+digits, strict e-mail, homePage URL, text, CAPTCHA) | VERIFIED | smoke 400-shape checks; xUnit `CommentValidatorUnitTests` in the 258 green |
| BASE-5 | CAPTCHA real PNG, TTL 300 s, one-time | VERIFIED | smoke: data:image/png, `expiresInSeconds=300`, no code leaked, replay 400, unknown id 400, alphabet excludes 0/O/1/I |
| BASE-6 | XSS: allow-list `a[href,title] code i strong` + tag closure | VERIFIED | smoke + adversarial: 12 stored-XSS variants → 400 `errors.text`; preview escapes `<script>`, drops `onerror`/`javascript:`/`data:text/html` |
| BASE-7 | SQL-injection safe | VERIFIED | smoke + adversarial: 16 probes in fields and query params (`sortBy/sortDir/page/pageSize/cursor/q`) → 200/400, no 500; DB alive |
| BASE-8 | Attachments JPG/GIF/PNG ≤5 MB resized ≤320×240, TXT ≤100 KB, TXT `/thumb` 404 | VERIFIED | smoke: 800×600 → 320×240, served resized, inline + nosniff; TXT 1 KB accepted / 100 KB+1 rejected |
| BASE-9 | All v1 endpoints/error shapes preserved | VERIFIED | xUnit **258/258** on PostgreSQL; smoke health/captcha/comments/preview/attachments 404/etc. |

## 4. Junior+ requirements

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| JNR-1 | Queue / background consumer | VERIFIED | `health.queue {pending, processed}` present; processed counter advances (`processed=1254` observed) |
| JNR-2 | In-process cache | VERIFIED | `/api/stats` `cacheHitRate ≈ 0.88`; `health.cache=ok` |
| JNR-3 | Domain events | VERIFIED | broker-fed `comment.created` with `eventId` observed over WS |
| JNR-4 | WebSocket `/ws` | VERIFIED | smoke: `hello`, `comment.created` for the created user, `ping`→`pong`; browser: live toast + row insert with no page reload |

## 5. Middle requirements

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| MID-1 | Clean Architecture (see ARC-1..3) | VERIFIED | static grep above |
| MID-2 | PostgreSQL instead of SQLite, migrations from repo, idempotent | VERIFIED | xUnit 258/258 against real PostgreSQL; `comments_id_seq` consistent; migrations applied on container start; `health.database=ok` |
| MID-3 | Redis cache + Redis CAPTCHA store, graceful degradation | VERIFIED | `health.redis=ok`; one-time code physically present at `captcha:{id}` (used by e2e); cache invalidated via generation version |
| MID-4 | RabbitMQ events + DLX/DLQ + retry, publisher confirms | NOT VERIFIED | Broker fed events verified (`health.broker=ok`, WS event over broker). DLQ/retry **behaviour** not exercised by QA (topology code/config present) |
| MID-5 | WebSocket broadcast via broker for multiple API instances | NOT VERIFIED | Broker-fed single-replica broadcast VERIFIED. Multi-instance fan-out not tested; API-v2 §7.1 per-instance auto-delete queue being finalised by core |
| MID-6 | GraphQL endpoint (HotChocolate): query + mutation, depth≤15, cost≤300 | VERIFIED | smoke: `comments`, `comment(id)`, `createComment` (success + bad-CAPTCHA payload) ; adversarial: depth-22 and 400-alias queries rejected with GraphQL errors |
| MID-7 | Elasticsearch search + async indexing + `/api/search` | VERIFIED | smoke: created comment indexed and found via `/api/search?q=<token>` within 15 s; `health.search=ok`; search DTO shape + `q` required 400 |
| MID-8 | clientIp + userAgent stored and returned; `X-Forwarded-For` first hop | VERIFIED | smoke: `XFF: 203.0.113.7, 10.0.0.1` → `clientIp=203.0.113.7`; userAgent truncated to 512; adversarial: single/multi-hop/empty-hop/X-Real-IP + 64-char truncation |
| MID-9 | Full stack via one `docker compose up --build` | VERIFIED (infra-run) | 6/6 healthy; see ENV-1. QA observed the running stack; QA did not run `up --build` itself |
| MID-10 | README with verified self-check | NOT VERIFIED (qa) | Lead owns README; QA did not run the README steps |
| MID-11 | Schema files (PostgreSQL + MySQL Workbench) | VERIFIED | `db/schema-postgres.sql`, `db/schema.sql` present |
| MID-12 | Angular replaces the old SPA | VERIFIED | Angular standalone app in `src/Frontend`; old ES-module files deleted (git status); browser 23/23 |

## 6. Middle+ requirements

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| MP-1 | Indexes + keyset pagination without OFFSET on deep pages | VERIFIED | migration defines `ix_comments_created_at_id`, `lower(user_name)`, `lower(email)`, `parent_created`; smoke keyset: `nextCursor`, page-2 no overlap, cursor/sort mismatch → 400 |
| MP-2 | No N+1 / BFS tree / batched writes / cache / async processing | NOT VERIFIED (qa) | Design-level; perf report covers measured behaviour. QA verified only the observable keyset/cache/search-async paths |
| MP-3 | Horizontal scalability (state outside process) | NOT VERIFIED | Single-replica compose verified; multi-instance API + shared Redis/RabbitMQ not exercised |
| MP-4 | k6 load test really run with numbers + bottleneck report | NOT VERIFIED (qa) | `perf` owner; `perf/k6` + `perf/report.md` exist; numbers not independently verified by QA |
| MP-5 | Seed endpoints for test data (dev/load profile) | VERIFIED / defect | Dev stack `POST /api/dev/seed` 200 (`count=3000` in 261 ms, 11494 rec/s); Production → 404. 1M API seed timeout was OPEN (task-12) → **FIXED** per core (~566 s, 200) |
| MP-6 | `/api/stats` + `/api/dev/*` guarded | VERIFIED | smoke `/api/stats` shape; Production: dev captcha 404, dev seed 404 |
| MP-7 | Cloud-ready seams documented, no cloud calls | VERIFIED | ports in Application; local adapters in Infrastructure; API-v2 §11 swap table; no cloud packages/secrets |

## 7. Cloud-ready

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| CLD-1 | External deps behind interfaces in Application | VERIFIED | interface list ARC-3 |
| CLD-2 | Default local implementations | VERIFIED | FileSystemStorage, Redis/Memory cache, RabbitMQ/in-memory bus, Elasticsearch index, Npgsql |
| CLD-3 | Swap points documented (S3/Azure Blob, Service Bus, ElastiCache, OpenSearch, Cloud SQL) | VERIFIED | `docs/API-v2.md` §11 table |
| CLD-4 | Config only via env/appsettings, no plaintext secrets in repo | VERIFIED | `.env.example` only non-secret local defaults; compose uses `${VAR:-default}`; no credentials in tracked source |
| CLD-5 | No real cloud calls in the mandatory startup path | VERIFIED | no cloud SDKs; all services local containers |

## 8. Frontend (§2.6) — real-browser verification

Playwright + headless Chromium 153 (repo-local `.playwright/`), against the real Angular bundle served by nginx :8080; 23/23 PASS.

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| FE-1 | Add form with CAPTCHA | VERIFIED | `[data-testid=captcha-image]` renders with pixels (`naturalWidth>0`); form present |
| FE-2 | Root table with sorting both directions | VERIFIED | click `sort-user-name`: page monotonic ascending, second click descending, `aria-sort` ascending→descending |
| FE-3 | Pagination 25 | VERIFIED | ≤25 rows; `data-page-size=25`; `page-next` navigates (page 1→2, rows change) |
| FE-4 | Cascade with indent + left quote bar | VERIFIED | `reply-toggle` expands `reply-cascade`; depth-1 `comment-card`; nested container x > root x; `border-left-width > 0` |
| FE-5 | AJAX preview without reload/navigation | VERIFIED | preview renders sanitized `<strong>`; URL unchanged and `window.__qaNoReload` marker survives |
| FE-6 | Toolbar `[i] [strong] [code] [a]` | VERIFIED | clicking buttons wraps the selection; tag marker present in textarea |
| FE-7 | Client-side validation mirroring server | NOT VERIFIED | Angular has validators, but QA did not drive invalid-field submissions in the browser |
| FE-8 | Lightbox for images, modal for TXT | NOT VERIFIED | Components/templates exist; not exercised by QA browser pass |
| FE-9 | WebSocket live update | VERIFIED | external create → `[data-testid=ws-toast]` with the new user AND the row prepended, no reload |
| FE-10 | CSS per `docs/task/page1-X10.png` | NOT VERIFIED | no pixel/visual diff performed |
| FE-11 | Angular Docker multi-stage build | VERIFIED | see ARC-8 |
| FE-12 | No `node_modules` in repo | VERIFIED | see ARC-7 |

## 9. QA / test-suite requirements

| ID | Requirement | Status | Evidence |
|---|---|---|---|
| QA-1 | Unit + integration tests against real dependencies | VERIFIED | xUnit factory creates a unique `comments_test_<guid>` DB on real PostgreSQL and drops it on dispose |
| QA-2 | Existing 251 tests continue to pass on PostgreSQL | VERIFIED | **258 passed / 0 failed** (7 added, none weakened/removed) |
| QA-3 | e2e script extended for v2 | VERIFIED | `tests/e2e/smoke.sh` — 93 checks incl. health v2, GraphQL, search, stats, child, WS, Production 404s |
| QA-4 | Adversarial pass | VERIFIED | `tests/e2e/adversarial.sh` — SAFE 75 / VULNERABLE 0 |
| QA-5 | Requirement checklist with honest gaps | VERIFIED | this file |

---

## 10. Adversarial pass — summary

Raw per-attack command + response: `tests/.runtime/qa-logs/final-adversarial.log`.

| Class | Probes | Verdict |
|---|---|---|
| Stored XSS (12 variants: script, img/onerror, svg/onload, iframe, body/onload, mixed case, `javascript:`, `data:`, style, userName/email/homePage, null-byte, unclosed tag) | 12 | all rejected `400 errors.<field>` |
| Preview XSS sinks | 7 | escaped/dropped, no dangerous sink survives |
| XSS via GraphQL mutation / JSON child | 2 | payload rejected |
| SQL injection (fields + `sortBy/sortDir/page/pageSize/cursor/q`) | 16 | 200/400, no 5xx; DB alive |
| Spoofed `X-Forwarded-For` / `X-Real-IP` | 6 | first-hop stored; 64-char truncation; malicious XFF kept verbatim (see RISK-1) |
| Attachments (oversize TXT, 0-byte, HTML-as-PNG, 2 MB binary, traversal filename, headers) | 8 | rejected except traversal-safe accept; inline + nosniff |
| Malformed multipart / JSON / GraphQL | 10 | 4xx, never 5xx |
| CAPTCHA replay (sequential + concurrent), wrong/unknown code | 4 | one-time enforced |
| Pagination abuse (`pageSize`/`page` extremes, 100 KB cursor, search limits) | 12 | clamped/400; one overflow defect QA-302 |
| GraphQL depth/aliasing/introspection | 3 | depth-22 and cost-4000 rejected; introspection off in Production |
| Production hardening (dev captcha/seed 404, introspection off, GET /graphql 405) | 4 | all PASS |

**ACCEPTED-RISK items (documented, not defects against the frozen contract):**
- RISK-1 `X-Forwarded-For: <script>alert(1)</script>` is stored verbatim in `clientIp`. API-v2 §6 explicitly keeps invalid values; UI renders it as text. Depends on `Proxy__TrustAll=true` being scoped to the isolated compose network — must be set to a known-proxy list if the API is ever exposed directly.
- RISK-2 GraphQL introspection enabled on the **Development** stack (only appeared while perf held the api in Development). On the clean Production run introspection is disabled (HTTP 400) and this risk does not apply.

**Earlier adversarial `UNEXPECTED=6`** (first run) were **my harness bugs**, not product issues: the XFF/retention cases posted JSON to the multipart-only `POST /api/comments`. Fixed to multipart; the clean Production run has **0 UNEXPECTED** (the only intermittent one was the QA-302 overflow probe, now fixed).

## 11. Defects

| ID | Severity | Status | Description / repro |
|---|---|---|---|
| **QA-301** | High | **FIXED** (core, task-11 P0) | `POST /api/comments` returned 500 during a concurrent `/api/dev/seed` bulk append; Postgres `duplicate key value violates unique constraint "PK_comments"` (14:59:37Z). Cause: seeder used explicit ids `MAX(id)+i` and only `setval`-ed after the COPY, so a concurrent normal INSERT drew a stale `nextval` inside the COPY range → 23505. **New repro (post-fix)**: `POST /api/dev/seed {count:3000,roots:300,depth:2}` then 10 parallel `POST /api/comments` → **10/10 HTTP 201, 0 duplicate-key errors**. |
| **QA-302** | Medium | **FIXED — re-verified** (core, task-13) | Offset integer overflow: `(page-1)*pageSize` in int32 → negative `OFFSET` → HTTP 500 (Postgres `OFFSET must not be negative`). Pre-fix repro: `curl 'http://localhost:8080/api/comments?page=2000000000'` → 500. **Re-verified on the clean Production stack**: smoke 93/93 includes `huge page (2000000000) -> 200 empty`; adversarial `huge page offset (107374183) -> 200` SAFE. Contract §4.4 satisfied. |
| **QA-303** | Low | **FIXED** (core) | Custom `UseExceptionHandler` swallowed 500s without logging. Now every 5xx logs `LogError(error, "Unhandled exception for {Method} {Path} -> {Status}")` with the stack trace. |
| **QA-304** | Medium | **FIXED** (core, task-12) | `POST /api/dev/seed` for 1M rows timed out. Core: 1M seed returns 200 in ~566 s and the DB auto-creates on first start. |
| **QA-305** | Cosmetic | **FIXED** (core, P2) | `health.queue.pending` observed as `-1`. After the rebuild `pending=0`; `health.broker` now reports `error` when RabbitMQ genuinely fails. |

## 12. Honest gaps / NOT VERIFIED by QA

1. **Clean-room `docker compose down -v && up --build -d`** and the **README self-check** — Lead's final acceptance owns these.
2. **k6 movement numbers / bottleneck report** — `perf` owner; QA did not independently reproduce them.
3. **DLQ/retry behaviour** (MID-4) and **multi-instance WS fan-out** (MID-5, API-v2 §7.1 per-instance queue) — not exercised.
4. **Frontend lightbox / TXT modal** (FE-8) and **client-side validation errors** (FE-7) — implemented but not driven in the browser pass.
5. **Visual fidelity** to `docs/task/page1-X10.png` (FE-10) — DOM/CSS assertions only, no pixel diff.
6. **Production 404 checks** — resolved: verified on the definitive clean Production stack (api `ASPNETCORE_ENVIRONMENT=Production`, `Features__DevCaptchaPeek=false`, `Features__Seed=false`, 0 rows at start): dev captcha 404, dev seed 404, introspection 400, GET /graphql 405.
7. **QA-302 re-verification** — done on the post-task-13 image (see §11); `page=2000000000` and `page=107374183` now both return 200 with empty `items`.
8. The old jsdom `tests/frontend/dom-harness.mjs` was **deliberately replaced** by the Playwright browser check (per task-10) and removed; history remains in git.

## 13. Verdict

On the **definitive clean Production stack** (`down -v && up --build -d`, fresh volumes, frozen code) QA recorded **smoke 93/93 PASS** and **adversarial 77 SAFE / 0 VULNERABLE / 0 UNEXPECTED**, plus **xUnit 258/258 on PostgreSQL** and **Playwright 23/23**. The two open defects found during the leak (QA-301, QA-302) are **FIXED and re-verified**; the 1M seed timeout (QA-304/task-12) and the 500 logging gap (QA-303) are FIXED. The residual `NOT VERIFIED` items are owner-scope (Lead/perf) or explicit coverage gaps listed in §12 — they are **not** silent passes.
