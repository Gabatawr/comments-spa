# perf/report.md — нагрузочное тестирование SPA «Комментарии» (Middle+)

> **Статус: финальный отчёт по реальным прогонам.** Все числа ниже — из
> фактических запусков k6 против compose-стека; сырые артефакты лежат в
> `perf/results/`. Оценок «на глаз» нет; пробелы перечислены в §7.

## 1. Стенд и методика

| Параметр | Значение |
|---|---|
| Хост | 6 vCPU / 11 GiB RAM (load-генератор и сервисы на одной машине) |
| Стек | `docker compose` профиль `load` + api/web/postgres/redis/rabbitmq/elasticsearch |
| Генератор | `grafana/k6:latest` (v2.3.0), контейнер `comments-spa-k6`, сеть `comments-spa-net`, `BASE_URL=http://api:8080` |
| Сценарии | `browse`, `read_one`, `search`, `create`, `websocket` — ramping-vus (ramp-up / steady / ramp-down) |
| Пороги | глобально `http_req_duration p(95)<500ms`, `p(99)<1500ms`, `http_req_failed<1%`, `checks>99%`; по сценариям см. §4 |
| Метрики | `summaryTrendStats`: avg/min/med/max/p(90)/p(95)/p(99) |

Важная оговорка: k6, api, postgres, ES и брокер делят один хост 6 vCPU. Числа
включают конкуренцию генератора с сервисами (генератор в пике ~28–48% CPU).

## 2. Датасет

Базовый путь — `POST /api/dev/seed` (Development/Load): реалистичная форма
(~10 ответов на корень, depth 3), плюс событийная индексация в Elasticsearch.
Резервный путь — `perf/seed.sh --via db` (set-based `INSERT ... generate_series`),
быстрый, но **не** эмитит `CommentCreated`, поэтому ES не наполняется.

| Прогон | Строк | Корней | Форма | Индексация ES | Размер БД |
|---|---|---|---|---|---|
| `100k-nocreate` (milestone 1) | 100 008 | ~872 (`/api/stats`) | перекос: ~100 ответов/корень | нет (raw DB seed) | 39 MB |
| `100k-full` | 100 011 | 10 006 | ~10 ответов/корень, depth 3 | ~300 доков (события от `create`, не от seed) | ~40 MB |
| `1m-full` | 1 000 001 | 100 001 | 9 ответов/корень, depth 1 (DB seed) | ~433 дока (DB seed обошёл события) | 356 MB |

Каждый заголовочный замер записан в `perf/results/env-<label>.txt` (точный
`actual_total`, git SHA, host CPU/RAM, флаги сценариев).

## 3. Команды

```bash
# включить Development (dev-ручки) на общем стеке — делает infra
export COMPOSE_FILE=docker-compose.yml:infra/compose.dev.yml

# реалистичный сид через API + замер
./perf/run-load.sh --label 100k-full --count 100000 --roots 10000 --via api --clear \
                   --scale 1 --ramp 15 --steady 60

# k6 напрямую
docker compose --profile load run --rm k6 run /scripts/scenarios.js

# разбор сырого JSON
python3 perf/summarize.py perf/results/summary-100k-full.json
```

## 4. Результаты (реальные прогоны)

### 4.1 `100k-nocreate` — milestone 1 (100 008 строк, без `create`)

Команда: `CREATE_ENABLED=false ./perf/run-load.sh --no-seed --label 100k-nocreate --count 100000 --scale 1 --ramp 15 --steady 60`
Длительность 91.5 c; пик 33 VU; `http_reqs` 10 238 → **RPS 111.9**; error rate **0.00%**;
checks 100%.

| Метрика | avg | p95 | p99 | max |
|---|---|---|---|---|
| Все запросы | 93.86 ms | 369.00 ms | 1117.03 ms | 4081.99 ms |
| `browse` | 155.26 ms | **708.57 ms (FAIL)** | 1403.59 ms | 4081.99 ms |
| `read_one` | 62.32 ms | 211.07 ms | 802.35 ms | 4069.36 ms |
| `search` | 23.82 ms | 85.71 ms | 144.95 ms | 252.09 ms |
| `websocket` connect | — | 193.00 ms | — | 466.00 ms |

k6 exit 99 — из-за единственного провала порога `browse p(95)<500ms`.
`search_hits` 349; `ws_hello_received` 3340.

Ресурсы (пик за прогон, `docker stats`): api **178.6%** CPU / 930 MiB,
postgres **270.7%** CPU / 188 MiB, ES 47% / 1033 MiB, rabbitmq 88% / 188 MiB,
redis 33.5% / 599 MiB, k6 47.8% / 227 MiB, web <4%.

Кэш: Redis `keyspace_hits=9808`, `misses=1309` → **88.2% hit ratio**
(API `/api/stats` в это же время: 0.82). ES: 111 документов (raw DB seed не
индексирует). Postgres: `idx_scan=500 543` против `seq_scan=5 660` — индексами.

Сырые файлы: `perf/results/summary-100k-nocreate.json`,
`k6-100k-nocreate.log`, `stats-100k-nocreate.log`, `env-100k-nocreate.txt`.

### 4.2 `100k-full` — реалистичный датасет, все сценарии (ОСНОВНОЙ замер)

Датасет: 100 011 комментариев, 10 006 корней (≈10 ответов/корень), создан через
`POST /api/dev/seed` (100 000 за 12.5 c → **7 995 комментариев/с**), плюс creates за прогон.

Команда: `./perf/run-load.sh --no-seed --label 100k-full --count 100000 --scale 1 --ramp 15 --steady 60`
(в компоуз-команде обязателен dev-override — см. §7). Длительность 91.1 c; пик 35 VU;
`http_reqs` 15 937 → **RPS 174.9**; error rate **0.00%**; checks 100%; `create_ok` 139.

| Метрика | avg | p95 | p99 | max |
|---|---|---|---|---|
| Все запросы | 36.41 ms | **76.38 ms** | 768.56 ms | 3198.03 ms |
| `browse` | 48.37 ms | 100.93 ms | 924.83 ms | 2385.04 ms |
| `create` (весь flow) | 294.83 ms | **1074.51 ms (FAIL)** | **2281.87 ms (FAIL)** | 3198.03 ms |
| `read_one` | 12.47 ms | 33.61 ms | 56.23 ms | 1056.08 ms |
| `search` | 7.89 ms | 21.98 ms | 43.25 ms | 111.88 ms |
| `websocket` connect | — | 192.00 ms | — | 218.00 ms |

Пороги: провалены только `create p(95)<800` и `create p(99)<2000`; остальные PASS,
`http_req_failed` 0%. k6 exit 99.

Ресурсы (пик): api 181.4% CPU / 150 MiB, postgres 159.9% / 182 MiB, rabbitmq 90.7%,
ES 50.3% / 1050 MiB, redis 34.6% / 246 MiB, k6 29.9%.

Redis: `keyspace_hits=19 704`, `misses=7 419` (72.6% — сюда входят и CAPTCHA-ключи).
ES: ~300 документов (нормальные `create` индексируются — проверено точечно:
созданный комментарий id=100153 найден в `_search`; `_count` отстаёт).

Сравнение форм датасета — главный вывод: при **той же** нагрузке browse p95 падает
с **708.57 ms** (перекос ~100 ответов/корень) до **100.93 ms** (≈10 ответов/корень).
То есть стоимость страницы линейна по числу ответов, а не по OFFSET.

Сырые файлы: `perf/results/summary-100k-full.json`, `k6-100k-full.log`,
`stats-100k-full.log`, `env-100k-full.txt`.

### 4.3 `1m-full` — 1 000 000 комментариев, все сценарии

Датасет: 1 000 001 комментарий, 100 001 корень (9 ответов/корень), загружен через
`perf/seed.sh --via db` за **1 m 52 s** (set-based `INSERT ... generate_series`).
`POST /api/dev/seed` на 1M **не сработал** (HTTP 500, `Npgsql ... Timeout during reading
attempt` в `CommentSeeder.SeedAsync`; серверный `COPY` остался висеть ~7 мин после
таймаута клиента — см. §5.6/§7).

Команда: `./perf/run-load.sh --no-seed --label 1m-full --count 1000000 --scale 1 --ramp 15 --steady 60`
Длительность 91.6 c; пик 35 VU; `http_reqs` 11 184 → **RPS 122.1**; error rate **0.00%**;
checks 100%; `create_ok` 133.

| Метрика | avg | p95 | p99 | max |
|---|---|---|---|---|
| Все запросы | 92.83 ms | **528.00 ms (FAIL)** | 1328.17 ms | 4243.46 ms |
| `browse` | 222.69 ms | **985.84 ms (FAIL)** | **2019.15 ms (FAIL)** | 4243.46 ms |
| `create` (весь flow) | 316.60 ms | **1246.39 ms (FAIL)** | 1908.17 ms | 3574.78 ms |
| `read_one` | 20.62 ms | 65.15 ms | 131.04 ms | 623.03 ms |
| `search` | 12.07 ms | 39.03 ms | 88.22 ms | 424.06 ms |
| `websocket` connect | — | 191.00 ms | — | 267.00 ms |

Разбивка `create` по шагам (custom trends):

| Шаг | avg | p95 | p99 |
|---|---|---|---|
| `GET /api/captcha` (генерация PNG) | 76.36 ms | 438.40 ms | 960.72 ms |
| `GET /api/dev/captcha/{id}` | 22.55 ms | 30.20 ms | 85.76 ms |
| `POST /api/comments/{id}/child` | **851.41 ms** | **1548.20 ms** | 2971.68 ms |

Ресурсы (пик): postgres **291.2%** CPU / 475 MiB, api 130.2% / 171 MiB,
rabbitmq 87.2%, ES 55.4% / 1075 MiB, k6 25%, redis 11.1%.
Postgres: `idx_scan=3 955 133` против `seq_scan=5 773`; `n_live_tup≈1 000 134`.
Redis: hits 23 717 / misses 10 722. ES: ~433 документа (DB-сид обошёл события;
нормальные `create` индексируются — точечно проверено).

Вывод: на 1M при той же нагрузке деградирует прежде всего **`browse`** (p95 986 ms
против 101 ms на 100k) и write-path (`create post` avg 851 ms). read_one/search/ws
остаются в норме. 0% ошибок и 100% checks — деградация по latency, не по отказам.

Сырые файлы: `perf/results/summary-1m-full.json`, `k6-1m-full.log`,
`stats-1m-full.log`, `env-1m-full.txt`.

## 5. Разбор узких мест

1. **`browse`: дерево ответов не ограничено + линейный рост с размером БД.**
   `GET /api/comments` отдаёт полное дерево каждого корня. Стоимость страницы
   линейна по числу ответов: при перекосе ~100 ответов/корень p95 708 ms,
   при ≈10 ответов/корень — 101 ms (100k) и 986 ms (1M). Сборка идёт
   level-by-level (запрос на уровень), затем JSON-сериализация каждого узла;
   на пике api 178–181% CPU, postgres 270–291%. На 1M добавляется `COUNT(*)`
   по корням и сортировка `lower(...)`/`OFFSET` по 100k корней **на каждый**
   запрос. Это не N+1 на комментарий, а «дерево без предела». Самое дешёвое
   улучшение: не отдавать полное дерево в листинге (только `replyCount`,
   ветка — по требованию) или жёсткий лимит узлов/глубины на страницу.
2. **`create`: доминирует `POST`, а не CAPTCHA.** Разбивка на 1M:
   CAPTCHA PNG avg 76 ms / p95 438 ms, peek avg 23 ms, **POST avg 851 ms /
   p95 1548 ms**. POST включает вставку с 5 индексами, FK и publish с
   publisher confirms; под параллельным `browse` postgres насыщен (291% CPU),
   и вставка ждёт. На 100k `create` p95 1074 ms. Направление: подтверждения
   публикации не должны блокировать HTTP-ответ (сейчас, судя по latency,
   ждут), пул соединений/батчинг вставок, отделить write-нагрузку.
3. **Кэш страниц помогает, но промахи дорогие.** Redis: 88% попаданий на 100k
   (9808/1309), 69% на 1M (23 717/10 722; сюда же входят CAPTCHA-ключи).
   Промах = `OFFSET`-выборка + сборка дерева. `cacheHitRate: 0` в `/api/stats`
   после прогонов — это сброс in-process счётчика при пересоздании api, а не
   отсутствие кэша. TTL 30 c; случайные `sortBy/sortDir/page` дают 120 ключей,
   так что часть промахов — холодные комбинации.
4. **PostgreSQL — главный потребитель CPU, но индексы работают.**
   `idx_scan` 3.96M против `seq_scan` 5.8k; планы идут по
   `ix_comments_created_at_id`, `ix_comments_parent_created`, функциональным
   `lower()`-индексам. На 1M postgres до 291% CPU — узкое место по CPU, не по
   соединениям. После массового `TRUNCATE`+вставки в таблице остаётся блoat и
   работает autovacuum, что добавляет I/O во время первого прогона — фактор,
   который стоит перепроверить на «остывшей» БД.
5. **Elasticsearch: поиск измерен, но индекс мал — это НЕ масштабный тест.**
   DB-сид обходит события, а `POST /api/dev/seed` **тоже не публикует события**
   (после API-сида 100k индекс остался 221 док; проверено). Индексируются
   только обычные `create` (точечно подтверждено: созданный id=100153 найден
   через `_search`; `_count` отстаёт из-за refresh). Поэтому `search` p95 22/39 ms
   отражает стоимость запроса, но не релевантность и не нагрузку на большой
   индекс. Нужен либо reindex-эндпоинт, либо публикация событий из сида.
6. **API-сид 1M падает по таймауту.** `POST /api/dev/seed {count:1000000}`
   → HTTP 500 через 107 s: `Npgsql.NpgsqlException: Exception while reading
   from stream → TimeoutException: Timeout during reading attempt` в
   `CommentSeeder.SeedAsync`. Серверный `COPY` продолжил выполняться и после
   таймаута клиента (~7 мин, пока не отменён `pg_cancel_backend`). Это блокер
   требования «seed 1M через API»; лечится `CommandTimeout`/`COPY`-стримингом
   и отменой по `CancellationToken`. 1M в этом отчёте загружен DB-путём.
   **Обновление (core, task-12):** исправлено — отдельное соединение с
   `CommandTimeout=900`, `synchronous_commit=off`, короткая транзакция резерва
   id и чанковый `NpgsqlBinaryImport` (chunk = clamp(batchSize, 5k, 100k)).
   Проверено core на compose-PG: `POST /api/dev/seed {count:1000000,roots:100000,
   depth:3}` → HTTP 200, `elapsedMs=565858`, **1 767 комментов/с**; 8
   параллельных POST create → 8/8 HTTP 201, без 23505. В замороженном на момент
   замеров образе фикса ещё не было; `perf/seed.sh` теперь по умолчанию
   `batchSize=50000` и таймаут запроса 1800 s.
7. **Гонка сида и обычного `create` (QA-301).** Пока bulk-sql сид шёл
   параллельно с записью через API, наблюдались 9 `duplicate key ...
   PK_comments`: `setval(max(id))` откатывал identity-последовательность ниже
   id, зарезервированных конкурирующим инсертом. В `perf/seed.sh` исправлено —
   `setval` только вперёд (`mx > cur`). Вывод: raw-DB сид нельзя запускать
   одновременно с записью через API; core закрывает это блокировкой таблицы.
8. **Брокер.** Топология §7.1 жива: у `comments.events.{ws,search,cache}` по
   1 консьюмеру, очереди дренированы (0 ready / 0 unacked), DLQ пусты.
   `queue.pending=-1` в `/api/health` — дефект старого образа до P1; в новом
   образе pending=0 (по данным infra).

## 6. Что изменить дальше

- Ограничить/ленивизировать дерево в листинге — наибольший эффект на `browse`,
  особенно на 1M.
- Кэшировать сериализованную страницу (байты, не объекты) + stale-while-revalidate.
- Keyset-пагинация (§4) вместо `OFFSET` и кэш счётчика корней, чтобы `COUNT(*)`
  и `OFFSET` не выполнялись на каждый запрос.
- Не ждать publisher confirms в HTTP-запросе (ответ после commit, публикация —
  фоном); проверить размер пула соединений Npgsql под пиком.
- Починить `POST /api/dev/seed` для 1M (`CommandTimeout`, стриминговый `COPY`,
  отмена по CancellationToken) и/или добавить публикацию событий/reindex для ES.
- Прогнать 1M на «остывшей» БД (после autovacuum) и отделить load-генератор от
  сервисов — на 6 vCPU генератор делит CPU с api/postgres/ES.

## 7. Честные пробелы

- **Write-path на 1M измерен на Development-контейнере** (create включён,
  15:27–15:29). Отдельный «Production, create off» прогон 1M по просьбе Lead не
  делался, чтобы не конфликтовать с параллельным Production-прогоном QA; данные
  §4.3 полные (все 5 сценариев) и валидны по нагрузке.
- `POST /api/dev/seed` для 1M **не работал в замороженном образе** (500 по
  таймауту, §5.6) — 1M загружен через `perf/seed.sh --via db`. Core закрыл это
  в task-12 (проверено ими: 1M за 566 s, 1 767/с); в моих замерах образ этот
  фикс ещё не содержал, поэтому повторный API-сид 1M под нагрузкой не
  выполнялся.
- Elasticsearch мал (~433 дока): `search` — замер latency, не масштаба.
- `browse` и `create` **не проходят** пороги на 1M, `create` — и на 100k;
  глобальный p95 528 ms на 1M (FAIL). Это зафиксировано, не скрыто.
- Load-генератор и сервисы на одном хосте 6 vCPU / 11 GiB; числа включают эту
  конкуренцию и занижают реальный потолок.
- Форма датасета 1M — depth 1 (DB-сид), 100k — depth 3 (API-сид); выводы по
  `browse` для 1M учитывают это.

