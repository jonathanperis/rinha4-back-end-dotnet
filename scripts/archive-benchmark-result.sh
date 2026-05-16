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
IVF_CLUSTERS="${IVF_CLUSTERS:-}"
IVF_TRAIN_SAMPLE="${IVF_TRAIN_SAMPLE:-}"
IVF_ITERATIONS="${IVF_ITERATIONS:-}"
IVF_SCALE="${IVF_SCALE:-}"
IVF_FAST_NPROBE="${IVF_FAST_NPROBE:-}"
IVF_FULL_NPROBE="${IVF_FULL_NPROBE:-}"
IVF_BOUNDARY_FULL="${IVF_BOUNDARY_FULL:-}"
IVF_BBOX_REPAIR="${IVF_BBOX_REPAIR:-}"
IVF_REPAIR_MIN_FRAUDS="${IVF_REPAIR_MIN_FRAUDS:-}"
IVF_REPAIR_MAX_FRAUDS="${IVF_REPAIR_MAX_FRAUDS:-}"
BENCHMARK_STACK_CPUSET="${BENCHMARK_STACK_CPUSET:-}"
BENCHMARK_K6_CPUSET="${BENCHMARK_K6_CPUSET:-}"
BENCHMARK_API_CPUSET="${BENCHMARK_API_CPUSET:-}"
BENCHMARK_PROXY_CPUSET="${BENCHMARK_PROXY_CPUSET:-}"
BENCHMARK_API_CPUS="${BENCHMARK_API_CPUS:-}"
BENCHMARK_PROXY_CPUS="${BENCHMARK_PROXY_CPUS:-}"
BENCHMARK_API_MEMORY="${BENCHMARK_API_MEMORY:-}"
BENCHMARK_PROXY_MEMORY="${BENCHMARK_PROXY_MEMORY:-}"
BENCHMARK_REPETITIONS="${BENCHMARK_REPETITIONS:-1}"
SCORER_MODE="${SCORER_MODE:-}"
ACCEPT_LOOPS="${ACCEPT_LOOPS:-}"
FD_RAW="${FD_RAW:-}"
MIN_WORKER_THREADS="${MIN_WORKER_THREADS:-}"
MAX_WORKER_THREADS="${MAX_WORKER_THREADS:-}"
MAX_IO_THREADS="${MAX_IO_THREADS:-}"
BUCKET_AVX_CUTOFF_DIMS="${BUCKET_AVX_CUTOFF_DIMS:-}"
BUCKET_REFERENCE_FASTPATH="${BUCKET_REFERENCE_FASTPATH:-}"
BUCKET_REFERENCE_FASTPATH_LEGIT="${BUCKET_REFERENCE_FASTPATH_LEGIT:-}"
BUCKET_REFERENCE_FASTPATH_FRAUD="${BUCKET_REFERENCE_FASTPATH_FRAUD:-}"
IVF_ZERO_FAST_APPROVE_WORST_DISTANCE="${IVF_ZERO_FAST_APPROVE_WORST_DISTANCE:-}"
IVF_FIVE_FAST_DENY_WORST_DISTANCE="${IVF_FIVE_FAST_DENY_WORST_DISTANCE:-}"
REPETITION_SUMMARY="${REPETITION_SUMMARY:-$(dirname "$RESULTS_JSON")/repetition-summary.json}"

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
safe_compose="${COMPOSE_FILE//[^a-zA-Z0-9]/-}"
report_suffix=""
if [[ "$REPORT_KIND" == "experiment" ]]; then
    report_suffix="-$safe_compose"
fi

report_file="${REPORT_PREFIX}-${stamp}-${SHORT_SHA}${report_suffix}.json"
report_path="$REPORTS_DIR/$report_file"
if [[ -e "$report_path" ]]; then
    safe_kind="${REPORT_KIND//[^a-zA-Z0-9]/-}"
    report_file="${REPORT_PREFIX}-${stamp}-${SHORT_SHA}-${safe_kind}-${safe_compose}.json"
    report_path="$REPORTS_DIR/$report_file"
fi
html_report_file=""
if [[ -f "$K6_HTML_REPORT" ]]; then
    html_report_file="${REPORT_PREFIX}-${stamp}-${SHORT_SHA}${report_suffix}.html"
    if [[ -e "$REPORTS_DIR/$html_report_file" ]]; then
        safe_kind="${REPORT_KIND//[^a-zA-Z0-9]/-}"
        html_report_file="${REPORT_PREFIX}-${stamp}-${SHORT_SHA}-${safe_kind}-${safe_compose}.html"
    fi
    cp "$K6_HTML_REPORT" "$REPORTS_DIR/$html_report_file"
fi

summary_arg=(--argjson repetition_summary null)
if [[ -f "$REPETITION_SUMMARY" ]]; then
    summary_arg=(--slurpfile repetition_summary "$REPETITION_SUMMARY")
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
    --arg ivf_clusters "$IVF_CLUSTERS" \
    --arg ivf_train_sample "$IVF_TRAIN_SAMPLE" \
    --arg ivf_iterations "$IVF_ITERATIONS" \
    --arg ivf_scale "$IVF_SCALE" \
    --arg ivf_fast_nprobe "$IVF_FAST_NPROBE" \
    --arg ivf_full_nprobe "$IVF_FULL_NPROBE" \
    --arg ivf_boundary_full "$IVF_BOUNDARY_FULL" \
    --arg ivf_bbox_repair "$IVF_BBOX_REPAIR" \
    --arg ivf_repair_min_frauds "$IVF_REPAIR_MIN_FRAUDS" \
    --arg ivf_repair_max_frauds "$IVF_REPAIR_MAX_FRAUDS" \
    --arg benchmark_stack_cpuset "$BENCHMARK_STACK_CPUSET" \
    --arg benchmark_k6_cpuset "$BENCHMARK_K6_CPUSET" \
    --arg benchmark_api_cpuset "$BENCHMARK_API_CPUSET" \
    --arg benchmark_proxy_cpuset "$BENCHMARK_PROXY_CPUSET" \
    --arg benchmark_api_cpus "$BENCHMARK_API_CPUS" \
    --arg benchmark_proxy_cpus "$BENCHMARK_PROXY_CPUS" \
    --arg benchmark_api_memory "$BENCHMARK_API_MEMORY" \
    --arg benchmark_proxy_memory "$BENCHMARK_PROXY_MEMORY" \
    --arg benchmark_repetitions "$BENCHMARK_REPETITIONS" \
    --arg scorer_mode "$SCORER_MODE" \
    --arg accept_loops "$ACCEPT_LOOPS" \
    --arg fd_raw "$FD_RAW" \
    --arg min_worker_threads "$MIN_WORKER_THREADS" \
    --arg max_worker_threads "$MAX_WORKER_THREADS" \
    --arg max_io_threads "$MAX_IO_THREADS" \
    --arg bucket_avx_cutoff_dims "$BUCKET_AVX_CUTOFF_DIMS" \
    --arg bucket_reference_fastpath "$BUCKET_REFERENCE_FASTPATH" \
    --arg bucket_reference_fastpath_legit "$BUCKET_REFERENCE_FASTPATH_LEGIT" \
    --arg bucket_reference_fastpath_fraud "$BUCKET_REFERENCE_FASTPATH_FRAUD" \
    --arg ivf_zero_fast_approve_worst_distance "$IVF_ZERO_FAST_APPROVE_WORST_DISTANCE" \
    --arg ivf_five_fast_deny_worst_distance "$IVF_FIVE_FAST_DENY_WORST_DISTANCE" \
    --arg source "zanfranceschi/rinha-de-backend-2026:test/test.js" \
    --slurpfile result "$RESULTS_JSON" \
    "${summary_arg[@]}" \
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
            environment: (if ($benchmark_api_cpus + $benchmark_proxy_cpus) != "" then
                "GitHub Actions ubuntu-latest; calibrated CPU limits api=" + (if $benchmark_api_cpus == "" then "default" else $benchmark_api_cpus end) + ", proxy=" + (if $benchmark_proxy_cpus == "" then "default" else $benchmark_proxy_cpus end) + "; not official Rinha hardware"
            elif $benchmark_stack_cpuset != "" then
                "GitHub Actions ubuntu-latest; stack pinned to cpuset " + $benchmark_stack_cpuset + "; closer contention probe, not official Rinha hardware"
            else
                "GitHub Actions ubuntu-latest; official-like only, not official Rinha hardware"
            end),
            benchmark_stack_cpuset: $benchmark_stack_cpuset,
            benchmark_k6_cpuset: $benchmark_k6_cpuset,
            benchmark_api_cpuset: $benchmark_api_cpuset,
            benchmark_proxy_cpuset: $benchmark_proxy_cpuset,
            benchmark_api_cpus: $benchmark_api_cpus,
            benchmark_proxy_cpus: $benchmark_proxy_cpus,
            benchmark_api_memory: $benchmark_api_memory,
            benchmark_proxy_memory: $benchmark_proxy_memory,
            benchmark_repetitions: $benchmark_repetitions,
            benchmark_config: {
                ivf_clusters: $ivf_clusters,
                ivf_train_sample: $ivf_train_sample,
                ivf_iterations: $ivf_iterations,
                ivf_scale: $ivf_scale,
                ivf_fast_nprobe: $ivf_fast_nprobe,
                ivf_full_nprobe: $ivf_full_nprobe,
                ivf_boundary_full: $ivf_boundary_full,
                ivf_bbox_repair: $ivf_bbox_repair,
                ivf_repair_min_frauds: $ivf_repair_min_frauds,
                ivf_repair_max_frauds: $ivf_repair_max_frauds,
                scorer_mode: $scorer_mode,
                accept_loops: $accept_loops,
                fd_raw: $fd_raw,
                min_worker_threads: $min_worker_threads,
                max_worker_threads: $max_worker_threads,
                max_io_threads: $max_io_threads,
                bucket_avx_cutoff_dims: $bucket_avx_cutoff_dims,
                bucket_reference_fastpath: $bucket_reference_fastpath,
                bucket_reference_fastpath_legit: $bucket_reference_fastpath_legit,
                bucket_reference_fastpath_fraud: $bucket_reference_fastpath_fraud,
                ivf_zero_fast_approve_worst_distance: $ivf_zero_fast_approve_worst_distance,
                ivf_five_fast_deny_worst_distance: $ivf_five_fast_deny_worst_distance
            },
            repetition_summary: (if ($repetition_summary | type) == "array" then ($repetition_summary[0] // null) else $repetition_summary end)
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
        report_kind: (.metadata.report_kind // (if (.metadata.compose_file == "docker-compose.yml") then "candidate" else "experiment" end)),
        benchmark_stack_cpuset: (.metadata.benchmark_stack_cpuset // ""),
        benchmark_k6_cpuset: (.metadata.benchmark_k6_cpuset // ""),
        benchmark_api_cpuset: (.metadata.benchmark_api_cpuset // ""),
        benchmark_proxy_cpuset: (.metadata.benchmark_proxy_cpuset // ""),
        benchmark_api_cpus: (.metadata.benchmark_api_cpus // ""),
        benchmark_proxy_cpus: (.metadata.benchmark_proxy_cpus // ""),
        benchmark_api_memory: (.metadata.benchmark_api_memory // ""),
        benchmark_proxy_memory: (.metadata.benchmark_proxy_memory // ""),
        benchmark_repetitions: (.metadata.benchmark_repetitions // "1"),
        ivf_scale: (.metadata.benchmark_config.ivf_scale // ""),
        ivf_fast_nprobe: (.metadata.benchmark_config.ivf_fast_nprobe // ""),
        ivf_full_nprobe: (.metadata.benchmark_config.ivf_full_nprobe // ""),
        fd_raw: (.metadata.benchmark_config.fd_raw // ""),
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

calibrated_file="$(jq -r 'map(select(.report_kind == "official-calibrated")) | .[0].file // empty' "$REPORTS_DIR/index.json")"
if [[ -n "$calibrated_file" ]]; then
    cp "$REPORTS_DIR/$calibrated_file" "$REPORTS_DIR/latest-calibrated.json"
fi

echo "Archived benchmark report: $report_path"
