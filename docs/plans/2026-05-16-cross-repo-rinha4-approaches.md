# Cross-Repo Rinha4 Approach Analysis

Date: 2026-05-16

Repos inspected:

| repo | inspected head | role |
|---|---|---|
| `rinha4-back-end-dotnet` | `6be6980` | .NET NativeAOT API candidate using ASM `rinha4-lb-yolo-mode` in fd-pass stream mode by default |
| `rinha4-back-end-c` | `d0c29a4` | C API candidate using `rinha4-lb-yolo-mode` in fd-pass mode |
| `rinha4-lb-yolo-mode` | `b5b0e37` | custom C load balancer shared by the candidates |

This document is a working map of the approaches we can take next. It separates low-risk benchmark/config experiments from code-level work and cross-repo transfers.

---

## Current snapshot

### .NET candidate

- Runtime: two .NET NativeAOT `WebApi` containers behind `rinha4-lb-yolo-mode`.
- Default transport: LB proxying TCP client traffic to API Unix sockets.
- Current compose split: API `0.45` each, LB `0.10`; total `1.00` CPU.
- Current LB image: `ghcr.io/jonathanperis/rinha4-lb-yolo-mode:asm-ci-dcc6b89ca9d21c7a8dbb1588a6bfbbc0bd20bb91`.
- Current scorer: `hybrid`, using bucket fast path first and IVF fallback.
- Hot files:
  - `docker-compose.yml`
  - `docker-compose.fdpass.yml`
  - `src/WebApi/Program.cs`
  - `src/WebApi/RawHttpServer.cs`
  - `src/WebApi/FraudRequestParser.cs`
  - `src/WebApi/FraudScorer.cs`
  - `src/WebApi/BucketIndex.cs`
  - `src/WebApi/IvfIndex*.cs`
  - `src/DataConverter/*`
- Latest archived candidate before this pass: p99 `0.37ms`, final score `6000`, `0 FP / 0 FN / 0 HTTP`, run `25947319201`.

### C candidate

- Runtime: two C API workers behind `rinha4-lb-yolo-mode`.
- Default transport: fd-pass. The LB accepts TCP and passes accepted client FDs to APIs over Unix control sockets via `SCM_RIGHTS`.
- Current compose split: API `0.46` each, LB `0.08`; total `1.00` CPU.
- Hot files:
  - `docker-compose.yml`
  - `src/api/main.c`
  - `src/common/fdpass.c`
  - `src/common/http.c`
  - `src/common/vectorize.c`
  - `src/common/search.c`
  - `src/common/index.c`
  - `src/preprocess/build_index.c`
- Latest archived candidate: p99 `0.36ms`, final score `6000`, `0 FP / 0 FN / 0 HTTP`, run `25947443368`.
- Official synced result is stale relative to current local candidate: official p99 around `1.45ms`, score `5839.52`, clean correctness.

### Load balancer

- Single C binary: `src/yolo_lb.c`.
- Modes:
  - `proxy`: TCP listener + UDS upstream connection per client + epoll byte proxying.
  - `fdpass`: TCP listener + persistent UDS control sockets + `SCM_RIGHTS` handoff.
- Build files:
  - `Makefile`
  - `Dockerfile`
  - `.github/workflows/build.yml`
- Important config knobs:
  - `LB_MODE`
  - `PORT`
  - `UPSTREAMS`
  - `BACKLOG`

---

## Cross-repo lessons

### 1. Transport mode changes the CPU budget math

The .NET default path uses proxy mode, so the LB copies request and response bytes and maintains one UDS backend connection per active TCP client. It needs enough CPU to avoid becoming the tail-latency bottleneck.

The C path uses fd-pass mode, so the LB does much less per request. It accepts, hands off the FD, and closes its copy. This lets more of the CPU budget go to API/search without starving the LB.

Practical consequence:

- Do **not** blindly copy C's LB CPU budget into .NET proxy mode.
- Do **not** assume .NET `docker-compose.fdpass.yml` is production-ready just because fd-pass works well in C.
- When comparing .NET vs C, record both transport mode and CPU split, otherwise the comparison is misleading.

### 2. Both backends are already in the same performance band

Current archived local evidence puts the two backends very close:

| candidate | latest local p99 | score | correctness |
|---|---:|---:|---|
| .NET proxy/hybrid | `0.37ms` | `6000` | clean |
| C fdpass/IVF-block8 | `0.36ms` | `6000` | clean |

The remaining gains are likely from tail shaving rather than broad architecture replacement.

### 3. Correctness is non-negotiable

Every approach below keeps the hard gate:

- final score `6000`
- `0` false positives
- `0` false negatives
- `0` HTTP errors
- no resource/rules violations

Small p99 wins are not worth a correctness regression.

---

## Approach inventory

### A. Benchmark/config approaches first

These are preferred before deeper code changes because they are cheap, reversible, and easy to isolate.

#### A1. Keep validating the .NET CPU split

Current best tested split is API `0.45` each / LB `0.10`.

Evidence so far:

- `0.48 / 0.04`: catastrophic proxy starvation; reverted.
- `0.45 / 0.10`: strongest repeated evidence; kept.
- `0.44 / 0.12`: correct but slower; reverted.
- conservative confirmation run `25948135170`: p99 reps `0.37, 0.36, 0.35ms`; median `0.36ms`; max `0.37ms`; score `6000`; `0 FP / 0 FN / 0 HTTP`.

Decision:

- the `0.45 / 0.10` split now passes the strong local promotion gate.
- treat this as the current .NET official-promotion candidate unless a same-window comparison or official-submission check finds a stale image/config issue.

#### A2. Threading and accept-loop matrix for .NET

Files:

- `docker-compose.yml`
- `src/WebApi/Program.cs`
- `src/WebApi/RawHttpServer.cs`

Variables:

- `ACCEPT_LOOPS=1/2/4`
- `MIN_WORKER_THREADS=32/64/128`
- optional `MAX_WORKER_THREADS` cap if runaway scheduling appears

Why:

- Current raw server uses blocking socket operations with configurable accept loops.
- Under a strict CPU quota, too many threads can increase scheduling/tail; too few can delay accepts or request handling.

Gate:

- one variable at a time,
- at least one 3-rep CI round per variant,
- immediately revert losers.

#### A3. LB-only benchmark matrix

Files:

- `.NET docker-compose.yml`
- `.NET docker-compose.fdpass.yml`
- C `docker-compose.yml`
- LB `src/yolo_lb.c`

Comparisons:

1. `.NET + proxy` with pinned current LB image.
2. `.NET + fdpass` only if the .NET FD receiving path is known-good and benchmarkable.
3. `C + fdpass` current baseline.
4. `C + proxy` only if the C API can expose a normal UDS HTTP listener for a fair proxy comparison.

Why:

- Separates backend search/parser cost from LB transport cost.
- Tells us whether fd-pass is worth investing in for .NET or whether proxy tuning is sufficient.

#### A4. Official projection refresh

Before official submission:

- fetch latest official preview data,
- verify current official entries for `jonathanperis-dotnet` and `jonathanperis-c`,
- verify submission branch image tags and CPU/memory limits,
- compare current local candidate image against official stale image.

Do this only after the candidate passes repeated local CI gates.

---

### B. .NET backend approaches

#### B1. Bucket fast-path tuning

Files:

- `docker-compose.yml`
- `src/WebApi/BucketSearchOptions.cs`
- `src/WebApi/BucketIndex.cs`

Knobs:

- `BUCKET_EARLY_CANDIDATES`
- `BUCKET_MIN_CANDIDATES`
- `BUCKET_MAX_CANDIDATES`
- `BUCKET_PROFILE_*`
- `BUCKET_REFERENCE_FASTPATH*`
- `BUCKET_EXACT_FALLBACK`
- `BUCKET_AVX_CUTOFF_DIMS`

Goal:

- increase direct bucket decisions,
- reduce expensive fallback work,
- preserve zero-error correctness.

Risk:

- high correctness risk if thresholds are loosened too far.

Recommended order:

1. instrument or infer fallback frequency,
2. test one knob at a time,
3. keep only variants that improve p99 and stay clean across repeated CI.

#### B2. Reduce hybrid fallback frequency/cost

Files:

- `src/WebApi/FraudScorer.cs`
- `src/WebApi/BucketIndex.cs`
- `src/WebApi/IvfIndex*.cs`

Goal:

- make the bucket fast path decide more requests;
- when fallback is required, make IVF repair as cheap as possible.

Experiments:

- tune IVF repair ranges and thresholds,
- evaluate `IVF_FAST_NPROBE` / `IVF_FULL_NPROBE`,
- test stricter 0-fraud and 5-fraud fast approve/deny distance thresholds.

Risk:

- medium/high correctness risk; run only after easier CPU/threading work plateaus.

#### B3. Parser/vectorizer micro-optimizations

Files:

- `src/WebApi/FraudRequestParser.cs`
- `src/WebApi/FraudScorer.cs`
- `src/WebApi/FraudVectorizer.cs`

Ideas:

- specialize scorer mode at startup so the hot path avoids repeated mode branching,
- precompute normalization reciprocals to replace divisions,
- review timestamp parsing and known-field scanning,
- keep prebuilt response dispatch as-is unless measurement shows a problem.

Risk:

- low/medium; correctness covered by parser/scorer tests plus CI benchmark.

#### B4. Index build/layout experiments

Files:

- `src/DataConverter/*`
- `src/WebApi/BucketIndex.cs`
- `src/WebApi/IvfIndex*.cs`

Ideas:

- tune bucket table generation to increase fast-path hit rate,
- adjust IVF cluster/list layout,
- experiment with quantization scale and training sample only under strict benchmark evidence.

Risk:

- high engineering cost and benchmark time; postpone until config/threading/parser gains plateau.

---

### C. C backend approaches to transfer or benchmark against

#### C1. Event-loop tail reduction

Files:

- `src/api/main.c`
- `tests/test_api_fdpass_immediate.c`

Ideas:

- drain client reads until `EAGAIN/EWOULDBLOCK` instead of returning after one positive read,
- add/verify `EPOLLRDHUP`,
- consider `EPOLLET` only after tests protect partial and pipelined requests.

Transfer value for .NET:

- Use as a comparison model for how much request-drain behavior affects p99.
- Do not directly port epoll logic to .NET, but use the same benchmark questions.

#### C2. Search hot-path tightening

Files:

- `src/common/search.c`
- `src/common/topk.h`
- `src/preprocess/build_index.c`

Ideas:

- fully vectorize lower-bound checks across all 14 dimensions,
- reduce stack stores and repeated top-5 worst-distance calls,
- compare block8 vs block16 layout if benchmark budget allows.

Transfer value for .NET:

- Use C as the lower-level reference for what search work remains unavoidable.
- If C gains from a layout idea, consider a managed/NativeAOT equivalent only after evidence.

#### C3. Search policy tuning

Files:

- C `docker-compose.yml`
- `src/common/index.c`
- `src/common/search.c`

Knobs:

- `INDEX_NPROBE`
- `INDEX_REPAIR_NPROBE`
- `INDEX_REPAIR_MIN_FRAUD`
- `INDEX_REPAIR_MAX_FRAUD`
- `INDEX_REPAIR*_WORST_THRESHOLD`
- `INDEX_EXACT_FALLBACK`

Transfer value for .NET:

- Match the conceptual C repair policy against .NET `IVF_*` and bucket fallback thresholds.
- Keep comparisons same-window because GitHub-hosted runners are noisy.

---

### D. Load-balancer approaches

#### D1. Proxy-mode tuning for .NET

File:

- `rinha4-lb-yolo-mode/src/yolo_lb.c`

Ideas:

- test larger proxy buffer sizes,
- reduce `epoll_ctl(EPOLL_CTL_MOD)` churn,
- evaluate connection object pooling instead of per-connection `calloc/free`,
- measure whether response/request sizes make buffer tuning relevant.

Risk:

- moderate; a bad LB image can harm both .NET and C comparisons. Pin immutable LB images for every run.

#### D2. fd-pass robustness and metrics

File:

- `rinha4-lb-yolo-mode/src/yolo_lb.c`

Ideas:

- expose or log failed handoff/retry counts in benchmark artifacts,
- verify whether the retry/sleep path is ever hit,
- test multiple upstream/control socket configurations.

Risk:

- low for C if isolated; higher for .NET unless FD receiving support is production-ready.

#### D3. Transport decision rule

Use this rule before investing more in fd-pass for .NET:

- If `.NET + proxy` at `0.45/0.10` continues to pass strong promote gates, prefer promotion over fd-pass rewrite.
- If .NET proxy tails remain unstable while C fd-pass stays stable in same-window runs, investigate .NET fd-pass or LB proxy-mode optimization.
- If proxy-mode starvation appears when LB CPU drops below `0.10`, do not reduce LB CPU further without a compensating LB change.

---

## Recommended execution order

1. **Finish conservative confirmation** for .NET `0.45/0.10` on current `main`.
2. If confirmed, decide whether to prepare official .NET promotion before more tuning.
3. Run a small `.NET` threading/accept-loop matrix.
4. Run one LB-only/proxy-mode experiment only if threading does not improve p99.
5. Compare against the C candidate in the same CI window before borrowing C mechanisms.
6. Only then attempt bucket/IVF/index code changes.

---

## Documentation cleanup found during analysis

Some .NET docs are stale relative to the current source/compose:

- Some docs still describe the runtime as IVF-only, but current default compose/source uses hybrid bucket/IVF.
- Some docs mention older CPU splits such as `0.42/0.16` or `0.46/0.08`; current candidate is `0.45/0.10`.

These should be cleaned up after the immediate benchmark/promotion decision so future context does not point to stale defaults.
