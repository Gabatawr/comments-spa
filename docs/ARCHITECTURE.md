# Архитектура и разграничение зон

## Стек
- **Backend**: .NET 10 (ASP.NET Core Minimal API + Controllers), EF Core 10, SQLite (по умолчанию).
- **Frontend**: SPA без сборки (ES-модули, vanilla JS) — статика отдаётся backend-ом.
  Выбор обоснован требованием «поднимается с нуля строго по README»: нет node-сборки,
  нет node_modules в Docker, один контейнер, один origin.
- **Junior+**: Queue (`System.Threading.Channels`), Cache (`IMemoryCache`), Events (in-process bus),
  WebSocket (raw `System.Net.WebSockets`).
- **Docker**: multi-stage (`sdk` → `aspnet`), SQLite-файл в volume.

## Структура

```
src/Backend/CommentsApi/       # backend (owner: backend)
  Domain/                      # Comment, Attachment
  Data/                        # AppDbContext, migrations, schema bootstrap
  Dtos/                        # API-контракт (docs/API.md)
  Validation/                  # валидаторы + HtmlSanitizer (XHTML)
  Services/                    # CaptchaService, AttachmentService, CommentService
  Infrastructure/              # EventBus, BackgroundQueue, CommentCache, WebSocketHub
  Controllers/                 # CaptchaController, CommentsController, AttachmentsController
src/Frontend/                  # SPA (owner: frontend)
  index.html, css/, js/, assets/
tests/                         # QA: xUnit + e2e (owner: qa)
docs/                          # README/API/ARCHITECTURE + docs/qa (owner: qa)
db/schema.sql                  # схема для MySQL Workbench (owner: lead)
Dockerfile, docker-compose.yml # owner: lead
```

## Модель данных (SQLite)

**Comments**
| Колонка | Тип | Примечание |
|---|---|---|
| Id | INTEGER PK | autoincrement |
| ParentId | INTEGER NULL | FK → Comments.Id, self-reference |
| UserName | TEXT NOT NULL | латиница+цифры |
| Email | TEXT NOT NULL | email |
| HomePage | TEXT NULL | url |
| TextHtml | TEXT NOT NULL | санитизированный HTML |
| TextPlain | TEXT NOT NULL | без тегов |
| CreatedAt | TEXT NOT NULL | ISO-8601 UTC, индекс |
| ClientIp | TEXT NULL | идентификация клиента |
| UserAgent | TEXT NULL | идентификация клиента |
| AttachmentId | INTEGER NULL | FK → Attachments.Id |

**Attachments**
| Колонка | Тип | Примечание |
|---|---|---|
| Id | INTEGER PK | |
| FileName | TEXT | оригинальное имя |
| StoredName | TEXT | имя на диске |
| StoragePath | TEXT | относительный путь |
| ContentType | TEXT | image/png, text/plain |
| Kind | TEXT | image / text |
| SizeBytes | INTEGER | |
| Width, Height | INTEGER NULL | после resize |
| OriginalWidth, OriginalHeight | INTEGER NULL | до resize |
| CreatedAt | TEXT | |
| Sha256 | TEXT | целостность |

Индексы: `Comments(ParentId)`, `Comments(CreatedAt)`, `Comments(UserName)`, `Comments(Email)`,
`Attachments(Kind)`.

## Защита
- **XSS**: серверный санитайзер по белому списку (`a[href,title]`, `code`, `i`, `strong`),
  экранирование всего остального, проверка парности/закрытия тегов (XHTML), запрет
  `javascript:`/`data:` в `href`. Фронтенд дополнительно прогоняет ответ через allowlist-парсер.
- **SQL-инъекции**: только EF Core LINQ с параметризацией; ни одной конкатенации SQL.
- **Файлы**: проверка magic bytes и размера, пересжатие картинок (ImageSharp), запрет апскейла,
  лимит TXT 100 КБ, раздача с `nosniff` и `Content-Disposition: inline`.
- **CAPTCHA**: one-time, TTL 5 мин, ответ сравнивается case-insensitive, never returned to client.

## Junior+
| Инструмент | Реализация |
|---|---|
| Queue | `CommentCreatedQueue` (Channel) + `CommentCreatedConsumer` (BackgroundService) |
| Cache | `CommentCache` (IMemoryCache) для страниц списка + CAPTCHA-store; инвалидация по событию |
| Events | `IEventBus` / `InMemoryEventBus`, событие `CommentCreatedEvent` |
| WebSocket | `/ws`, `WebSocketHub` с broadcast; health отдаёт число клиентов |

## Middle (заложено, не реализуется полностью — вне зоны)
- Graph/GraphQL: выделить read-model `CommentReadService` под GraphQL-резолверы.
- Брокер (RabbitMQ/Kafka): `IEventBus` — точка расширения, заменить InMemory на брокер.
- NoSQL (Elasticsearch/Redis): `CommentCache` — интерфейс, Redis-реализация; поиск — ES-индекс по `TextPlain`.
- Cloud: конфиг через env, health/readiness, stateless web-слой, SQLite → PostgreSQL/MS SQL.
- Middle+ (1M сообщений / 100k юзеров за 24ч): партиционирование по дате, read-реплики,
  CQRS read-model, rate-limit, CDN для вложений, асинхронная запись через брокер.

## Разграничение write-зон (нет изоляции ФС!)
| Зона | Владелец |
|---|---|
| `src/Backend/**` | backend |
| `src/Frontend/**` | frontend |
| `tests/**`, `docs/qa/**` | qa |
| корень, `db/**`, `scripts/**`, `docs/*.md` (кроме qa) | lead |
