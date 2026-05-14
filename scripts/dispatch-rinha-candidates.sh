#!/usr/bin/env bash
set -euo pipefail
cd /workspace/rinha-comparison

log() { printf '[%s] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }

wait_api() {
  for attempt in $(seq 1 120); do
    if curl -4 -fsS --connect-timeout 8 --max-time 15 -I https://api.github.com >/dev/null 2>&1; then
      log "GitHub API reachable"
      return 0
    fi
    log "GitHub API unavailable; retry $attempt/120 in 30s"
    sleep 30
  done
  log "GitHub API did not recover within retry window"
  return 1
}

dispatch_run() {
  local label="$1" ref="$2" sha_prefix="$3" compose_file="$4" reps="$5"
  log "Dispatching $label ref=$ref sha=$sha_prefix compose=$compose_file reps=$reps"
  for attempt in $(seq 1 5); do
    if gh workflow run benchmark.yml --ref "$ref" \
      -f compose_file="$compose_file" \
      -f official_ref=main \
      -f k6_image=grafana/k6:latest \
      -f benchmark_repetitions="$reps"; then
      break
    fi
    log "Dispatch failed for $label attempt $attempt/5"
    sleep 20
    if [[ "$attempt" == 5 ]]; then return 1; fi
  done

  sleep 12
  local run_id=""
  for attempt in $(seq 1 20); do
    run_id=$(gh run list --workflow benchmark.yml --branch "$ref" --limit 10 --json databaseId,headSha,createdAt,status,url \
      | jq -r --arg sha "$sha_prefix" '[.[] | select(.headSha | startswith($sha))] | sort_by(.createdAt) | reverse | .[0].databaseId // empty')
    if [[ -n "$run_id" ]]; then
      log "$label run_id=$run_id"
      printf '%s' "$run_id"
      return 0
    fi
    log "Waiting for run id for $label attempt $attempt/20"
    sleep 10
  done
  log "Could not find run id for $label"
  return 1
}

wait_run() {
  local label="$1" run_id="$2"
  while true; do
    local json status conclusion
    json=$(gh run view "$run_id" --json status,conclusion,url 2>/dev/null || true)
    status=$(jq -r '.status // "unknown"' <<<"$json")
    conclusion=$(jq -r '.conclusion // ""' <<<"$json")
    log "$label run_id=$run_id status=$status conclusion=${conclusion:-none}"
    if [[ "$status" == "completed" ]]; then
      [[ "$conclusion" == "success" ]] && return 0 || return 2
    fi
    sleep 30
  done
}

summarize_run() {
  local run_id="$1"
  local out="/workspace/rinha-ci-artifacts/$run_id"
  rm -rf "$out"
  mkdir -p "$out"
  gh run download "$run_id" --dir "$out" || true
  python3 - "$run_id" <<'PY'
from pathlib import Path
import json,re,sys
run_id=sys.argv[1]
root=Path('/workspace/rinha-ci-artifacts')/run_id
rows=[]
for p in root.glob('rinha-benchmark-*/results.json'):
    d=json.loads(p.read_text()); sc=d.get('scoring') or {}; bd=sc.get('breakdown') or {}
    m=re.match(r'rinha-benchmark-(.*)-\d+$', p.parent.name)
    name=m.group(1) if m else p.parent.name
    summ_path=p.parent/'repetition-summary.json'
    summ=json.loads(summ_path.read_text()) if summ_path.exists() else None
    rows.append((name, float(sc.get('final_score', float('nan'))), float(str(d.get('p99','nan')).replace('ms','')), bd.get('false_positive_detections'), bd.get('false_negative_detections'), bd.get('http_errors'), summ))
print(f'\nSUMMARY run {run_id}')
if not rows:
    print('No results.json artifacts found')
for name,score,p99,fp,fn,http,summ in sorted(rows,key=lambda x:(-x[1],x[2])):
    extra=f" reps_p99={summ.get('p99_ms')} reps_score={summ.get('final_score')}" if summ else ''
    print(f'{name:16s} score={score:8.2f} p99={p99:5.2f} fp={fp} fn={fn} http={http}{extra}')
# Print Jonathan docker log when no results are present or for v1 diagnostics
for logp in root.glob('rinha-benchmark-jonathanperis-*/docker-compose.log'):
    print(f'\nJonathan docker-compose.log ({logp}):')
    print(logp.read_text(errors='replace')[:4000])
PY
}

main() {
  wait_api

  # Candidate 4: Forevis v0.0.2, API-heavy 0.44/0.44/0.12 split, full comparison.
  run_v002=$(dispatch_run "v002-api-heavy" "comparison-v002-api-heavy" "e1649f203bac" "all-comparison" "3")

  # Candidate 5: requested Forevis v1.0.0, same API-heavy split, Jonathan-only readiness/perf probe.
  run_v100=$(dispatch_run "v100-api-heavy" "comparison-forevis-v100" "ff2124048c9d" "competitor-compose/jonathanperis/docker-compose.yml" "1")

  set +e
  wait_run "v100-api-heavy" "$run_v100"
  v100_status=$?
  summarize_run "$run_v100"

  wait_run "v002-api-heavy" "$run_v002"
  v002_status=$?
  summarize_run "$run_v002"
  set -e

  log "Finished. v100_status=$v100_status v002_status=$v002_status"
  if [[ "$v100_status" -ne 0 || "$v002_status" -ne 0 ]]; then
    exit 1
  fi
}

main "$@"
