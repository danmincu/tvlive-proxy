#!/usr/bin/env bash
# Start rdslive-proxy and keep it resident.
#
# `restart: unless-stopped` in docker-compose.yml means the container comes back
# automatically after a crash or a host reboot (as long as the Docker daemon is
# set to start on boot). `--build` ensures the image is current.
set -euo pipefail
cd "$(dirname "$0")"

# Detect this host's primary LAN IP so the Chromecast can fetch media locally even
# when the player is opened via a DDNS/public hostname (see PROXY_CAST_HOST). This
# runs at startup — NOT baked into the image — so the same image/scripts work on any
# network. Override by exporting PROXY_CAST_HOST=<ip> before running this script.
detect_lan_ip() {
  local ip=""
  if command -v ip >/dev/null 2>&1; then            # Linux
    ip=$(ip route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}')
  fi
  if [ -z "$ip" ] && command -v hostname >/dev/null 2>&1; then
    ip=$(hostname -I 2>/dev/null | awk '{print $1}')
  fi
  if [ -z "$ip" ] && command -v ipconfig >/dev/null 2>&1; then   # macOS
    ip=$(ipconfig getifaddr en0 2>/dev/null || true)
  fi
  # Only accept a real dotted IPv4; reject anything else (e.g. Windows ipconfig help text).
  printf '%s' "$ip" | grep -Eom1 '^[0-9]{1,3}(\.[0-9]{1,3}){3}$' || true
}

export PROXY_CAST_HOST="${PROXY_CAST_HOST:-$(detect_lan_ip)}"
if [ -n "$PROXY_CAST_HOST" ]; then
  echo "==> PROXY_CAST_HOST = $PROXY_CAST_HOST (Chromecast media host)"
else
  echo "==> PROXY_CAST_HOST not set/detected; casting will use the browser's hostname."
fi

echo "==> Starting rdslive-proxy (detached, restart=unless-stopped)..."
docker compose up -d --build

echo "==> Status:"
docker compose ps

echo
echo "==> Web player: http://localhost:13001/"
echo "==> VLC / m3u8: http://localhost:13001/stream.m3u8"
echo "==> Logs:       docker compose logs -f"
echo "==> Stop:       docker compose down"
