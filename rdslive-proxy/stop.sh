#!/usr/bin/env bash
# Stop and remove a channel's containers.
#
# This also clears the `restart: unless-stopped` policy for that stack, so it will NOT
# come back on reboot until you start it again with ./run.sh.
#
# Usage:
#   ./stop.sh            # stop the primary antena-1 stack
#   ./stop.sh <name>     # stop the channel started as ./run.sh --name <name> (or --source ...)
set -euo pipefail
cd "$(dirname "$0")"

case "${1:-}" in
  -h|--help)
    echo "Usage: ./stop.sh [channel-name]   (no name = the primary stack)"
    exit 0 ;;
esac

if [ -n "${1:-}" ]; then
  export COMPOSE_PROJECT_NAME="rdslive-$1"
  export CHANNEL_SUFFIX="-$1"
  echo "==> Stopping channel '$1' (project $COMPOSE_PROJECT_NAME)..."
else
  echo "==> Stopping the primary stack..."
fi

docker compose down

echo "==> Stopped. Start again with: ./run.sh${1:+ --name $1 ...}"
