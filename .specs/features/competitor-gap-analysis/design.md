# Design

## Comparison Method

Use layered isolation. Change one layer at a time. Same test data, same CI runner class, same image when possible.

```text
compose/LB/cpuset -> transport -> parser/vectorizer -> fast path -> scorer/index -> runtime knobs -> official preview
```

## Phase A - Fix Measurement Skew

- Update comparison branch Jonathan compose to benchmark latest known candidate image/config, not stale nginx pin.
- Add Jonathan variants in comparison branch:
  - `jonathan-nginx-current`
  - `jonathan-forevis-current`
  - `jonathan-clb-current`
  - `jonathan-forevis-cpuset`
- Run `all-comparison` plus Jonathan variants with `benchmark_repetitions=3`.
- Output table must include image SHA, LB, CPU/mem, cpuset, env knobs, p99, FP/FN/HTTP.

## Phase B - Infra/LB Gap

Compare only routing layer:

- Forevis vs nginx vs custom C LB vs fd-pass prototype.
- CPU split: `0.40/0.40/0.20` vs Danilo-like `0.425/0.425/0.15`.
- cpuset: none vs Pedro-like API `0/1`, LB `2,3` vs Danilo-like overlapped sets.
- readiness: both API sockets must exist before LB ready.

Decision rule: keep only `0%` failure variants with lower median p99 on same CI.

## Phase C - Transport Gap

Compare request handling cost independent of scorer where possible:

- Add benchmark-only constant scorer mode behind env, never use for submission.
- Measure raw socket server overhead vs Kestrel-style path only if branch-safe.
- Compare our async accept loops vs Danilo/Pedro accept-thread + ThreadPool handoff pattern.
- Sweep `ACCEPT_LOOPS`, `KEEP_ALIVE_MAX`, worker min/max, receive timeout.

Expected finding to test: accept/threading jitter may matter less than scorer, but must be quantified.

## Phase D - Fast-Path Gap

Compare abstaining decision tables built only from allowed reference data:

- Our profile fast path and reference fast paths.
- Pedro-style selective cascade:
  - reference purity stage
  - residual sparse/modal stage
  - abstain on unsafe buckets
- Danilo-style profile masks/counts and risky fallback gating.

Required instrumentation:

- hit rate by stage
- FP/FN by stage on public replay
- fallback rate
- p50/p95/p99 per stage
- table bytes and startup cost

Promotion rule: reference-derived table may ship only if public replay + CI stay `0` errors. No public-test-derived keys committed.

## Phase E - Scorer/Index Gap

Compare hot scanner designs:

### Danilo-style bucket path

- Native AVX2 helper for bucket candidate scan.
- Native AVX2 helper for risky fine fallback.
- Precompute risky fine sections into `references.bucket.bin` instead of building managed arrays at runtime.
- Keep existing C# path as fallback toggle.

### Pedro-style IVF path

- Mmap IVF arrays instead of loading managed arrays per API.
- Block-SoA Q16 scan with 8 lanes and 16 padded dims.
- Worst-index top-5 update instead of sorted insert where beneficial.
- Partial-dimension abort before finishing all dimensions.
- Software prefetch next blocks.
- Borderline rerank/nprobe only for uncertain counts.

Required metrics:

- candidates scanned
- blocks scanned/skipped
- bbox clusters repaired
- fallback candidates
- path p99
- FP/FN/HTTP
- RSS per service

## Phase F - Runtime/Memory Gap

- Compare managed-array IVF vs mmaped IVF RSS and p99.
- Prefault/madvise experiment for mmaped indexes.
- GC latency mode `SustainedLowLatency` experiment.
- ThreadPool min/max sweep: `16`, `32`, `64`, `128`, capped vs uncapped.
- Confirm total memory stays <= `350 MB` with `docker stats`/runtime info.

## Deliverables

- `docs/public/reports` archive for each CI candidate.
- `.specs/project/STATE.md` update after major result.
- Competitor gap table with verdicts:
  - keep
  - reject: slower
  - reject: errors
  - inconclusive: rerun needed
