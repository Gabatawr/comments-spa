# Infra — stage 2 (Middle+)

Owner: `infra`. Write scope: `docker-compose.yml`, `infra/**`, `.env.example`,
`.dockerignore`. See `AGENTS.md` (never prune / never touch other projects'
Docker resources) and `docs/API-v2.md` §7–§10.

## Layout

| Path | Purpose |
|---|---|
| `compose.dev.yml` | Override enabling Development mode (dev seed endpoint + captcha peek) for the `tools`/`load` profiles. Use with `-f docker-compose.yml -f infra/compose.dev.yml`. |
| `compose.s3.yml` | Override adding an S3-compatible server (`adobe/s3mock`) and pointing the API at it (`Providers__Storage=s3`), so the provider switch can be demonstrated end to end without an AWS account. The file header shows the MinIO swap for a real host. |
| `hetzner/` | VDS deployment: `cloud-init.sh` (host prep), `comments-spa.service` (stack on boot) and `deploy.sh` (create/status/destroy the server). See `hetzner/README.md`. |
| `postgres/init.sql` | Runs once on an empty data dir. Only tunes the cluster (UTC, `pg_trgm`). **Schema is owned by EF Core migrations.** |
| `rabbitmq/definitions.json` | Reference/optional pre-provisioning of the API-v2 §7 topology (`comments.events`, `comments.dlx`, `*.dlq`, retry queue). **Not auto-loaded** — see below. |

There are intentionally **no host-mounted config files** beyond `postgres/init.sql`.
Files written in this workspace are mode 600 (uid 1000); a read-only mount of a
mode-600 file makes the container user fail with `eacces`. RabbitMQ therefore
uses the `RABBITMQ_DEFAULT_USER`/`PASS` environment variables (a non-guest user,
so the loopback restriction does not apply) instead of a mounted `rabbitmq.conf`,
and nginx config lives inside the `web` image (`web` teammate) rather than being
bind-mounted from the repo.

## Commands

```bash
# Full stack (builds api + web images) — Production
docker compose up --build -d
docker compose ps

# Data services only (tests / integration work)
docker compose up -d postgres redis rabbitmq elasticsearch

# --- Dev/load mode: Development api (seed + captcha peek enabled) ----------
# Either repeat `-f` on every command:
DEV="-f docker-compose.yml -f infra/compose.dev.yml"
# ...or export it once (docker compose honours COMPOSE_FILE, colon-separated):
#   export COMPOSE_FILE=docker-compose.yml:infra/compose.dev.yml
#   docker compose up -d --build
#   docker compose --profile load run --rm k6 run /scripts/scenarios.js
#
# WARNING: any compose command that omits the override recreates the `api`
# dependency in Production (dependencies come from the merged model). Export
# COMPOSE_FILE for the whole dev session, or pass both `-f` flags on EVERY
# command — including `--profile load run`.

# k6 load profile (perf/k6 mounted read-only at /scripts, results -> perf/results)
docker compose $DEV --profile load run --rm k6 run /scripts/scenarios.js

# Seed helper (dev seed endpoint; appsettings.Development.json + Features:Seed=true)
docker compose $DEV --profile tools run --rm seed

# Tear down ONLY this project (never run against another project)
docker compose --profile load --profile tools down -v
```

### Provider switch: object storage (S3-compatible)

Proves that "swap the storage provider by configuration" is real rather than documented:

```bash
docker compose -f docker-compose.yml -f infra/compose.s3.yml up -d --build

curl -s localhost:8081/api/info | jq '.providers[] | select(.port=="storage")'
# → { "port": "storage", "value": "s3", "status": "ok", "alternatives": ["filesystem","azureblob"], ... }

# upload a comment with an image, then confirm the object is in the bucket:
docker exec comments-spa-s3 sh -c 'ls -R /s3mockroot'

# and that the volume stayed empty:
docker exec comments-spa-api sh -lc 'find /app/storage -type f | head'

# back to the local filesystem
docker compose -f docker-compose.yml up -d api
```

The API image is byte-identical in both cases — only `Providers__Storage` and the `Storage__S3__*`
variables change, which is the same thing you would set for AWS S3, MinIO, Yandex Object Storage,
Cloudflare R2 or DigitalOcean Spaces. The file header of `compose.s3.yml` shows the two-line MinIO
swap for a real host.

Any other port switches the same way (see `.env.example`): `Providers__Cache=memory`,
`Providers__Messaging=inmemory`, `Providers__Search=none`. An unknown value is a startup error,
and `GET /api/info` always reports what is actually wired.

## Host ports

| Service | Host | Container |
|---|---|---|
| web (nginx) | 8080 | 80 |
| api | 8081 | 8080 |
| postgres | 55432 | 5432 |
| redis | 56379 | 6379 |
| rabbitmq AMQP / UI | 5672 / 15672 | 5672 / 15672 |
| elasticsearch | 59200 | 9200 |
| s3 (S3 API, `compose.s3.yml` only) | 59000 | 9090 |

## Connection defaults (local dev, non-secret)

| Setting | Value |
|---|---|
| PostgreSQL | `Host=localhost;Port=55432;Database=comments;Username=comments;Password=comments` |
| Redis | `localhost:56379` |
| RabbitMQ | `amqp://comments:comments@rabbitmq:5672/` (management UI `comments`/`comments`) |
| Elasticsearch | `http://localhost:59200` |
| S3 (`compose.s3.yml`) | endpoint `http://s3:9090` (host `http://localhost:59000`), bucket `comments-attachments`, any credentials |

## RabbitMQ topology ownership

The API's messaging adapter (`Comments.Infrastructure/Messaging`) declares the
exchanges/queues/bindings at startup, matching `docs/API-v2.md` §7. Infra ships
the definitions as a **reference** so the topology can be inspected or imported
manually:

```bash
docker cp infra/rabbitmq/definitions.json comments-spa-rabbitmq:/tmp/definitions.json
docker compose exec rabbitmq rabbitmqadmin -u comments -p comments \
    import /tmp/definitions.json
```

Auto-loading via `management.load_definitions` is intentionally disabled: if the
pre-declared arguments ever diverge from the app's declaration the channel fails
with `PRECONDITION_FAILED` and event flow breaks. One declaration owner is safer.

The live adapter is the source of truth: it currently declares the §7 shared
queues (`comments.events.search`, `comments.events.cache`, DLQs, retry) plus a
per-instance WebSocket fan-out queue (`comments.events.ws.<instanceId>`) so
multiple API replicas do not steal each other's broadcasts, and probes queue
arguments passively so an old container's declaration cannot raise AMQP 406.
`definitions.json` is the §7 reference snapshot, not a live mirror.

## Credentials

There are no secrets in this repository. `.env.example` holds plain local-dev
defaults; `docker compose` reads `.env` (git-ignored) automatically. The default
stack is **Production** (`FEATURES_SEED=false`, dev endpoints 404); Development
mode for seeding/load is opt-in via `infra/compose.dev.yml`.

## Resource notes

Elasticsearch heap is capped at `-Xms512m -Xmx512m` and the container at
`mem_limit: 2g` because the host runs other containers. Stateful images are
pinned (`postgres:16-alpine`, `redis:7-alpine`, `rabbitmq:3-management-alpine`,
`elasticsearch:8.15.3`); only k6 uses `latest`.

## Sandbox note: buildx state directory

In this workspace `~/.docker/buildx` is read-only, so a plain `docker compose
build` can fail with `failed to update builder last activity time: ...
read-only file system`. Point buildx at the repo-local (git-ignored) state dir:

```bash
BUILDX_CONFIG="$PWD/.buildx" docker compose up --build -d
```

`.dockerignore` already excludes `.buildx/`, so the workaround never leaks into
image contexts. On a normal host the plain command works unchanged.
