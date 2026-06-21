#!/usr/bin/env bash
# Build and (re)start rdslive-proxy, resident.
#
# `restart: unless-stopped` in docker-compose.yml means the container comes back
# automatically after a crash or a host reboot. `--build` ensures the image is current.
set -euo pipefail
cd "$(dirname "$0")"

PORT="${PROXY_PORT:-13001}"

usage() {
  cat <<EOF
Usage: ./run.sh [LAN_IP]

Builds and (re)starts rdslive-proxy.

Arguments:
  LAN_IP        The proxy's LAN IP that a LOCAL Chromecast uses to fetch media
                (sets PROXY_CAST_HOST). Pass this when installing on a different
                network where auto-detection is wrong, e.g.:
                    ./run.sh 192.168.0.10
                Default: auto-detected from this host (the currently working one).
                Precedence: this argument > \$PROXY_CAST_HOST env > auto-detect.

Options:
  -h, --help    Show this help and exit.

Examples:
  ./run.sh                  # auto-detect the LAN IP (default)
  ./run.sh 192.168.0.10     # force a specific LAN IP for this network

Notes:
  - Casting locally uses this IP over http on port \$PROXY_PORT (default 13001).
  - To cast from another network, open the player via your public/DDNS host and
    forward that http port — the page then casts to that public host instead.
EOF
}

# Detect this host's primary LAN IP (used when no IP argument is given). Runs at
# startup — NOT baked into the image — so the same image works on any network.
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

case "${1:-}" in
  -h|--help) usage; exit 0 ;;
esac

# Cast host: explicit IP argument, else $PROXY_CAST_HOST, else auto-detected LAN IP.
if [ -n "${1:-}" ]; then
  if ! printf '%s' "$1" | grep -Eq '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'; then
    echo "Error: '$1' is not a valid IPv4 address." >&2
    echo "Run './run.sh --help' for usage." >&2
    exit 1
  fi
  CAST_IP="$1"
else
  CAST_IP="${PROXY_CAST_HOST:-$(detect_lan_ip)}"
fi

export PROXY_CAST_HOST="$CAST_IP"
if [ -n "$PROXY_CAST_HOST" ]; then
  echo "==> PROXY_CAST_HOST = $PROXY_CAST_HOST (Chromecast media host for local casting)"
else
  echo "==> PROXY_CAST_HOST not set/detected; casting will use the browser's hostname."
fi

echo "==> Starting rdslive-proxy (detached, restart=unless-stopped)..."
docker compose up -d --build

echo "==> Status:"
docker compose ps

echo
echo "==> Web player: http://localhost:${PORT}/"
echo "==> VLC / m3u8: http://localhost:${PORT}/stream.m3u8"
echo "==> Logs:       docker compose logs -f"
echo "==> Stop:       docker compose down"
