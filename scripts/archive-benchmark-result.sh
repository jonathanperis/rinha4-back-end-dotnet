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

cat > "$REPORTS_DIR/index.html" <<'HTML'
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Rinha 2026 Benchmark Reports</title>
  <style>
    :root { color-scheme: light dark; --border: #d4d4d8; --muted: #71717a; --bg: #fff; --fg: #111827; }
    @media (prefers-color-scheme: dark) { :root { --border: #27272a; --muted: #a1a1aa; --bg: #09090b; --fg: #f4f4f5; } }
    body { margin: 0; background: var(--bg); color: var(--fg); font: 14px/1.5 system-ui, sans-serif; }
    main { max-width: 1040px; margin: 0 auto; padding: 48px 20px; }
    h1 { margin: 0 0 8px; font-size: 28px; }
    p { margin: 0 0 28px; color: var(--muted); }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 10px 12px; border-bottom: 1px solid var(--border); text-align: left; }
    th.num, td.num { text-align: right; font-variant-numeric: tabular-nums; }
    a { color: inherit; text-decoration: none; border-bottom: 1px solid var(--border); }
    code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px; }
    .muted { color: var(--muted); }
  </style>
</head>
<body>
  <main>
    <h1>Rinha 2026 Benchmark Reports</h1>
    <p>Official-like CI runs using the public Rinha k6 script. These are not official Rinha hardware results.</p>
    <table>
      <thead>
        <tr>
          <th>Run</th>
          <th class="num">p99</th>
          <th class="num">Failures</th>
          <th class="num">Score</th>
          <th class="num">HTTP</th>
          <th>Report</th>
        </tr>
      </thead>
      <tbody id="rows"></tbody>
    </table>
  </main>
  <script>
    const fmt = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    fetch('./index.json', { cache: 'no-store' })
      .then(r => r.json())
      .then(rows => {
        document.getElementById('rows').innerHTML = rows.map(row => `
          <tr>
            <td><a href="${row.run_url || '#'}">${new Date(row.timestamp).toISOString().replace('T', ' ').replace('.000Z', ' UTC')}</a><br><code class="muted">${row.short_sha}</code></td>
            <td class="num">${row.p99}</td>
            <td class="num">${row.failure_rate}</td>
            <td class="num">${fmt.format(row.final_score)}</td>
            <td class="num">${row.http_errors}</td>
            <td><a href="./${row.file}">json</a></td>
          </tr>
        `).join('');
      });
  </script>
</body>
</html>
HTML

echo "Archived benchmark report: $report_path"
