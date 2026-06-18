#!/usr/bin/env bash
# Start rdslive-proxy and keep it resident.
#
# `restart: unless-stopped` in docker-compose.yml means the container comes back
# automatically after a crash or a host reboot (as long as the Docker daemon is
# set to start on boot). `--build` ensures the image is current.
set -euo pipefail
cd "$(dirname "$0")"

echo "==> Starting rdslive-proxy (detached, restart=unless-stopped)..."
docker compose up -d --build

echo "==> Status:"
docker compose ps

echo
echo "==> Web player: http://localhost:13001/"
echo "==> VLC / m3u8: http://localhost:13001/stream.m3u8"
echo "==> Logs:       docker compose logs -f"
echo "==> Stop:       docker compose down"
