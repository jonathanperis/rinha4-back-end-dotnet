# Tasks

| ID | Status | Task | Verify |
| --- | --- | --- | --- |
| CG1 | . | Fix comparison branch Jonathan compose to latest candidate image/config | comparison compose points at current GHCR image and Forevis option |
| CG2 | . | Add Jonathan comparison variants for nginx/Forevis/custom LB/cpuset | `gh workflow run benchmark.yml --ref comparison` accepts variant inputs |
| CG3 | . | Run 3-rep same-CI comparison against Danilo/Pedro/current Jonathan | result table has p99 median and `0` FP/FN/HTTP |
| CG4 | . | Build layer gap table from public source and compose evidence | `.specs/features/competitor-gap-analysis/results.md` exists |
| CG5 | . | Add benchmark-only constant scorer mode to isolate transport | local/CI transport benchmark excludes submission path |
| CG6 | . | Sweep LB/resource/cpuset variants with latest Jonathan image | median p99 comparison and no HTTP errors |
| CG7 | . | Add path instrumentation to AccuracyProbe for hybrid/bucket/IVF | path hit/fallback/candidate p99 printed without hot-path logging |
| CG8 | . | Prototype Danilo-style native AVX2 bucket candidate scan behind env | public replay `0` errors; CI p99 vs managed path |
| CG9 | . | Prototype precomputed/mmaped risky fine fallback sections | startup allocations/RSS down; public replay `0` errors |
| CG10 | . | Prototype Pedro-style mmaped Block-SoA Q16 IVF scan | vector tests pass; public replay `0` errors; CI p99 measured |
| CG11 | . | Prototype selective cascade from reference-only tables | hit rate + FP/FN reported; no public-test-derived committed keys |
| CG12 | . | Sweep ThreadPool/GC/prefault knobs | same-CI table, keep only `0%` failure wins |
| CG13 | . | Promote first measured missing-factor win | commit small diff, archive run, update STATE |
| CG14 | . | Re-run official preview only after same-CI evidence beats target | official issue has `0%` failures |
