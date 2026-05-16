# Top Global Competitor Enhancement Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Close the official leaderboard gap to the current top global submissions `fksegundo/rinha-rust` and `RonieNeubauer/rinha2026` while preserving `0 FP / 0 FN / 0 HTTP errors`.

**Architecture:** Keep the current .NET NativeAOT + fd-pass topology, but import the winning ideas that are language-independent: tighter fd handoff/threading, parser-to-Q16 fusion, build-time single-file mmap index, SIMD-friendly index layout, and calibrated fast/fallback search. Every scoring change must be differential-tested against the exact oracle before CI benchmark promotion.

**Tech Stack:** .NET 10 NativeAOT, raw fd HTTP path, C/ASM fd-pass LB, GitHub Actions comparison workflow, official Rinha k6 dataset, `AccuracyProbe`/`VectorizationTests` guardrails.

---

## Evidence Baseline

### Our current state

- Repo: `jonathanperis/rinha4-back-end-dotnet`
- `origin/main`: `04d8798356...` after benchmark archive; latest scorer commit `18f04ba test: add cascade replay instrumentation`.
- `origin/submission`: `35cb18c bench: promote single accept loop submission`.
- Official preview ranking snapshot fetched `2026-05-16`:
  - Jonathan: score `5959.7`, p99 `1.10ms`, `FP=0`, `FN=0`, `HTTP=0`.
- Latest CI/local projection after cascade replay instrumentation:
  - official-like CI p99 `0.29ms`, score `6000`, `FP=0`, `FN=0`, `HTTP=0`.
  - Treat as projection until a new official submission confirms the runner gap.

### Competitor #1: `fksegundo/rinha-rust`

- `origin/main`: `9ea539e84101d2c871cc6dde6af1ac404246b32e`.
- `origin/submission`: `0ef144fbeda3aecdcbfd94f924ed0db71152543f`.
- Official issue: `zanfranceschi/rinha-de-backend-2026#4641`.
- Official result: score `6000`, p99 `0.83ms`, `FP=0`, `FN=0`, `HTTP=0`.
- Submission images/resources:
  - `ghcr.io/fksegundo/rinha-rust-api:latest`: `api1/api2`, each `0.42 CPU / 165M`.
  - `ghcr.io/fksegundo/rinha-api-lb:latest`: `lb`, `0.16 CPU / 20M`.
- Runtime shape: custom LB accepts TCP and passes connected FDs to Rust APIs over Unix sockets; APIs own request/response directly.

### Competitor #2: `RonieNeubauer/rinha2026`

- `origin/main`: `ff9c6c228760420caf08e13549b79a6f30f30886`.
- `origin/submission`: `557ac41b2fdbec4b991e25c917773fae31f2ca5c`.
- Official issue: `zanfranceschi/rinha-de-backend-2026#4682`.
- Official result: score `6000`, p99 `0.86ms`, `FP=0`, `FN=0`, `HTTP=0`.
- Submission image/resources:
  - `ronieneubauer/rinha2026:2.0.0-preview13`.
  - `api1/api2`: each `0.47 CPU / 160M`.
  - `lb`: `0.06 CPU / 30M`.
- Runtime shape: tiny C LB accepts TCP and passes FDs over Unix sockets; APIs run blocking connection workers, pre-rendered responses, mmap IVF index, AVX2 search.

### Comparison harness update

- Branch `comparison` now includes:
  - `competitor-compose/fksegundo-rust/docker-compose.yml`
  - `competitor-compose/ronieneubauer-rinha2026/docker-compose.yml`
  - workflow choices and participant selection entries for both.
- Commit: `3618165 bench: add top global competitors`.
- Triggered comparison run: `https://github.com/jonathanperis/rinha4-back-end-dotnet/actions/runs/25965557948`.

## Verified Competitor Mechanisms

### Shared mechanisms in both top global repos

1. **FD passing is the production transport.** The LB accepts public TCP and transfers the connected client socket to an API. The API writes directly to the client, so the LB does not proxy request/response bytes.
2. **No framework serialization in the hot path.** Both use minimal HTTP parsers and one of six pre-rendered JSON responses.
3. **Build-time index creation.** Runtime images contain already-built binary indexes from official references.
4. **Integer quantization at scale `10000`.** Query/reference vectors are compact `i16`/`short` data.
5. **SIMD-friendly layouts.** Rust uses AoSoA blocks; Ronie uses SoA dimension-pair arrays for AVX2 `_madd_epi16` style distance scans.
6. **Correctness gates before latency gates.** Both have brute-force or exact-verifier tooling and reject FP/FN before benchmark promotion.

### `fksegundo/rinha-rust` specifics worth copying

- `mmap` read-only specialist index plus `madvise(MADV_HUGEPAGE)`.
- Exact-safe partition/bbox pruning: `key-first` searches the likely partition first but continues any partition/node whose lower bound can beat current kth distance.
- Threadpool with many small-stack workers to keep accepted FDs moving under small CPU budgets.
- Fast request parsing for known JSON order with serde fallback for unusual order.

### `RonieNeubauer/rinha2026` specifics worth copying

- Very small LB CPU (`0.06`) because it only accepts and sends FDs; almost all CPU is moved to APIs (`0.47 + 0.47`).
- API can use `rtprio`/`SCHED_FIFO` when available to reduce wakeup tail.
- IVF `2048` clusters with bbox lower-bound ordering, `RINHA_FAST_PROBES=5`, `RINHA_MAX_PROBES=20`, class-specific fast thresholds, and full fallback when confidence is not enough.
- Dense 10,000-entry MCC risk table; no dictionary lookup on request path.
- Explicit deterministic tie-breaking with `(distance, global_index)` ordering.

## Hypotheses to Validate

1. **Official gap is now mostly stale-submission / official-runner timing, not algorithmic.** Our CI projection is already p99 `0.29ms` and score `6000`; official snapshot is older p99 `1.10ms`.
2. **The biggest remaining robust win is reducing .NET hot-path overhead after FD receipt.** Competitors avoid managed socket wrapping and framework dispatch entirely.
3. **Parser-to-Q16 fusion can reduce p99 variance more than another scorer threshold tweak.** Competitors parse directly into compact vectors; our parser still materializes more intermediate shape than necessary.
4. **A single mmap index file will improve startup/page-fault behavior and reduce duplicated data loading, but is not the first latency bottleneck.** Useful for stability and memory, higher risk than parser/threading.
5. **Ronie-style calibrated fast probes may improve fallback rate only if guarded by cascade replay/oracle approval drift checks.** Our current bucket fast path is strong; copying approximate thresholds blindly risks correctness.

## Hard Gates

- Correctness: `0 FP`, `0 FN`, `0 HTTP errors`; approval drift against exact/IVF oracle must be `0` for promoted fast paths.
- Resource: total compose limits <= `1 CPU / 350 MB`.
- Transport rule: LB must only distribute sockets/traffic; no fraud payload scoring in the LB.
- Benchmark: do not promote from a single noisy comparison if gap is small; use at least 3 comparison repetitions for final promotion.
- Official submission: verify immutable image tags and submission branch compose before opening/presenting official issue.

---

## Task 1: Finish top-global comparison evidence

**Objective:** Turn the newly added competitor composes into artifact-backed comparison numbers.

**Files:**
- Read: `.github/workflows/benchmark.yml` on branch `comparison`
- Read artifacts from run `25965557948`

**Steps:**
1. Wait for run `25965557948` to finish.
2. Download artifacts for `jonathanperis`, `fksegundo-rust`, and `ronieneubauer-rinha2026`.
3. Extract p99, final score, FP, FN, HTTP errors.
4. If any participant fails readiness/image pull, fix compose/workflow and rerun only that participant explicitly.
5. Run a 3-repetition `all-comparison` after the single-run smoke passes.

**Verification:** Report min/median/latest p99 for all three participants and include run URLs.

## Task 2: Verify official promotion freshness before changing scorer

**Objective:** Determine whether our current submission image is simply stale relative to our CI projection.

**Files:**
- Read: `docker-compose.yml`
- Read: submission branch `docker-compose.yml`
- Read: latest CI benchmark artifacts

**Steps:**
1. Compare `origin/submission` image tag against latest successful `main` candidate image.
2. Confirm submission branch contains the current fd-pass single-accept-loop image and env defaults.
3. If stale, prepare a promotion report rather than modifying algorithm code first.
4. Only proceed to invasive scorer work if current image still loses repeated comparison/official projection.

**Verification:** Promotion report lists exact image tag, commit, CI run, and official ranking delta.

## Task 3: Microbenchmark fd-receive raw path vs managed socket wrapping

**Objective:** Quantify the remaining transport overhead after fd-pass.

**Files:**
- Modify/Test: `tests/VectorizationTests` or add a small benchmark harness under `tests/TransportBenchmarks/`
- Inspect/Modify: `src/WebApi/FdPassingServer.cs`
- Inspect/Modify: `src/WebApi/RawHttpServer.cs`

**Steps:**
1. Add a microbench that exercises the fd/raw handler path with synthetic already-parsed buffers where possible.
2. Compare `FD_RAW=1` and `FD_RAW=0` paths locally without Docker.
3. Profile allocation counts and request loop dispatch points.
4. Remove or guard any leftover managed wrapper path from submission defaults if raw path is consistently better.

**Verification:** Benchmark output shows per-request overhead and allocation count for both paths; normal tests still pass.

## Task 4: Fuse parser output directly into Q16 query vector

**Objective:** Match competitor parser design by skipping intermediate request objects on the canonical path.

**Files:**
- Modify: `src/WebApi/FraudRequestParser.cs`
- Modify: `src/WebApi/*Scorer*.cs` / vectorization entrypoints
- Test: `tests/VectorizationTests/*`

**Steps:**
1. Add a failing test that parses official sample JSON into Q16 and compares it against the existing parser+vectorizer output.
2. Implement `TryParseQueryVector(ReadOnlySpan<byte> body, Span<short> q16, out ParseStatus status)` for canonical k6 field order.
3. Keep existing parser as fallback behind a branch for non-canonical/diagnostic inputs.
4. Route hot `/fraud-score` path to the direct Q16 parser.
5. Run `AccuracyProbe` against the official corpus.

**Verification:** Exact vector equivalence on samples and corpus; p99 comparison run does not regress correctness.

## Task 5: Replace dictionary MCC lookup with dense table

**Objective:** Copy Ronie's cheap MCC risk lookup if our current path still uses hash/dictionary/string work.

**Files:**
- Inspect/Modify: `src/DataConverter/*`
- Inspect/Modify: `src/WebApi/*Mcc*` or parser/vectorizer files
- Test: vectorization tests

**Steps:**
1. Confirm current MCC lookup cost and representation.
2. Generate a `short[10000]` or compact binary dense table during `DataConverter`.
3. Parse MCC digits into integer `0..9999` without allocating strings.
4. Use table default risk for missing/unknown MCC.

**Verification:** Vectorization equality with current implementation; no additional FP/FN in `AccuracyProbe`.

## Task 6: Design single-file mmap index format

**Objective:** Consolidate bucket/IVF/exact artifacts into one versioned mmap-friendly binary file.

**Files:**
- Modify: `src/DataConverter/*`
- Modify: `src/WebApi/*Index*`, `BucketIndex`, `IvfIndex`
- Test: `tests/VectorizationTests` and `AccuracyProbe`

**Steps:**
1. Define header: magic, version, dims, scale, section directory offsets/lengths.
2. Pack normalization, MCC dense table, bucket profile/reference tables, IVF centroids/bboxes/vectors/labels into aligned sections.
3. Implement mmap loader with version/scale validation.
4. Keep old multi-file loader behind `INDEX_FORMAT=legacy` until benchmark-proven.
5. Add build-time converter verification that loads the new file and matches old scorer output.

**Verification:** New index produces identical fraud counts/approvals to legacy on official corpus; memory stays under budget.

## Task 7: Add Ronie-style calibrated fast/fallback sweeper

**Objective:** Systematically search fast-path thresholds with zero approval drift rather than tuning by hand.

**Files:**
- Create/Modify: `src/AccuracyProbe` sweeper mode
- Modify: scorer config parsing only after sweep results are proven

**Steps:**
1. Extend `AccuracyProbe` to sweep fast thresholds/probe counts and emit fraud-count drift plus approval drift.
2. Include per-stage breakdown from cascade replay instrumentation.
3. Reject any threshold set with nonzero approval drift.
4. Promote only threshold sets that reduce fallback rate materially and pass CI benchmark.

**Verification:** Sweep report includes total, per-stage drift, fallback rate, and selected thresholds; selected thresholds pass official corpus with `0` approval drift.

## Task 8: Evaluate .NET fixed worker model under fd-pass

**Objective:** Test whether a competitor-style fixed worker model beats ThreadPool dispatch under CPU quotas.

**Files:**
- Modify: `src/WebApi/FdPassingServer.cs`
- Modify: `src/WebApi/RawHttpServer.cs`
- Test: transport smoke tests and CI benchmark branch

**Steps:**
1. Add env-gated `FD_WORKER_MODE=fixed|threadpool`.
2. Implement bounded fixed workers per API process, pinned/affinitized only if safe in container.
3. Keep ThreadPool default until repeated comparison shows a win.
4. Benchmark with API-heavy CPU split (`0.47/0.47/0.06`) and current split (`0.42/0.42/0.16`).

**Verification:** Repeated comparison shows median p99 improvement without HTTP errors or readiness instability.

## Task 9: Promote only after repeated top-global comparison win or verified official-stale gap

**Objective:** Avoid chasing noisy results or publishing a risky submission.

**Files:**
- Modify: `submission` branch compose only after approval
- Update: docs/report with benchmark evidence

**Steps:**
1. Run 3+ comparison repetitions against `fksegundo-rust` and `ronieneubauer-rinha2026`.
2. Confirm our p99 median is within target or better, with score `6000`.
3. Verify immutable image tags in submission compose.
4. Present promotion report for user approval before official issue.

**Verification:** Report includes exact refs, images, comparison run URLs, min/median/max p99, and correctness counts.

## Prioritized Recommendation

1. **Do not rewrite the scorer first.** Current CI evidence suggests we may already have a score-6000, sub-top-global candidate that is not reflected in official ranking yet.
2. **Finish the top-global comparison run and 3-repeat confirmation.** This tells us whether the official gap is stale-submission or real.
3. **If real, attack parser/Q16 fusion and fd raw/fixed-worker overhead first.** These match both competitors and carry less correctness risk than approximate scorer changes.
4. **Then consolidate mmap/index and add threshold sweeper.** These are valuable, but they must remain behind oracle-driven correctness gates.
