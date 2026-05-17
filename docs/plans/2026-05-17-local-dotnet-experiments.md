# Local .NET Experiment Notes — 2026-05-17

Scope: safe local experiments against the official `test/test-data.json` corpus using the checked-in generated data under `data/`. Full Docker/k6 runs were not possible from this host because the Docker daemon is unavailable; treat these as scorer/harness evidence, not official p99 evidence.

## Baseline verification

Commands:

```sh
dotnet build tests/AccuracyProbe/AccuracyProbe.csproj -c Release
dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
```

Result: build passed and all vectorization/parser/fd flag guard checks passed.

## New local harnesses

`AccuracyProbe` now has a `score-bench` mode:

```sh
SCORER_MODE=hybrid \
  dotnet run --project tests/AccuracyProbe/AccuracyProbe.csproj -c Release --no-build -- \
  /opt/data/github/jonathanperis/rinha-de-backend-2026/test/test-data.json \
  data score-bench 10
```

It warms one full corpus pass, then scores the corpus for `N` loops, verifies approval correctness, and prints per-request scorer elapsed nanoseconds. It is intended for quick local scorer-path comparisons before spending CI runs.

The probe also has a `wire-bench` mode:

```sh
SCORER_MODE=hybrid \
  dotnet run --project tests/AccuracyProbe/AccuracyProbe.csproj -c Release --no-build -- \
  /opt/data/github/jonathanperis/rinha-de-backend-2026/test/test-data.json \
  data wire-bench 5
```

`wire-bench` wraps each corpus request in an HTTP/1.1 `/fraud-score` request with k6-like headers, then times `HttpWire.FindHeaderEnd` + `GetContentLength` + route check + scorer. It does not include socket reads/writes, fd handoff, ThreadPool scheduling, or concurrency, so interpret it as a lower-bound local parser+scorer microbench rather than an end-to-end transport benchmark.

## Cascade / bucket-profile sweep

All rows used `CASCADE_REPLAY=1` with `hybrid-profile`, except the bucket-only profile row.

| Experiment | Fast | Fallback | Fallback rate | Correctness | Replay approval drift | Notes |
|---|---:|---:|---:|---:|---:|---|
| default | 51,554 | 2,546 | 4.71% | 0 FP / 0 FN | 0 | Strong current baseline. |
| `BUCKET_REFERENCE_FASTPATH=0` | 50,671 | 3,429 | 6.34% | 0 / 0 | 0 | Reference fast paths save 883 IVF fallbacks; keep them. |
| `BUCKET_PROFILE_FASTPATH=0` | 40,708 | 13,392 | 24.75% | 0 / 0 | 0 | Profile fast path is the main win; disabling it multiplies fallback work. |
| `BUCKET_PROFILE_MIN_COUNT=10` | 51,558 | 2,542 | 4.70% | 1 FP / 0 FN | 1 | Rejected: tiny fallback-rate improvement but breaks approval correctness. |
| `BUCKET_PROFILE_MIN_COUNT=20` | 51,553 | 2,547 | 4.71% | 0 / 0 | 0 | Safe but no material improvement over default. |
| `BUCKET_REFERENCE_FASTPATH_LEGIT=1` | 51,555 | 2,545 | 4.70% | 0 FP / 1 FN | 1 | Rejected: one unsafe approval drift. |
| `BUCKET_REFERENCE_FASTPATH2_FRAUD=0` | 51,416 | 2,684 | 4.96% | 0 / 0 | 0 | Safe but worse fallback rate; keep default on. |
| `BUCKET_EARLY_CANDIDATES=8000/12000` | 51,554 | 2,546 | 4.71% | 0 / 0 | 0 | No effect in hybrid fast/fallback decision distribution. |
| `BUCKET_MAX_CANDIDATES=20000/30000` | 51,554 | 2,546 | 4.71% | 0 / 0 | 0 | No effect in hybrid fast/fallback decision distribution. |
| bucket-only profile | — | — | — | 131 FP / 147 FN | — | Confirms bucket-only is not submission-safe despite low median scorer time. |

Default fallback details: `fallback_total_blocks avg=1012.87 p50=938 p90=1809 p95=2132 p99=2669 max=7478`.

## Local scorer timing sweep

`score-bench 10` over 541,000 measured requests after warmup:

| Mode | Correctness | Avg ns | p50 ns | p90 ns | p95 ns | p99 ns | Notes |
|---|---:|---:|---:|---:|---:|---:|---|
| `SCORER_MODE=hybrid` | 0 FP / 0 FN | 2441 | 830 | 1320 | 2320 | 41021 | Current submission-style baseline. |
| `SCORER_MODE=hybrid SUBMITTED_FAST_PATH=0` | 0 / 0 | 2366 | 760 | 1230 | 2280 | 40581 | Locally a hair faster/no worse; likely noise or branch/JIT shape. Needs CI before changing default. |
| `SCORER_MODE=hybrid BUCKET_PROFILE_MIN_COUNT=20` | 0 / 0 | 2472 | 850 | 1320 | 2370 | 41390 | Safe but not faster locally. |
| `SCORER_MODE=ivf` | 0 / 0 | 59513 | 48091 | 102851 | 127312 | 183511 | Correct but far too much CPU; hybrid fast path is essential. |
| `SCORER_MODE=bucket` | 393 FP / 441 FN over 3 loops | 6678 | 821 | 1240 | 2540 | 164442 | Incorrect and worse average/tail than hybrid because slow bucket fallbacks still occur. |

## Local wire/parser timing sweep

`wire-bench 5` with k6-like synthetic headers over 270,500 measured requests after warmup:

| Mode | Correctness | Avg ns | p50 ns | p90 ns | p95 ns | p99 ns | Notes |
|---|---:|---:|---:|---:|---:|---:|---|
| `score-bench` | 0 FP / 0 FN | 2402 | 840 | 1360 | 2580 | 39090 | Scorer-only lower bound. |
| `wire-bench` | 0 / 0 | 2393 | 830 | 1290 | 2529 | 39161 | HTTP header parse + route check added no measurable local overhead versus scorer noise. |

Takeaway: on this local single-thread harness, `HttpWire` header parsing is not the bottleneck. Remaining transport work should focus on fd handoff, socket read/write, keep-alive/concurrency scheduling, and hosted-runner resource split rather than micro-optimizing header parsing.

## Learnings

1. **Do not relax profile/reference fast-path thresholds blindly.** The tempting low-count/legit knobs found correctness drifts immediately on the official corpus.
2. **The current bucket cascade is already near the safe local frontier.** `BUCKET_PROFILE_MIN_COUNT=20` is safe but does not reduce fallbacks; disabling any major fast path gets worse.
3. **Hybrid is mandatory.** IVF-only is roughly 24× the local average scorer cost of hybrid; bucket-only is not correct.
4. **Next useful experiments should target non-scorer overhead or new safe decision mechanisms:** raw fd worker/dispatch overhead, parser hot path, or a generated selective cascade with corpus replay gates. Simple bucket candidate/threshold sweeps did not reveal a promotable config change.
5. **`SUBMITTED_FAST_PATH=0` deserves only a cheap CI A/B, not a code change yet.** Local timing did not prove the submitted hybrid branch is faster; however, the difference is small enough that hosted noise may dominate.
6. **The local HTTP parser layer is below scorer/tail noise.** `wire-bench` was essentially tied with scorer-only timing, so the next transport experiments need real fd/socket/concurrency evidence.

## CI fd raw A/B

The manual `Official-like Benchmark` workflow now exposes an `fd_raw` input (`1`/`0`) and records that value through the archive path. Because `docker-compose.yml` is now the only current fd-pass topology, the workflow no longer exposes the deleted `docker-compose.fdpass.yml`/compose override path. This makes a clean managed-socket vs raw-fd official-like A/B possible against the same immutable WebApi image.

The first A/B used the same immutable image (`ghcr.io/jonathanperis/rinha4-back-end-dotnet:ci-c23ff7ad4b2911b59342dfbcd4f08770305b7088`) and calibrated CPU quotas (`benchmark_api_cpus=0.40`, `benchmark_proxy_cpus=0.20`) with three repetitions per arm:

| Workflow run | Toggle | p99 min | p99 median | p99 max | Final score | Failure rate |
|---:|---|---:|---:|---:|---:|---|
| `25981537579` | `FD_RAW=1` | 0.31ms | 0.32ms | 0.35ms | 6000 / 6000 / 6000 | 0% / 0% / 0% |
| `25981537926` | `FD_RAW=0` | 0.31ms | 0.34ms | 0.34ms | 6000 / 6000 / 6000 | 0% / 0% / 0% |

Takeaway: raw fd was slightly better on median p99 in this small hosted-runner sample, but the ranges overlap. Keep `FD_RAW=1` as the default because it is at least not worse and was marginally ahead here; do not claim a large advantage without more repeated rounds.

The push run for the workflow-input fix (`25981532396`) also passed candidate and calibrated official-like benchmarks at score `6000`, with `FD_RAW=1` recorded in the archived metadata.

## Proposed next actions

- Keep current runtime defaults unchanged (`FD_RAW=1`, `SCORER_MODE=hybrid`, current bucket thresholds).
- Use `score-bench` before future local scorer refactors and `wire-bench` before parser/request-shape refactors.
- If spending more CI, repeat `FD_RAW=1` vs `FD_RAW=0` in additional same-image pairs before making stronger transport claims.
- Prioritize a real concurrency/worker experiment over more bucket threshold sweeps; the parser-only local layer was below scorer/tail noise.
