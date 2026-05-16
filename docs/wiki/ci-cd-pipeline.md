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
and standalone `lb` on `2,3`, while Docker resource limits remain active. The
current default stack uses `SCORER_MODE=hybrid`, fd-pass API handoff with
`FD_RAW=1`, API CPU quotas of `0.44` each, and LB CPU quota `0.12`.

The build workflow also archives an `official-calibrated` run after the normal
candidate run. That lane can override service CPU quotas to screen alternative
splits. It is a prediction/screening signal only; the candidate/submission
compose remains the source for official testing.

Manual **Official-like Benchmark** runs can archive experiment reports too. Use
`report_kind=experiment` for non-default scorer/config tests. The manual workflow
currently exposes scorer choices `hybrid`, `bucket`, `ivf`, and `exact`; hybrid is
the default candidate path. It also exposes IVF build/repair knobs, bucket AVX
cutoff, optional compose override, and repetition count for median-p99 screening.
The `docker-compose.fdpass.yml` override keeps fd-pass topology but sets
`FD_RAW=0` so manual runs can compare the managed `Socket` fallback against the
root compose raw-fd default.

Manual contention knobs:

- `benchmark_api_cpuset` and `benchmark_proxy_cpuset`: optionally override Docker cpusets for API or proxy containers.
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
