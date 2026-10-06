# Развёртывание на VDS (Hetzner Cloud)

Здесь лежит то, чем поднимается демо-стенд, на который ссылается корневой
[`README.md`](../../README.md). Артефакты рабочие — это не описание «как надо»,
а ровно те файлы, которыми стенд был создан и проверен.

| Файл | Что делает |
|---|---|
| `cloud-init.sh` | готовит хост при первой загрузке: Docker, swap, ufw, ключи-only SSH; при заданном `REPO_URL` клонирует репозиторий, собирает стек и включает systemd-unit |
| `comments-spa.service` | поднимает стек при загрузке; устанавливается **из репозитория**, чтобы не было расходящейся копии |
| `deploy.sh` | создаёт / показывает / удаляет сервер через API Hetzner |

## Что нужно

- Аккаунт Hetzner Cloud и токен API (проект → Security → API tokens), `read & write`
- Локально: `curl`, `python3`, `ssh-keygen`

## Создание

```bash
export HCLOUD_TOKEN=...                        # не коммитить и не хранить в репозитории
infra/hetzner/deploy.sh create \
  --repo https://github.com/Gabatawr/comments-spa.git
```

Скрипт создаст SSH-ключ `~/.ssh/hetzner_comments` (если его нет), зарегистрирует
его, поднимет сервер и подставит `--repo` в cloud-init. Дальше 5–10 минут идёт
подготовка хоста и первая сборка — прогресс видно по ссылке, которую он напечатает:

```bash
ssh -i ~/.ssh/hetzner_comments root@<ip> 'tail -f /var/log/comments-spa-init.log'
curl http://<ip>/api/health
```

## Параметры по умолчанию

| | |
|---|---|
| Тип | `cx23` — 2 vCPU / 4 ГБ / 40 ГБ. Это **минимальный** x86-инстанс, которого хватает: в покое стек занимает ~1.8 ГБ, Elasticsearch держит лимит 2 ГБ. Меньше 4 ГБ брать нельзя |
| Образ | `ubuntu-24.04` |
| Локация | `fsn1` (Falkenstein) |
| Стоимость | ≈ $0.0114/ч ≈ $7.09/мес с IPv4. Счёт идёт, пока сервер существует |

## Что делает `cloud-init.sh`

- ставит Docker из официального репозитория (не через convenience-скрипт)
- создаёт swap 2 ГБ — 4 ГБ впритык при сборке .NET и работе JVM
- поднимает `vm.max_map_count` до 262144
- `ufw`: только 22/80/443; остальные порты и так привязаны к `127.0.0.1` самим compose
- запрещает вход по паролю (только ключ)
- при `REPO_URL`: клонирует репозиторий в `/srv/comments-spa`, генерирует боевой
  `.env`, собирает образы и включает `comments-spa.service`

Про `vm.max_map_count` честно: это **не** блокер для этого стека. В compose стоит
`discovery.type: single-node`, при нём production-проверки Elasticsearch не
форсируются, и ES 8.x стартует с дефолтным 65530, написав в лог только `WARN`
(проверено на 8.15.3). Значение поднимается потому, что так рекомендует сам
Elasticsearch, и потому что оно реально важно, когда шардов и индексов много.

## Сгенерированный `.env`

Без него стенд встал бы на общих dev-значениях: пароли `comments/comments` и порты
баз, Redis, брокера и Elasticsearch опубликованные наружу. Поэтому на первом запуске
cloud-init создаёт `/srv/comments-spa/.env` (режим 600):

- случайные пароли PostgreSQL и RabbitMQ (`openssl rand -hex 16`) вместо дефолтных;
- `*_HOST_PORT=127.0.0.1:…` у всех сервисов данных и `WEB_HOST_PORT=80` — снаружи
  отвечает только nginx;
- `ASPNETCORE_ENVIRONMENT=Production`, `PROVIDERS_STRICT=true`,
  `PROVIDERS_FAIL_FAST=true` — недоступный провайдер валит старт, а не деградирует молча.

`.env` не попадает в git и переживает `git pull`. Если его удалить, следующий прогон
cloud-init создаст новый — с другими паролями.

## Эксплуатация

```bash
# состояние
infra/hetzner/deploy.sh status

# перезагрузка переживается сама: systemd поднимает стек с теми же томами
ssh -i ~/.ssh/hetzner_comments root@<ip> systemctl status comments-spa

# обновление кода
ssh -i ~/.ssh/hetzner_comments root@<ip> 'cd /srv/comments-spa && git pull && docker compose up -d --build'

# удалить сервер и остановить счёт
infra/hetzner/deploy.sh destroy
```

## Что важно знать

- **Наружу открыт только порт 80.** API, PostgreSQL, Redis, RabbitMQ и Elasticsearch
  опубликованы на `127.0.0.1` и доступны лишь с самого сервера. Пароли к БД и брокеру
  генерируются случайно и лежат только в `/srv/comments-spa/.env` (режим 600).
- **TLS нет.** Стенд отдаётся по `http://<ip>/`. Для HTTPS нужен домен и reverse-proxy
  с сертификатом — это следующий шаг, а не сделанное.
- **Резервных копий нет.** Данные живут в docker-томах; `docker compose down -v` их удалит.
- **Дев-режим на публичном стенде.** `/api/dev/*` включается только для загрузки данных
  (`infra/compose.dev.yml`). Теперь путь закрыт дважды: API отвечает 404 вне Development, и nginx
  отдаёт 404 по всему `/api/dev/` независимо от окружения. Порт API при этом слушает `127.0.0.1`.