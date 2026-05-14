#!/usr/bin/env bash
set -euo pipefail
cd /workspace/rinha-comparison

REPO="jonathanperis/rinha4-back-end-dotnet"
API_IP="140.82.112.6"
TOKEN="$(gh auth token)"
LOG_FILE="/workspace/rinha-ci-artifacts/dispatch-rest.log"
mkdir -p /workspace/rinha-ci-artifacts
: > "$LOG_FILE"

log() {
  local msg="[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*"
  printf '%s\n' "$msg" >> "$LOG_FILE"
  printf '%s\n' "$msg" >&2
}

api_curl() {
  curl -4 -fsS --retry 3 --retry-delay 3 --connect-timeout 10 --max-time 60 \
    --resolve "api.github.com:443:${API_IP}" \
    -H "Authorization: Bearer ${TOKEN}" \
    -H "Accept: application/vnd.github+json" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    "$@"
}

api_get() {
  api_curl "https://api.github.com/$1"
}

dispatch_run() {
  local label="$1" ref="$2" sha_prefix="$3" compose_file="$4" reps="$5"
  local body
  body=$(jq -nc --arg ref "$ref" --arg compose "$compose_file" --arg reps "$reps" '{ref:$ref, inputs:{compose_file:$compose, official_ref:"main", k6_image:"grafana/k6:latest", benchmark_repetitions:$reps}}')
  log "Dispatching $label ref=$ref sha=$sha_prefix compose=$compose_file reps=$reps"
  api_curl -X POST "https://api.github.com/repos/${REPO}/actions/workflows/benchmark.yml/dispatches" \
    -H "Content-Type: application/json" \
    --data "$body" \
    -o /tmp/rinha-dispatch-${label}.out

  sleep 12
  local run_id=""
  for attempt in $(seq 1 24); do
    api_get "repos/${REPO}/actions/runs?branch=${ref}&event=workflow_dispatch&per_page=20" > /tmp/rinha-runs-${label}.json
    run_id=$(jq -r --arg sha "$sha_prefix" '[.workflow_runs[] | select(.head_sha | startswith($sha))] | sort_by(.created_at) | reverse | .[0].id // empty' /tmp/rinha-runs-${label}.json)
    if [[ -n "$run_id" ]]; then
      local url
      url=$(jq -r --argjson id "$run_id" '.workflow_runs[] | select(.id == $id) | .html_url' /tmp/rinha-runs-${label}.json | head -1)
      log "$label run_id=$run_id url=$url"
      printf '%s' "$run_id"
      return 0
    fi
    log "Waiting for run id for $label attempt $attempt/24"
    sleep 10
  done
  log "Could not find run id for $label"
  return 1
}

wait_run() {
  local label="$1" run_id="$2"
  while true; do
    api_get "repos/${REPO}/actions/runs/${run_id}" > /tmp/rinha-run-${run_id}.json
    local status conclusion url
    status=$(jq -r '.status // "unknown"' /tmp/rinha-run-${run_id}.json)
    conclusion=$(jq -r '.conclusion // ""' /tmp/rinha-run-${run_id}.json)
    url=$(jq -r '.html_url // ""' /tmp/rinha-run-${run_id}.json)
    log "$label run_id=$run_id status=$status conclusion=${conclusion:-none} url=$url"
    if [[ "$status" == "completed" ]]; then
      [[ "$conclusion" == "success" ]] && return 0 || return 2
    fi
    sleep 30
  done
}

download_artifacts() {
  local run_id="$1"
  local out="/workspace/rinha-ci-artifacts/${run_id}"
  rm -rf "$out"
  mkdir -p "$out"
  api_get "repos/${REPO}/actions/runs/${run_id}/artifacts?per_page=100" > "$out/artifacts.json"
  local count
  count=$(jq -r '.artifacts | length' "$out/artifacts.json")
  log "run $run_id artifact_count=$count"
  if [[ "$count" == "0" ]]; then
    return 0
  fi
  jq -r '.artifacts[] | [.name, .archive_download_url] | @tsv' "$out/artifacts.json" |
  while IFS=$'\t' read -r name url; do
    log "Downloading artifact $name"
    local zip="$out/${name}.zip"
    api_curl -L "$url" -o "$zip"
    mkdir -p "$out/$name"
    unzip -q "$zip" -d "$out/$name"
  done
}

summarize_run() {
  local run_id="$1" label="$2"
  python3 - "$run_id" "$label" <<'PY'
from pathlib import Path
import json,re,sys
run_id,label=sys.argv[1],sys.argv[2]
root=Path('/workspace/rinha-ci-artifacts')/run_id
rows=[]
for p in root.glob('rinha-benchmark-*/results.json'):
    d=json.loads(p.read_text()); sc=d.get('scoring') or {}; bd=sc.get('breakdown') or {}
    m=re.match(r'rinha-benchmark-(.*)-\d+$', p.parent.name)
    name=m.group(1) if m else p.parent.name
    summ_path=p.parent/'repetition-summary.json'
    summ=json.loads(summ_path.read_text()) if summ_path.exists() else None
    rows.append((name, float(sc.get('final_score', float('nan'))), float(str(d.get('p99','nan')).replace('ms','')), bd.get('false_positive_detections'), bd.get('false_negative_detections'), bd.get('http_errors'), summ))
print(f'\n===== SUMMARY {label} run {run_id} =====')
if not rows:
    print('No results.json artifacts found')
for name,score,p99,fp,fn,http,summ in sorted(rows,key=lambda x:(-x[1],x[2])):
    extra=f" reps_p99={summ.get('p99_ms')} reps_score={summ.get('final_score')}" if summ else ''
    print(f'{name:16s} score={score:8.2f} p99={p99:5.2f} fp={fp} fn={fn} http={http}{extra}')
for logp in root.glob('rinha-benchmark-jonathanperis-*/docker-compose.log'):
    print(f'\nJonathan docker-compose.log ({logp}):')
    print(logp.read_text(errors='replace')[:4000])
PY
}

main() {
  log "API check via --resolve"
  api_get "rate_limit" >/tmp/rinha-rate-limit.json
  log "API OK"

  run_v002=$(dispatch_run "v002-api-heavy" "comparison-v002-api-heavy" "e1649f203bac" "all-comparison" "3")
  run_v100=$(dispatch_run "v100-api-heavy" "comparison-forevis-v100" "ff2124048c9d" "competitor-compose/jonathanperis/docker-compose.yml" "1")

  set +e
  wait_run "v100-api-heavy" "$run_v100"
  status_v100=$?
  download_artifacts "$run_v100"
  summarize_run "$run_v100" "v100-api-heavy"

  wait_run "v002-api-heavy" "$run_v002"
  status_v002=$?
  download_artifacts "$run_v002"
  summarize_run "$run_v002" "v002-api-heavy"
  set -e

  log "Finished statuses: v100=$status_v100 v002=$status_v002"
  if [[ "$status_v100" -ne 0 || "$status_v002" -ne 0 ]]; then
    exit 1
  fi
}

main "$@"
