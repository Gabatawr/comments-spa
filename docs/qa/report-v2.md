# Отчёт приёмки v2 — SPA «Комментарии», этап Middle+

- **Этап**: второй (Middle+), поверх принятого `v1.0.0`.
- **Владелец приёмки**: Lead. Исполнители: `core`, `api`, `web`, `infra`, `perf`, `qa`.
- **Контракт**: [`docs/API-v2.md`](../API-v2.md) заморожен до распараллеливания; швы Clean Architecture —
  [`docs/ARCHITECTURE-v2.md`](../ARCHITECTURE-v2.md).
- **Матрица требований**: [`docs/qa/checklist-v2.md`](checklist-v2.md).
- **Среда**: Linux, Docker 29.4.2 / compose v5.1.3, .NET SDK 10.0.112, Node 24.21.0 (Angular 22),
  6 vCPU / 11 GiB. Всё локально, без облака.
- **Итог**: **принято с оговорками** — все три уровня ТЗ закрыты и проверены; открытые пункты
  перечислены в §8 без приукрашивания.

---

## 1. Приёмочные прогоны (реальные команды и вывод)

| # | Проверка | Команда | Результат |
|---|---|---|---|
| 1 | Чистая сборка + полный набор тестов | `CLEAN=1 scripts/test.sh` (rm bin/obj → restore → build → test на PostgreSQL) | **265 passed / 0 failed / 0 skipped, 6 m 52 s** (лог `acceptance-logs/lead-final-tests-154218.log`) |
| 2 | Стек с нуля | `docker compose down -v && docker compose up --build -d` | **exit 0, 1 m 10 s, 6/6 healthy** (лог `acceptance-logs/lead-clean-compose-155551.log`) |
| 3 | Health | `curl localhost:8080/api/health` | `status=ok`, `database/cache/redis/broker/search/storage=ok`, `version 2.0.0` |
| 4 | e2e smoke v2 | `tests/e2e/smoke.sh http://localhost:8080` | **93 PASS / 0 FAIL / 0 SKIP, exit 0** |
| 5 | Adversarial (~80 атак) | `tests/e2e/adversarial.sh` | **SAFE 77 / VULNERABLE 0 / ACCEPTED-RISK 1 / UNEXPECTED 0** |
| 6 | Реальный браузер (Playwright/Chromium) | `tests/frontend/run-browser-check.sh` | **23 PASS / 0 FAIL**, 0 uncaught-ошибок, 0 API 5xx из UI |
| 7 | Graceful degradation брокера | `docker stop rabbitmq` → health → `docker start` | во время сбоя `broker=error`, API и CAPTCHA работают; после старта `broker=ok` (≤20 с, авто-reconnect) |
| 8 | k6 (подтверждение Lead на принятом стеке) | `PERF_PROD=1 CREATE_ENABLED=false perf/run-load.sh --no-seed --label lead-accept-100k` | **209.6 RPS, p95 22.0 ms, p99 42.5 ms, 0.00 % ошибок**, все пороги PASS |
| 9 | k6 (основные прогоны `perf`) | `perf/run-load.sh` | 100k: **174.9 RPS, p95 76 ms, p99 769 ms, 0 %**; 1M: **122.1 RPS, p95 528 ms, p99 1328 ms, 0 %** |
| 10 | GraphQL | `POST /graphql` (query + mutation) | схема ровно по API-v2 §5; невалидная CAPTCHA → `success=false` + `errors`, HTTP 200 |
| 11 | Elasticsearch-поиск | `GET /api/search?q=…` | проиндексированный комментарий найден (реальный ES 8.15.3, индексация по событию) |
| 12 | IP/User-Agent за прокси | `curl -H 'X-Forwarded-For: 203.0.113.7, 10.0.0.1'` | `clientIp=203.0.113.7` (первый хоп), UA сохранён |
| 13 | Clean Architecture | grep по csproj | `Domain`/`Application` без EF/Npgsql/Redis/RabbitMQ/ES/ASP.NET ссылок |
| 14 | SQL-инъекции | статический grep + adversarial | `FromSqlRaw`/`ExecuteSqlRaw`/склейки SQL отсутствуют; атаки без результата |
| 15 | Секреты | grep по репозиторию | только `.env.example` с локальными dev-дефолтами, секретов нет |
| 16 | Git | `git log --graph`, `git tag` | `feature/*` влиты в `main` через `--no-ff`, тег `v2.0.0` |

---

## 2. Архитектура

```
Comments.Domain  ──►  Comments.Application  ──►  Comments.Infrastructure
                            ▲                          ▲
                            └──────  Comments.Api  ─────┘   (composition root)
```

- Зависимости строго внутрь; инфраструктура — за портами (`IFileStorage`, `ICacheService`,
  `IEventPublisher`/`IEventConsumer`/`IWorkEventConsumer`, `ICommentSearchIndex`,
  `ICommentRepository`, `ICaptchaStore`, `IClock`).
- Переключатели: `Cache:Provider=redis|memory`, `Messaging:Provider=rabbitmq|inmemory`,
  `Search:Enabled`, `Storage:Provider=filesystem`; `AddCommentsApplication()`,
  `AddCommentsInfrastructure(IConfiguration)`, `ApplyMigrationsAsync()`, `EnsureSearchIndexAsync()`.
- Graceful degradation подтверждена живьём (см. §1, пункт 7).
- Точки подмены под облако (S3/Azure Blob, Service Bus, ElastiCache, OpenSearch, Cloud SQL) —
  `docs/API-v2.md` §11. **Облачных вызовов нет, обязательный путь запуска полностью локальный.**

---

## 3. PostgreSQL

- EF Core 10.0.12 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3; миграция в репозитории
  (`Comments.Infrastructure/Persistence/Migrations`); применение при старте идемпотентно под
  `pg_advisory_lock` (безопасно для нескольких инстансов); отсутствующая БД создаётся на старте.
- Регистронезависимая сортировка `userName`/`email` через `lower()` + функциональные индексы
  (SQLite-специфичный `NOCASE` удалён).
- Индексы `comments`: `parent_id`, `created_at DESC, id DESC` (LIFO/keyset),
  `(parent_id, created_at, id)`, `lower(user_name)`, `lower(email)`, FK на `attachments`.
- Пагинация: offset 25 (требование ТЗ) **и** keyset `cursor` (Middle+); страницы вне диапазона
  возвращают 200 + пустой `items`.
- Схема для ревью: [`db/schema-postgres.sql`](../../db/schema-postgres.sql); MySQL-диалект
  прошлого этапа сохранён в `db/schema.sql`.

---

## 4. Стек compose (проверен с нуля)

| Сервис | Образ | Хост-порт | Статус |
|---|---|---|---|
| web (nginx + Angular) | сборка `src/Frontend/Dockerfile` | 8080 | healthy |
| api | сборка `src/Backend/Comments.Api/Dockerfile` | 8081 | healthy |
| postgres | `postgres:16-alpine` | 55432 | healthy |
| redis | `redis:7-alpine` | 56379 | healthy |
| rabbitmq | `rabbitmq:3-management-alpine` | 5672 / 15672 | healthy |
| elasticsearch | `docker.elastic.co/elasticsearch/elasticsearch:8.15.3` | 59200 | healthy (green) |

`docker compose up --build -d` с нуля: **exit 0 за 1 m 10 s**, все healthcheck'и зелёные;
`depends_on: condition: service_healthy` обеспечивает порядок старта. Профили `load` (k6) и
`tools` (seed) валидны (`docker compose config -q`).

Реальный `/api/health` на свежем Production-стеке:

```json
{"status":"ok","database":"ok","cache":"ok","queue":{"pending":0,"processed":0},
 "websocket":{"clients":0},"redis":"ok","broker":"ok","search":"ok","storage":"ok","version":"2.0.0"}
```

Во время принудительной остановки RabbitMQ: `broker":"error"`, при этом `status":"ok"`,
`database":"ok"`, создание комментариев и CAPTCHA продолжают работать; после
перезапуска health вернулся в `broker":"ok"` без рестарта API.

---

## 5. Нагрузочные тесты k6 (реальные цифры)

Сценарий `perf/k6/scenarios.js`: 5 сценариев (browse, read_one, search, create, websocket),
ramping-vus, пороги p95/p99/error, экспорт JSON. Сырые данные — `perf/results/`.

| Прогон | Датасет | RPS | p95 | p99 | Ошибки | browse p95 | read p95 | search p95 | create p95 |
|---|---|---|---|---|---|---|---|---|---|
| perf 100k-full | 100 011 / 10 006 корней | 174.9 | **76 ms** | **769 ms** | 0.00 % | 101 ms | 34 ms | 22 ms | 1075 ms |
| perf 1M-full | 1 000 001 / 100 001 корень | 122.1 | 528 ms | 1328 ms | 0.00 % | 986 ms | 65 ms | 39 ms | 1246 ms |
| Lead, принятый стек | 100 000 / 9 994 корня | **209.6** | **22.0 ms** | **42.5 ms** | 0.00 % | 20 ms | 23 ms | 21 ms | — (create off) |

Пики контейнеров на 1M: postgres 291 % CPU / 475 MiB, api 130 % / 171 MiB, rabbitmq 87 %,
elasticsearch 55 % / 1075 MiB. PG: `idx_scan` 3.96 M против `seq_scan` 5.8 k. Redis при 100k:
88 % попаданий.

**Узкие места (честно):** на 1M порог p95 500 ms не выдержан (528 ms). Причины:
(а) ответ списка отдаёт всё дерево ответов без ограничения — browse p95 **708 ms** при ~100
ответах на корень против **101 ms** при ~10 на той же нагрузке; (б) `COUNT` + `OFFSET` по 100k+
корней на каждый запрос; (в) запись — это сам `POST` (вставка + 5 индексов + FK + publisher
confirms), а не CAPTCHA (captcha avg 76 ms против POST avg 851 ms). Направления улучшения:
keyset по умолчанию, лимит глубины/числа потомков в списке, кэш `COUNT`, партиционирование
по `created_at`. Подробнее — `perf/report.md`.

---

## 6. Frontend (Angular) — проверено в браузере

Playwright/Chromium против `http://localhost:8080` (реальный nginx + реальный API):
**23 PASS / 0 FAIL**, `BROWSER_EXIT=0`, 0 uncaught-ошибок, 0 API 5xx из UI. Проверены: загрузка
SPA, таблица корней, сортировка в обе стороны (`aria-sort` + ▲/▼), пагинация 25, каскад с
отступом и левой полосой любой глубины, AJAX-предпросмотр без перезагрузки, тулбар
`[i] [strong] [code] [a]`, клиентская валидация, CAPTCHA (PNG с реальными пикселями),
WebSocket-обновление без перезагрузки (`comment.created` → тост + новая строка).

Сборка: `ng build --configuration production` → 334.75 kB raw / 88.28 kB transfer, strict +
strictTemplates; Docker-образ web 94.6 MB; `nginx -t` успешно.

---

## 7. Дефекты, найденные в ходе приёмки

| ID | Severity | Описание | Статус |
|---|---|---|---|
| QA-301 | High | Гонка seed/identity: seed вставлял явные id и делал `setval` после COPY → параллельный обычный INSERT получал stale `nextval` → `23505` → HTTP 500 | **FIXED / перепроверено**: транзакция + `LOCK TABLE … EXCLUSIVE` + `setval` до COPY; `SeedConcurrencyTests` зелёный; 10 параллельных create после seed → 10/10 201 |
| QA-302 | Medium | `(page-1)*pageSize` в int32 → отрицательный OFFSET → HTTP 500 на больших `page` | **FIXED / перепроверено**: `page=2000000000` → 200, `items=[]`; регрессионные `PaginationOverflowTests` (6) зелёные |
| QA-303 | Low | Unhandled 500 не логировался | **FIXED**: `UseExceptionHandler` пишет exception + `METHOD PATH -> status` |
| QA-304 | Medium | `POST /api/dev/seed` на 1 000 000 падал по таймауту Npgsql | **FIXED**: `CommandTimeout=900`, `synchronous_commit=off`, чанковый `BeginBinaryImport`; 1M → HTTP 200, `created=1000000`, 565 858 ms (~1767 комм./с), 8/8 параллельных POST 201 |
| QA-305 | Cosmetic | `health.queue.pending = -1` | **FIXED**: `Math.Max(0, …)` |
| API-1 | Design | Общая durable-очередь WS = work-queue → событие получил бы только один инстанс | **FIXED**: API-v2 §7.1 уточнён; WS — пер-инстансная auto-delete/exclusive очередь; `IWorkEventConsumer` для search/cache |
| API-2 | Low | `health.broker=ok` при молчаливом fallback в in-memory | **FIXED**: `IsAvailable=false`, health честный (проверено живьём) |
| API-3 | Low | AMQP 406 при объявлении очередей со старыми аргументами при upgrade убивал шину | **FIXED**: passive-probe перед declare |
| INFRA-1 | High (dev) | mode-600 bind-mount `rabbitmq.conf` → RabbitMQ crash-loop (`eacces`) | **FIXED**: bind-mount убран, non-guest `comments/comments` через env |
| INFRA-2 | Medium | Healthcheck-окно RabbitMQ/ES мало для холодного старта → `up --build` падал на свежем томе | **FIXED**: `start_period` 120 s + `retries` 30; чистый `up --build` теперь exit 0 за 1 m 10 s |

### Adversarial

`VULNERABLE=0` на всех прогонах. Один документированный `ACCEPTED-RISK`: `X-Forwarded-For`
сохраняется дословно в `clientIp` (контракт API-v2 §6 разрешает невалидные значения; UI
рендерит их как текст; безопасно при корректной настройке `Proxy:TrustAll`/`KnownProxies`).
Покрыты: stored XSS (`<script>`, `onerror`, `javascript:`/`data:` href), SQLi в полях и
query-параметрах, подмена/обрезка XFF, некорректные и огромные вложения, битый multipart/JSON,
replay CAPTCHA (в т.ч. конкурентный), неограниченная пагинация, GraphQL depth/alias abuse,
Production introspection/GET `/graphql` (405) и dev-ручки (404).

---

## 8. Чего нет (честный список)

1. **Порог k6 на 1M не выдержан**: p95 528 ms > 500 ms (p99 1328 ms в норме, ошибок 0 %).
   Причины и план — §5. Код остался корректным, но архитектурные улучшения (keyset по умолчанию,
   лимит дерева, кэш COUNT) в этой итерации не внедрены.
2. **Поиск на 1M не измерен на объёме**: оба пути seed не публикуют `CommentCreated`, поэтому
   после seed индекс ES содержит ~сотни документов. Событийная индексация проверена на обычных
   созданиях; `/api/search` измерен как latency, а не как поиск по миллиону.
3. **Write-бенчмарк на 100k, не на 1M**: сценарий `create` основного прогона 1M выполнялся на
   Development-стеке; отдельный Production/create-off прогон 1M не делался.
4. **Мульти-инстансная эксплуатация не проверена**: компоуз поднимает один экземпляр API.
   Пер-инстансная WS-очередь реализована, но fan-out на 2+ репликах живьём не запускался;
   DLQ/retry-поведение (topology есть) не упражнялось сбоями консьюмера.
5. **Фронтенд**: lightbox/TXT-модал и клиентская валидация ошибок не «прощёлканы» в браузере
   (компоненты и хуки есть, покрытие частичное); пиксельного сравнения с `page1-X10.png` нет.
6. **1M через API медленный**: 566 с (нижняя оценка под нагрузкой); на практике для 1M
   используется `perf/seed.sh --via db` (set-based insert, ~2 мин).
7. **Тесты**: полный прогон ~7 мин в тишине и до ~23 мин под параллельной нагрузкой
   (каждый тест создаёт/мигрирует/удаляет свою БД). Template-DB ускорение не внедрено
   (риск гонки с параллельными прогонами).
8. **Развёртывание на хостинге/VDS и облако** — вне зоны задания (нет аккаунтов); реализованы
   только локальные адаптеры и задокументированные точки подмены. Облачных вызовов нет.
9. **Легаси-тома v1** `comments-spa_comments-data` / `-storage` не удалялись: они не описаны в
   новом compose, `docker compose down -v` их не трогает, prune запрещён. На v2 не влияют.
10. **MySQL-схема** `db/schema.sql` — артефакт прошлого этапа, реализации не соответствует;
    актуальна `db/schema-postgres.sql`.
11. **`ng test`** для Angular не настроен; браузерная проверка живёт в `tests/frontend`.

---

## 9. Как воспроизвести всё

```bash
# 0) зависимости: Docker + compose, .NET SDK 10, Node 22+ (для Angular-сборки в Docker не нужен)
# 1) чистая сборка + 265 тестов на PostgreSQL (поднимет postgres из compose)
CLEAN=1 scripts/test.sh

# 2) стек с нуля
docker compose down -v && docker compose up --build -d
docker compose ps

# 3) e2e / adversarial / реальный браузер
tests/e2e/smoke.sh http://localhost:8080
tests/e2e/adversarial.sh
tests/frontend/run-browser-check.sh

# 4) нагрузка (create-сценарий требует dev-профиля: CAPTCHA peek)
docker compose --profile load run --rm k6 run /scripts/scenarios.js
```

Полная цепочка: `scripts/acceptance.sh` (чистая сборка, тесты, compose с нуля, smoke, k6);
сырые логи — в `docs/qa/acceptance-logs/`.

---

## 10. Вывод

Все три уровня ТЗ закрыты. База и Junior+ сохранены и покрыты тестами (265/265 на реальном
PostgreSQL), Middle реализован на живых Redis, RabbitMQ, GraphQL и Elasticsearch, Middle+
подтверждён реально прогнанным k6 с числами и разбором узких мест. Clean Architecture,
PostgreSQL, Angular, сохранение IP/User-Agent с `X-Forwarded-For` и cloud-ready швы — на месте.
Обе находки приёмки в коде (гонка seed, переполнение offset) исправлены и перепроверены;
оставшиеся ограничения (§8) — это честные пробелы по объёму/мульти-инстансности/визуальной
проверке, а не скрытые отказы.
