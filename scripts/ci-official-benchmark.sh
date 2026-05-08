#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OFFICIAL_REPO="${OFFICIAL_REPO:-https://github.com/zanfranceschi/rinha-de-backend-2026.git}"
OFFICIAL_REF="${OFFICIAL_REF:-main}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.nginx.yml}"
RESULTS_DIR="${RESULTS_DIR:-benchmark-results}"
K6_IMAGE="${K6_IMAGE:-grafana/k6:latest}"
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
BENCHMARK_STANDALONE_COMPOSE="${BENCHMARK_STANDALONE_COMPOSE:-false}"

cd "$ROOT_DIR"

if [[ -z "$COMPOSE_FILE" || "$COMPOSE_FILE" == "docker-compose.yml" ]]; then
    COMPOSE_FILE="docker-compose.nginx.yml"
fi

if [[ "$BENCHMARK_STANDALONE_COMPOSE" == "true" ]]; then
    compose_args=(--compatibility -f "$COMPOSE_FILE")
else
    compose_args=(--compatibility -f docker-compose.yml)
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
if [[ "$BENCHMARK_STANDALONE_COMPOSE" == "true" ]]; then
    if [[ -n "$api_cpuset$proxy_cpuset$BENCHMARK_API_CPUS$BENCHMARK_PROXY_CPUS$BENCHMARK_API_MEMORY$BENCHMARK_PROXY_MEMORY" ]]; then
        echo "Ignoring calibration overrides for standalone compose: service names are compose-specific."
    fi
elif [[ -n "$api_cpuset$proxy_cpuset$BENCHMARK_API_CPUS$BENCHMARK_PROXY_CPUS$BENCHMARK_API_MEMORY$BENCHMARK_PROXY_MEMORY" ]]; then
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
        echo "benchmark_k6_cpuset=$BENCHMARK_K6_CPUSET"
        echo "benchmark_api_cpus=$BENCHMARK_API_CPUS"
        echo "benchmark_proxy_cpus=$BENCHMARK_PROXY_CPUS"
        echo "benchmark_api_memory=$BENCHMARK_API_MEMORY"
        echo "benchmark_proxy_memory=$BENCHMARK_PROXY_MEMORY"
        echo "benchmark_repetitions=$BENCHMARK_REPETITIONS"
        echo "benchmark_standalone_compose=$BENCHMARK_STANDALONE_COMPOSE"
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
    if [[ "$BENCHMARK_STANDALONE_COMPOSE" == "true" ]]; then
        docker compose "${compose_args[@]}" pull
    else
        docker compose "${compose_args[@]}" pull webapi1 webapi2
    fi
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
    echo "==> k6 repetition $repetition/$BENCHMARK_REPETITIONS"
    rm -f "$RESULTS_DIR/official/test/results.json" "$RESULTS_DIR/official/test/k6-report.html"
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
    capture_docker_state "after-$repetition"

    result_file="$RESULTS_DIR/results-repetition-$repetition.json"
    cp "$RESULTS_DIR/official/test/results.json" "$result_file"
    repeat_files+=("$result_file")
    if [[ -f "$RESULTS_DIR/official/test/k6-report.html" ]]; then
        cp "$RESULTS_DIR/official/test/k6-report.html" "$RESULTS_DIR/k6-report-repetition-$repetition.html"
    else
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
        to_entries
        | sort_by((.value.p99 | sub("ms"; "") | tonumber))
        | .[((length - 1) / 2 | floor)].key + 1
    ' "${repeat_files[@]}")"
    cp "$RESULTS_DIR/results-repetition-$selected_repetition.json" "$RESULTS_DIR/results.json"
    if [[ -f "$RESULTS_DIR/k6-report-repetition-$selected_repetition.html" ]]; then
        cp "$RESULTS_DIR/k6-report-repetition-$selected_repetition.html" "$RESULTS_DIR/k6-report.html"
    fi

    jq -s --argjson selected_repetition "$selected_repetition" '{
        repetitions: length,
        selected: "median_by_p99",
        selected_repetition: $selected_repetition,
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
        echo
        echo '```json'
        jq . "$RESULTS_DIR/results.json"
        echo '```'
    } >> "$GITHUB_STEP_SUMMARY"
fi
