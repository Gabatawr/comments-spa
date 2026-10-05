# SPA «Комментарии» / Comments SPA — этап Middle+

Тестовое задание: SPA-приложение для комментариев с каскадным отображением ответов,
серверной и клиентской валидацией, CAPTCHA, вложениями, защитой от XSS/SQL-инъекций.
Второй этап доводит проект до **базового уровня + Junior+ + Middle** и закладывает
**Middle+** (архитектура под 1 000 000 сообщений / 100 000 пользователей в сутки),
подтверждённый реально прогнанным нагрузочным тестом **k6**.

| Документ | Что это |
|---|---|
| [`docs/API-v2.md`](docs/API-v2.md) | **Замороженный контракт v2**: REST, GraphQL, события, модель данных, IP/User-Agent, сортировка/пагинация |
| [`docs/API.md`](docs/API.md) | Контракт v1 (предыдущий этап) |
| [`docs/ARCHITECTURE-v2.md`](docs/ARCHITECTURE-v2.md) | Clean Architecture, порты/адаптеры, точки подмены под облако |
| [`docs/target-middle-plus.md`](docs/target-middle-plus.md) | Полная спецификация этого этапа |
| [`TASK.md`](TASK.md) | Исходное ТЗ |
| [`docs/qa/report-v2.md`](docs/qa/report-v2.md) | Отчёт приёмки с реальными цифрами и честным списком пробелов |
| [`docs/qa/checklist-v2.md`](docs/qa/checklist-v2.md) | Матрица требований с доказательствами |

---

## Стек

| Слой | Технология |
|---|---|
| Backend | .NET 10 (ASP.NET Core minimal APIs), **Clean Architecture**: `Comments.Domain` → `Comments.Application` → `Comments.Infrastructure` / `Comments.Api` |
| БД | **PostgreSQL 16** через EF Core 10 + Npgsql, миграции в репозитории, идемпотентное применение при старте |
| Frontend | **Angular** (standalone components), сборка multi-stage в Docker, отдаётся nginx |
| Кэш (Middle) | **Redis 7** (страницы/счётчики/CAPTCHA), память — fallback |
| Брокер (Middle) | **RabbitMQ 3** (topic exchange, доменные события, DLQ + retry), in-process шина — fallback |
| Graph (Middle) | **GraphQL** (HotChocolate) рядом с REST |
| Поиск (Middle) | **Elasticsearch 8** (индексация по событию, отдельный поисковый эндпоинт) |
| Middle+ | keyset-пагинация, индексы, батч-seed, кэш горячих страниц, асинхронная обработка, горизонтальная масштабируемость (состояние вне процесса) |
| Нагрузка | **k6** (compose-профиль `load`), реально прогнанный сценарий с p95/p99 |
| Контейнеризация | Docker (multi-stage), docker compose (один стек) |
| Тесты | xUnit + `Microsoft.AspNetCore.Mvc.Testing` против **реального PostgreSQL**, curl/GraphQL e2e, браузерная проверка Angular |

Cloud-ready без облака: все внешние зависимости спрятаны за портами в `Comments.Application`,
реализации локальные, точки подмены (S3/Azure Blob, Service Bus, ElastiCache, OpenSearch,
Cloud SQL) задокументированы в [`docs/API-v2.md`](docs/API-v2.md) §11 и
[`docs/ARCHITECTURE-v2.md`](docs/ARCHITECTURE-v2.md). **Никаких реальных вызовов облачных API.**

---

## Быстрый старт (Docker)

Нужен только Docker с плагином compose.

```bash
git clone <URL-репозитория> comments-spa
cd comments-spa
docker compose up --build -d
```

Поднимается весь стек: `api`, `web` (nginx + Angular), `postgres`, `redis`, `rabbitmq`,
`elasticsearch`. Дождитесь, пока все сервисы станут `healthy`:

```bash
docker compose ps
```

| URL | Назначение |
|---|---|
| <http://localhost:8080> | **SPA (Angular)** |
| <http://localhost:8080/api/health> | health-check через nginx |
| <http://localhost:8081/api/health> | health API напрямую |
| <http://localhost:8081/swagger> | OpenAPI UI (только Development) |
| <http://localhost:8081/graphql> | GraphQL playground (только Development) |
| <http://localhost:15672> | RabbitMQ management (guest/guest — локальные dev-креды) |
| <http://localhost:59200> | Elasticsearch |
| `ws://localhost:8080/ws` | WebSocket живых обновлений |

```bash
docker compose logs -f api web     # логи
docker compose down                # остановить (данные в volume сохраняются)
docker compose down -v             # остановить и удалить данные ТОЛЬКО этого проекта
```

## Самопроверка с нуля (строго по README)

Полная приёмочная цепочка одной командой (чистая сборка, тесты, стек с нуля, smoke, k6):

```bash
scripts/acceptance.sh
```

Отдельные шаги:

```bash
# 1) чистая сборка + 325 тестов против реального PostgreSQL (поднимет postgres из compose)
CLEAN=1 scripts/test.sh

# 2) стек с нуля
docker compose down -v && docker compose up --build -d
docker compose ps

# 3) e2e и нагрузка
tests/e2e/smoke.sh http://localhost:8080
docker compose --profile load run --rm k6 run /scripts/scenarios.js
```

> Первый запуск скачивает образы и пакеты (`postgres`, `redis`, `rabbitmq`, `elasticsearch`,
> `node`, `dotnet`, `nginx`, `k6`) — нужен доступ в интернет. Облачные сервисы не используются.

## Локальная разработка без полного стека

```bash
scripts/run-dev.sh          # API в Development на http://localhost:5080 (+ compose data services)
```

---

## Возможности

### Базовый уровень
- **Форма**: User Name (латиница+цифры), E-mail, Home page (URL, необязательно), CAPTCHA, Text.
- **CAPTCHA**: PNG, 6 символов, TTL 5 минут, одноразовая, код не покидает сервер.
- **Главная**: корневые комментарии таблицей с сортировкой по User Name / E-mail / дате
  в обе стороны, LIFO по умолчанию, пагинация 25.
- **Каскад**: ответы любой вложенности; шапка записи по образцу из ТЗ (аватар-инициалы, дата
  `DD.MM.YY в HH:MM`, четыре SVG-иконки, декоративное голосование справа), отступ 32 px и линия
  вложенности, функциональные врезки-цитаты (снимок плоского текста родителя, см. v2.1 ниже).
- **Вложения**: JPG/GIF/PNG (ресайз ≤ 320×240, без апскейла), TXT ≤ 100 КБ,
  lightbox для картинок и модал для текста.
- **HTML**: allow-list `a[href,title]`, `code`, `i`, `strong`, проверка закрытия тегов (XHTML).
- **AJAX-предпросмотр** без перезагрузки, панель `[i] [strong] [code] [a]`,
  клиентская валидация зеркалит серверную.
- **Данные клиента**: IP и User-Agent сохраняются и отдаются в API;
  за прокси IP берётся из `X-Forwarded-For` (первый хоп).
- **Безопасность**: allow-list санитайзер (XSS), только параметризованный LINQ (SQLi),
  `X-Content-Type-Options: nosniff`.

### Junior+
- **Queue** — фоновая обработка создания комментария, HTTP-ответ не ждёт постобработки.
- **Cache** — кэш страниц/элементов/CAPTCHA, инвалидация по событию.
- **Events** — доменные события `CommentCreated` и др.
- **WebSocket** — `/ws`: `hello`, `comment.created`, ping/pong.

### Middle
- **Redis** — кэш страниц с версионной инвалидацией (работает между инстансами API).
- **RabbitMQ** — публикация доменных событий в topic-exchange, отдельные консьюмеры
  (WebSocket-broadcast, индексация в Elasticsearch, инвалидация кэша), **DLQ + retry**.
- **GraphQL** — `comments`, `comment`, `search`, `createComment` (см. `docs/API-v2.md` §5).
- **Elasticsearch** — полнотекстовый поиск `/api/search` с подсветкой, индексация по событию.
- **Cloud-ready: переключатель провайдеров** — каждая внешняя зависимость за портом, выбор одним
  конфигом (`Providers:*`), без правки кода. Реализованы S3-совместимое хранилище (AWS S3, MinIO,
  Yandex Object Storage, Cloudflare R2, DO Spaces) и Azure Blob (connection string / SAS / managed
  identity); поиск умеет basic auth, API key и приватный CA для managed-кластеров. `GET /api/info`
  отдаёт активный набор и альтернативы по каждому порту, старт пишет его одной строкой в лог.
  Неизвестное имя провайдера — **ошибка старта** со списком допустимых, а не тихий откат на дефолт.

### Middle+ (архитектура + измерения)
- Индексы под сортировки/LIFO/дерево, отсутствие N+1 (BFS-выборка по уровням).
- **Keyset-пагинация** без `OFFSET` на глубоких страницах (`cursor`), offset сохранён для ТЗ.
- Батч-вставка и seed-эндпоинт для наполнения базы (100k…1M записей).
- Кэширование горячих страниц, асинхронная обработка через брокер.
- Горизонтальная масштабируемость: состояние в PostgreSQL/Redis/RabbitMQ, не в памяти процесса.
- **Нагрузочный тест k6** — реально прогнан, цифры в [`docs/qa/report-v2.md`](docs/qa/report-v2.md).

### v2.1 — каскад под образец из ТЗ + функциональные цитаты
- Шапка записи приведена к `docs/task/page1-X10.png`: аватар-инициалы в светлом кольце, жирное имя,
  дата строго `DD.MM.YY в HH:MM`, четыре SVG-иконки `#7b95c8` (`#` якорь · закладка · ответ · информация
  об авторе), голосование `↑ 0 ↓` прижато вправо. E-mail, IP и «домашняя страница» убраны из шапки
  как видимый текст и доступны через 4-ю иконку (поповер автора).
- **Голосование — только внешний вид.** В ТЗ (`docs/source/task-raw.txt`) голосования нет: оно
  добавлено исключительно ради соответствия образцу. Бэкенд не затронут, состояние не сохраняется
  (`aria-hidden`, некликабельно).
- **Функциональные цитаты.** При создании ответа сервер сохраняет в `comments.quoted_text` снимок
  плоского текста родителя: пробелы схлопнуты, обрезка до 160 символов по границе слова + `…`
  (итого ≤ 161). Поле `quotedText` **аддитивное и необязательное** — `docs/API-v2.md` не изменён.
  Рендер цитаты экранирован (Angular-интерполяция), `innerHTML` для неё запрещён.
- Миграция `AddCommentQuotedText`; схемы обновлены в `db/schema.sql` (MySQL Workbench),
  `db/schema-postgres.sql`, `db/schema.md`.
- Измеренные константы, трактовки и **честный список расхождений с образцом** —
  [`docs/DESIGN-v2.1-decisions.md`](docs/DESIGN-v2.1-decisions.md) и
  [`docs/qa/design-v2/README.md`](docs/qa/design-v2/README.md).

### v2.2 — cloud-ready переключатель провайдеров
- **Один переключатель на весь внешний мир.** `Providers:Database|Cache|Messaging|Search|Storage`
  разбирается один раз при старте (`ProviderResolver` → `ActiveProviders`), и этот же снимок
  используют композиционный корень, стартовый лог и `GET /api/info` — разойтись не могут.
  Прежний синтаксис (`Cache:Provider`, `Storage:RootProvider`, `Search:Enabled`) продолжает работать.
- **Объектное хранилище реализовано, а не заглушено**: `S3FileStorage` (AWS SDK: AWS S3, MinIO,
  Yandex, R2, Spaces; пустые ключи = цепочка AWS, включая instance role) и `AzureBlobFileStorage`
  (connection string / SAS / `DefaultAzureCredential`). Поиск умеет basic auth, API key и приватный
  CA managed-кластера.
- **Видно, что включено** — строка `Providers: …` в логе при старте и матрица
  «порт → значение → статус → альтернативы» в `GET /api/info`.
- **Ошибка конфига — отказ старта**, а не тихий откат: неизвестное имя перечисляет допустимые,
  объявленный-но-нереализованный шов (`mssql`) говорит об этом прямо; `Providers:Strict=false`
  оставляет прежнее поведение с предупреждением.
- **Живая проверка переключения** — `infra/compose.s3.yml`: стек поднимается с S3-совместимым
  сервером, вложение уходит в бакет, том API остаётся пустым, скачивание идёт стримом.
  В песочнице, где снимался прогон, образы `minio/*` недоступны в реестре — использован
  `adobe/s3mock`, замена на MinIO показана в шапке файла.
- **Найдено и починено по дороге**: поиск создавал `HttpClient` без аутентификации (managed-кластер
  так не подключить); `IFileStorage.GetFullPath` протекал файловой системой в порт (заменён на
  `TryGetLocalPath` — объектное хранилище отказывается, веб-слой стримит); health хардкодил имена
  провайдеров; секция `Providers:*` в `appsettings.json` перекрывала старые ключи окружения.
- **Устаревшее убрано**: шапка `db/schema.sql` утверждала SQLite, `tests/README.md` описывал
  снесённый DOM-харнесс, `AGENTS.md` — состояние v1; старые документы помечены как исторические.
- Отчёт с измерениями: [`docs/qa/report-v2.2.md`](docs/qa/report-v2.2.md).

---

## Структура репозитория

```
src/Backend/Comments.Domain/          # сущности, доменные события — без инфраструктуры
src/Backend/Comments.Application/     # use-cases, DTO, валидация, порты
src/Backend/Comments.Infrastructure/  # EF/Npgsql, Redis, RabbitMQ, Elasticsearch, FS, CAPTCHA
src/Backend/Comments.Api/             # endpoints, GraphQL, WebSocket, DI, Dockerfile
src/Frontend/                         # Angular SPA + Dockerfile + nginx.conf
db/schema.sql                         # схема для MySQL Workbench (прошлый этап)
db/schema-postgres.sql                # PostgreSQL-схема (этот этап)
docs/API-v2.md                        # замороженный контракт v2
docs/ARCHITECTURE-v2.md               # Clean Architecture, порты, точки подмены
docs/qa/report-v2.md                  # отчёт приёмки (реальные измерения)
docs/qa/design-v2/README.md           # скриншоты каскада vs образец + расхождения
docs/DESIGN-v2.1-decisions.md         # замороженные решения по каскаду v2.1 и цитатам
docs/qa/checklist-v2.md               # матрица требований
perf/                                 # k6-сценарии, seed, результаты
infra/                                # конфиги сервисов + профиль S3 (compose.s3.yml)
tests/CommentsApi.Tests/              # xUnit против PostgreSQL
tests/e2e/                            # curl/GraphQL e2e
tests/frontend/                       # браузерная проверка Angular
scripts/test.sh                       # тесты против PostgreSQL
scripts/run-dev.sh                    # локальный API
scripts/acceptance.sh                 # полная приёмка
docker-compose.yml                    # весь стек + профили load/tools
Comments.slnx                         # .NET-решение
```

---

## Тесты

```bash
# полный набор против реального PostgreSQL (325: база v2.0 + цитаты v2.1 + провайдеры)
CLEAN=1 scripts/test.sh

# только unit-часть
scripts/test.sh --filter "FullyQualifiedName~CommentValidatorUnitTests"

# e2e против запущенного стека
tests/e2e/smoke.sh http://localhost:8080
```

Тестовая инфраструктура поднимает отдельную БД `comments_test_<guid>` на сервере
`COMMENTS_TEST_POSTGRES` (по умолчанию `localhost:55432`, user/password/db `comments`) и удаляет
её после прогона. Одна и та же схема создаётся EF Core-миграциями, что и в production.

---

## Конфигурация

Все значения — через переменные окружения / `appsettings`; секретов в репозитории нет
(только `.env.example` с локальными dev-дефолтами). Полная таблица — `docs/API-v2.md` §10.

| Переменная | Значение по умолчанию (compose) | Назначение |
|---|---|---|
| `ConnectionStrings__Default` | `Host=postgres;Database=comments;…` | PostgreSQL |
| `Redis__ConnectionString` | `redis:6379` | кэш (TLS — `rediss://`) |
| `RabbitMq__ConnectionString` | `amqp://comments:comments@rabbitmq:5672/` | брокер (TLS — `amqps://`) |
| `Elastic__Url` | `http://elasticsearch:9200` | поиск |
| `Search__Username` / `Search__Password` / `Search__ApiKey` | — | auth managed ES/OpenSearch |
| `Providers__Database` | `postgres` | `postgres` \| `mssql` (шов, не реализован) |
| `Providers__Cache` | `redis` | `redis` \| `memory` |
| `Providers__Messaging` | `rabbitmq` | `rabbitmq` \| `inmemory` |
| `Providers__Search` | `elastic` | `elastic` \| `none` |
| `Providers__Storage` | `filesystem` | `filesystem` \| `s3` \| `azureblob` |
| `Providers__Strict` | `true` | неизвестное имя провайдера → ошибка старта |
| `Providers__FailFastOnUnavailable` | `false` | недоступный провайдер → ошибка старта |
| `Storage__S3__*`, `Storage__AzureBlob__*` | — | бакет/контейнер/endpoint/креды облака |
| `Proxy__TrustAll` | `true` | доверять `X-Forwarded-For` от nginx |
| `Features__DevCaptchaPeek` | `false` | dev-ручка подсказки CAPTCHA |
| `Features__Seed` | `false` | dev-ручка массового наполнения |

Полный список ключей и что где лежит — `docs/API-v2.md` §10. Активный набор видно в
`GET /api/info`, статусы — в `GET /api/health`.

---

## Известные ограничения / вне зоны

- **Развёртывание на хостинге/VDS** — вне зоны задания (нет аккаунтов). Docker-упаковка
  и запуск на хосте проверены. Само переключение на облако проверено локально: профиль
  `infra/compose.s3.yml` поднимает S3-совместимый сервер, и стек с `Providers__Storage=s3` кладёт вложения
  в бакет — `/api/info` показывает `storage=s3`, `status=ok`.
- **Облачные адаптеры, требующие аккаунта, не проверялись живьём** (AWS S3, Azure Blob, managed
  Elasticsearch/OpenSearch): реализация и переключение есть, реальных вызовов облачных API нет.
  Для Azure Blob есть отдельный путь без секретов — `DefaultAzureCredential` (managed identity).
- **`Providers__Database=mssql`** — объявленный шов под предпочтение ТЗ (MS SQL), не реализован:
  нужен второй набор миграций. Выбор падает с точным сообщением.
- **1 000 000 сообщений** — архитектура рассчитана и проверена seed-эндпоинтом и k6;
  фактический объём датасета указан в отчёте (не все величины достигнуты на этой машине).
- SQLite/MySQL прошлого этапа в рабочем пути не используются; PostgreSQL — единственная БД.
- Актуальный честный список пробелов — в [`docs/qa/report-v2.md`](docs/qa/report-v2.md).
