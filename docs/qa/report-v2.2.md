# Приёмка v2.2 — cloud-ready переключатель провайдеров

Lead-owned отчёт. Все числа — из прогонов на замороженном дереве; отдельно и прямо перечислено то,
что **не** проверялось, и почему.

**Итог: принято.** Каждый внешний порт выбирается одним конфигом и без правки кода; объектное
хранилище реализовано двумя настоящими адаптерами; выбранный набор виден в `GET /api/info` и в логе
старта; неизвестное значение останавливает старт. 325 тестов зелёные, стек поднят, переключение
проверено живьём в обе стороны.

---

## 1. Что вошло

| Область | Изменение |
|---|---|
| Переключатель | `ProviderCatalog` + `ProviderResolver` → `ActiveProviders`; один неизменяемый снимок на старте |
| Совместимость | прежние ключи (`Cache:Provider`, `Storage:RootProvider`, `Search:Enabled`) продолжают работать; канонические `Providers:*` приоритетнее |
| Хранилище | `S3FileStorage` (AWSSDK.S3) и `AzureBlobFileStorage` (Azure.Storage.Blobs + Identity) вместо заглушек с `NotSupportedException` |
| Порт хранилища | `IFileStorage.GetFullPath` → `TryGetLocalPath`: облако отказывается, веб-слой стримит |
| Поиск | basic auth / API key / приватный CA — то, без чего managed Elasticsearch/OpenSearch не подключить |
| Наблюдаемость | строка `Providers: …` на старте, `GET /api/info`, health без хардкода имён провайдеров |
| Политика | `Providers:Strict` (по умолчанию `true`), `Providers:FailFastOnUnavailable` (по умолчанию `false`) |
| Демонстрация | `infra/compose.s3.yml` — стек с S3-совместимым сервером и пустым томом API |
| Тесты | +40 кейсов: 33 на переключатель и `/api/info`, 7 протокольных на S3-адаптер |
| Документы | `README.md`, `AGENTS.md`, `docs/API-v2.md` (§10–§11), `docs/ARCHITECTURE-v2.md`, `infra/README.md`, `.env.example` |

## 2. Как устроен переключатель

`ProviderResolver.Resolve` читает конфигурацию один раз при старте и возвращает `ActiveProviders`.
Тот же экземпляр регистрируется в DI и используется тремя потребителями: композиционным корнем,
баннером и `/api/info`. Это и есть ответ на вопрос «как понять, что реально включено»: набор
зарегистрированных адаптеров и то, что отдаёт отчёт, не могут разойтись — они читают один объект.

`ProviderCatalog` — единственный источник правды о том, что вообще выбираемо: значение, описание,
альтернативы и признак «внутрипроцессный» (нужен health, чтобы не показывать `disabled` для Redis,
который на самом деле работает в процессе).

Две ошибки конфигурации теперь **останавливают старт** вместо тихого отката:

- неизвестное значение — с перечислением допустимых;
- объявленный, но нереализованный шов (`mssql`) — с прямым текстом о том, что нужен свой набор миграций.

`Providers:Strict=false` возвращает прежнее поведение: предупреждение в лог и откат на дефолт.

## 3. Живые доказательства

### 3.1 Стек с объектным хранилищем

Поднят `docker compose -f docker-compose.yml -f infra/compose.s3.yml up -d --build`.
Образ API один и тот же — меняются только переменные окружения.

```
Providers: database=postgres cache=redis messaging=rabbitmq search=elastic storage=s3 (strict=True, failFastOnUnavailable=False)
S3 storage: bucket=comments-attachments prefix=(root) endpoint=http://s3:9090 pathStyle=True
```

`GET /api/info`:

```json
{
  "port": "storage",
  "value": "s3",
  "status": "ok",
  "implemented": true,
  "selfContained": false,
  "description": "S3-compatible object storage: AWS S3, MinIO, Yandex Object Storage, Cloudflare R2, DO Spaces",
  "alternatives": ["filesystem", "azureblob"]
}
```

`GET /api/health` — матрица активных провайдеров рядом со статусами (имена не захардкожены):

```json
{"providers": {"database": "postgres", "cache": "redis", "messaging": "rabbitmq", "search": "elastic", "storage": "s3"}}
```

Загрузка вложения 400×300 через `POST /api/comments` (multipart, реальная CAPTCHA из Redis):

```
вложение #46: 400×300 → 320×240, 768 Б
в бакете:     eddb998bdc76446b976161708a252d1a.png   ← объект лежит здесь
в томе API:   НЕТ                                     ← и только здесь
```

```
$ docker exec … find /app/storage -type f | wc -l   → 45   (файлы от 2026-10-04, прошлые прогоны)
$ docker exec comments-spa-api test -f /app/storage/<ключ>  → НЕТ (ожидаемо)
```

Скачивание идёт **стримом**, а не через отдачу файла с локального пути — именно это и означает
`TryGetLocalPath() == false` у объектного хранилища:

```
HTTP/1.1 200 OK
Content-Length: 768
Content-Type: image/png
Content-Disposition: inline; filename="upload.png"
→ PIL: 320x240, режим RGB, 768 байт
```

Демонстрационный комментарий и его вложение после проверки удалены (`DELETE 1`, `DELETE 1`),
чтобы в базе не осталось записи, вложение которой недостижимо после возврата на файловую систему.

### 3.2 Ошибка конфигурации останавливает старт

Три запуска одного и того же образа с разным окружением:

| Окружение | Результат |
|---|---|
| `Providers__Storage=swift` | `Providers:Storage='swift' is not a known storage provider. Allowed values: filesystem, s3, azureblob.` |
| `Providers__Database=mssql` | `Providers:Database='mssql' is a declared but unimplemented seam. MS SQL Server via EF Core SqlServer — the assignment's preferred engine, needs its own migration set. Implemented values: postgres.` |
| `Providers__Storage=swift`, `Strict=false` | `warn: Providers:Storage='swift' is not a known storage provider… Falling back to 'filesystem'.` — старт продолжается, баннер показывает `storage=filesystem` |

### 3.3 Обратное переключение

После возврата к `docker compose -f docker-compose.yml up -d --remove-orphans`:

```
Providers: database=postgres cache=redis messaging=rabbitmq search=elastic storage=filesystem (strict=True, failFastOnUnavailable=False)
/api/info → version=2.2.0 strict=True failFast=False
  database   = postgres    ok   []
  cache      = redis       ok   ['memory']
  messaging  = rabbitmq    ok   ['inmemory']
  search     = elastic     ok   ['none']
  storage    = filesystem  ok   ['s3', 'azureblob']
```

Контейнеры S3-профиля убраны как orphans; стек — 6/6 `healthy`, `ASPNETCORE_ENVIRONMENT=Production`,
SPA отдаёт 200.

## 4. Тесты и нагрузка

```
Passed!  - Failed: 0, Passed: 325, Skipped: 0, Total: 325, Duration: 7 m 25 s - CommentsApi.Tests.dll (net10.0)
```

Против реального PostgreSQL, на замороженном дереве. Было 285 на v2.1 → **+40**:

| Набор | Кейсов | Что проверяет |
|---|---|---|
| `Providers/ProviderResolverTests` + `Providers/StorageProviderTests` + `InfoEndpointTests` | 33 | дефолты, приоритет канонических ключей над legacy-алиасами, регистронезависимость, отказ при неизвестном значении, отказ на объявленном шве, откат в non-strict, регистрация нужного адаптера, `TryGetLocalPath` у всех трёх реализаций, блокировка path traversal, состав `/api/info` |
| `Providers/S3StorageAdapterTests` | 7 | протокол: настоящий AWS SDK с подписью SigV4 против S3-эндпоинта в процессе — round-trip под настроенным префиксом, content type, отсутствующий ключ → `null`, удаление, проба доступности, недостижимый эндпоинт → `false`, а не исключение |

Протокольный набор намеренно не требует ни контейнеров, ни облачных ключей: он поднимает
S3-совместимый эндпоинт внутри тестового процесса, поэтому проходит где угодно и в CI.

**k6-smoke** на этом же билде:

```
checks        ✓ 'rate>0.99' rate=100.00%
http_req_failed  0.00%  0 out of 8
✓ health 200  ✓ database ok  ✓ list 200  ✓ list has items  ✓ detail 200
✓ search 200 or 503  ✓ captcha 200  ✓ dev peek 200  ✓ create 201  ✓ stats 200
```

Полные нагрузочные сценарии (`100k-full`, `1m-full`) для этого инкремента **не перепрогонялись** —
см. §6.

## 5. Найдено и исправлено по дороге

- **Поиск создавал `HttpClient` без аутентификации.** С managed Elastic Cloud / OpenSearch так не
  подключиться вообще. Добавлены basic auth, API key и приватный CA (`Search:AllowInvalidCertificate`).
- **`IFileStorage.GetFullPath` протекал файловой системой в порт.** Облачная реализация обязана была
  бросать, а веб-слой — проверять имя провайдера. Заменено на `TryGetLocalPath`; проверка имени
  из эндпоинта убрана.
- **Health хардкодил имена** (`Cache:Provider == "redis"`). Теперь статус берётся у самого адаптера,
  а «внутрипроцессный» признак — из каталога.
- **Секция `Providers:*` в `appsettings.json` перекрывала legacy-ключи из окружения** — существующий
  деплой молча остался бы на старом провайдере. Секция убрана, дефолты живут в каталоге.
- **Побочная находка для будущих S3-тестов:** AWSSDK v4 по умолчанию отправляет `PutObject` с
  `aws-chunked`-фреймингом (защита целостности), поэтому тело запроса на проводе — не сырые байты.
  Заглушка в тесте это декодирует; при написании своих S3-стендов это первое, обо что спотыкаешься.

## 6. Чего в этом отчёте нет

- **Реальных облачных вызовов нет** — как и было заявлено с самого начала: нет аккаунтов, и это
  сознательно вне зоны. Всё, что проверено, проверено на локальном стеке.
- **MinIO в этой песочнице не запускался.** Реестр ограничен allow-list'ом: `minio/minio`, `minio/mc`,
  `seaweedfs/seaweedfs` отдают `pull access denied`, `dl.min.io` — HTTP 410. Поэтому демо-профиль
  собран на `adobe/s3mock` (настоящий S3 по протоколу, проверен живьём), а замена на MinIO показана
  в шапке `infra/compose.s3.yml` двумя строками. **На реальном хосте профиль с MinIO не прогонялся.**
- **Azure Blob не проверен против живого аккаунта и эмулятора.** Подтверждено только то, что адаптер
  регистрируется, отказывается работать без контейнера и сообщает об отсутствии локального пути.
  Реальный ввод-вывод Azure-адаптера не выполнялся.
- **S3-адаптер проверен против протокола, а не против AWS.** Подпись и обмен настоящие, но
  специфика самого AWS (региональные редиректы, права IAM) не затронута.
- **`Providers:Database=mssql` остаётся объявленным швом.** ТЗ предпочитает MS SQL Server; для него
  нужен отдельный набор миграций — это по-прежнему не сделано и теперь говорит об этом внятно.
- **Полный k6 не перепрогонялся.** Инкремент не трогает горячий путь запросов (только сборка DI и
  адаптеры), а база сейчас содержит seed на 100k, не на 1M: набор данных для `1m-full` был снесён
  при чистой приёмке v2.1. Цифры нагрузки остаются за `perf/report.md` (v2.0/v2.1);
  выше приведён свежий smoke на текущем билде.
- **Тестовый харнесс по-прежнему создаёт БД на каждый тест** (~2.4 с на тест). Шаблонная БД ускорила
  бы прогон в разы; в этот инкремент не входило.

## 7. Git

`main`, тег **`v2.2.0`**, дерево чистое. Ветка инкремента влита через `--no-ff`; прежние теги
(`v1.0.0`, `v2.0.0`, `v2.1.0`) не переписывались.
