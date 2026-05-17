# Local .NET Experiment Notes — 2026-05-17

Scope: safe local experiments against the official `test/test-data.json` corpus using the checked-in generated data under `data/`. Full Docker/k6 runs were not possible from this host because the Docker daemon is unavailable; treat these as scorer/harness evidence, not official p99 evidence.

## Baseline verification

Commands:

```sh
dotnet build tests/AccuracyProbe/AccuracyProbe.csproj -c Release
dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
```

Result: build passed and all vectorization/parser/fd flag guard checks passed.

## New local harness

`AccuracyProbe` now has a `score-bench` mode:

```sh
SCORER_MODE=hybrid \
  dotnet run --project tests/AccuracyProbe/AccuracyProbe.csproj -c Release --no-build -- \
  /opt/data/github/jonathanperis/rinha-de-backend-2026/test/test-data.json \
  data score-bench 10
```

It warms one full corpus pass, then scores the corpus for `N` loops, verifies approval correctness, and prints per-request scorer elapsed nanoseconds. It is intended for quick local scorer-path comparisons before spending CI runs.

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

## Learnings

1. **Do not relax profile/reference fast-path thresholds blindly.** The tempting low-count/legit knobs found correctness drifts immediately on the official corpus.
2. **The current bucket cascade is already near the safe local frontier.** `BUCKET_PROFILE_MIN_COUNT=20` is safe but does not reduce fallbacks; disabling any major fast path gets worse.
3. **Hybrid is mandatory.** IVF-only is roughly 24× the local average scorer cost of hybrid; bucket-only is not correct.
4. **Next useful experiments should target non-scorer overhead or new safe decision mechanisms:** raw fd worker/dispatch overhead, parser hot path, or a generated selective cascade with corpus replay gates. Simple bucket candidate/threshold sweeps did not reveal a promotable config change.
5. **`SUBMITTED_FAST_PATH=0` deserves only a cheap CI A/B, not a code change yet.** Local timing did not prove the submitted hybrid branch is faster; however, the difference is small enough that hosted noise may dominate.

## Proposed next actions

- Keep current runtime defaults unchanged.
- Use `score-bench` before future local scorer refactors.
- If spending CI, run a small official-like A/B for current default vs `SUBMITTED_FAST_PATH=0` only after the current official/submission freshness question is resolved.
- Prioritize a transport microbenchmark or fixed-worker experiment over more bucket threshold sweeps.
