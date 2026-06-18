#!/usr/bin/env bash
# Stop and remove the rdslive-proxy container.
#
# This also clears the `restart: unless-stopped` policy for this run, so the
# container will NOT come back on reboot until you start it again with ./run.sh
set -euo pipefail
cd "$(dirname "$0")"

echo "==> Stopping rdslive-proxy..."
docker compose down

echo "==> Stopped. Start again with: ./run.sh"
