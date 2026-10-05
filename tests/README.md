# tests/ — QA test suite

Owner: **qa**. Everything here verifies the frozen contract in [`../docs/API-v2.md`](../docs/API-v2.md)
and the requirements in `TASK.md` / [`../docs/source/task-raw.txt`](../docs/source/task-raw.txt).

Current requirement matrix with per-row evidence: [`../docs/qa/checklist-v2.md`](../docs/qa/checklist-v2.md).
Latest live verification report: [`../docs/qa/report-v2.1.md`](../docs/qa/report-v2.1.md).
Stage-1 artefacts (`docs/API.md`, `docs/qa/checklist.md`, `docs/qa/report.md`) are kept as history
and describe the SQLite / ES-module era.

## Layout

```
tests/
  CommentsApi.Tests/            xUnit integration + security + attachment tests (net10.0)
    Infrastructure/
      TestAppFactory.cs         WebApplicationFactory<Program> + throwaway PostgreSQL DB + storage
      IntegrationTestBase.cs    HTTP helpers, CAPTCHA solving, multipart builder, assertions
      ImageFixtures.cs          deterministic PNG/JPG/GIF generators (ImageSharp)
      ExternalServicesTests.cs  opt-in run against real Redis / Elasticsearch / RabbitMQ
      KeysetPaginationTests.cs, PaginationOverflowTests.cs, SeedConcurrencyTests.cs
    Providers/
      ProviderResolverTests.cs  the provider switchboard: defaults, canonical and legacy keys,
                                strict/fail-fast rules, and that storage=… wires the right adapter
      StorageProviderTests.cs   filesystem hands out a path (and blocks traversal); object stores do not
      S3StorageAdapterTests.cs  the real S3 adapter (AWS SDK, SigV4) against an in-process S3 endpoint
    InfoEndpointTests.cs        GET /api/info provider matrix + the providers block in /api/health
    SanitizerTests.cs           allowlist, tag closing, XHTML, javascript:/data: hrefs
    ValidationTests.cs          server-side field validation + error keys
    CaptchaTests.cs             issuance, one-time use, TTL, charset, case-insensitive match
    CommentsTests.cs            create/list/get, LIFO default, sorting, pagination, reply tree
    QuoteTests.cs               parent-text snapshot stored on reply
    PreviewTests.cs             POST /api/preview
    HealthTests.cs              /api/health, /api/health/live, /api/health/ready
    SecurityTests.cs            XSS (stored/attribute/every field), SQLi, unicode/BOM
    AttachmentTests.cs          size/format limits, magic bytes, downscale, headers
    WebSocketTests.cs           /ws hello, comment.created broadcast, ping/pong
    *UnitTests.cs               pure unit tests (sanitizer, captcha service, validator, quote snapshot)
  e2e/
    smoke.sh                    curl end-to-end smoke against a running stack (PASS/FAIL summary)
    adversarial.sh              XSS/SQLi adversarial corpus, SAFE/VULNERABLE counters
    adversarial-quote.sh/.mjs   quote and ancestor-chain adversarial corpus
    ws-probe.mjs                WebSocket round-trip probe
  frontend/
    angular-check.mjs           real-browser (Playwright) assertions for the Angular SPA
    run-browser-check.sh        wrapper: repo-local Playwright, artifact dir, exit code
    cascade-compare.mjs         pixel comparison of the cascade against the reference sample
    run-cascade-compare.sh      wrapper for the above
    compose-compare.py, measure-reference.py   reference-image measurement helpers
```

## Prerequisites

- .NET SDK 10 (`dotnet --version` → 10.0.x)
- A reachable **PostgreSQL** for the xUnit suite. `scripts/test.sh` starts the compose `postgres`
  service and exports `COMMENTS_TEST_POSTGRES` for you (host port `55432`, user/password `comments`).
  Running `dotnet test` directly requires that variable to point at an admin connection.
- `curl`, `python3` with **Pillow** (`python3 -c "import PIL"`) for `smoke.sh`.
- Node 22+ for `tests/frontend/*` (Playwright is installed into the repo, see below).

## Run the xUnit suite

The supported entry point starts PostgreSQL first and then runs the suite:

```bash
scripts/test.sh                     # start postgres if needed, run everything
CLEAN=1 scripts/test.sh             # also remove bin/obj first (clean room)
scripts/test.sh "FullyQualifiedName~CommentsTests"   # pass extra dotnet test args
```

Direct invocation works too, once `COMMENTS_TEST_POSTGRES` is set:

```bash
export COMMENTS_TEST_POSTGRES="Host=localhost;Port=55432;Username=comments;Password=comments;Database=postgres;Include Error Detail=true"
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj
```

> **NuGet.** The repo has a root `NuGet.config` that keeps the global packages folder inside the
> repository (`.nuget/packages/`), so restore works even where the user-level cache
> (`~/.nuget/packages`) is read-only, and no environment variable is needed. On a normal machine
> the behaviour is the same; the folder is git-ignored and disposable, as are
> `tests/**/bin/`, `tests/**/obj/` and `tests/frontend/node_modules/`.

### Isolation model

Each test instance builds its own `WebApplicationFactory<Program>` with:

- a **throwaway PostgreSQL database** (`comments_test_<guid>`), created in the factory constructor
  and dropped on dispose — migrations are applied by the application's own startup path, so every
  test also exercises the cold-start migration;
- its own temp storage root (`Path.GetTempPath()/comments-qa/<guid>/storage`).

The connection string is injected through the `ConnectionStrings__Default` **environment
variable**, because `Program.cs` reads it during composition — before `WebApplicationFactory`'s
`ConfigureAppConfiguration` callbacks run. Process-wide environment mutation is why
`AssemblyInfo.cs` disables parallelisation (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`).

> Cost note: one database per test means a `CREATE DATABASE` + full migration run + `DROP DATABASE`
> per test (~2 s each), which dominates suite wall time and produces a steady stream of
> `relation "__EFMigrationsHistory" does not exist` errors in the PostgreSQL log (EF's standard
> existence probe on a brand-new database — harmless, but noisy). A template database
> (`CREATE DATABASE ... TEMPLATE comments_test_template`) would reduce both.

Useful variants:

```bash
# single class / single test
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj --filter "FullyQualifiedName~SecurityTests"
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj --filter "FullyQualifiedName~Pagination"

# verbose, with per-test names
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj -l "console;verbosity=normal"
```

### Opt-in: real Redis / Elasticsearch / RabbitMQ

`ExternalServicesTests` verifies the real adapters, not the fallbacks. Without the opt-in variable
the tests are inert (the default suite must not depend on those services):

```bash
COMMENTS_TEST_EXTERNAL_SERVICES=1 \
COMMENTS_TEST_REDIS=localhost:56379 \
COMMENTS_TEST_ELASTIC=http://localhost:59200 \
COMMENTS_TEST_RABBITMQ=amqp://comments:comments@localhost:5672/ \
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj --filter Category=IntegrationBroker
```

## Run the end-to-end suites

Both need a running stack (`docker compose up -d --build`, SPA on `:8080`, API on `:8081`):

```bash
tests/e2e/smoke.sh                       # default http://localhost:8080 (nginx, full stack)
tests/e2e/smoke.sh http://localhost:8081 # direct API port
tests/e2e/adversarial.sh                 # XSS/SQLi corpus
tests/e2e/adversarial-quote.sh           # quote/ancestor corpus
scripts/acceptance.sh                    # clean build + tests + compose + smoke + k6
```

`tests/e2e/smoke.sh` prints a `PASS/FAIL/SKIP` summary and exits non-zero if any check fails.
It covers health, CAPTCHA issue + one-time use, create (201 + `Location` + client identity),
sorting by userName/createdAt in both directions, LIFO default, keyset pagination, preview, XSS
(preview + stored + attribute), SQLi in query params, TXT ≤100 KB accepted / >100 KB rejected,
image downscaled to ≤320×240 and served resized, attachment headers (`nosniff` + `inline`),
attachment 404, SPA `/`, asset 200, SPA fallback, `/api/*` 404 and WebSocket broadcast.

CAPTCHA handling: in Development with `Features:DevCaptchaPeek=true` the code comes from
`GET /api/dev/captcha/{id}`. Against the default Production stack the script falls back to reading
the one-time code from Redis (`CAPTCHA_PEEK=auto|dev|redis`). The dev endpoint is absent in
Production by design.

## Run the frontend browser checks (Playwright)

`tests/frontend/angular-check.mjs` drives the real Angular SPA in Chromium through nginx.

```bash
tests/frontend/run-browser-check.sh                # BASE defaults to http://localhost:8080
tests/frontend/run-browser-check.sh http://localhost:8080
tests/frontend/run-cascade-compare.sh              # cascade vs reference sample, writes a side-by-side PNG
```

The wrapper prefers a repo-local Playwright install (`PLAYWRIGHT_BROWSERS_PATH=$ROOT/.playwright`,
`NPM_CONFIG_CACHE=$ROOT/.npm-cache`) and falls back to the official Playwright container when the
local package or browser is unavailable. Browser and screenshot artefacts land in
`tests/.runtime/ui-artifacts/` (git-ignored).

`angular-check.mjs` asserts: app boot, health badge, root table with 25 rows per page, pagination
control, sortable headers in both directions, cascade rendering and 32 px nesting indent, the
`[i][strong][code][a]` toolbar, client-side validation messages, AJAX preview (and no `<script>`
injection), CAPTCHA refresh, lightbox open/close, TXT modal via `textContent`, and the WebSocket
client connecting with a `comment.created` toast.

## Notes / known limitations

- WebSocket tests use the in-memory `TestServer` (`Factory.Server.CreateWebSocketClient()`) with a
  20 s timeout; the real live round-trip is covered by `tests/e2e/ws-probe.mjs` and `smoke.sh`.
- A real remote `clientIp` cannot be asserted through `TestServer`; `smoke.sh` asserts it against
  the live server behind nginx.
- `tests/.runtime/` holds scratch state from live runs (API logs, storage roots, screenshots) and is
  git-ignored.
