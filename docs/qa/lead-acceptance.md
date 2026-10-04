# Lead acceptance addendum — SPA «Комментарии»

Owner: **lead**. This document records the verification that QA explicitly left to the Lead
(`docs/qa/report.md` §5, `docs/qa/checklist.md` rows `ENV-04`…`ENV-07`, `ART-01`…`ART-07`,
`MID-01`…`MID-05`). It complements, and does not replace, the QA report.

Environment: .NET SDK 10.0.112, Docker 29.4.2 / Compose v5.1.3, Node 24, Linux.
All commands below were executed in this workspace after every writer stopped.

---

## 1. Clean-room build and test (README path)

Package cache and all build output were purged first, so nothing was reused from earlier runs:

```bash
rm -rf .nuget
find . -path ./.git -prune -o -type d \( -name obj -o -name bin \) -print0 | xargs -0 -r rm -rf
rm -rf src/Backend/CommentsApi/wwwroot

dotnet restore src/Backend/CommentsApi/CommentsApi.csproj          # Restored (20.9 s)
dotnet restore tests/CommentsApi.Tests/CommentsApi.Tests.csproj    # Restored (14.3 s)
dotnet build src/Backend/CommentsApi/CommentsApi.csproj --no-restore
#   -> Build succeeded. 0 Warning(s) [1 harmless NU1900] 0 Error(s)
ls src/Backend/CommentsApi/wwwroot/index.html src/Backend/CommentsApi/wwwroot/js/app.js
#   -> both present: the CopySpaToWwwroot MSBuild target ran on a fresh build
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj --no-restore
#   -> Passed! - Failed: 0, Passed: 251, Skipped: 0, Total: 251 (17 s)
```

Result: **PASS** — the repository is reproducible from scratch using only the committed files and
the commands documented in `README.md` / `tests/README.md`.

> `NU1900` (vulnerability-data HTTP cache read-only) is a sandbox artefact, non-fatal; documented
> in `NuGet.config`.

## 2. Docker packaging

```bash
docker compose config --quiet          # exit 0 (valid compose file)

docker compose down -v                 # removed network + both named volumes
docker compose up --build -d           # multi-stage build, fresh volumes

docker inspect --format 'health={{.State.Health.Status}} restarts={{.RestartCount}}' comments-spa
#   -> health=healthy restarts=0 (healthy at 3rd probe)
curl -s http://localhost:8080/api/health
#   -> {"status":"ok","database":"ok","cache":"ok","queue":{"pending":0,"processed":0},
#       "websocket":{"clients":0},"version":"1.0.0"}
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/            # 200 (SPA index)
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/api/comments # 200
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/api/dev/captcha/x # 404 (Production)
```

A defect found and fixed during acceptance: the published `appsettings.json` carried mode `600`,
so the non-root `app` user could not read it and the container crash-looped. The Dockerfile now
uses `COPY --chown=app:app` plus `chown -R`/`chmod -R` on `/app`. Verified by re-running from a
fresh `down -v` + `up --build`: healthy, 0 restarts.

## 3. End-to-end behaviour inside the packaged container

The Development-only CAPTCHA peek is disabled in Production by design, so the full e2e suite was
run against a Development-mode container built from the same image:

```bash
docker run -d --name comments-spa-dev \
  -e ASPNETCORE_ENVIRONMENT=Development -e Features__DevCaptchaPeek=true \
  -p 8081:8080 comments-spa:latest
tests/e2e/smoke.sh http://localhost:8081
#   -> PASS: 38  FAIL: 0  SKIP: 0   (exit 0)
#      incl. 201+Location, sorting both directions, LIFO default, 25/page,
#      XSS + javascript: stripped, SQLi-safe params, TXT >100 KB rejected,
#      800x600 PNG -> 320x240, nosniff+inline, SPA hosting/fallback
```

## 4. Local README path (`scripts/run-dev.sh`)

```bash
./scripts/run-dev.sh 5090
curl -s http://localhost:5090/api/health      # {"status":"ok",...}
scripts/smoke.sh http://localhost:5090         # delegates to tests/e2e/smoke.sh
#   -> PASS: 38  FAIL: 0  SKIP: 0
```

## 5. Mandatory artifacts audit

| Artifact | Status | Evidence |
|----------|--------|----------|
| `README.md` | PASS | what/features/quick-start (Docker + local)/URLs/tests/config/limits; every command executed as written |
| `db/schema.sql` | PASS | MySQL 8 DDL, `comments` self-FK + `attachments`, utf8mb4, indexes, column comments — opens in MySQL Workbench (`File → Open SQL Script`); mapping in `db/schema.md` |
| `Dockerfile` | PASS | multi-stage `sdk:10.0` → `aspnet:10.0`, non-root `app`, curl HEALTHCHECK, `/app/data` + `/app/storage` |
| `docker-compose.yml` | PASS | `config --quiet` OK; port 8080, named volumes, Production env, healthcheck, restart policy |
| `docs/API.md`, `docs/ARCHITECTURE.md` | PASS | frozen contract + architecture/Middle plan |
| `docs/qa/checklist.md`, `docs/qa/report.md` | PASS | requirement matrix with evidence; defects and residual risks |
| Git history | PASS | see §7 |

## 6. Defect reconciliation

| ID | Severity | Final state |
|----|----------|-------------|
| QA-001 | Medium | **Fixed** by backend (strict anchored e-mail regex + length + whitespace); regression test green inside the 251/251 run |
| QA-002 | Info | **Accepted** — leading UTF-8 BOM is consumed by the ASP.NET form reader; documented in `docs/qa/report.md` |
| Docker `appsettings.json` 0600 | High (build-time) | **Fixed** by the Lead in `Dockerfile` (`--chown` + recursive chmod); re-verified healthy |

No other defects are open. QA's adversarial pass found no additional issues.

## 7. Git history (branching evidence)

```text
*   f6a27d1 merge: docker packaging, DB schema and README
|\
| * 6ceb43c build(docker+docs): MySQL Workbench schema, multi-stage image, compose and README
|/
*   7232300 merge: QA suite and verification report
|\
| * 9920169 test(qa): requirement checklist, 251 xUnit checks, e2e smoke and DOM harness
|/
*   a0681dc merge: frontend SPA
|\
| * 6740b3a feat(frontend): build-free SPA — cascade tree, sortable table, AJAX preview, lightbox
|/
*   2aaed9d merge: backend (API, security, Junior+)
|\
| * 1c1624f feat(backend): .NET 10 + EF Core API with XSS/SQLi protection and Junior+ features
|/
* 54d84b8 chore: repository scaffolding, frozen API contract and architecture
```

4 feature branches (`feature/backend`, `feature/frontend`, `feature/qa`, `feature/infra`),
9 commits, 4 `--no-ff` merges, tag `v1.0.0`.

## 8. What is intentionally NOT done (and why)

- **Hosting / VDS deployment** — outside the automation scope defined for this task (requires an
  external account); Docker packaging is provided and verified instead.
- **Middle level implementation** (GraphQL, broker, Redis/Elasticsearch, cloud) — only
  extension points and the architecture plan in `docs/ARCHITECTURE.md`.
- **Middle+ load test** (1M messages / 100k users per 24 h) — architecture documented, no load
  test executed.
- **Pixel-perfect visual diff** against `docs/task/page1-X10.png` — DOM structure, cascade
  indent/quote-bar and CSS were verified, but no pixel comparison was performed.
- **Real 5-minute CAPTCHA wall-clock expiry** — TTL and one-time consumption are unit-tested; the
  actual wait was not performed.
- **Video demo** of the deployed app — a manual step outside the repository.
