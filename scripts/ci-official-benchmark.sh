#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OFFICIAL_REPO="${OFFICIAL_REPO:-https://github.com/zanfranceschi/rinha-de-backend-2026.git}"
OFFICIAL_REF="${OFFICIAL_REF:-main}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"
RESULTS_DIR="${RESULTS_DIR:-benchmark-results}"
K6_IMAGE="${K6_IMAGE:-grafana/k6:latest}"
BENCHMARK_K6_MODE="${BENCHMARK_K6_MODE:-docker}"
BENCHMARK_PULL_IMAGE="${BENCHMARK_PULL_IMAGE:-false}"
BENCHMARK_NO_BUILD="${BENCHMARK_NO_BUILD:-false}"
BENCHMARK_STACK_CPUSET="${BENCHMARK_STACK_CPUSET:-}"
BENCHMARK_K6_CPUSET="${BENCHMARK_K6_CPUSET:-}"
BENCHMARK_API_CPUSET="${BENCHMARK_API_CPUSET:-}"
BENCHMARK_PROXY_CPUSET="${BENCHMARK_PROXY_CPUSET:-}"
BENCHMARK_API_CPUS="${BENCHMARK_API_CPUS:-}"
BENCHMARK_PROXY_CPUS="${BENCHMARK_PROXY_CPUS:-}"
BENCHMARK_API_MEMORY="${BENCHMARK_API_MEMORY:-}"
BENCHMARK_PROXY_MEMORY="${BENCHMARK_PROXY_MEMORY:-}"
BENCHMARK_REPETITIONS="${BENCHMARK_REPETITIONS:-1}"
SCORER_MODE="${SCORER_MODE:-ivf}"
ACCEPT_LOOPS="${ACCEPT_LOOPS:-1}"
FD_RAW="${FD_RAW:-1}"
export FD_RAW
MIN_WORKER_THREADS="${MIN_WORKER_THREADS:-128}"
MAX_WORKER_THREADS="${MAX_WORKER_THREADS:-}"
MAX_IO_THREADS="${MAX_IO_THREADS:-}"
BUCKET_AVX_CUTOFF_DIMS="${BUCKET_AVX_CUTOFF_DIMS:-6}"
BUCKET_REFERENCE_FASTPATH="${BUCKET_REFERENCE_FASTPATH:-true}"
BUCKET_REFERENCE_FASTPATH_LEGIT="${BUCKET_REFERENCE_FASTPATH_LEGIT:-false}"
BUCKET_REFERENCE_FASTPATH_FRAUD="${BUCKET_REFERENCE_FASTPATH_FRAUD:-true}"
IVF_ZERO_FAST_APPROVE_WORST_DISTANCE="${IVF_ZERO_FAST_APPROVE_WORST_DISTANCE:-0}"
IVF_FIVE_FAST_DENY_WORST_DISTANCE="${IVF_FIVE_FAST_DENY_WORST_DISTANCE:-0}"

cd "$ROOT_DIR"

if [[ -z "$COMPOSE_FILE" || "$COMPOSE_FILE" == "docker-compose.yml" ]]; then
    COMPOSE_FILE="docker-compose.yml"
fi

compose_args=(--compatibility -f docker-compose.yml)
if [[ "$COMPOSE_FILE" != "docker-compose.yml" ]]; then
    compose_args+=(-f "$COMPOSE_FILE")
fi

mkdir -p "$RESULTS_DIR"
rm -rf "$RESULTS_DIR/official"

if ! [[ "$BENCHMARK_REPETITIONS" =~ ^[0-9]+$ ]] || [[ "$BENCHMARK_REPETITIONS" -lt 1 ]]; then
    echo "BENCHMARK_REPETITIONS must be a positive integer" >&2
    exit 1
fi

CALIBRATION_COMPOSE_FILE="$RESULTS_DIR/docker-compose.calibration.yml"
api_cpuset="${BENCHMARK_API_CPUSET:-$BENCHMARK_STACK_CPUSET}"
proxy_cpuset="${BENCHMARK_PROXY_CPUSET:-$BENCHMARK_STACK_CPUSET}"
if [[ -n "$api_cpuset$proxy_cpuset$BENCHMARK_API_CPUS$BENCHMARK_PROXY_CPUS$BENCHMARK_API_MEMORY$BENCHMARK_PROXY_MEMORY" ]]; then
    {
        echo "services:"
        for service in webapi1 webapi2; do
            echo "  $service:"
            if [[ -n "$api_cpuset" ]]; then
                echo "    cpuset: \"$api_cpuset\""
            fi
            if [[ -n "$BENCHMARK_API_CPUS$BENCHMARK_API_MEMORY" ]]; then
                echo "    deploy:"
                echo "      resources:"
                echo "        limits:"
                if [[ -n "$BENCHMARK_API_CPUS" ]]; then
                    echo "          cpus: \"$BENCHMARK_API_CPUS\""
                fi
                if [[ -n "$BENCHMARK_API_MEMORY" ]]; then
                    echo "          memory: \"$BENCHMARK_API_MEMORY\""
                fi
            fi
        done
        echo "  lb:"
        if [[ -n "$proxy_cpuset" ]]; then
            echo "    cpuset: \"$proxy_cpuset\""
        fi
        if [[ -n "$BENCHMARK_PROXY_CPUS$BENCHMARK_PROXY_MEMORY" ]]; then
            echo "    deploy:"
            echo "      resources:"
            echo "        limits:"
            if [[ -n "$BENCHMARK_PROXY_CPUS" ]]; then
                echo "          cpus: \"$BENCHMARK_PROXY_CPUS\""
            fi
            if [[ -n "$BENCHMARK_PROXY_MEMORY" ]]; then
                echo "          memory: \"$BENCHMARK_PROXY_MEMORY\""
            fi
        fi
    } > "$CALIBRATION_COMPOSE_FILE"
    compose_args+=(-f "$CALIBRATION_COMPOSE_FILE")
elif [[ -n "$BENCHMARK_STACK_CPUSET" ]]; then
    cat > "$CALIBRATION_COMPOSE_FILE" <<YAML
services:
  webapi1:
    cpuset: "$BENCHMARK_STACK_CPUSET"
  webapi2:
    cpuset: "$BENCHMARK_STACK_CPUSET"
  lb:
    cpuset: "$BENCHMARK_STACK_CPUSET"
YAML
    compose_args+=(-f "$CALIBRATION_COMPOSE_FILE")
fi

capture_docker_state() {
    local phase="$1"
    local output="$RESULTS_DIR/docker-state-$phase.txt"

    {
        echo "phase=$phase"
        echo "compose_file=$COMPOSE_FILE"
        echo "benchmark_stack_cpuset=$BENCHMARK_STACK_CPUSET"
        echo "benchmark_api_cpuset=$BENCHMARK_API_CPUSET"
        echo "benchmark_proxy_cpuset=$BENCHMARK_PROXY_CPUSET"
        echo "benchmark_k6_mode=$BENCHMARK_K6_MODE"
        echo "benchmark_k6_cpuset=$BENCHMARK_K6_CPUSET"
        echo "benchmark_api_cpus=$BENCHMARK_API_CPUS"
        echo "benchmark_proxy_cpus=$BENCHMARK_PROXY_CPUS"
        echo "benchmark_api_memory=$BENCHMARK_API_MEMORY"
        echo "benchmark_proxy_memory=$BENCHMARK_PROXY_MEMORY"
        echo "benchmark_repetitions=$BENCHMARK_REPETITIONS"
        echo "scorer_mode=$SCORER_MODE"
        echo "accept_loops=$ACCEPT_LOOPS"
        echo "fd_raw=$FD_RAW"
        echo "min_worker_threads=$MIN_WORKER_THREADS"
        echo "max_worker_threads=$MAX_WORKER_THREADS"
        echo "max_io_threads=$MAX_IO_THREADS"
        echo "bucket_avx_cutoff_dims=$BUCKET_AVX_CUTOFF_DIMS"
        echo "bucket_reference_fastpath=$BUCKET_REFERENCE_FASTPATH"
        echo "bucket_reference_fastpath_legit=$BUCKET_REFERENCE_FASTPATH_LEGIT"
        echo "bucket_reference_fastpath_fraud=$BUCKET_REFERENCE_FASTPATH_FRAUD"
        echo "ivf_zero_fast_approve_worst_distance=$IVF_ZERO_FAST_APPROVE_WORST_DISTANCE"
        echo "ivf_five_fast_deny_worst_distance=$IVF_FIVE_FAST_DENY_WORST_DISTANCE"
        echo
        echo "host_uname=$(uname -a)"
        echo "host_nproc=$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || true)"
        echo "host_lscpu="
        lscpu || true
        echo
        echo "host_cpu_model=$(awk -F': ' '/model name|Hardware|Processor/ {print $2; exit}' /proc/cpuinfo 2>/dev/null || true)"
        echo "host_cpu_max_freq=$(cat /sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq 2>/dev/null || true)"
        echo "host_meminfo="
        sed -n '1,8p' /proc/meminfo 2>/dev/null || true
        echo
        docker compose "${compose_args[@]}" ps || true
        echo

        local container
        for container in $(docker compose "${compose_args[@]}" ps -q); do
            echo "container=$container"
            docker inspect --format 'name={{.Name}} image={{.Config.Image}} nano_cpus={{.HostConfig.NanoCpus}} memory={{.HostConfig.Memory}} cpuset={{.HostConfig.CpusetCpus}}' "$container" || true
            docker exec "$container" sh -c '
                echo "cpu.max=$(cat /sys/fs/cgroup/cpu.max 2>/dev/null || true)"
                echo "cpu.stat=$(tr "\n" " " < /sys/fs/cgroup/cpu.stat 2>/dev/null || true)"
                echo "cpuset.cpus=$(cat /sys/fs/cgroup/cpuset.cpus 2>/dev/null || true)"
                echo "cpuset.cpus.effective=$(cat /sys/fs/cgroup/cpuset.cpus.effective 2>/dev/null || true)"
                echo "memory.max=$(cat /sys/fs/cgroup/memory.max 2>/dev/null || true)"
                echo "memory.current=$(cat /sys/fs/cgroup/memory.current 2>/dev/null || true)"
            ' || true
            echo
        done
    } > "$output" 2>&1 || true
}

echo "==> Fetching official Rinha test"
git clone --depth 1 --branch "$OFFICIAL_REF" "$OFFICIAL_REPO" "$RESULTS_DIR/official"

cleanup() {
    echo "==> Capturing docker compose logs"
    docker compose "${compose_args[@]}" logs --no-color > "$RESULTS_DIR/docker-compose.log" 2>&1 || true
    capture_docker_state "cleanup"

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
capture_docker_state "before"

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
repeat_files=()
for repetition in $(seq 1 "$BENCHMARK_REPETITIONS"); do
    echo "==> k6 repetition $repetition/$BENCHMARK_REPETITIONS ($BENCHMARK_K6_MODE)"
    rm -f "$RESULTS_DIR/official/test/results.json" "$RESULTS_DIR/official/test/k6-report.html"
    case "$BENCHMARK_K6_MODE" in
        native)
            if ! command -v k6 >/dev/null 2>&1; then
                echo "BENCHMARK_K6_MODE=native requires k6 on PATH" >&2
                exit 1
            fi
            (cd "$RESULTS_DIR/official" && bash ./run.sh > "../k6-output-repetition-$repetition.json")
            ;;
        docker)
            k6_args=(run --rm)
            if [[ -n "$BENCHMARK_K6_CPUSET" ]]; then
                k6_args+=(--cpuset-cpus "$BENCHMARK_K6_CPUSET")
            fi
            k6_args+=(
                --network host
                --user "$(id -u):$(id -g)"
                -e K6_NO_USAGE_REPORT=true
                -e K6_WEB_DASHBOARD=true
                -e K6_WEB_DASHBOARD_PORT=-1
                -e K6_WEB_DASHBOARD_EXPORT=test/k6-report.html
                -v "$ROOT_DIR/$RESULTS_DIR/official:/official"
                -w /official
                "$K6_IMAGE" run test/test.js
            )
            docker "${k6_args[@]}"
            ;;
        *)
            echo "Unsupported BENCHMARK_K6_MODE=$BENCHMARK_K6_MODE (expected native or docker)" >&2
            exit 1
            ;;
    esac
    capture_docker_state "after-$repetition"

    result_file="$RESULTS_DIR/results-repetition-$repetition.json"
    cp "$RESULTS_DIR/official/test/results.json" "$result_file"
    repeat_files+=("$result_file")
    if [[ -f "$RESULTS_DIR/official/test/k6-report.html" ]]; then
        cp "$RESULTS_DIR/official/test/k6-report.html" "$RESULTS_DIR/k6-report-repetition-$repetition.html"
    elif [[ "$BENCHMARK_K6_MODE" == "docker" ]]; then
        echo "k6 HTML report was not generated for repetition $repetition" >&2
    fi
done

if [[ "$BENCHMARK_REPETITIONS" -eq 1 ]]; then
    cp "${repeat_files[0]}" "$RESULTS_DIR/results.json"
    if [[ -f "$RESULTS_DIR/k6-report-repetition-1.html" ]]; then
        cp "$RESULTS_DIR/k6-report-repetition-1.html" "$RESULTS_DIR/k6-report.html"
    fi
else
    selected_repetition="$(jq -s '
        def median_index: ((length - 1) / 2 | floor);
        [to_entries[] | {
            repetition: (.key + 1),
            score: .value.scoring.final_score,
            p99_ms: (.value.p99 | sub("ms"; "") | tonumber)
        }]
        | sort_by(.score)
        | (.[median_index].score) as $median_score
        | map(select(.score == $median_score))
        | sort_by(.p99_ms)
        | .[median_index].repetition
    ' "${repeat_files[@]}")"
    cp "$RESULTS_DIR/results-repetition-$selected_repetition.json" "$RESULTS_DIR/results.json"
    if [[ -f "$RESULTS_DIR/k6-report-repetition-$selected_repetition.html" ]]; then
        cp "$RESULTS_DIR/k6-report-repetition-$selected_repetition.html" "$RESULTS_DIR/k6-report.html"
    fi

    jq -s --argjson selected_repetition "$selected_repetition" '{
        repetitions: length,
        selected: "median_score_then_median_p99",
        selected_repetition: $selected_repetition,
        selected_p99_ms: (.[($selected_repetition - 1)].p99 | sub("ms"; "") | tonumber),
        p99_ms: (map(.p99 | sub("ms"; "") | tonumber) | sort | {min: .[0], median: .[((length - 1) / 2 | floor)], max: .[-1]}),
        final_score: (map(.scoring.final_score) | sort | {min: .[0], median: .[((length - 1) / 2 | floor)], max: .[-1]}),
        failure_rate: map(.scoring.failure_rate)
    }' "${repeat_files[@]}" > "$RESULTS_DIR/repetition-summary.json"
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
        echo "- k6 mode: \`$BENCHMARK_K6_MODE\`"
        echo "- k6 image: \`$K6_IMAGE\`"
        if [[ -n "$BENCHMARK_STACK_CPUSET" ]]; then
            echo "- Stack cpuset: \`$BENCHMARK_STACK_CPUSET\`"
        fi
        if [[ -n "$BENCHMARK_API_CPUS$BENCHMARK_PROXY_CPUS" ]]; then
            echo "- Calibrated CPU limits: api=\`${BENCHMARK_API_CPUS:-default}\`, proxy=\`${BENCHMARK_PROXY_CPUS:-default}\`"
        fi
        if [[ -n "$BENCHMARK_K6_CPUSET" ]]; then
            echo "- k6 cpuset: \`$BENCHMARK_K6_CPUSET\`"
        fi
        echo "- Repetitions: \`$BENCHMARK_REPETITIONS\`"
        echo "- Scorer mode: \`$SCORER_MODE\`"
        echo "- Accept loops: \`$ACCEPT_LOOPS\`"
        echo "- Worker threads: min=\`$MIN_WORKER_THREADS\`, max=\`${MAX_WORKER_THREADS:-default}\`, io=\`${MAX_IO_THREADS:-default}\`"
        echo "- Bucket fastpath: reference=\`$BUCKET_REFERENCE_FASTPATH\`, legit=\`$BUCKET_REFERENCE_FASTPATH_LEGIT\`, fraud=\`$BUCKET_REFERENCE_FASTPATH_FRAUD\`, avx_cutoff=\`$BUCKET_AVX_CUTOFF_DIMS\`"
        echo "- IVF fast thresholds: zero=\`$IVF_ZERO_FAST_APPROVE_WORST_DISTANCE\`, five=\`$IVF_FIVE_FAST_DENY_WORST_DISTANCE\`"
        echo
        echo '```json'
        jq . "$RESULTS_DIR/results.json"
        echo '```'
    } >> "$GITHUB_STEP_SUMMARY"
fi
