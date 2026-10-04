# SPA «Комментарии» / Comments SPA

Тестовое задание: SPA-приложение для комментариев с каскадным отображением,
серверной/клиентской валидацией, CAPTCHA, загрузкой файлов, защитой от XSS и
SQL-инъекций. Реализован **базовый уровень + Junior+** (Queue, Cache, Events,
WebSocket) и заложена архитектура под Middle/Middle+ (см. `docs/ARCHITECTURE.md`).

> Оригинальное ТЗ: [`TASK.md`](TASK.md), полный текст — `docs/source/task-raw.txt`,
> образец интерфейса — `docs/task/page1-X10.png`.

---

## Стек

| Слой | Технология |
|------|-----------|
| Backend | .NET 10 (ASP.NET Core), EF Core 10, SQLite (по умолчанию) |
| Frontend | SPA без сборки: ES-модули + CSS (см. «Почему без фреймворка») |
| Файлы | SixLabors.ImageSharp (пропорциональное уменьшение изображений) |
| Junior+ | `System.Threading.Channels` (Queue), `IMemoryCache` (Cache), in-process Event Bus (Events), `System.Net.WebSockets` (WebSocket) |
| Контейнеризация | Docker (multi-stage), docker compose |
| Тесты | xUnit + `Microsoft.AspNetCore.Mvc.Testing`, curl-e2e |

**Почему SPA без фреймворка.** ТЗ разрешает любой frontend («на ваш выбор»).
Выбран вариант без сборки, потому что он гарантирует требование «проект
поднимается с нуля строго по README»: нет `node_modules`, нет шага сборки,
один контейнер и один origin для API и UI. Вся логика разбита на ES-модули
(`src/Frontend/js/*.js`), реализован полноценный SPA-роутинг состоянием.

---

## Возможности

### Базовый уровень
- **Форма добавления**: User Name (только латиница+цифры, обязательное),
  E-mail (формат, обязательное), Home page (URL, необязательное), CAPTCHA
  (картинка PNG, цифры+латинские буквы, обязательное), Text (обязательное).
- **CAPTCHA**: 6 символов, PNG, one-time, TTL 5 минут, ответ никогда не
  отдаётся клиенту, кнопка обновления.
- **Главная страница**:
  - заглавные комментарии — **таблицей** с сортировкой по User Name, E-mail и
    дате **в обе стороны** (клик по заголовку переключает направление);
  - **каскадное** отображение ответов: вложенность с отступом и левой
    полосой-цитатой (как на образце);
  - пагинация по **25** сообщений;
  - сортировка по умолчанию — **LIFO** (дата по убыванию).
- **Файлы**: к комментарию можно приложить картинку **или** TXT.
  - JPG/GIF/PNG; изображения больше 320×240 **пропорционально уменьшаются**
    сервером (без апскейла);
  - TXT ≤ **100 КБ**;
  - просмотр с визуальными эффектами: lightbox для картинок, модальное окно
    для текста.
- **HTML-теги**: разрешены только `a[href,title]`, `code`, `i`, `strong`;
  проверка закрытия тегов, результат — валидный XHTML.
- **Клиент + сервер**: клиентская валидация с подсказками и серверная
  валидация; ошибки сервера раскладываются по полям.
- **AJAX-предпросмотр** сообщения без перезагрузки страницы.
- **Панель кнопок** `[i]`, `[strong]`, `[code]`, `[a]`.
- **Безопасность**: экранирование/allowlist-санитайз HTML (XSS), только
  параметризованные запросы EF Core (SQL-инъекции), `X-Content-Type-Options: nosniff`.
- **CSS-дизайн** в стиле образца.

### Junior+
- **Queue** — `Channel<CommentCreatedNotification>` + `BackgroundService`:
  HTTP-ответ не ждёт постобработки события.
- **Cache** — `IMemoryCache` для CAPTCHA и страниц списка, инвалидация по событию.
- **Events** — внутрипроцессная шина `IEventBus`, событие `CommentCreatedEvent`
  (обновление кэша, WebSocket-broadcast, лог).
- **WebSocket** — `/ws`: `hello` при подключении, broadcast `comment.created`,
  ping/pong, счётчик клиентов в `/api/health`.

---

## Быстрый старт

### Вариант A — Docker (рекомендуется)

```bash
git clone <URL-репозитория> comments-spa
cd comments-spa
docker compose up --build -d
```

Открыть: **http://localhost:8080**

```bash
curl http://localhost:8080/api/health
docker compose logs -f comments
docker compose down          # остановить (данные остаются в volume)
docker compose down -v       # остановить и удалить данные
```

### Вариант B — локально, без Docker

Требуется **.NET SDK 10**.

```bash
git clone <URL-репозитория> comments-spa
cd comments-spa
./scripts/run-dev.sh          # Development, http://localhost:5080
```

Либо напрямую:

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/Backend/CommentsApi/CommentsApi.csproj --urls http://localhost:5080
```

При первой сборке backend сам копирует `src/Frontend/**` в `wwwroot`
(MSBuild-таргет `CopySpaToWwwroot`), поэтому SPA доступен с того же origin.

### Возможные проблемы

| Симптом | Решение |
|---------|---------|
| `docker compose build` падает с `read-only file system` в `~/.docker/buildx` | Состояние buildx недоступно для записи (частая ситуация в песочницах/CI). Укажите свой каталог: `BUILDX_CONFIG="$PWD/.buildx" docker compose build` |
| `dotnet restore` не может писать в `~/.nuget/packages` | Уже решено корневым `NuGet.config` (кеш в `.nuget/packages/`). Каталог можно удалить — он восстановится при следующем restore |
| Порт 8080 занят | Измените маппинг в `docker-compose.yml` (`"8090:8080"`) или запустите локально на другом порту: `./scripts/run-dev.sh 5090` |
| Контейнер `unhealthy` | `docker compose logs comments` — приложение пишет причину (обычно права на `/app/data` или `/app/storage`) |

### URL-ы

| URL | Назначение |
|-----|-----------|
| `http://localhost:8080/` (Docker) / `:5080/` (dev) | SPA |
| `/api/health` | health-check (db, cache, queue, websocket) |
| `/api/captcha` | получить CAPTCHA |
| `/api/comments` | список корневых комментариев (сортировка/пагинация) |
| `/api/comments/{id}` | комментарий с деревом ответов |
| `/api/attachments/{id}` | файл вложения |
| `/ws` | WebSocket |
| `/swagger` | OpenAPI UI (только Development) |

---

## Структура репозитория

```
src/Backend/CommentsApi/   # .NET 10 API + EF Core + Queue/Cache/Events/WS
src/Frontend/              # SPA (index.html, css/, js/) — без сборки
tests/CommentsApi.Tests/   # xUnit: санитайзер, валидация, API, безопасность
tests/e2e/smoke.sh         # curl e2e против запущенного приложения
db/schema.sql              # схема БД для MySQL Workbench (+ db/schema.md)
docs/API.md                # зафиксированный контракт API
docs/ARCHITECTURE.md       # архитектура, модель данных, Junior+/Middle
docs/qa/checklist.md       # чек-лист требований с доказательствами
docs/qa/report.md          # итоговый отчёт верификации
Dockerfile                 # multi-stage сборка
docker-compose.yml         # стек
scripts/run-dev.sh         # локальный запуск
scripts/smoke.sh           # быстрый smoke (делегирует в tests/e2e)
```

---

## Тесты

```bash
# 1) Модульные + интеграционные тесты (WebApplicationFactory, отдельная БД на тест)
dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj

# 2) e2e против запущенного приложения (Development: нужен dev-CAPTCHA peek)
./scripts/run-dev.sh &                       # либо docker compose up
tests/e2e/smoke.sh http://localhost:5080

# 3) DOM-харнесс frontend (jsdom, реальные ES-модули против живого API)
cd tests/frontend && npm install
BASE=http://127.0.0.1:5080 node dom-harness.mjs
```

Подробности по запуску и требованиям — в [`tests/README.md`](tests/README.md).

### Результаты верификации (итоговый прогон)

| Проверка | Результат |
|----------|-----------|
| xUnit: `dotnet test tests/CommentsApi.Tests/CommentsApi.Tests.csproj` | **251 passed / 0 failed / 0 skipped** |
| e2e `tests/e2e/smoke.sh` (контейнер Docker, Development) | **38 PASS / 0 FAIL / 0 SKIP**, exit 0 |
| DOM-харнесс frontend (jsdom) | **31 PASS / 0 FAIL**, exit 0 |
| `docker compose up` → `/api/health` | `healthy`, `{"status":"ok",...}` |
| Production-контейнер: `/api/dev/captcha/*` | **404** (dev-хук выключен) |

Чек-лист соответствия требованиям с доказательствами: [`docs/qa/checklist.md`](docs/qa/checklist.md),
итоговый отчёт верификации: [`docs/qa/report.md`](docs/qa/report.md).

> **NuGet.** Корневой [`NuGet.config`](NuGet.config) держит глобальный кеш пакетов внутри
> репозитория (`.nuget/packages/`), поэтому `dotnet restore/build/test` работают даже там, где
> пользовательский кеш `~/.nuget/packages` недоступен для записи. Папка git-ignored;
> первый restore скачивает пакеты из nuget.org.

---

## Схема БД

- `db/schema.sql` — MySQL-диалект, открывается в **MySQL Workbench**
  (`File → Open SQL Script`).
- `db/schema.md` — соответствие MySQL ↔ SQLite и объяснение отличий.
- Рабочая БД — SQLite (`comments.db` или `/app/data/comments.db` в Docker),
  схема создаётся EF Core-миграцией при старте.

---

## Конфигурация

| Переменная окружения | По умолчанию | Назначение |
|----------------------|--------------|-----------|
| `ConnectionStrings__Default` | `Data Source=comments.db` | строка подключения БД |
| `Storage__Root` | `storage/` | каталог вложений |
| `ASPNETCORE_URLS` | `http://+:8080` (Docker) | адрес прослушивания |
| `Features__DevCaptchaPeek` | Development: `true`, Production: `false` | dev-эндпоинт подсказки CAPTCHA |
| `ASPNETCORE_ENVIRONMENT` | Docker: `Production` | окружение |

### Безопасность dev-хука
`GET /api/dev/captcha/{id}` возвращает код CAPTCHA и существует **только** при
`ASPNETCORE_ENVIRONMENT=Development` **и** `Features__DevCaptchaPeek=true`
(в Production по умолчанию выключен). Нужен исключительно для автоматических
тестов и e2e; в продакшене эндпоинт недоступен.

---

## Покрытие уровней

| Уровень | Статус | Комментарий |
|---------|--------|-------------|
| Базовый | ✅ реализован | все функциональные требования ТЗ |
| Junior+ | ✅ реализован | Queue, Cache, Events, WebSocket |
| Middle | 🟡 заложено | интерфейсы-точки расширения, схема и описание в `docs/ARCHITECTURE.md`; внешние сервисы (RabbitMQ, Redis, Elasticsearch, cloud) не поднимаются |
| Middle+ | 🟡 заложено | архитектурные решения под 1M сообщений/100k пользователей описаны в `docs/ARCHITECTURE.md`; нагрузочный тест не входит в зону |

---

## Известные ограничения / вне зоны

- **Развёртывание на хостинге/VDS** — по условию задачи вне зоны автоматизации
  (требует внешних аккаунтов). Docker-упаковка выполнена и проверена локально.
- **Middle-сервисы** (GraphQL, брокер, NoSQL, cloud) не реализованы, только
  точки расширения и проектные решения.
- **Нагрузочный тест** под 1M/100k не проводился.
- SQLite выбран по умолчанию из требований ТЗ; переход на MS SQL/PostgreSQL
  описан в `docs/ARCHITECTURE.md`.
- Запись видео-демо развёрнутого приложения — ручной шаг вне репозитория.
