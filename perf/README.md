# perf — k6 load testing and dataset seeding

Middle+ load story for SPA «Комментарии»: a realistic k6 mix against the full
compose stack, a seeding path for 100k–1M comments, raw measured results, and a
bottleneck analysis.

> All numbers quoted in `report.md` come from real runs. Raw artifacts live in
> `perf/results/`: `summary-<label>.json` (machine-readable k6 export),
> `k6-<label>.log` (console), `stats-<label>.log` (`docker stats` samples),
> `env-<label>.txt` (dataset size, host, git SHA).

## Layout

```
perf/
  k6/scenarios.js   main load mix (browse, read_one, search, create, websocket)
  k6/smoke.js       1-VU endpoint validation before a real run
  seed.sh           API-first seeding, psql/generate_series fallback, --clear
  run-load.sh       orchestrate: bookkeeping -> seed -> docker stats -> k6
  results/          raw outputs from real runs
  report.md         measurements + bottleneck analysis
  README.md         this file
```

## Prerequisites

The stack must be up and healthy:

```bash
docker compose up --build -d
docker compose ps
```

The `create` and seeding flows need dev endpoints enabled on the `api` service:

```
ASPNETCORE_ENVIRONMENT=Development   # or Load
Features__Seed=true
Features__DevCaptchaPeek=true
```

The compose `load` profile provides the `k6` service with:

```
volumes:
  - ./perf/k6:/scripts:ro
  - ./perf/results:/results
environment:
  BASE_URL=http://api:8080
  WS_URL=ws://api:8080/ws
  SUMMARY_DIR=/results
```

## Seed

```bash
# preferred: POST /api/dev/seed  (idempotent, skips when already satisfied)
./perf/seed.sh --count 100000

# reset and re-create 1M rows, 100k roots
./perf/seed.sh --count 1000000 --roots 100000 --clear

# force the direct-DB fallback (docker exec psql + generate_series)
./perf/seed.sh --count 100000 --via db --container comments-spa-postgres
```

Flags: `--count`, `--roots`, `--depth`, `--batch`, `--clear`, `--via api|db|auto`,
`--base-url`, `--container`. The DB path is a single set-based `INSERT ... SELECT
FROM generate_series(...)`, so 1M rows do not stream through the host. It is
idempotent (counts existing rows, appends only up to `--count`) and verifies the
final count. Env: `BASE_URL`, `PG_CONTAINER`, `PGDATABASE`, `PGUSER`.

Note: raw-DB seeding bypasses `CommentCreated` events, so Elasticsearch is not
populated that way. The API seed endpoint also does not publish events in the
current build, so only normal `POST` creates index documents — `search` latency
is real but the index stays small (see `report.md` §5.5).

## Run

Quick sanity check (1 VU, one iteration per flow):

```bash
docker compose --profile load run --rm k6 run /scripts/smoke.js
```

Full measured run, default (dataset must already be seeded to the size you want):

```bash
docker compose --profile load run --rm k6 run /scripts/scenarios.js
```

Recommended orchestrated form — records dataset size, seeds, samples
`docker stats`, and writes named raw files:

```bash
./perf/run-load.sh --label 100k-full --count 100000 --scale 1 --steady 60
./perf/run-load.sh --label 1m-full  --count 1000000 --scale 1 --steady 60
```

> **Dev-override trap (hit for real).** `docker compose ... run` reconciles the
> dependencies from the config files it is given. A base-file-only
> `--profile load run` silently recreates `api` in **Production** and kills
> `Features:Seed`/`DevCaptchaPeek` mid-run. `run-load.sh` always adds
> `infra/compose.dev.yml` (override with `PERF_PROD=1` for a deliberate
> Production run). For manual commands, export it once:
> `export COMPOSE_FILE=docker-compose.yml:infra/compose.dev.yml`.
> The API seed path needs Development; the DB path works in Production too.


Or invoke the compose service directly with overrides:

```bash
docker compose --profile load run --rm \
  -e RUN_LABEL=100k-browse -e STEADY_S=90 -e SCALE=1 \
  -e SEARCH_ENABLED=true -e CREATE_ENABLED=true -e WS_ENABLED=true \
  k6 run /scripts/scenarios.js
```

### Scenarios and mix

| Scenario | Flow | Default VUs (steady) |
|---|---|---|
| `browse` | `GET /api/comments?sortBy=createdAt\|userName\|email&sortDir=asc\|desc&page=1..20&pageSize=25` (full reply trees, exercises Redis page cache + DB) | 15 |
| `read_one` | `GET /api/comments/{id}` for ids sampled in `setup()` | 8 |
| `search` | `GET /api/search?q=...&page=1..5` (Elasticsearch) | 5 |
| `create` | `GET /api/captcha` → `GET /api/dev/captcha/{id}` → `POST /api/comments/{parentId}/child` (JSON) | 2 |
| `websocket` | connect `ws://api:8080/ws`, expect `{"type":"hello"}`, measure connect latency | 5 |

Every scenario uses `ramping-vus`: ramp-up (`RAMP_S`, default 20 s), steady
(`STEADY_S`, default 60 s), ramp-down (`RAMP_S`). `SCALE` multiplies every VU
target (e.g. `SCALE=2` doubles the load).

### Thresholds (SLO)

```
http_req_duration            p(95)<500ms   p(99)<1500ms
http_req_failed              rate<1%
checks                       rate>99%
http_req_duration{scenario:browse|read_one|search}  p(95)<500ms  p(99)<1500ms
http_req_duration{scenario:create}                  p(95)<800ms  p(99)<2000ms
ws_connect_ms                p(95)<500ms
```

k6 exits `99` when a threshold fails; the raw summary is still written. A failed
threshold is reported honestly in `report.md`, not hidden.

### Env overrides

| Var | Default | Meaning |
|---|---|---|
| `BASE_URL` | `http://api:8080` | REST base (host runs use `http://localhost:8081`) |
| `WS_URL` | `ws://api:8080/ws` | WebSocket URL |
| `SCALE` | `1` | multiply all VU targets |
| `RAMP_S` / `STEADY_S` | `20` / `60` | stage durations (s) |
| `SEARCH_ENABLED` | `true` | include the ES scenario (set `false` if ES off) |
| `CREATE_ENABLED` | `true` | include the write scenario |
| `WS_ENABLED` | `true` | include the WebSocket scenario |
| `RUN_LABEL` | `default` | output file label |
| `SUMMARY_DIR` | `/results` | where `summary-<label>.json` is written |

## Reading the results

```bash
python3 perf/summarize.py perf/results/summary-100k-full.json
```

prints RPS, global p95/p99, per-scenario p95/p99, error rate, WebSocket
latency and threshold pass/fail from the raw k6 export.

- `http_reqs` → total requests; divide by steady-state duration for RPS.
- Per-scenario latency uses the auto `scenario` tag; request tags
  (`endpoint`, `sortBy`, `sortDir`) allow finer slicing in the JSON.
- `perf/results/stats-*.log` gives per-container CPU/mem during the run; the
  load generator and API share the same 6 vCPU host, which is called out in the
  report.

## Rules

- Only this project's compose resources are used; no `docker prune`, no other
  projects' containers/images/volumes.
- No cloud calls, no secrets.
