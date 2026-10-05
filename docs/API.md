# API Contract (FROZEN — v1)

> **Исторический документ (этап 1).** Замороженный контракт v1. Действующий —
> [`API-v2.md`](API-v2.md): он расширяет v1, не меняя существующие ручки, коды и формат ошибок.

> Этот файл — единый контракт между backend, frontend и QA. Менять его можно только
> через Lead (обновить файл и уведомить участников). Все три потока работают
> параллельно именно по этой спецификации.

## 0. Общие правила

- Base URL: тот же origin, что и SPA (`/`). Все API-роуты начинаются с `/api`.
- Формат: JSON UTF-8 (`application/json`), кроме загрузки файла (`multipart/form-data`).
- Даты: ISO-8601 UTC, например `2025-05-22T22:30:00Z`.
- Ошибки валидации: HTTP 400, тело вида
  ```json
  {
    "title": "Validation failed",
    "status": 400,
    "errors": {
      "userName": ["User Name may contain only latin letters and digits."],
      "captcha": ["CAPTCHA answer is invalid or expired."]
    }
  }
  ```
- Прочие ошибки: HTTP 4xx/5xx + `{ "title": "...", "status": N, "detail": "..." }`.
- Все тексты ошибок — на английском (их показывает UI).
- Идентификация клиента: сервер сохраняет IP и User-Agent каждого комментария.

## 1. DTO

### CommentDto
```json
{
  "id": 12,
  "parentId": null,
  "userName": "Alice",
  "email": "alice@example.com",
  "homePage": "https://example.com",
  "text": "<strong>Hello</strong> &amp; <i>welcome</i>",
  "textPlain": "Hello & welcome",
  "createdAt": "2025-05-22T22:30:00Z",
  "clientIp": "203.0.113.7",
  "userAgent": "Mozilla/5.0 ...",
  "attachment": null,
  "replyCount": 2,
  "replies": [ /* CommentDto[] — вложенное дерево, всегда по возрастанию createdAt */ ]
}
```
- `text` — **уже санитизированный** сервером HTML (белый список: `a[href,title]`, `code`, `i`, `strong`).
- `textPlain` — тот же текст без тегов (для таблицы/анонса).
- `replies` — полное дерево потомков (не только первый уровень), отсортировано по `createdAt` ASC.
- `homePage` может быть `null`.
- `attachment` — `AttachmentDto` или `null`.

### AttachmentDto
```json
{
  "id": 7,
  "fileName": "photo.png",
  "contentType": "image/png",
  "kind": "image",
  "sizeBytes": 12345,
  "width": 320,
  "height": 240,
  "url": "/api/attachments/7",
  "thumbUrl": "/api/attachments/7/thumb"
}
```
- `kind`: `"image"` | `"text"`.
- Для `text`-вложения `width`/`height` = `null`, `thumbUrl` = `null`.
- Для image `width`/`height` — итоговые размеры **после** пропорционального уменьшения
  (≤ 320×240, апскейл запрещён).

## 2. Эндпоинты

### 2.1 `GET /api/captcha`
Возвращает новую CAPTCHA (6 символов: заглавные латинские буквы + цифры, без похожих 0/O/1/I).
```json
{
  "captchaId": "6f1c1e0e-6f2a-4f2e-9d0a-2b6c9f0f2a11",
  "image": "data:image/png;base64,iVBORw0KGgo...",
  "expiresInSeconds": 300
}
```
- Код никогда не возвращается клиенту.
- Повторное использование `captchaId` запрещено (one-time use).

### 2.2 `GET /api/comments`
Список **корневых** комментариев (`parentId == null`) с полным вложенным деревом.

Query-параметры:
| Параметр   | Тип   | По умолчанию | Допустимые значения |
|------------|-------|--------------|---------------------|
| `page`     | int   | `1`          | ≥ 1 |
| `pageSize` | int   | `25`         | 1..100 (жёсткий дефолт задания — 25) |
| `sortBy`   | enum  | `createdAt`  | `createdAt` \| `userName` \| `email` |
| `sortDir`  | enum  | `desc`       | `asc` \| `desc` |

- По умолчанию — **LIFO**: `sortBy=createdAt&sortDir=desc`.
- Сортировка применяется только к корневым комментариям; `replies` внутри всегда ASC.
- `page` вне диапазона → пустой `items` (не 404).

Ответ 200:
```json
{
  "items": [ /* CommentDto[] */ ],
  "page": 1,
  "pageSize": 25,
  "totalItems": 42,
  "totalPages": 2,
  "sortBy": "createdAt",
  "sortDir": "desc"
}
```
> `totalItems`/`totalPages` считают **только корневые** комментарии.

### 2.3 `GET /api/comments/{id}`
Один комментарий с деревом ответов. 404, если нет.

### 2.4 `POST /api/comments`
Создание комментария. **Всегда `multipart/form-data`** (attachment опционален).

Поля формы:
| Поле            | Обяз. | Правила |
|-----------------|-------|---------|
| `userName`      | да    | 3..50, `^[A-Za-z0-9]+$` |
| `email`         | да    | валидный email, ≤ 100 |
| `homePage`      | нет   | абсолютный `http(s)://` URL, ≤ 200; пусто = null |
| `text`          | да    | 1..5000 символов; только разрешённые теги; закрытие тегов проверяется (валидный XHTML) |
| `captchaId`     | да    | из `GET /api/captcha` |
| `captchaAnswer` | да    | сравнение без учёта регистра |
| `parentId`      | нет   | id существующего комментария |
| `attachment`    | нет   | один файл: JPG/JPEG/PNG/GIF ≤ 5 МБ **или** TXT ≤ 100 КБ |

Ответ 201:
```json
{ "comment": { /* CommentDto */ } }
```
Заголовок `Location: /api/comments/{id}`.

Ошибки: 400 с `errors` (ключи: `userName`, `email`, `homePage`, `text`, `captcha`, `parentId`, `attachment`).

### 2.5 `POST /api/preview`
Предпросмотр без сохранения и без перезагрузки.
Request: `application/json` `{ "text": "<b>hi</b>" }`
Response 200 (даже если текст невалиден):
```json
{
  "valid": false,
  "html": "<strong>hi</strong>",
  "plain": "hi",
  "errors": ["Tag <b> is not allowed."]
}
```
`html` — санитизированный фрагмент; при невалидном тексте возвращается максимально
безопасный результат + список ошибок.

### 2.6 `GET /api/attachments/{id}`
Отдаёт файл. `image/*` — inline; `text/plain` — inline (charset=utf-8).
Заголовки: `Content-Disposition: inline; filename="..."`, `X-Content-Type-Options: nosniff`.
404, если нет.

### 2.7 `GET /api/attachments/{id}/thumb`
Для изображений — уменьшенная копия (≤ 320×240). Для TXT — 404.

### 2.8 `GET /api/health`
```json
{
  "status": "ok",
  "database": "ok",
  "cache": "ok",
  "queue": { "pending": 0, "processed": 12 },
  "websocket": { "clients": 1 },
  "version": "1.0.0"
}
```

### 2.9 `WS /ws`
WebSocket. При подключении сервер присылает:
```json
{ "type": "hello", "message": "connected", "clients": 2 }
```
При создании комментария — broadcast всем:
```json
{ "type": "comment.created", "comment": { /* CommentDto */ } }
```
Клиент может прислать `{"type":"ping"}` → сервер отвечает `{"type":"pong"}`.
Невалидный JSON игнорируется (соединение не рвётся).

### 2.10 `GET /api/dev/captcha/{captchaId}` — ТОЛЬКО ДЛЯ РАЗРАБОТКИ/ТЕСТОВ
Возвращает `{ "captchaId": "...", "code": "AB12CD" }`.
Доступен **только** при `ASPNETCORE_ENVIRONMENT=Development` **и** `Features:DevCaptchaPeek=true`
(в `appsettings.Development.json` = true, в Production = false → 404). Нужен для curl/e2e-проверок.
В Production-контейнере эндпоинт отсутствует.

## 3. Статика / SPA
- `/` → `index.html`.
- Любой GET без префикса `/api` и без расширения файла → `index.html` (SPA fallback).
- Статика физически лежит в `src/Frontend/**`; backend на сборке копирует её в `wwwroot`
  (MSBuild-таргет `CopySpaToWwwroot` в `CommentsApi.csproj`).

## 4. Junior+ (Queue / Cache / Events / WebSocket)
- **Queue**: `Channel<CommentCreatedNotification>` + `BackgroundService`; HTTP-ответ не ждёт обработки.
- **Cache**: `IMemoryCache` для CAPTCHA (TTL 5 мин) и страниц списка; инвалидация при создании.
- **Events**: внутрипроцессная шина `IEventBus`; `CommentCreatedEvent` → WS-broadcast, сброс кэша, лог.
- **WebSocket**: см. 2.9.
- Health-эндпоинт отражает состояние queue/cache/ws.
