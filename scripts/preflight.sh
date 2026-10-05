#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/preflight.sh — check the host BEFORE `docker compose up --build -d`.
#
# Read-only: it changes nothing, it only looks and reports. Every check that
# fails prints what to do about it. Exit code 0 = ready to go, 1 = something
# must be fixed first.
#
#   scripts/preflight.sh
#
# Ports are taken from `.env` when it exists, so a customised deployment is
# checked against its real settings rather than the defaults.
# ---------------------------------------------------------------------------
set -u

# Host ports declared by docker-compose.yml, with the same defaults the compose
# file uses. Kept as a plain list so this script has no dependency on compose
# itself (it must run even when docker is missing).
DEFAULTS="POSTGRES_HOST_PORT=55432
REDIS_HOST_PORT=56379
RABBITMQ_HOST_PORT=5672
RABBITMQ_MGMT_HOST_PORT=15672
ELASTIC_HOST_PORT=59200
API_HOST_PORT=8081
WEB_HOST_PORT=8080"

# `.env` overrides the defaults, exactly as compose does.
if [ -f .env ]; then
  PORTS="$(printf '%s\n' "$DEFAULTS" | while IFS='=' read -r k v; do
    from_env="$(grep -E "^${k}=" .env 2>/dev/null | tail -1 | cut -d= -f2-)"
    printf '%s=%s\n' "$k" "${from_env:-$v}"
  done)"
  ENV_NOTE=" (.env найден — проверяю по нему)"
else
  PORTS="$DEFAULTS"
  ENV_NOTE=" (.env нет — проверяю дефолты)"
fi

FAILED=0
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; FAILED=1; }
warn() { printf '  \033[33m!\033[0m %s\n' "$1"; }
note() { printf '      %s\n' "$1"; }

echo 'Проверка окружения перед запуском'
echo

# --------------------------------------------------------------------- docker
echo 'Docker:'
if ! command -v docker >/dev/null 2>&1; then
  bad 'docker не найден'
  note 'установите Docker Engine 24+ : https://docs.docker.com/engine/install/'
else
  version="$(docker version --format '{{.Server.Version}}' 2>/dev/null)"
  if [ -z "$version" ]; then
    bad "docker установлен, но демон не отвечает ($(docker --version 2>/dev/null))"
    note 'запустите демон: systemctl start docker  (или откройте Docker Desktop)'
    note 'и проверьте, что у вашего пользователя есть доступ: docker run --rm hello-world'
  else
    major="${version%%.*}"
    if [ "$major" -ge 24 ] 2>/dev/null; then
      ok "Docker Engine $version"
    else
      warn "Docker Engine $version — рекомендуется 24+"
    fi

    if docker compose version >/dev/null 2>&1; then
      compose="$(docker compose version --short 2>/dev/null)"
      ok "docker compose v2 ($compose)"
    else
      bad 'нет плагина `docker compose` (v2)'
      note 'этот проект требует v2: `docker compose …`, а не `docker-compose …`'
      note 'установка: https://docs.docker.com/compose/install/linux/'
    fi
  fi
fi
echo

# ------------------------------------------------------------------- resources
echo 'Ресурсы:'
if [ -r /proc/meminfo ]; then
  total_mb="$(awk '/^MemTotal:/ {printf "%d", $2/1024}' /proc/meminfo)"
  if [ "$total_mb" -ge 3500 ]; then
    ok "память: ${total_mb} МБ"
  else
    bad "память: ${total_mb} МБ — стек не поместится"
    note 'нужно от 4 ГБ: Elasticsearch работает с лимитом 2 ГБ'
    note 'если Elasticsearch не нужен, поднимите без него и задайте Providers__Search=none'
  fi
else
  warn 'не удалось прочитать /proc/meminfo — проверьте память вручную (нужно от 4 ГБ)'
fi

avail_kb="$(df -Pk . 2>/dev/null | awk 'NR==2 {print $4}')"
if [ -n "${avail_kb:-}" ]; then
  avail_gb=$((avail_kb / 1024 / 1024))
  if [ "$avail_gb" -ge 12 ]; then
    ok "свободно на диске: ${avail_gb} ГБ"
  else
    bad "свободно на диске: ${avail_gb} ГБ — мало"
    note 'нужно от 15 ГБ: образы (~3 ГБ) + тома данных + слои сборки'
  fi
else
  warn 'не удалось определить свободное место — нужно от 15 ГБ'
fi
echo

# ----------------------------------------------------------------------- ports
echo "Порты на хосте${ENV_NOTE}:"

if command -v ss >/dev/null 2>&1; then
  listener_cmd='ss -ltnH'
elif command -v netstat >/dev/null 2>&1; then
  listener_cmd='netstat -ltn'
else
  listener_cmd=''
fi

if [ -n "$listener_cmd" ]; then
  busy=""
  while IFS='=' read -r name port; do
    [ -n "$port" ] || continue
    # `ss`/`netstat` output: the local address column contains ":<port>".
    if $listener_cmd 2>/dev/null | grep -qE "[:.]${port}[[:space:]]"; then
      bad "$port занят ($name)"
      busy="$busy $name"
    fi
  done <<EOF
$PORTS
EOF

  if [ -z "$busy" ]; then
    ok 'все нужные порты свободны'
  else
    note 'занятые порты можно переназначить: скопируйте .env.example в .env и поменяйте'
    note 'соответствующие *_HOST_PORT (например WEB_HOST_PORT=9080), затем повторите запуск.'
  fi
else
  warn 'нет ни ss, ни netstat — проверьте занятость портов вручную:'
  note "$(printf '%s\n' "$PORTS" | tr '\n' ' ')"
fi
echo

# ------------------------------------------------------------------------ done
if [ "$FAILED" -eq 0 ]; then
  echo 'Итог: окружение готово.'
  echo
  echo 'Дальше:'
  echo '  docker compose up --build -d'
  echo '  docker compose ps          # ждём статуса healthy у всех сервисов'
  echo '  открыть http://localhost:8080'
else
  echo 'Итог: есть проблемы — см. пометки выше.'
fi
exit "$FAILED"
