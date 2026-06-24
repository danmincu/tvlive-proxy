#!/usr/bin/env bash
# Build and (re)start an rdslive-proxy channel, resident.
#
# Each channel is an independent Docker Compose stack (own ports, own DVR volume, own
# resolver source) so you can watch + DVR several channels at once. The bare `./run.sh`
# runs the PRIMARY antena-1 stack exactly as before (same container/volume, no disruption).
#
# `restart: unless-stopped` means a stack comes back after a crash/reboot; `--build` keeps
# the image current.
set -euo pipefail
cd "$(dirname "$0")"

DEFAULT_SOURCE="https://rdslive.org/antena-1/"

usage() {
  cat <<EOF
Usage: ./run.sh [options]

Runs an rdslive-proxy channel. With no options it (re)starts the primary antena-1 stack
on the default ports — identical to before. Pass options to roll up additional channels.

Options:
  --source URL     Resolver source page = the channel to record.
                   Default: $DEFAULT_SOURCE
                   e.g. --source https://rdslive.org/antena-3-cnn/
  --http PORT      HTTP port  (media/VLC/Chromecast).  Default: 13001
  --https PORT     HTTPS port (player page / cast button). Default: 13443
  --dvr-hours N    DVR retention in hours.               Default: 24
  --ip IP          LAN IP a local Chromecast fetches media from (PROXY_CAST_HOST).
                   Default: auto-detected. (A bare IPv4 positional arg also works.)
  --name NAME      Channel/stack name (a-z0-9-). Default: derived from --source.
  -h, --help       Show this help and exit.

Examples:
  ./run.sh                                            # primary antena-1, 13001/13443, 24h
  ./run.sh --ip 192.168.0.10                          # primary, force the cast IP
  ./run.sh --dvr-hours 6                              # primary, keep only 6h of DVR
  ./run.sh --source https://rdslive.org/antena-3-cnn/ \\
           --http 14001 --https 14443 --dvr-hours 6   # a SECOND channel alongside the first

A non-default channel runs as its own stack named "rdslive-<name>" with its own DVR volume.
Stop it with:  ./stop.sh <name>     (primary: ./stop.sh)
EOF
}

# Detect this host's primary LAN IP (used when no IP is given). Runs at startup — NOT baked
# into the image — so the same image works on any network.
detect_lan_ip() {
  local ip=""
  if command -v ip >/dev/null 2>&1; then            # Linux
    ip=$(ip route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}' || true)
  fi
  if [ -z "$ip" ] && command -v hostname >/dev/null 2>&1; then
    ip=$(hostname -I 2>/dev/null | awk '{print $1}' || true)
  fi
  if [ -z "$ip" ] && command -v ipconfig >/dev/null 2>&1; then   # macOS
    ip=$(ipconfig getifaddr en0 2>/dev/null || true)
  fi
  printf '%s' "$ip" | grep -Eom1 '^[0-9]{1,3}(\.[0-9]{1,3}){3}$' || true
}

is_ip()   { printf '%s' "$1" | grep -Eq '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'; }
is_port() { printf '%s' "$1" | grep -Eq '^[0-9]{1,5}$' && [ "$1" -ge 1 ] && [ "$1" -le 65535 ]; }
is_num()  { printf '%s' "$1" | grep -Eq '^[0-9]+$'; }

SOURCE=""; HTTP_PORT=""; HTTPS_PORT=""; DVR_HOURS=""; IP=""; NAME=""
SOURCE_GIVEN=0; NAME_GIVEN=0
while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --source)            SOURCE="${2:-}";     SOURCE_GIVEN=1; shift 2 ;;
    --http)              HTTP_PORT="${2:-}";  shift 2 ;;
    --https)             HTTPS_PORT="${2:-}"; shift 2 ;;
    --dvr-hours|--hours) DVR_HOURS="${2:-}";  shift 2 ;;
    --ip)                IP="${2:-}";         shift 2 ;;
    --name)              NAME="${2:-}";       NAME_GIVEN=1; shift 2 ;;
    *)
      if is_ip "$1"; then IP="$1"; shift     # backward-compat: bare IPv4 = the cast IP
      else echo "Unknown argument: $1" >&2; echo "Run './run.sh --help' for usage." >&2; exit 1; fi ;;
  esac
done

# Defaults
SOURCE="${SOURCE:-$DEFAULT_SOURCE}"
HTTP_PORT="${HTTP_PORT:-13001}"
HTTPS_PORT="${HTTPS_PORT:-13443}"
DVR_HOURS="${DVR_HOURS:-24}"

# Validate
case "$SOURCE" in http://*|https://*) ;; *) echo "Error: --source must be an http(s) URL." >&2; exit 1 ;; esac
is_port "$HTTP_PORT"  || { echo "Error: --http must be a port 1-65535."  >&2; exit 1; }
is_port "$HTTPS_PORT" || { echo "Error: --https must be a port 1-65535." >&2; exit 1; }
[ "$HTTP_PORT" != "$HTTPS_PORT" ] || { echo "Error: --http and --https must differ." >&2; exit 1; }
is_num "$DVR_HOURS"   || { echo "Error: --dvr-hours must be a whole number." >&2; exit 1; }
if [ -n "$IP" ] && ! is_ip "$IP"; then echo "Error: --ip must be a valid IPv4 address." >&2; exit 1; fi

# Channel name: explicit --name, else derived from the source path (last segment).
if [ -z "$NAME" ]; then
  NAME=$(printf '%s' "$SOURCE" | sed -E 's#/+$##; s#.*/##' | tr 'A-Z' 'a-z' | tr -cs 'a-z0-9-' '-' | sed -E 's#^-+|-+$##g')
  [ -z "$NAME" ] && NAME="channel"
fi

# Cast IP: --ip > $PROXY_CAST_HOST > auto-detect.
IP="${IP:-${PROXY_CAST_HOST:-$(detect_lan_ip)}}"

# Primary stack = bare invocation (default source/name/ports): keep the original project,
# container names and DVR volume so an existing install is updated in place, not duplicated.
# Anything else becomes its own namespaced stack "rdslive-<name>".
if [ "$SOURCE_GIVEN" = "0" ] && [ "$NAME_GIVEN" = "0" ] && [ "$HTTP_PORT" = "13001" ] && [ "$HTTPS_PORT" = "13443" ]; then
  export CHANNEL_SUFFIX=""
  unset COMPOSE_PROJECT_NAME 2>/dev/null || true
  PROJECT="(primary)"
  STOP_HINT="./stop.sh"
  LOGS_HINT="docker compose logs -f"
else
  export CHANNEL_SUFFIX="-$NAME"
  export COMPOSE_PROJECT_NAME="rdslive-$NAME"
  PROJECT="$COMPOSE_PROJECT_NAME"
  STOP_HINT="./stop.sh $NAME"
  LOGS_HINT="docker compose -p $COMPOSE_PROJECT_NAME logs -f"
fi

export SOURCE_PAGE="$SOURCE"
export PROXY_PORT="$HTTP_PORT"
export PROXY_HTTPS_PORT="$HTTPS_PORT"
export PROXY_DVR_HOURS="$DVR_HOURS"
export PROXY_CAST_HOST="$IP"

echo "==> Channel : $NAME   (project: $PROJECT)"
echo "==> Source  : $SOURCE"
echo "==> Ports   : http $HTTP_PORT  /  https $HTTPS_PORT"
echo "==> DVR     : ${DVR_HOURS}h"
echo "==> Cast IP : ${PROXY_CAST_HOST:-<browser hostname>}"
echo "==> Starting (detached, restart=unless-stopped)..."
docker compose up -d --build

echo "==> Status:"
docker compose ps

echo
echo "==> Web player: https://localhost:${HTTPS_PORT}/"
echo "==> VLC / m3u8: http://localhost:${HTTP_PORT}/stream.m3u8"
echo "==> Logs:       ${LOGS_HINT}"
echo "==> Stop:       ${STOP_HINT}"
