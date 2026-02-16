#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
IMAGE="rockylhotka/mcp-aggregator:latest"

echo "Building Docker image: $IMAGE"
docker build -t "$IMAGE" -f "$REPO_ROOT/src/McpAggregator.HttpServer/Dockerfile" "$REPO_ROOT"

echo "Pushing image: $IMAGE"
docker push "$IMAGE"

echo "Done. Image pushed: $IMAGE"
