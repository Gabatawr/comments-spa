#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# infra/hetzner/deploy.sh — create / inspect / destroy the demo VDS.
#
#   HCLOUD_TOKEN=... infra/hetzner/deploy.sh create --repo https://github.com/<user>/comments-spa.git
#   HCLOUD_TOKEN=... infra/hetzner/deploy.sh status
#   HCLOUD_TOKEN=... infra/hetzner/deploy.sh destroy
#
# The host is prepared by infra/hetzner/cloud-init.sh, which installs Docker,
# a 2 GB swapfile, ufw (22/80/443 only) and, when --repo is given, clones the
# repository, builds the stack once and enables infra/hetzner/comments-spa.service.
#
# Requires: curl, python3, ssh-keygen. Only ever touches resources named by
# --name (default `comments-spa`), and refuses to delete anything that does not
# carry its own `managed-by` label.
# ---------------------------------------------------------------------------
set -uo pipefail

API=https://api.hetzner.cloud/v1
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

NAME=comments-spa
TYPE=cx23            # 2 vCPU / 4 GB / 40 GB — the smallest x86 type that fits the stack
IMAGE=ubuntu-24.04
LOCATION=fsn1        # fsn1 | nbg1 | hel1 | ash | hil | sin
REPO=""
SSH_KEY="$HOME/.ssh/hetzner_comments"
SSH_KEY_REF=""       # numeric id of the Hetzner SSH key the server is created with
LABEL_MANAGER=comments-spa-deploy

usage() {
  cat <<'TXT'
usage: deploy.sh <create|status|destroy> [options]

  --repo URL        git URL cloned on first boot (create)
  --name NAME       server + ssh key name            (default comments-spa)
  --type TYPE       server type                      (default cx23)
  --image IMAGE     OS image                         (default ubuntu-24.04)
  --location LOC    fsn1|nbg1|hel1|ash|hil|sin      (default fsn1)
  --ssh-key PATH    private key to authorise         (default ~/.ssh/hetzner_comments)

  HCLOUD_TOKEN must be set (Hetzner Cloud → Project → Security → API tokens).
TXT
}

ACTION="${1:-}"
[ -n "$ACTION" ] && shift
while [ $# -gt 0 ]; do
  case "$1" in
    --repo)      REPO="$2"; shift 2 ;;
    --name)      NAME="$2"; shift 2 ;;
    --type)      TYPE="$2"; shift 2 ;;
    --image)     IMAGE="$2"; shift 2 ;;
    --location)  LOCATION="$2"; shift 2 ;;
    --ssh-key)   SSH_KEY="$2"; shift 2 ;;
    -h|--help)   usage; exit 0 ;;
    *)           echo "неизвестный аргумент: $1" >&2; usage; exit 2 ;;
  esac
done

case "$ACTION" in
  create|status|destroy) ;;
  *) usage; exit 2 ;;
esac

[ -n "${HCLOUD_TOKEN:-}" ] || { echo 'нужен HCLOUD_TOKEN (Hetzner → Project → Security → API tokens)' >&2; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo 'нужен python3' >&2; exit 2; }

api() { # api METHOD PATH [BODY]
  local method="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl -sS -X "$method" -H "Authorization: Bearer $HCLOUD_TOKEN" \
      -H 'Content-Type: application/json' "$API$path" -d "$body"
  else
    curl -sS -X "$method" -H "Authorization: Bearer $HCLOUD_TOKEN" "$API$path"
  fi
}

# Prints "id<tab>ip" for our server, or nothing.
server_row() {
  api GET "/servers?name=$NAME" | python3 -c '
import json, sys
for s in json.load(sys.stdin).get("servers", []):
    ip = ((s.get("public_net") or {}).get("ipv4") or {}).get("ip") or "-"
    st = (s.get("server_type") or {}).get("name")
    print("%s\t%s\t%s\t%s" % (s["id"], ip, s["status"], st))
'
}

ensure_ssh_key() {
  if [ ! -f "$SSH_KEY" ]; then
    echo "  создаю ключ $SSH_KEY"
    ssh-keygen -t ed25519 -f "$SSH_KEY" -N '' -C "$NAME" >/dev/null
  fi
  # Hetzner rejects a public key that is already registered, so look the
  # fingerprint up first and reuse whatever already carries it.
  local fp reuse
  fp="$(ssh-keygen -lf "$SSH_KEY.pub" -E md5 | awk '{print $2}' | sed 's/^MD5://')"
  reuse="$(api GET '/ssh_keys' | FP="$fp" python3 -c '
import json, os, sys
fp = os.environ["FP"]
for k in json.load(sys.stdin).get("ssh_keys", []):
    if k.get("fingerprint") == fp:
        print("%s\t%s" % (k["id"], k["name"]))
        break
')"
  if [ -n "$reuse" ]; then
    SSH_KEY_REF="$(printf '%s' "$reuse" | cut -f1)"
    echo "  ssh-ключ уже в аккаунте: «$(printf '%s' "$reuse" | cut -f2)» (#$SSH_KEY_REF) — использую его"
    return
  fi
  local body
  body="$(NAME="$NAME" PUB="$(cat "$SSH_KEY.pub")" python3 <<'PY'
import json, os
print(json.dumps({"name": os.environ["NAME"], "public_key": os.environ["PUB"]}))
PY
)"
  SSH_KEY_REF="$(api POST "/ssh_keys" "$body" | python3 -c '
import json, sys
d = json.load(sys.stdin)
if "error" in d: sys.exit("  ошибка API: %s" % d["error"].get("message"))
print(d["ssh_key"]["id"])
')"
  [ -n "$SSH_KEY_REF" ] || { echo '  не удалось зарегистрировать ssh-ключ' >&2; exit 1; }
  echo "  ssh-ключ «$NAME» зарегистрирован (#$SSH_KEY_REF)"
}

do_create() {
  existing="$(server_row)"
  if [ -n "$existing" ]; then
    echo "Сервер «$NAME» уже существует — ничего не создаю:"
    echo "  $existing"
    echo "Сначала удалите: deploy.sh destroy --name $NAME"
    exit 1
  fi

  echo 'Подготовка:'
  ensure_ssh_key

  echo 'Создание сервера:'
  echo "  $TYPE / $IMAGE / $LOCATION"
  [ -n "$REPO" ] && echo "  репозиторий: $REPO" || echo '  репозиторий: не задан (клонируйте вручную)'

  body="$(NAME="$NAME" TYPE="$TYPE" IMAGE="$IMAGE" LOCATION="$LOCATION" REPO="$REPO" \
          MANAGER="$LABEL_MANAGER" KEYREF="$SSH_KEY_REF" CI="$HERE/cloud-init.sh" python3 <<'PY'
import json, os, pathlib
ci = pathlib.Path(os.environ["CI"]).read_text(encoding="utf-8")
repo = os.environ["REPO"]
if repo:
    ci = ci.replace('REPO_URL=""', 'REPO_URL="%s"' % repo, 1)
print(json.dumps({
    "name": os.environ["NAME"],
    "server_type": os.environ["TYPE"],
    "image": os.environ["IMAGE"],
    "location": os.environ["LOCATION"],
    "start_after_create": True,
    "ssh_keys": [int(os.environ["KEYREF"])],
    "labels": {"project": os.environ["NAME"], "managed-by": os.environ["MANAGER"]},
    "user_data": ci,
}))
PY
)"
  if [ -z "$body" ]; then echo '  не удалось собрать запрос' >&2; exit 1; fi

  created="$(api POST "/servers" "$body")"
  echo "$created" | python3 -c '
import json, sys
d = json.load(sys.stdin)
if "error" in d: sys.exit("  ошибка API: %s" % d["error"].get("message"))
s = d.get("server") or {}
print("  создан: id=%s" % s.get("id"))
' || exit 1

  echo
  echo 'Ожидание запуска:'
  for _ in $(seq 1 30); do
    row="$(server_row)"
    status="$(printf '%s' "$row" | cut -f3)"
    [ "$status" = "running" ] && break
    sleep 5
  done
  ip="$(printf '%s' "$row" | cut -f2)"
  echo "  статус=$status  ip=$ip"
  echo
  echo "Готово. cloud-init ставит Docker и (если был --repo) собирает стек —"
  echo "это 5-10 минут. Прогресс: ssh -i $SSH_KEY root@$ip 'tail -f /var/log/comments-spa-init.log'"
  echo "Проверка готовности: curl http://$ip/api/health"
}

do_status() {
  row="$(server_row)"
  if [ -z "$row" ]; then
    echo "Сервер «$NAME» не найден"
    return
  fi
  echo "$row" | while IFS=$'\t' read -r id ip status type; do
    echo "  id=$id  имя=$NAME  тип=$type  статус=$status"
    echo "  ip=$ip   http://$ip/"
  done
  api GET "/servers?name=$NAME" | python3 -c '
import json, sys
for s in json.load(sys.stdin).get("servers", []):
    p = s.get("public_net") or {}
    print("  ipv4: %s" % ((p.get("ipv4") or {}).get("ip")))
    print("  создан: %s" % s.get("created"))
    print("  метки: %s" % s.get("labels"))
'
}

do_destroy() {
  row="$(server_row)"
  if [ -z "$row" ]; then
    echo "Сервер «$NAME» не найден — нечего удалять"
    return
  fi
  id="$(printf '%s' "$row" | cut -f1)"
  # Safety: only delete what this script created.
  labels="$(api GET "/servers/$id" | python3 -c '
import json, sys
print(json.dumps(json.load(sys.stdin)["server"].get("labels") or {}))
')"
  case "$labels" in
    *"$LABEL_MANAGER"*) ;;
    *) echo "Отказ: сервер #$id не помечен managed-by=$LABEL_MANAGER (метки: $labels)" >&2; exit 1 ;;
  esac
  echo "Удаляю сервер #$id («$NAME»)…"
  api DELETE "/servers/$id" | python3 -c '
import json, sys
d = json.load(sys.stdin)
if "error" in d: sys.exit("  ошибка API: %s" % d["error"].get("message"))
print("  принято: %s" % (d.get("action", {}).get("command")))
'
}

case "$ACTION" in
  create)  do_create ;;
  status)  do_status ;;
  destroy) do_destroy ;;
esac
