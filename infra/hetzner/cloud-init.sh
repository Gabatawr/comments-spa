#!/bin/bash
# ---------------------------------------------------------------------------
# cloud-init for the comments-spa VDS demo host (Ubuntu 24.04).
#
# Idempotent: safe to re-run. Everything is appended to
# /var/log/comments-spa-init.log, and a marker lands in
# /var/log/comments-spa-init.done when the host is ready.
#
# Set REPO_URL below (or pass it through deploy.sh, which substitutes it) to
# have the host clone the repository and start the stack on first boot.
# Leave it empty to prepare the host only and clone by hand.
# ---------------------------------------------------------------------------
set -uo pipefail
export DEBIAN_FRONTEND=noninteractive
exec > /var/log/comments-spa-init.log 2>&1

REPO_URL=""
APP_DIR=/srv/comments-spa

echo "=== $(date -Is) starting ==="

# --- Kernel: Elasticsearch's own guidance is vm.max_map_count >= 262144.
# Ubuntu ships 65530. This is NOT a hard blocker for this stack: the compose
# file uses discovery.type=single-node, which disables the production bootstrap
# checks, so ES 8.x logs a WARN and starts anyway (verified on 8.15.3). We raise
# it because it removes the warning and because it genuinely matters once the
# node holds many shards or open indexes.
echo 'vm.max_map_count=262144' > /etc/sysctl.d/99-elasticsearch.conf

# --- 2 GB swap: a 4 GB box building .NET and running a JVM needs the headroom.
if [ ! -f /swapfile ]; then
  fallocate -l 2G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=2048
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi
echo 'vm.swappiness=10' > /etc/sysctl.d/99-swappiness.conf
sysctl --system

# --- Wait out any first-boot apt/dpkg lock before touching packages.
for _ in $(seq 1 60); do
  fuser /var/lib/dpkg/lock-frontend >/dev/null 2>&1 || break
  sleep 5
done

apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg git ufw jq

# --- Docker from the official repository (not the convenience script).
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
  > /etc/apt/sources.list.d/docker.list
apt-get update -qq
apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker

# --- Only ssh + http(s) are reachable; every data-service port stays on
# loopback (the compose file binds them to 127.0.0.1), so ufw is the second line
# of defence rather than the only one.
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable

# --- Keys only, no passwords. The cloud provider injects the key at creation.
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
systemctl restart ssh || true

# --- Optional: clone, build once, and hand the stack over to systemd.
if [ -n "$REPO_URL" ]; then
  mkdir -p /srv
  if [ ! -d "$APP_DIR/.git" ]; then
    git clone "$REPO_URL" "$APP_DIR"
  fi

  # The first build happens here, not in the unit: cloud-init is expected to
  # take minutes, whereas a oneshot unit would block boot for the same time.
  # On 2 vCPU this is roughly 5-10 minutes (the images are ~3 GB).
  cd "$APP_DIR"
  docker compose build
  docker compose up -d --remove-orphans

  # The unit lives in the repository: one source of truth, no drifting copy.
  if [ -f "$APP_DIR/infra/hetzner/comments-spa.service" ]; then
    install -m 0644 "$APP_DIR/infra/hetzner/comments-spa.service" /etc/systemd/system/comments-spa.service
    systemctl daemon-reload
    systemctl enable --now comments-spa.service
  fi
fi

echo "ok $(date -Is)" > /var/log/comments-spa-init.done
echo "=== $(date -Is) done ==="
