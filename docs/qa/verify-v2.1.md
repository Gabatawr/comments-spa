# Верификация v2.1 — adversarial-отчёт

**Задача:** task-4 (independent verification of task-1/api and task-2/web).
**Автор отчёта:** teammate `verifier` — не автор проверяемых изменений.
**Стек на момент финального прогона:** `docker compose ps` — 6/6 healthy; `GET /api/health` → `{"status":"ok",...,"version":"2.1.0"}`; web отдаёт бандл `main-4IHZ62UA.js`, в котором есть `quotedText`, `comment-quote`, `comment-vote`, `author-info-button`, `author-popover`.

Это состязательный отчёт: цель — сломать фичу, а не подтвердить её. Ниже только проверяемые факты и сырые команды. Всё, что не удалось сломать, перечислено с указанием, чем именно пытались.

## 0. Как воспроизвести

```bash
# API + jsdom + Playwright, одним прогоном:
VERIFY_LOG=$PWD/tests/.runtime/verify/final.log \
Q_FIXTURE_OUT=$PWD/tests/.runtime/verify/final-fixture.json \
  bash tests/e2e/adversarial-quote.sh

# только API (без браузера):
SKIP_BROWSER=1 bash tests/e2e/adversarial-quote.sh

# только браузерная часть против готового фикстура:
QROOT=$PWD BASE=http://localhost:8080 Q_REQUIRE_BROWSER=1 \
Q_FIXTURE=$PWD/tests/.runtime/verify/final-fixture.json \
PLAYWRIGHT_BROWSERS_PATH=$PWD/.playwright node tests/e2e/adversarial-quote.mjs

# регресс:
ADV_LOG=$PWD/tests/.runtime/verify/adversarial-regression.log bash tests/e2e/adversarial.sh
bash tests/frontend/run-browser-check.sh
```

Артефакты прогонов (gitignored, `tests/.runtime/`):
`final.log` (57 PASS / 1 FAIL — до удаления `.work`), `post-cleanup.log` (**57 PASS / 0 FAIL** — после), `adversarial-regression.log` (+`.stdout`), `browser-check.stdout`, `final-fixture.json`, `post-cleanup-fixture.json`.

## 1. Сводка

| Метрика | Значение |
|---|---|
| API/e2e-проверки (`adversarial-quote.sh`, разделы 1–6, `SKIP_BROWSER=1`, **после** удаления `.work`) | **57 PASS / 0 FAIL** |
| API/e2e-проверки до удаления `.work` (полный прогон с браузером) | 57 PASS / 1 FAIL (единственный FAIL — D-1) |
| Playwright/jsdom (`adversarial-quote.mjs`) | **80 PASS / 0 FAIL** |
| Регресс `tests/e2e/adversarial.sh` | exit 0, **SAFE=73, VULNERABLE=0**, ACCEPTED-RISK=1, SKIP=1 |
| Регресс `tests/frontend/run-browser-check.sh` | exit 0, **23 PASS / 0 FAIL** |
| Продуктовых дефектов XSS / контракта / обрезки / голосования | **не найдено** |
| Открытые дефекты | **D-2**: `src/Frontend/.work/` пересоздан в 16:50–16:51 локальным parity/diff-харнессом зоны Frontend (web/Lead; qa отрицает — grep по её файлам 0) — чистота дерева, зона Lead. Продуктовых дефектов нет. |
| `smoke.sh` на общем стеке после пробников (до самоочистки) | 92 PASS / **1 FAIL** — «stored XSS in list»; причина — тестовые данные adversarial-проб в выборке, не XSS (разбор в 5.1) |
| `adversarial-quote.sh` → сразу `smoke.sh`, прогон 1 (после самоочистки) | **59 PASS / 0 FAIL** → **93 PASS / 0 FAIL**; раздел 8 удалил 28 своих строк, БД/API без `<script` |
| то же, прогон 2 (идемпотентность) | **58 PASS / 1 FAIL** — единственный FAIL 6.4 (`.work` пересоздан не мной); раздел 8 снова `remaining=0`, `smoke.sh` → **93 PASS / 0 FAIL** |

## 2. Проверки

### 2.1 XSS через цитату (группа 1)

| # | Проверка | Метод | Команда | Результат | Доказательство |
|---|---|---|---|---|---|
| 1.1 | Entity-encoded `<script>` принимается как плоский текст | REST multipart + разбор JSON | `post_comment … text='&lt;script&gt;alert(1)&lt;/script&gt;'` | PASS | `HTTP 201`; `textPlain="<script>alert(1)</script>"` |
| 1.2 | Ответ хранит снимок родителя; своё тело не загрязнено | REST + JSON | `POST /api/comments` c `parentId` | PASS | reply id=100234: `text="benign reply one"`, `quotedText="<script>alert(1)</script>"`; в `text` нет `<script` |
| 1.3 | Raw опасная разметка режется санитайзером | REST multipart | raw `<img src=x onerror=alert(1)>`, `<script>alert(1)</script>`, `<svg/onload=…>`, `<iframe src=javascript:…>`, `<body onload=…>` | PASS ×5 | все `HTTP 400`, `errors.text` присутствует |
| 1.4 | Entity-encoded `<img onerror>` — плоский текст | REST + JSON | `text='&lt;img src=x onerror=alert(1)&gt;'` | PASS | reply: `quotedText == "<img src=x onerror=alert(1)>"` |
| 1.5 | Attribute-breakout и hex-entities | REST + JSON | `'" onmouseover=alert(1) x="'`, `&#x3c;script&#x3e;…` | PASS | `quotedText == '" onmouseover=alert(1) x="'`; `quotedText == "<script>alert(1)</script>"` |
| 1.6 | Разрешённая inline-разметка не попадает в снимок | REST + JSON | `<strong>bold</strong> <a href="https://example.com/">link</a>` | PASS | `quotedText == "hello bold link world"`, теги отсутствуют |
| 1.7 | **DOM:** внутри `.comment-quote` нет исполняемых элементов | Chromium, реальный стек | `adversarial-quote.mjs` B1 (encoded-script, encoded-img) | PASS | `script/img/iframe/svg/a/style/object/embed` внутри `.comment-quote` → `count=0`; `innerHTML` для script-кейса = `<span …>&lt;script&gt;alert(1)&lt;/script&gt;</span>` |
| 1.8 | **DOM:** текстовый узел содержит литерал, а не разметку | Chromium | `.comment-quote-text` `textContent` | PASS | `"<script>alert(1)</script>"`, `"<img src=x onerror=alert(1)>"` — точное совпадение с `quotedText` |
| 1.9 | **DOM:** `alert()` не срабатывает | Chromium dialog-listener | `page.on('dialog')` | PASS | `dialogs=[]` |
| 1.10 | Client-side санитайзер `src/Frontend/src/app/core/sanitize.ts` (регресс-риск `<br>`-границ) под реальным jsdom | Node 24 type-stripping + jsdom, импорт изменённого файла | `adversarial-quote.mjs` часть A | PASS | 20 опасных payload: `<svg onload>`, `<svg/onload>`, `<iframe javascript:>`, `<img onerror>` (в т.ч. `<img/src=x/onerror=…>`), `<a href="javascript:">` (mixed-case, whitespace, hex-entity), `data:text/html`, `<object>/<embed>`, `<style>`, `<form>/<input>`, `<body onload>`, mixed-case `<ScRiPt>` → ноль опасных тегов/атрибутов, sink-регексп не срабатывает |
| 1.11 | Новые `<br>`-границы не добавляют ничего, кроме `<br>` | jsdom | `sanitizeHtml('<p>one</p><p>two</p>')` и др. | PASS | `out="one<br>two"`, `nonBr=[]`; safe href сохраняется (`<a href="https://example.com/x">`) |
| 1.12 | `findTagErrors` отклоняет опасный ввод | jsdom | `findTagErrors(<svg…>)`, `<iframe…>`, `javascript:`-href и т.д. | PASS | непустые списки ошибок; обработчики на разрешённых тегах ожидаемо не валят валидатор, но срезаются `sanitizeHtml` (`<strong>b</strong>`) |

### 2.2 Аддитивность контракта (группа 2)

| # | Проверка | Команда | Результат | Доказательство |
|---|---|---|---|---|
| 2.1 | Замороженные документы не изменены | `git diff -- docs/API-v2.md docs/ARCHITECTURE-v2.md \| wc -c` | PASS | `0` байт |
| 2.2 | Корневые комментарии отдают `quotedText=null` | `GET /api/comments?page=1&pageSize=100` | PASS | `checked=100 non-null=0` |
| 2.3 | `GET /api/comments/{id}`: корень null, ответ — снимок | `GET /api/comments/100233` | PASS | root `quotedText=None`; reply `quotedText="<script>alert(1)</script>"` |
| 2.4 | GraphQL добавляет поле автоматически (без явного `Field`) | `POST /graphql {comment(id){quotedText}}` | PASS | reply → строка; root → `null`; unknown id → `null`, HTTP 200 (не 5xx) |
| 2.5 | Мутация GraphQL и JSON-создание (`/child`) тоже пишут снимок | `mutation{createComment{comment{quotedText}}}`; `POST /api/comments/{id}/child` | PASS | оба вернули `quotedText="<script>alert(1)</script>"`, `success=true`/`201` |
| 2.6 | Keyset-пагинация не сломана новым полем | `GET /api/comments?cursor=<nextCursor>` | PASS | HTTP 200, `items=5`, `quotedText` присутствует |
| 2.7 | Миграция применена, колонка nullable | `__EFMigrationsHistory`; `information_schema.columns` | PASS | `20261004161433_AddCommentQuotedText`; `quoted_text / text / YES` |

### 2.3 Обрезка (группа 3)

Метод: снимок сверяется с независимой реализацией алгоритма §1.1 на **фактическом `textPlain` родителя** (не на исходной строке), плюс проверки длины/многоточия.

| # | Кейс | Результат | Доказательство |
|---|---|---|---|
| 3.1 | 200 символов без пробелов | PASS | `len=161`, `…` в конце, `== 'x'*160 + '…'` |
| 3.2 | 275 символов со словами | PASS | `len=155`, `…`, рез по границе слова |
| 3.3 | ровно 160 символов | PASS | `len=160`, без `…` |
| 3.4 | 161 символ (пробел внутри первых 160) | PASS | `len=160`, с `…` |
| 3.5 | 150 символов | PASS | `len=150`, без `…` |
| 3.6 | схлопывание пробелов `one     two       three` | PASS | `quotedText="one two three"` |
| 3.7 | 161 при пробеле в позиции 160 | PASS | `len=157`, с `…` |
| 3.8 | ограничение длины | PASS для всех кейсов | `len <= 161` |

### 2.4 Голосование (группа 4)

| # | Проверка | Метод | Результат | Доказательство |
|---|---|---|---|---|
| 4.1 | Нет полей/таблиц голосования в бэкенде и БД | grep `src/Backend`, `db/`; `information_schema` | PASS | в `src/Backend+db` нет `upvote|downvote|vote_count|voteCount|comment_votes|vote_score`; в `comments` нет колонок `vote|rating|score`; нет таблиц `vote|rating` |
| 4.2 | В шапке ровно `↑ 0 ↓` | Playwright `.comment-vote` | PASS | `innerText → "↑0↓"` |
| 4.3 | Неинтерактивно | Playwright | PASS | `aria-hidden="true"`, `pointer-events:none`, нет `button/a/[role=button]/[tabindex]` внутри, нет `onclick` |
| 4.4 | Клик не шлёт запросов | Playwright request-лог | PASS | после `dispatchEvent('click')` + 800 мс: `requests=[]` (фильтр `/api/`, `vote`, `rating`, `score`) |
| 4.5 | Клик ничего не меняет | Playwright | PASS | текст остался `"↑0↓"`; реальный mouse-click перехвачен `pointer-events:none` (`blocked=true`) |

### 2.5 Утечка e-mail/IP/сайта (группа 5)

| # | Проверка | Результат | Доказательство |
|---|---|---|---|
| 5.1 | В `.comment-head` нет e-mail | PASS | `header="Q1\nQ132114AuthorCard\n04.10.26 в 16:42\n↑\n0\n↓"` — нет `@` |
| 5.2 | В `.comment-head` нет IP | PASS | нет `::ffff:172.22.0.1` |
| 5.3 | В `.comment-head` нет ссылки/сайта | PASS | нет `https?://` и `example.com` |
| 5.4 | Поповер закрыт по умолчанию | PASS | `[data-testid=author-info]` count = 0 до клика |
| 5.5 | 4-я иконка реально открывает карточку | PASS | `[data-testid=author-info-button]` + SVG → после клика `author-info` видим |
| 5.6 | E-mail/IP/сайт доступны в поповере | PASS | `author-email="q132114authorcard@example.com"`, `author-ip="::ffff:172.22.0.1"`, `author-home href="https://example.com/q132114"`, `rel="noopener noreferrer nofollow"` |
| 5.7 | Нет uncaught-ошибок страницы | PASS | `pageErrors=[]` |

### 2.6 Инварианты репозитория (группа 6)

| # | Проверка | Команда | Результат | Доказательство |
|---|---|---|---|---|
| 6.1 | 6 контейнеров running + healthy | `docker compose ps --format '{{.Service}} {{.Status}}'` | PASS | api/web/elasticsearch/postgres/rabbitmq/redis — все `(healthy)` |
| 6.2 | Тома проекта на месте | `docker volume ls` | PASS | 7 томов `comments-spa*` (postgres/redis/rabbitmq/elasticsearch/api-storage/comments-data/comments-storage) |
| 6.3 | Нет секретов в diff/новых файлах | grep по `git diff` + untracked | PASS | пусто (исключены только плейсхолдеры `.env.example`/`POSTGRES_PASSWORD`) |
| 6.4 | Нет мусора в дереве (`src/Frontend/.work`) | `ls -ld src/Frontend/.work`; `git status --porcelain \| grep '^??'` | **PASS** после удаления Lead'ом (`post-cleanup.log`: `absent`); **FAIL повторно** в `selfclean3` — каталог пересоздан parity-харнессом Frontend в 16:50–16:51, см. D-2 | `ls: No such file or directory` (16:47) → затем `parity.mjs/proxy.mjs/shot.mjs/esbuild…tgz` (16:50–16:51) |

### 2.7 Регресс (группа 7)

| # | Прогон | Команда | Результат | Доказательство |
|---|---|---|---|---|
| 7.1 | Существующий adversarial-набор | `bash tests/e2e/adversarial.sh` | PASS, exit 0 | `SAFE=73 VULNERABLE=0 ACCEPTED-RISK=1 UNEXPECTED=0 SKIP=1` |
| 7.2 | Существующий browser-check | `bash tests/frontend/run-browser-check.sh` | PASS, exit 0 | `PASS: 23 FAIL: 0 SKIP: 0`, включая «cascade has a left quote bar» и «preview output contains no <script>» |
| 7.3 | Новый состязательный набор | `tests/e2e/adversarial-quote.sh` (разделы 1–6) | до удаления `.work`: 57 PASS / 1 FAIL; после (`post-cleanup.log`): **57 PASS / 0 FAIL** | единственный FAIL — D-1 (устранён) |
| 7.4 | Новый Playwright/jsdom набор | `tests/e2e/adversarial-quote.mjs` | PASS | `RESULT pass=80 fail=0` |

## 3. Найденные дефекты

### D-1 (УСТРАНЁН): мусор в дереве — `src/Frontend/.work/`

Найден в первом прогоне, сразу передан Lead'у. Lead удалил каталог (realpath проверен) и попросил перепроверить. Финальная перепроверка после удаления:

```
$ ls -ld src/Frontend/.work
ls: cannot access 'src/Frontend/.work': No such file or directory

$ git status --porcelain | grep .work
(нет вывода)

$ tests/.runtime/verify/post-cleanup.log
PASS  6.4 src/Frontend/.work removed or gitignored   absent
```

Первоначальная картина (для протокола, прогон `final.log`, 16:4x):

```
$ git status --porcelain | grep .work
?? src/Frontend/.work/

$ git check-ignore -v src/Frontend/.work
(нет вывода, exit 1)

$ ls src/Frontend/.work/
check.mjs  clip.mjs  compare.png  proxy.mjs  qa-artifacts/  shared/
shot.mjs   ui-cascade.png  ui-full.png  ui-only.png
```

Каталог был untracked и **не** покрыт `.gitignore`, то есть попадал в «грязное дерево» и мог быть случайно закоммичен; по постановке это временная зона web. Зона записи verifier (`tests/e2e/**`, `docs/qa/verify-v2.1.md`) не покрывает `src/Frontend/**`, поэтому удаление сделал Lead. После удаления API-набор перепрогнан: `57 PASS / 0 FAIL`, раздел 6.4 — PASS. Бандл web не менялся (`main-4IHZ62UA.js`, `version=2.1.0`), поэтому результаты DOM-проверок остаются действительными.

### D-2 (ОТКРЫТ): `src/Frontend/.work/` пересоздан

Во втором прогоне самоочистки (`selfclean3`, ~16:52) пункт 6.4 снова упал: каталог `src/Frontend/.work/` появился заново в 16:50–16:51, уже с другим содержимым — `parity.mjs`, `proxy.mjs`, `shot.mjs`, `esbuild-linux-x64-0.28.2.tgz`, `.npm-cache/`, `compare.png`, `ui-cascade.png`, `ui-only.png`, `qa-artifacts/final.png`, симлинк `node_modules → ../../../tests/frontend/node_modules`.

Атрибуция (проверена, а не предположена):
- qa отрицает авторство; `grep -rn '\.work'` по её файлам (`tests/frontend/**`, `docs/qa/design-v2/**`) — **0 совпадений**, весь её тулинг лежит в `tests/frontend/`;
- содержимое указывает на локальный parity/diff-харнесс зоны Frontend: в шапках файлов прямо написано `.work/parity.mjs — проверка CSS-пасса: чётность шапок, тени…`, `.work/proxy.mjs — статика свежего dist + прокси … чтобы прогнать tests/frontend/angular-check.mjs`, `.work/shot.mjs — снимок каскада с цитатами (фикстуры v2.1)`. Симлинк на Playwright из `tests/frontend` — переиспользование чужой установки, а не qa-харнесс;
- вывод: это scratch-зона web/Lead для parity-сверки (не task-1/2-код, не мой скрипт, не qa). Каталог по-прежнему untracked и не в `.gitignore`.

```
$ ls -la --time-style=+%H:%M:%S src/Frontend/.work/
16:51:59 .  16:50:39 .npm-cache  16:50:28 node_modules -> …
16:50:47 esbuild-linux-x64-0.28.2.tgz  16:50:47 compare.png  16:51:21 proxy.mjs
16:51:26 parity.mjs  16:51:45 shot.mjs  16:51:52 ui-cascade.png  ui-only.png  16:52:32 qa-artifacts/final.png

$ git status --porcelain | grep .work
?? src/Frontend/.work/
```

Требование к финалу прежнее: каталог удалить или добавить `src/Frontend/.work/` в `.gitignore` (зона Lead). Пока harness существует и запускается, каталог будет появляться снова — поэтому `.gitignore` надёжнее разового удаления. Зона verifier это не покрывает. Первая версия этого пункта ошибочно приписывала каталог qa — исправлено после её опровержения и проверки состава/шапок файлов.

Продуктовых дефектов (XSS, потеря данных, неаддитивность контракта, обрезка, сетевые эффекты голосования, утечка в шапке) не найдено.

## 4. Что именно пытался сломать и почему не вышло

1. **XSS через цитату, вариант A.** Снимок намеренно не чистит `<`/`>`, поэтому `quotedText` буквально содержит `<script>alert(1)</script>` (это и есть зафиксированное решение §1.1). Пытался получить исполнение: entity-encoded и hex-encoded script, entity-encoded `<img onerror>`, attribute-breakout `" onmouseover=…`, raw `<svg onload>`, raw `<iframe>`, `<body onload>`. Raw-варианты режет сервер (400), закодированные становятся плоским текстом, а в реальном Chromium внутри `.comment-quote` не появляется ни одного элемента, `innerHTML` экранирован (`&lt;script&gt;`), `textContent` — точный литерал, `alert()` не вызывается. Второго пути (не интерполяции) для `quotedText` в шаблоне нет: `grep` по `comment-node.html` — только `{{ comment.quotedText }}`.
2. **Регресс в client-side `sanitize.ts`.** Web добавил вставку `<br>` на границах блочных тегов. Прогнал изменённый файл под jsdom на 20 опасных payload и 4 блочных кейсах: добавляются только `<br>`, опасные теги/атрибуты не проходят, `href`-схемы (`javascript:`, `data:`, `vbscript:`, в т.ч. через entities и whitespace) отбрасываются, `on*`-атрибуты срезаются. Сломал бы, если бы `<br>`-ветка пропускала атрибуты или обходила `DROP_SUBTREE` — не пропускает.
3. **Аддитивность.** Проверял, что поле не «приклеилось» к замороженному контракту: `git diff` по `docs/API-v2.md` и `docs/ARCHITECTURE-v2.md` пуст; все 100 корневых в выборке имеют `quotedText=null`; GET/GraphQL/JSON-child/GraphQL-мутация/keyset-пагинация работают без 5xx; EF-миграция добавлена отдельно и nullable.
4. **Обрезка.** Сравнивал не «похоже», а на точное равенство с независимой реализацией алгоритма на фактическом `textPlain`. Границы 160/161, отсутствие пробела (жёсткий рез), пробел в первых 160 (рез по границе), схлопывание пробелов, потолок 161 — расхождений нет.
5. **Голосование.** Искал скрытый бэкенд: grep по `src/Backend`/`db`, `information_schema` по колонкам и таблицам — ничего. В браузере: `aria-hidden`, `pointer-events:none`, нет интерактивных потомков и `onclick`, request-лог пуст после клика, состояние `0` не меняется.
6. **Сокрытие e-mail/IP/сайта.** Мерял текст шапки до открытия поповера: `@`, IPv4/IPv6 и `http` отсутствуют; 4-я иконка открывает карточку, где эти данные есть, ссылка с `rel=noopener`. Единственное место — поповер.

## 5. Принятые риски / наблюдения (не дефекты)

- **`quotedText` — валидный плоский текст, а не разметка, по решению §1.1 (вариант A).** Любой сторонний потребитель, который вставит это поле через `innerHTML`, получит XSS. В текущем SPA это исключено (только интерполяция) и проверено в DOM. Если поле появится в другом клиенте — контракт должен явно требовать экранирования; сейчас в замороженном `docs/API-v2.md` поля нет вовсе (осознанно аддитивное решение §1).
- **E-mail всё ещё виден как текст в таблице списка** (колонка «E-mail», `data-testid`-колонок нет, файл `comments-list.html` задачей-2 не менялся). Требование «не видно в шапке» относится к шапке карточки; в шапке — не видно. IP и сайт в таблице не показываются.
- **Поповер автора дополнительно показывает User-Agent** — сверх перечисленного в DESIGN (имя/e-mail/IP/сайт). Не утечка в шапку, но расширение состава данных; отмечено как наблюдение.
- **`X-Forwarded-For` → `clientIp` не валидируется.** `tests/e2e/adversarial.sh` создаёт запись `AdvXffBad…` с заголовком `X-Forwarded-For: <script>alert(1)</script>`, и значение сохраняется в `comments.client_ip` как есть (проверено: `GET /api/comments?pageSize=100` → `100404 AdvXffBad10879 clientIp="<script>alert(1)</script>"`). Это pre-existing поведение v2 (adversarial.sh классифицирует его как единственный `ACCEPTED-RISK=1`), не изменение task-1/2; на рендер влияния нет — `comment-node.html` выводит `{{ comment.clientIp }}` интерполяцией. Остаётся data-integrity-замечанием: значение из заголовка стоит валидировать/нормализовать.
- **Entity-encoded `<script>` проходит валидацию и раскрывается в `textPlain`/`quotedText`.** Это осознанное решение §1.1 (вариант A): `<`/`>` не зачищаются, `text` хранится экранированным (`&lt;script&gt;…`), а плоские `textPlain`/`quotedText` содержат литерал `<script>alert(1)</script>`. DOM-рендер безопасен (раздел 2.1). Побочный эффект: любой глобальный substring-скан API-ответа (как в `smoke.sh`) считает такие легитимные данные «stored XSS» — см. 5.1.
- **Ранний прогон (черновик) ловил HTTP 000** в момент, когда Lead пересобирал api: контейнер `api` был `Up 24 seconds`. Эти FAIL'ы — гонка с рестартом, а не поведение продукта; финальный прогон сделан на стабильном `version=2.1.0` и зелёный.

### 5.1 Инцидент: `smoke.sh` FAIL «stored XSS in list» на данных adversarial-проб (16:44)

Сообщён qa, независимо перепроверен. Причина — не рендеринг и не регресс, а глобальный substring-скан smoke:

```
$ sed -n '592,596p' tests/e2e/smoke.sh
req GET "$BASE/api/comments?pageSize=100"
printf '%s' "$BODY" | grep -qi '<script' \
  && fail "stored XSS in list" "list payload contains <script" \
  || pass "list payload contains no raw <script>"
```

Скан ищет подстроку `<script` во **всём** JSON 100 свежих корней — включая `textPlain`/`quotedText`/`clientIp`. Параллельно со smoke qa (окончился 16:44:06) на том же стеке работали мои и существующие пробники, поэтому в выборку попали:

```
100360 Q132232EncScript  text='&lt;script&gt;…'      textPlain='<script>alert(1)</script>'
100363 Q132232EncImg     text='&lt;img …&gt;'         textPlain='<img src=x onerror=alert(1)>'
100368 Q132232HexScript  text='&lt;script&gt;…'      textPlain='<script>alert(1)</script>'
100404 AdvXffBad10879    clientIp='<script>alert(1)</script>'
```

- `Q132232*` — мои фикстуры (`tests/e2e/adversarial-quote.sh`, прогон 16:43:52–16:44:14); их `text` экранирован, а `textPlain`/`quotedText` — это и есть проверяемый снимок по §1.1.
- `AdvXffBad10879` — из `tests/e2e/adversarial.sh` (`AdvXffBad$RANDOM`), это его XFF-проба, не мой скрипт.

Вывод: FAIL вызван тестовыми данными и порядком прогонов, а не XSS. Рендер `<script>` из `textPlain`/`quotedText`/`clientIp` — только интерполяция (`{{ … }}`), `innerHTML` для этих полей нет (проверено по шаблонам и в DOM). Устранение описано в 5.2.

### 5.2 Устранение: скрипт стал самоочищающимся (follow-up Lead)

Lead вручную вычистил 172 строки-payload (children-first, рекурсивно) и сбросил Redis-кэш, после чего smoke снова стал зелёным; вариант со сбросом docker-томов отклонён (ТЗ запрещает удалять тома, данные perf-seed нужны). По follow-up доработан `tests/e2e/adversarial-quote.sh`:

- все созданные комментарии записываются в `$CREATED_IDS` (id из ответа `201`) — чужие строки не отслеживаются и не трогаются;
- в конце (раздел 8) `cleanup_fixtures()` удаляет **только свои** id deepest-first (FK `parent_id` → `ON DELETE RESTRICT`, цикл `DELETE … WHERE NOT EXISTS (child)`), затем делает `INCR comments:version` (инвалидация page-кэша), после чего проверяет 8.1 (в БД не осталось созданных id) и 8.2 (`/api/comments?pageSize=100` без `<script`);
- очистка идемпотентна: флаг `CLEANUP_DONE` + `trap … EXIT` делают повторный вызов no-op; ошибка очистки логируется как `WARN` и **не** превращается в FAIL проверок; `KEEP_FIXTURES=1` оставляет данные для отладки.

Проверка порядконезависимости (`adversarial-quote.sh` создаёт hostile-фикстуры → сразу `smoke.sh`), два прогона подряд:

```
$ SKIP_BROWSER=1 bash tests/e2e/adversarial-quote.sh
...
========== 8. Self-cleanup of fixtures (shared DB hygiene) ==========
cleanup: bumped comments:version (page cache invalidated)
cleanup: removed 28 fixture rows (remaining=0)
PASS  8.1 all created fixture rows removed (created=28)          remaining=0
PASS  8.2 shared /api/comments has no raw <script payload        scan-clean
PASS=59 FAIL=0

$ bash tests/e2e/smoke.sh
  PASS  list payload contains no raw <script>
PASS: 93  FAIL: 0  SKIP: 0
```

После прогона: `select count(*) from comments where text_plain like '%<script%' or quoted_text like '%<script%' or client_ip like '%<script%'` → `0`; `grep '<script'` по `GET /api/comments?pageSize=100` → clean. Второй прогон (`selfclean3`) подтвердил идемпотентность самоочистки: раздел 8 снова `remaining=0`, `smoke.sh` — **93 PASS / 0 FAIL**; единственный FAIL второго прогона — 6.4 (`.work`, пересоздан parity-харнессом Frontend, см. D-2), к самоочистке отношения не имеет. Артефакты: `tests/.runtime/verify/selfclean2.*`, `selfclean3.*`, `smoke-after-selfclean2.stdout`, `smoke-after-selfclean3.stdout`.

Остаточный риск: stale-документы Elasticsearch по удалённым строкам. Он принят Lead'ом и закрывается отдельно (`_delete_by_query` по идентифицируемым `userName`); на smoke-чек `stored XSS in list` не влияет, т.к. тот читает `/api/comments` из БД. Найденная при отладке самоочистки ошибка генерации SQL (`INSERT … VALUES` без скобок → `syntax error`) исправлена на `INSERT … SELECT unnest(ARRAY[…]::bigint[])`; повторный прогон это подтвердил.

## 6. Ограничения проверки

- Проверялся только Chromium (Playwright 1.63, headless). Firefox/WebKit, мобильные браузеры, screen readers — не проверялись.
- CAPTCHA решалась чтением одноразового кода из Redis (`docker exec comments-spa-redis`), потому что dev-peek в Production-конфигурации выключен (404). Это доверяет локальному Docker-хосту; сетевой периметр CAPTCHA не проверялся (это вне task-4).
- HTTP-уровень (заголовки безопасности, `Content-Security-Policy`, прокси) не проверялся.
- CSS-геометрия (56 px шапка, цвета, 32 px вложенность, `#b3c2e1` полоса) — зона qa/`design-v2`; здесь проверялись только DOM-инварианты безопасности/интерактивности.
- `quotedText` не проверялся в Elasticsearch-индексе и WebSocket-полезной нагрузке как отдельные каналы (WebSocket-рендер идёт тем же компонентом `comment-node`, отдельного пути для цитаты нет).
- Полный `docker compose up --build` с нуля и 265 xUnit-тестов — зона Lead/qa; здесь — живой стек и существующие e2e-скрипты.
- Нагрузочные/перф-проверки — зона perf, вне task-4.
