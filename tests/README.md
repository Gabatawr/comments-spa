# tests/ — QA test suite

Owner: **qa**. Everything here verifies the frozen contract in `docs/API.md` and the
requirements in `TASK.md` / `docs/source/task-raw.txt`. The requirement matrix with per-row
evidence lives in [`../docs/qa/checklist.md`](../docs/qa/checklist.md); the final live
verification report is [`../docs/qa/report.md`](../docs/qa/report.md).

## Layout

```
tests/
  CommentsApi.Tests/          xUnit integration + security + attachment tests (net10.0)
    Infrastructure/
      TestAppFactory.cs       WebApplicationFactory<Program> with temp SQLite + storage per instance
      IntegrationTestBase.cs  HTTP helpers, CAPTCHA solving, multipart builder, assertions
      ImageFixtures.cs        deterministic PNG/JPG/GIF generators (ImageSharp)
    SanitizerTests.cs         allowlist, tag closing, XHTML, javascript:/data: hrefs
    ValidationTests.cs        server-side field validation + error keys
    CaptchaTests.cs           issuance, one-time use, TTL, charset, case-insensitive match
    CommentsTests.cs          create/list/get, LIFO default, sorting, pagination, reply tree
    PreviewTests.cs           POST /api/preview
    HealthTests.cs            /api/health + queue counter
    SecurityTests.cs          XSS (stored/attribute/every field), SQLi, unicode/BOM
    AttachmentTests.cs        size/format limits, magic bytes, downscale, headers
    WebSocketTests.cs         /ws hello, comment.created broadcast, ping/pong
  e2e/
    smoke.sh                  curl-based end-to-end smoke (PASS/FAIL summary, non-zero on failure)
```

## Prerequisites

- .NET SDK 10 (`dotnet --version` → 10.0.x)
- `curl`, `python3` with **Pillow** (`python3 -c "import PIL"`) for `smoke.sh`
- For `smoke.sh` the app must run in **Development** with `Features:DevCaptchaPeek=true`
  (default in `appsettings.Development.json`) so `GET /api/dev/captcha/{id}` can solve CAPTCHAs.
  The endpoint is absent in Production by design.

## Run the xUnit suite

```bash
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj
```

> **NuGet.** The repo has a root `NuGet.config` that keeps the global packages folder inside the
> repository (`.nuget/packages/`), so restore works even where the user-level cache
> (`~/.nuget/packages`) is read-only, and no environment variable is needed. On a normal machine
> the behaviour is the same; the folder is git-ignored and disposable, as are
> `tests/**/bin/`, `tests/**/obj/` and `tests/frontend/node_modules/`.
>
> Run the suite **sequentially** (default): the per-factory DB override is applied at
> `CreateBuilder` time, so parallel test hosts can race on environment variables.

Each test uses its own `WebApplicationFactory<Program>` instance, its own temp SQLite file
(`Path.GetTempPath()/comments-qa/<guid>/comments.db`) and its own storage dir, so tests are
isolated and parallel-safe. CAPTCHA codes are obtained through the Development-only
`/api/dev/captcha/{id}` endpoint, with a reflection fallback to the public `ICaptchaService`
if the dev endpoint is missing.

Useful variants:

```bash
# single class / single test
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj --filter "FullyQualifiedName~SecurityTests"
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj --filter "FullyQualifiedName~Pagination"

# verbose, with per-test names
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj -l "console;verbosity=normal"
```

## Run the end-to-end smoke test

Start the app first (Development):

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Backend/CommentsApi --urls http://localhost:5080
```

Then, in another shell:

```bash
tests/e2e/smoke.sh                       # default base URL http://localhost:5080
tests/e2e/smoke.sh http://localhost:8080 # docker compose instance
```

The script prints a `PASS/FAIL/SKIP` summary and exits non-zero if any check fails. It covers:
health, CAPTCHA issue + dev peek, create (201 + Location + client identity), sorting by
userName/createdAt in both directions, LIFO default, pagination math, preview, XSS
(preview + stored + attribute), SQLi in query params, TXT ≤100 KB accepted / >100 KB rejected,
image 800×600 downscaled to ≤320×240 and served resized, attachment headers (`nosniff` +
`inline`), attachment 404, SPA `/`, asset 200, SPA fallback, and `/api/*` 404.

## Run the frontend DOM harness (jsdom)

`tests/frontend/dom-harness.mjs` loads the real `index.html` from a running server into jsdom,
boots the real ES modules (`src/Frontend/js/*.js`) with `fetch` pointed at the live API and a
fake `WebSocket`, then drives the UI. It seeds its own data (26 roots + a reply thread).

```bash
cd tests/frontend
npm install                 # jsdom, dev-only; node_modules is git-ignored
BASE=http://127.0.0.1:5080 node dom-harness.mjs
```

It asserts (exit non-zero on failure): app boot, health badge, root table + 25/page,
pagination control, sortable headers both directions and LIFO default, cascade
expand/indent, the `[i][strong][code][a]` toolbar, client-side validation messages, AJAX
preview (and no `<script>` injection), CAPTCHA refresh, lightbox open/close, TXT modal via
`textContent`, WS client connection and `comment.created` toast.

## Notes / known limitations

- WebSocket tests use the in-memory `TestServer` (`Factory.Server.CreateWebSocketClient()`)
  with a 20 s timeout, plus a real live round-trip is checked manually (`hello → comment.created
  → pong`, see `docs/qa/report.md`).
- A real remote `clientIp` cannot be asserted through `TestServer`; `smoke.sh` asserts it
  against the live server.
- `tests/e2e/smoke.sh` intentionally depends on the Development-only CAPTCHA peek endpoint.
  Running it against a Production container will fail at the CAPTCHA step by design.
  `tests/frontend/dom-harness.mjs` has the same requirement.

