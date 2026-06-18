#!/usr/bin/env bash
# Rebuild the rdslive-proxy container image from scratch.
set -euo pipefail
cd "$(dirname "$0")"

echo "==> Removing any existing rdslive-proxy container..."
docker compose down --remove-orphans

echo "==> Building rdslive-proxy image..."
docker compose build --pull --no-cache

echo "==> Done. Built image:"
docker images rdslive-proxy --format '    {{.Repository}}:{{.Tag}}  {{.Size}}'
echo "==> Run it with: ./run.sh"
