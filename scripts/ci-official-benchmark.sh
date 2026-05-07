#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OFFICIAL_REPO="${OFFICIAL_REPO:-https://github.com/zanfranceschi/rinha-de-backend-2026.git}"
OFFICIAL_REF="${OFFICIAL_REF:-main}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"
RESULTS_DIR="${RESULTS_DIR:-benchmark-results}"
K6_IMAGE="${K6_IMAGE:-grafana/k6:latest}"
BENCHMARK_PULL_IMAGE="${BENCHMARK_PULL_IMAGE:-false}"
BENCHMARK_NO_BUILD="${BENCHMARK_NO_BUILD:-false}"

cd "$ROOT_DIR"

compose_args=(--compatibility -f docker-compose.yml)
if [[ "$COMPOSE_FILE" != "docker-compose.yml" ]]; then
    compose_args+=(-f "$COMPOSE_FILE")
fi

mkdir -p "$RESULTS_DIR"
rm -rf "$RESULTS_DIR/official"

echo "==> Fetching official Rinha test"
git clone --depth 1 --branch "$OFFICIAL_REF" "$OFFICIAL_REPO" "$RESULTS_DIR/official"

cleanup() {
    echo "==> Capturing docker compose logs"
    docker compose "${compose_args[@]}" logs --no-color > "$RESULTS_DIR/docker-compose.log" 2>&1 || true

    echo "==> Stopping stack"
    docker compose "${compose_args[@]}" down --volumes --remove-orphans > /dev/null 2>&1 || true
}
trap cleanup EXIT

echo "==> Starting stack with compose resource compatibility"
if [[ -n "${WEBAPI_IMAGE:-}" ]]; then
    echo "Using WEBAPI_IMAGE=$WEBAPI_IMAGE"
fi

if [[ "$BENCHMARK_PULL_IMAGE" == "true" ]]; then
    docker compose "${compose_args[@]}" pull webapi1 webapi2
fi

up_args=(up -d)
if [[ "$BENCHMARK_NO_BUILD" != "true" ]]; then
    up_args+=(--build)
else
    up_args+=(--no-build)
fi

docker compose "${compose_args[@]}" "${up_args[@]}"

echo "==> Waiting for /ready"
ready_url="http://localhost:9999/ready"
for attempt in {1..20}; do
    if curl -fsS "$ready_url" > /dev/null 2>&1; then
        echo "Ready after attempt $attempt"
        break
    fi

    if [[ "$attempt" == "20" ]]; then
        echo "Service did not become ready at $ready_url" >&2
        exit 1
    fi

    sleep 3
done

echo "==> Running official k6 benchmark"
chmod -R a+rwX "$RESULTS_DIR/official"
docker run --rm \
    --network host \
    --user "$(id -u):$(id -g)" \
    -e K6_NO_USAGE_REPORT=true \
    -e K6_WEB_DASHBOARD=true \
    -e K6_WEB_DASHBOARD_PORT=-1 \
    -e K6_WEB_DASHBOARD_EXPORT=test/k6-report.html \
    -v "$ROOT_DIR/$RESULTS_DIR/official:/official" \
    -w /official \
    "$K6_IMAGE" run test/test.js

cp "$RESULTS_DIR/official/test/results.json" "$RESULTS_DIR/results.json"
if [[ -f "$RESULTS_DIR/official/test/k6-report.html" ]]; then
    cp "$RESULTS_DIR/official/test/k6-report.html" "$RESULTS_DIR/k6-report.html"
else
    echo "k6 HTML report was not generated" >&2
fi

echo "==> Result"
jq . "$RESULTS_DIR/results.json"

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
        echo "## Rinha Official-Like Benchmark"
        echo
        echo "- Compose file: \`$COMPOSE_FILE\`"
        if [[ -n "${WEBAPI_IMAGE:-}" ]]; then
            echo "- WebApi image: \`$WEBAPI_IMAGE\`"
        fi
        echo "- Official ref: \`$OFFICIAL_REF\`"
        echo "- k6 image: \`$K6_IMAGE\`"
        echo
        echo '```json'
        jq . "$RESULTS_DIR/results.json"
        echo '```'
    } >> "$GITHUB_STEP_SUMMARY"
fi
