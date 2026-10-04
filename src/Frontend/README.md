# Angular SPA «Комментарии»

Фронтенд тестового задания: Angular 22 (standalone-компоненты, zoneless, signals, без SSR),
собирается в Docker (node build → nginx) и работает против **замороженного API v2** —
`docs/API-v2.md`.

## Что реализовано

| Требование ТЗ | Где |
|---|---|
| Форма: User Name, E-mail, Home page, CAPTCHA, Text, вложение (multipart) | `src/app/components/comment-form/` |
| CAPTCHA с картинкой `GET /api/captcha`, кнопкой обновления и свежим id | `comment-form.ts` (`loadCaptcha`) |
| Ответ на комментарий с предзаполненным `parentId` | `comments.store.ts` (`replyTarget`) + форма |
| Таблица корневых, сортировка User Name / E-mail / дата в обе стороны, дефолт LIFO | `comments-list/` + `comments.store.ts` |
| Пагинация по 25 с навигацией | `comments-list.html` (`pages()`) |
| Каскад ответов: отступ + левая полоса-цитата, любая глубина | `comment-node/` (рекурсивный `ngTemplateOutlet`) |
| AJAX-предпросмотр `POST /api/preview` без перезагрузки | `comment-form.ts` (`runPreview`) |
| Toolbar `[i] [strong] [code] [a]`, оборачивает выделение, `[a]` спрашивает href+title | `comment-form.ts` (`applyTag`, `insertLink`) |
| Клиентская валидация — зеркало серверных правил, inline-ошибки, маппинг 400 `errors` | `core/validation.ts`, `comment-form.ts` (`applyServerErrors`) |
| Вложения: lightbox для изображений, модалка TXT, ссылки из API | `core/viewer.service.ts`, `app.html` |
| XSS-safe рендер: `[innerHTML]` только для уже санитизированного `text` | `core/sanitize.ts` |
| WebSocket `/ws`, live-вставка `comment.created` | `core/realtime.service.ts`, `comments.store.ts` |
| Дизайн по `docs/task/page1-X10.png` | `src/styles.css` |

## Локальная разработка

```bash
cd src/Frontend
export NPM_CONFIG_CACHE=/home/gab/projects/comments-spa/.npm-cache   # sandbox: ~/.npm недоступен
npm ci
npm start          # ng serve + proxy.conf.json (/api и /ws -> http://localhost:8081)
```

`proxy.conf.json` рассчитан на API, поднятый на хост-порту `8081`.

## Сборка

```bash
npm ci
npx ng build --configuration production
# результат: dist/comments-spa/browser/index.html
```

## Docker

```bash
docker build -t comments-spa-web src/Frontend
```

- Стадия 1: `node:22-alpine`, `npm ci --no-audit --no-fund`, `ng build --configuration production`.
- Стадия 2: `nginx:alpine`, статика из `dist/comments-spa/browser`, `src/Frontend/nginx.conf`.
- nginx: SPA fallback `try_files $uri $uri/ /index.html`, `client_max_body_size 8m`,
  прокси `/api/`, `/graphql` и `/ws` на `http://api:8080` (`/ws` — с заголовками
  `Upgrade`/`Connection`). Резолвинг `api` ленивый (Docker DNS `127.0.0.11`), поэтому
  nginx не падает, если API стартует позже.

`node_modules` в репозиторий не коммитится. Итоговый образ web поднимается `docker compose`
(зона `infra`), хост-порт `8080`.

## Структура

```
src/app/
  app.ts / app.html              # shell: статусы API/WS, тосты, lightbox, модалка TXT
  app.config.ts                  # provideHttpClient(withFetch) + router
  core/
    models.ts                    # DTO и ApiError (docs/API-v2.md §1)
    api.service.ts               # REST-обёртка
    comments.store.ts            # состояние: сортировка, пагинация, дерево, live-insert
    realtime.service.ts          # WebSocket /ws
    validation.ts                # клиентское зеркало CommentValidator/HtmlSanitizer
    sanitize.ts                  # defense-in-depth allow-list
    viewer.service.ts            # lightbox + TXT-модалка
    toast.service.ts
    format.ts
  components/
    comment-form/                # форма + CAPTCHA + toolbar + preview + вложения
    comments-list/               # таблица корней + пагинация
    comment-node/                # рекурсивный узел каскада
```
