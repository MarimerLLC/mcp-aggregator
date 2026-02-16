#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--restart" ]]; then
  echo "Restarting deployment (pulling latest image)..."
  kubectl rollout restart deployment/mcp-aggregator -n mcp-aggregator
  kubectl rollout status deployment/mcp-aggregator -n mcp-aggregator --timeout=120s
  echo ""
  echo "Restarted. Access at: https://mcp-aggregator.tail920062.ts.net"
  exit 0
fi

echo "Applying Kubernetes manifests..."
kubectl apply -f "$SCRIPT_DIR/namespace.yaml"
kubectl apply -f "$SCRIPT_DIR/configmap.yaml"
kubectl apply -f "$SCRIPT_DIR/pvc.yaml"
kubectl apply -f "$SCRIPT_DIR/deployment.yaml"
kubectl apply -f "$SCRIPT_DIR/service.yaml"
kubectl apply -f "$SCRIPT_DIR/ingress.yaml"

echo ""
echo "Waiting for rollout..."
kubectl rollout status deployment/mcp-aggregator -n mcp-aggregator --timeout=120s

echo ""
echo "Deployment complete!"
echo "Access at: https://mcp-aggregator.tail920062.ts.net"
echo ""
echo "Verify with:"
echo "  kubectl get pods -n mcp-aggregator"
echo "  curl https://mcp-aggregator.tail920062.ts.net/health"
