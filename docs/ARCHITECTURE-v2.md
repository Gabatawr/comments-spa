# Architecture v2 — Clean Architecture, порты и адаптеры

> Lead-owned. Дополняет `docs/API-v2.md`. Меняется только через Lead.
> Цель — зафиксировать швы между `core` (Domain/Application/Infrastructure) и `api`
> (Comments.Api) **до** распараллеливания.

## 1. Проекты и правило зависимостей

```
src/Backend/
  Comments.Domain/           ← сущности, value-objects, доменные события. НОЛЬ зависимостей.
  Comments.Application/      ← use-cases, DTO, валидация, порты (интерфейсы). Зависит только на Domain.
  Comments.Infrastructure/   ← адаптеры: EF/Npgsql, Redis, RabbitMQ, Elasticsearch, FS, CAPTCHA. Зависит на Application.
  Comments.Api/              ← composition root: endpoints, GraphQL, WebSocket, DI, конфиг. Зависит на Application + Infrastructure.
tests/CommentsApi.Tests/     ← ссылается на Comments.Api (+ при необходимости Application/Domain).
Comments.slnx                ← решение (.NET 10 slnx).
```

Жёсткие правила (проверяются на приёмке `grep`/csproj):
- `Comments.Domain.csproj` — **нет** `PackageReference` на EF Core / ASP.NET / Redis / RabbitMQ.
- `Comments.Application.csproj` — только `ProjectReference` на Domain, без инфраструктурных пакетов.
- `Comments.Infrastructure.csproj` — инфраструктурные пакеты здесь.
- `Comments.Api.csproj` — ASP.NET + ссылки на Application и Infrastructure.

## 2. Namespaces

| Проект | Корневой namespace |
|---|---|
| Domain | `Comments.Domain` |
| Application | `Comments.Application` |
| Infrastructure | `Comments.Infrastructure` |
| Api | `Comments.Api` |

Тесты `CommentsApi.Tests` адаптируются: `using CommentsApi.Validation;` →
`using Comments.Application.Validation;`, `using CommentsApi.Services;` →
`using Comments.Application.Services;`. Это осознанная адаптация, зафиксировать в отчёте.

## 3. Порты (Application.Abstractions)

Порты — интерфейсы; реализации — в Infrastructure. Точные сигнатуры — за `core`, но **имена и
семантика заморожены**:

| Интерфейс | Namespace | Ответственность |
|---|---|---|
| `ICommentRepository` | `Comments.Application.Abstractions.Persistence` | дерево/страницы/курсоры, `AddAsync`, `ExistsAsync`, `Count*` |
| `IAttachmentRepository` | `...Abstractions.Persistence` | чтение/запись attachments |
| `IUnitOfWork` | `...Abstractions.Persistence` | `SaveChangesAsync` |
| `ICommentQueryService` | `Comments.Application.Services` | read-модель (`GetRootPageAsync`, `GetByIdAsync`) — v1-сигнатуры сохранить |
| `ICommentCreateService` | `Comments.Application.Services` | создание (`CreateAsync(CommentCreateModel, ct)`) |
| `IAttachmentService` | `Comments.Application.Services` | валидация/ресайз/сохранение файла (v1-методы сохранить) |
| `ICaptchaService` | `Comments.Application.Services` | `Generate()`, `Validate(id, answer)`, `PeekAnswer(id)` — **v1-контракт, менять нельзя** |
| `ICaptchaStore` | `...Abstractions.Caching` | хранение challenge (Redis/memory) |
| `ICacheService` | `...Abstractions.Caching` | `GetAsync/SetAsync/RemoveAsync/IncrementAsync` |
| `IFileStorage` | `...Abstractions.Storage` | `SaveAsync`, `OpenRead`, `DeleteAsync`, `Exists` |
| `IEventPublisher` | `...Abstractions.Messaging` | `PublishAsync(DomainEventEnvelope, ct)` + publisher confirms |
| `IEventBus` | `...Abstractions.Messaging` | внутренняя шина (fallback), как в v1 |
| `ICommentSearchIndex` | `...Abstractions.Search` | `IndexAsync`, `SearchAsync`, `EnsureIndexAsync` |
| `IClock` | `...Abstractions` | `UtcNow` (тестируемость) |
| `IEventConsumer` | `...Abstractions.Messaging` | фоновая подписка на очередь |

Публичные типы, которые обязаны существовать (их используют тесты и `api`):
`CommentValidator`, `HtmlSanitizer`, `CommentSortOptions`, `ValidationErrors`, `CommentMapper`,
`CommentCreateModel`, `CaptchaChallenge`, DTO из `docs/API-v2.md` §1.

## 4. Точки DI (то, что вызывает `api`)

Infrastructure обязана предоставить расширения (в `Comments.Infrastructure`):

```csharp
// Comments.Infrastructure/DependencyInjection.cs
public static IServiceCollection AddCommentsInfrastructure(
    this IServiceCollection services, IConfiguration configuration);

// Comments.Application/DependencyInjection.cs
public static IServiceCollection AddCommentsApplication(this IServiceCollection services);

// Comments.Infrastructure/Persistence — регистрация DbContext + миграции
public static async Task ApplyMigrationsAsync(this IServiceProvider services, CancellationToken ct = default);

// Comments.Infrastructure/Search
public static async Task EnsureSearchIndexAsync(this IServiceProvider services, CancellationToken ct = default);

// Comments.Infrastructure/Messaging — hosted services, поднимающие консьюмеров
// (регистрируются внутри AddCommentsInfrastructure)
```

`Program.cs` (зона `api`) делает:
```csharp
builder.Services.AddCommentsApplication();
builder.Services.AddCommentsInfrastructure(builder.Configuration);
...
await app.Services.ApplyMigrationsAsync();
await app.Services.EnsureSearchIndexAsync();
```

## 5. Провайдеры: переключатель, валидация, деградация

Каждая внешняя зависимость — адаптер за портом. Какой адаптер работает, решает **один**
переключатель, разобранный ровно один раз при старте (`ProviderResolver` → `ActiveProviders`)
и общий для композиционного корня, баннера и `GET /api/info` — расходиться они не могут.

| Порт | Значения | По умолчанию |
|---|---|---|
| `Providers:Database` | `postgres`, `mssql`\* | `postgres` |
| `Providers:Cache` | `redis`, `memory` | `redis` |
| `Providers:Messaging` | `rabbitmq`, `inmemory` | `rabbitmq` |
| `Providers:Search` | `elastic`, `none` | `elastic` |
| `Providers:Storage` | `filesystem`, `s3`, `azureblob` | `filesystem` |

\* `mssql` объявлен как шов (ТЗ предпочитает MS SQL), но не реализован: выбор падает с точным
сообщением, а не с «неизвестное значение». Все остальные значения реализованы настоящими
адаптерами — включая S3-совместимое хранилище (AWS S3, MinIO, Yandex, R2, Spaces) и Azure Blob
с managed identity.

Две политики:

| Настройка | По умолчанию | Смысл |
|---|---|---|
| `Providers:Strict` | `true` | неизвестное или нереализованное имя → ошибка старта со списком допустимых значений |
| `Providers:FailFastOnUnavailable` | `false` | выбранный провайдер недоступен → ошибка старта; `false` — деградация с `health.<порт>=error` |

Деградация по умолчанию как и раньше: недоступный Redis → memory-fallback и `cache=error`,
недоступный брокер → in-memory шина и `broker=error`, недоступный ES → `search=error`.
`FailFastOnUnavailable=true` переключает это на отказ старта — то, что нужно управляемому
облаку: молчаливая деградация в проде хуже, чем контейнер, который не поднялся.

**Совместимость.** Канонических ключей ровно пять — по одному на порт. Прежние
(`Cache:Provider`, `Messaging:Provider`, `Storage:Provider`, `Search:Enabled`) удалены: это была
совместимость с предыдущей итерацией того же проекта, а не с чужим API, и она стоила ветку на
каждый порт плюс абзац в четырёх документах. В `appsettings.json` секции `Providers` намеренно
**нет**: значение в файле перекрыло бы окружение, и деплой молча остался бы на файловом значении.
Дефолты живут в `ProviderCatalog`.

**Наблюдаемость.** Разрешённый набор пишется одной строкой в лог при старте, отдаётся в
`GET /api/health` (блок `providers` — имена, рядом со статусами) и в `GET /api/info` (полная
матрица: активное значение, статус, описание, альтернативы). Health больше **не знает имён**
провайдеров: он спрашивает `IsAvailable` у самого адаптера, а `IsSelfContained` — у каталога,
поэтому новый адаптер появляется в health без правки эндпоинта.

Это важно: **тесты по умолчанию** гоняются на `Providers:Cache=memory`,
`Messaging=inmemory`, `Search=none`, чтобы не требовать Redis/RabbitMQ/ES
для юнитов. Интеграционный прогон против реальных сервисов — отдельный фильтр (см. §7).

## 6. Тесты: адаптация 251 теста под PostgreSQL

- `tests/CommentsApi.Tests/Infrastructure/TestAppFactory.cs` больше не создаёт SQLite-файл.
  Вместо этого — уникальная БД на процесс/тест в общем PostgreSQL.
- Connection string сервера берётся из `COMMENTS_TEST_POSTGRES` (env). Значение по умолчанию:
  `Host=localhost;Port=55432;Username=comments;Password=comments;Database=postgres`.
- `TestAppFactory`:
  1. создаёт `comments_test_<guid>` через `NpgsqlConnection` к `postgres` БД;
  2. подставляет `ConnectionStrings__Default` на новую БД (через env, как в v1);
  3. при старте приложения применяет миграции;
  4. на `Dispose` — `DROP DATABASE ... WITH (FORCE)` (best effort).
- Полный прогон `dotnet test` **требует доступного PostgreSQL**. Команда (Lead-owned скрипт):
  `scripts/test.sh` — поднимает `postgres` из compose, ждёт health, запускает `dotnet test`.
- 251 тест должен проходить без изменения ожиданий. Если тест проверяет SQLite-специфику
  (например `EF.Functions.Collate(..., "NOCASE")`) — правится production-код (`lower()`), а не тест.
- Порядок: тесты **не параллельны** (`AssemblyInfo.cs`), менять не нужно.

## 7. Профили тестов (xUnit)

| Фильтр | Что гоняет | Требует |
|---|---|---|
| (без фильтра) | полный набор 251+ | PostgreSQL |
| `Category=IntegrationBroker` | реальные Redis/RabbitMQ/ES | compose-стек |
| `Category=Load` | smoke k6-сценария | профиль load |

## 8. Git-процесс

Все работают в **одном** рабочем каталоге на `main` (незакоммиченные изменения).
**Никто, кроме Lead, не выполняет `git checkout/branch/commit/merge/rebase/tag`.**
Только чтение: `git status`, `git diff`, `git log`. Lead в конце создаёт `feature/*`-ветки,
коммитит по зонам и вливает в `main` через `--no-ff`, затем тег `v2.0.0`.

## 9. Запрещено

- `docker image prune`, `docker system prune`, `docker volume prune`, удаление чужих
  контейнеров/образов.
- Cloud-вызовы в обязательном пути запуска.
- Секреты в репозитории (только `.env.example`).
- Правки вне своей write-зоны (при необходимости — запрос Lead через task board).
