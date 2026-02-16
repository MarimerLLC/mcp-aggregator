#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SKILLS_DIR="$REPO_ROOT/data/skills"
NAMESPACE="mcp-aggregator"

copy_skills() {
  echo ""
  echo "Copying skill documents to pod..."
  local pod
  pod=$(kubectl get pod -n "$NAMESPACE" -l app.kubernetes.io/name=mcp-aggregator -o jsonpath='{.items[0].metadata.name}')
  for skill in "$SKILLS_DIR"/*.md; do
    [ -f "$skill" ] || continue
    local name
    name=$(basename "$skill")
    echo "  $name"
    cat "$skill" | MSYS_NO_PATHCONV=1 kubectl exec -i -n "$NAMESPACE" "$pod" -- sh -c "cat > /data/skills/$name"
  done
  echo "Skills copied."
}

if [[ "${1:-}" == "--restart" ]]; then
  echo "Restarting deployment (pulling latest image)..."
  kubectl rollout restart deployment/mcp-aggregator -n mcp-aggregator
  kubectl rollout status deployment/mcp-aggregator -n "$NAMESPACE" --timeout=120s
  copy_skills
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
kubectl rollout status deployment/mcp-aggregator -n "$NAMESPACE" --timeout=120s

copy_skills

echo ""
echo "Deployment complete!"
echo "Access at: https://mcp-aggregator.tail920062.ts.net"
echo ""
echo "Verify with:"
echo "  kubectl get pods -n mcp-aggregator"
echo "  curl https://mcp-aggregator.tail920062.ts.net/health"
