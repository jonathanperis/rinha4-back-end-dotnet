# CI/CD Pipeline

Main build flow:

1. Build amd64 Docker image.
2. Push immutable `ci-${GITHUB_SHA}` tag to GHCR.
3. Start Docker Compose with that exact image.
4. Clone official Rinha 2026 repo.
5. Run public `test/test.js` through k6.
6. Upload raw benchmark artifacts.
7. Archive summarized JSON into `docs/public/reports`.
8. GitHub Pages deploys the docs site.

The automatic main-branch benchmark runs against the immutable image tag built in
the same workflow, not a locally rebuilt image. The canonical submission/runtime
shape is root `docker-compose.yml`: `webapi1` on cpuset `0`, `webapi2` on `1`,
and standalone `lb` on `2,3`, while Docker resource limits remain active. Manual
runs can add a one-core overlay when diagnosing official mismatch, but that
stress mode is stricter than the candidate tracking run.

The build workflow also archives an `official-calibrated` run after the normal
candidate run. That lane can override service CPU quotas to screen splits such as
`api=0.40` and `proxy=0.20`. It is a prediction/screening signal only; the
candidate/submission compose remains the source for official testing.

Manual **Official-like Benchmark** runs can archive experiment reports too. For
IVF, dispatch with `report_kind=experiment`, `IVF_FAST_NPROBE=1`,
`IVF_FULL_NPROBE=1`, bbox repair on, `IVF_BOUNDARY_FULL=false`, repair fraud
range `0..5`, and the `IVF_SCALE` value under test.

Manual contention knobs:

- `benchmark_stack_cpuset=0`: pin the standalone LB and WebApi containers to one host CPU.
- `benchmark_k6_cpuset=0`: also pin k6 to that CPU. Use only when diagnosing
  host contention; it is intentionally harsher than normal candidate tracking.
- `benchmark_api_cpus` and `benchmark_proxy_cpus`: override service CPU quotas
  for calibrated or split-screening runs, for example `0.40` and `0.20`.
- `benchmark_repetitions`: run k6 multiple times and archive the median-p99
  result, with raw repetition files uploaded as artifacts.

## Report files

| File | Purpose |
| --- | --- |
| `latest.json` | latest benchmark result |
| `latest-candidate.json` | latest default submission-stack result |
| `latest-calibrated.json` | latest official-calibrated prediction run |
| `latest-experiment.json` | latest non-default experiment result |
| `index.json` | sorted benchmark history |
| `rinha-benchmark-*.json` | immutable benchmark records |
| `rinha-benchmark-*.html` | k6 HTML reports when generated |

Uploaded workflow artifacts also include `docker-state-*.txt` with Docker
limits, cpuset, memory, and cgroup counters captured before and after k6. Use
those files to confirm which cpuset mode the run used.

The report archive commit is docs-only. The build workflow ignores `docs/**`, so
report commits do not trigger a new benchmark loop.

When benchmark reports change, the build workflow triggers the Pages workflow so
`/reports/` refreshes without manual action. The manual benchmark workflow does
the same refresh after archiving a report.
