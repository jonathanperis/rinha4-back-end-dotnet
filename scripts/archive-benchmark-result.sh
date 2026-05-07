#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_JSON="${RESULTS_JSON:-$ROOT_DIR/benchmark-results/results.json}"
K6_HTML_REPORT="${K6_HTML_REPORT:-$ROOT_DIR/benchmark-results/k6-report.html}"
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
REPORT_KIND="${BENCHMARK_REPORT_KIND:-}"
BUILD_IVF="${BUILD_IVF:-false}"
SCORER_MODE="${SCORER_MODE:-bucket}"
IVF_CLUSTERS="${IVF_CLUSTERS:-}"
IVF_TRAIN_SAMPLE="${IVF_TRAIN_SAMPLE:-}"
IVF_ITERATIONS="${IVF_ITERATIONS:-}"
IVF_FAST_NPROBE="${IVF_FAST_NPROBE:-}"
IVF_FULL_NPROBE="${IVF_FULL_NPROBE:-}"
IVF_BOUNDARY_FULL="${IVF_BOUNDARY_FULL:-}"
IVF_BBOX_REPAIR="${IVF_BBOX_REPAIR:-}"
IVF_EXACT_RERANK="${IVF_EXACT_RERANK:-}"
IVF_RERANK_CANDIDATES="${IVF_RERANK_CANDIDATES:-}"
IVF_REPAIR_MIN_FRAUDS="${IVF_REPAIR_MIN_FRAUDS:-}"
IVF_REPAIR_MAX_FRAUDS="${IVF_REPAIR_MAX_FRAUDS:-}"

if [[ -z "$REPORT_KIND" ]]; then
    if [[ "$COMPOSE_FILE" == "docker-compose.yml" ]]; then
        REPORT_KIND="candidate"
    else
        REPORT_KIND="experiment"
    fi
fi

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
html_report_file=""
if [[ -f "$K6_HTML_REPORT" ]]; then
    html_report_file="${REPORT_PREFIX}-${stamp}-${SHORT_SHA}.html"
    cp "$K6_HTML_REPORT" "$REPORTS_DIR/$html_report_file"
fi

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
    --arg report_kind "$REPORT_KIND" \
    --arg html_report "$html_report_file" \
    --arg build_ivf "$BUILD_IVF" \
    --arg scorer_mode "$SCORER_MODE" \
    --arg ivf_clusters "$IVF_CLUSTERS" \
    --arg ivf_train_sample "$IVF_TRAIN_SAMPLE" \
    --arg ivf_iterations "$IVF_ITERATIONS" \
    --arg ivf_fast_nprobe "$IVF_FAST_NPROBE" \
    --arg ivf_full_nprobe "$IVF_FULL_NPROBE" \
    --arg ivf_boundary_full "$IVF_BOUNDARY_FULL" \
    --arg ivf_bbox_repair "$IVF_BBOX_REPAIR" \
    --arg ivf_exact_rerank "$IVF_EXACT_RERANK" \
    --arg ivf_rerank_candidates "$IVF_RERANK_CANDIDATES" \
    --arg ivf_repair_min_frauds "$IVF_REPAIR_MIN_FRAUDS" \
    --arg ivf_repair_max_frauds "$IVF_REPAIR_MAX_FRAUDS" \
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
            report_kind: $report_kind,
            html_report: (if $html_report == "" then null else $html_report end),
            official_ref: $official_ref,
            k6_image: $k6_image,
            source: $source,
            environment: "GitHub Actions ubuntu-latest; official-like only, not official Rinha hardware",
            benchmark_config: {
                build_ivf: $build_ivf,
                scorer_mode: $scorer_mode,
                ivf_clusters: $ivf_clusters,
                ivf_train_sample: $ivf_train_sample,
                ivf_iterations: $ivf_iterations,
                ivf_fast_nprobe: $ivf_fast_nprobe,
                ivf_full_nprobe: $ivf_full_nprobe,
                ivf_boundary_full: $ivf_boundary_full,
                ivf_bbox_repair: $ivf_bbox_repair,
                ivf_exact_rerank: $ivf_exact_rerank,
                ivf_rerank_candidates: $ivf_rerank_candidates,
                ivf_repair_min_frauds: $ivf_repair_min_frauds,
                ivf_repair_max_frauds: $ivf_repair_max_frauds
            }
        },
        result: $result[0]
    }' > "$report_path"

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
        report_kind: (.metadata.report_kind // (if .metadata.compose_file == "docker-compose.yml" then "candidate" else "experiment" end)),
        scorer_mode: (.metadata.benchmark_config.scorer_mode // "bucket"),
        build_ivf: (.metadata.benchmark_config.build_ivf // "false"),
        ivf_fast_nprobe: (.metadata.benchmark_config.ivf_fast_nprobe // ""),
        ivf_full_nprobe: (.metadata.benchmark_config.ivf_full_nprobe // ""),
        ivf_exact_rerank: (.metadata.benchmark_config.ivf_exact_rerank // ""),
        ivf_rerank_candidates: (.metadata.benchmark_config.ivf_rerank_candidates // ""),
        html_report: .metadata.html_report,
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

candidate_file="$(jq -r 'map(select(.report_kind == "candidate")) | .[0].file // empty' "$REPORTS_DIR/index.json")"
if [[ -n "$candidate_file" ]]; then
    cp "$REPORTS_DIR/$candidate_file" "$REPORTS_DIR/latest-candidate.json"
    cp "$REPORTS_DIR/$candidate_file" "$REPORTS_DIR/latest.json"
fi

experiment_file="$(jq -r 'map(select(.report_kind == "experiment")) | .[0].file // empty' "$REPORTS_DIR/index.json")"
if [[ -n "$experiment_file" ]]; then
    cp "$REPORTS_DIR/$experiment_file" "$REPORTS_DIR/latest-experiment.json"
fi

echo "Archived benchmark report: $report_path"
