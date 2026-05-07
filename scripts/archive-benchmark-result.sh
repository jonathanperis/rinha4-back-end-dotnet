#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_JSON="${RESULTS_JSON:-$ROOT_DIR/benchmark-results/results.json}"
REPORTS_DIR="${REPORTS_DIR:-$ROOT_DIR/docs/public/reports}"
REPORT_PREFIX="${REPORT_PREFIX:-rinha-benchmark}"
TIMESTAMP="${BENCHMARK_TIMESTAMP:-${GITHUB_RUN_STARTED_AT:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}}"
SHA="${BENCHMARK_SHA:-${GITHUB_SHA:-$(git -C "$ROOT_DIR" rev-parse HEAD)}}"
SHORT_SHA="${SHA:0:12}"
RUN_ID="${BENCHMARK_RUN_ID:-${GITHUB_RUN_ID:-}}"
RUN_URL="${BENCHMARK_RUN_URL:-${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-jonathanperis/rinha4-back-end-dotnet}/actions/runs/${RUN_ID}}"
IMAGE="${BENCHMARK_IMAGE:-${WEBAPI_IMAGE:-}}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"
OFFICIAL_REF="${OFFICIAL_REF:-main}"
K6_IMAGE="${K6_IMAGE:-grafana/k6:latest}"

if [[ ! -f "$RESULTS_JSON" ]]; then
    echo "Result file not found: $RESULTS_JSON" >&2
    exit 1
fi

mkdir -p "$REPORTS_DIR"

if stamp="$(date -u -d "$TIMESTAMP" '+%Y%m%d%H%M%S' 2>/dev/null)"; then
    :
elif stamp="$(date -u -j -f '%Y-%m-%dT%H:%M:%SZ' "$TIMESTAMP" '+%Y%m%d%H%M%S' 2>/dev/null)"; then
    :
else
    stamp="$(date -u '+%Y%m%d%H%M%S')"
fi
report_file="${REPORT_PREFIX}-${stamp}-${SHORT_SHA}.json"
report_path="$REPORTS_DIR/$report_file"

jq -n \
    --arg timestamp "$TIMESTAMP" \
    --arg sha "$SHA" \
    --arg short_sha "$SHORT_SHA" \
    --arg run_id "$RUN_ID" \
    --arg run_url "$RUN_URL" \
    --arg image "$IMAGE" \
    --arg compose_file "$COMPOSE_FILE" \
    --arg official_ref "$OFFICIAL_REF" \
    --arg k6_image "$K6_IMAGE" \
    --arg source "zanfranceschi/rinha-de-backend-2026:test/test.js" \
    --slurpfile result "$RESULTS_JSON" \
    '{
        metadata: {
            timestamp: $timestamp,
            sha: $sha,
            short_sha: $short_sha,
            run_id: $run_id,
            run_url: $run_url,
            image: $image,
            compose_file: $compose_file,
            official_ref: $official_ref,
            k6_image: $k6_image,
            source: $source,
            environment: "GitHub Actions ubuntu-latest; official-like only, not official Rinha hardware"
        },
        result: $result[0]
    }' > "$report_path"

cp "$report_path" "$REPORTS_DIR/latest.json"

tmp_index="$(mktemp)"
: > "$tmp_index"
for report in "$REPORTS_DIR"/${REPORT_PREFIX}-*.json; do
    [[ -e "$report" ]] || continue
    jq --arg file "$(basename "$report")" '{
        file: $file,
        timestamp: .metadata.timestamp,
        sha: .metadata.sha,
        short_sha: .metadata.short_sha,
        run_id: .metadata.run_id,
        run_url: .metadata.run_url,
        image: .metadata.image,
        compose_file: .metadata.compose_file,
        p99: .result.p99,
        failure_rate: .result.scoring.failure_rate,
        final_score: .result.scoring.final_score,
        http_errors: .result.scoring.breakdown.http_errors,
        false_positive_detections: .result.scoring.breakdown.false_positive_detections,
        false_negative_detections: .result.scoring.breakdown.false_negative_detections
    }' "$report" >> "$tmp_index"
done

jq -s 'sort_by(.file) | reverse' "$tmp_index" > "$REPORTS_DIR/index.json"
rm -f "$tmp_index"

echo "Archived benchmark report: $report_path"
