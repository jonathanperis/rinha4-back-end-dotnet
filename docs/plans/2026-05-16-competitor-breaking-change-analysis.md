# Competitor cross-analysis: breaking-change opportunities

Date: 2026-05-16

Scope:

- **Ours:** `jonathanperis/rinha4-back-end-dotnet` at `481e621379b5` locally after fast-forward from origin.
- **Competitor A:** `daniloitagyba/rinha-2026-dotnet` at `93c4f5963962` (`origin/submission` currently `8e47a3db18d3`).
- **Competitor B:** `pedrosakuma/rinha-backend-2026` at `5dbe3164bf03` (`origin/submission` currently `ed213ef44e31`).

Intent: identify **breaking changes** we could accept in our internals/submission shape to copy what works in the competitors while making our stack more efficient. "Breaking" here means changes that may invalidate current data files, env names, Docker topology, or exact local workflow, not breaking the public `/fraud-score` contract.

## Current high-level comparison

| Area | Ours | Danilo competitor | Pedro competitor | Takeaway |
|---|---|---|---|---|
| Runtime | .NET AOT raw socket server, two API containers, custom C LB | .NET raw socket server, two API containers, custom C LB | .NET/Kestrel default on `main`, raw mode exists in submission; external LB image | Everyone avoids full ASP.NET model binding; Danilo goes furthest by FD-passing accepted client sockets. |
| LB/API link | Main compose: proxy over UDS. Overlay: fdpass exists but not default. | FD-passing is default in compose/submission. | UDS proxy default on `main`; submission has raw API with UDS. | Our `docker-compose.fdpass.yml` exists; biggest low-risk topology test is to make FD-pass the submission default. |
| CPU allocation | Main: API `0.45 + 0.45`, LB `0.10`; cpuset APIs on `0`,`1`, LB `2,3`. FD-pass overlay: APIs `0.44 + 0.44`, LB `0.12`. | APIs `0.44 + 0.44`, LB `0.12`; cpuset overlaps LB `0,2` with API cores. | Main: APIs `0.42 + 0.42`, LB `0.16`; isolated cpuset. Submission: APIs `0.425 + 0.425`, LB `0.15`. | Our LB is starved in main compose vs competitors. FD-pass should pay for itself only if LB gets ~0.12-0.16 CPU. |
| JSON parse/vectorization | Custom canonical fast parser with fallback to `Utf8JsonReader`, produces `FraudInput`, then quantizes. | Hand slice parser, no STJ fallback on normal path. | `Utf8JsonReader` parser directly writes Q16/float spans. | We already have advanced parser. Next breakage is fusing parse + quantize to write Q16 directly and removing the intermediate struct/fallback from the hot path. |
| Scorer | Hybrid: bucket fast paths + IVF fallback. Bucket uses int16 vectors, profile/reference fast paths, risky exact fallback. IVF scans int16 blocked arrays. | Single binary index file with many extension sections: profile masks/counts, risky mapped/fine/SoA, native AVX2 ANN. Tuned ANN window ~9.8k-11k candidates. | IVF-blocked: centroid/Q8 scan, Q16/float rerank, early-stop, borderline expansion to 32 cells / 128 rerank. | Our current correctness win comes from bucket/risky fallback; performance cost likely in managed int16 bucket scans and broader candidate windows. |
| Data loading | Bucket index mmapped; IVF loads arrays into managed memory. | One mmapped index; optional hugepage `madvise`, prefault, native AVX2. | Dataset mmapped/prefetched/mlocked with THP advice. | Break the data format to keep all hot arrays mmapped/SoA/aligned and avoid loading IVF into managed arrays. |
| Observed benchmark evidence | Latest candidate JSON at `7cb3a3067489`: p99 `0.34ms`, final `6000`, no errors on candidate runner. Latest official issue #2088: p99 `2.01ms`, final `3031.24`, with 697 FP / 574 FN. | No local report parsed here. | No local report parsed here. | Candidate runner says our stack can be perfect under one dataset/config; official-like run still missed accuracy/latency. Prioritize changes that preserve candidate correctness but reduce official p99 variance. |

## Recommended breaking changes, in priority order

### 1) Make FD-pass the primary submission topology

**What changes**

- Stop treating `docker-compose.fdpass.yml` as an optional overlay.
- Make `BIND_ADDR=fd:/sockets/apiN.sock.ctrl` and `LB_MODE=fdpass` the default submission path.
- Align resource defaults with competitors: APIs around `0.44` each, LB `0.12`-`0.16`, memory roughly `156MB + 156MB + 30/32MB`.
- Keep UDS proxy compose only as a debug/local compatibility mode.

**Why it matches competitors**

- Danilo uses FD-pass as the default in both main/submission. The LB accepts the external TCP client and passes the accepted fd to an API over a control UDS. This removes an entire proxying data path between LB and API.
- Pedro uses UDS proxy on `main`, but its submission/raw knobs still optimize the API/LB handoff and give the LB more CPU than our main compose.

**Why it can be more efficient**

- One less read/write/copy hop in the LB for each request/response.
- API receives the real client fd and can serve synchronously from the raw socket loop.
- Our code already supports fd-pass, so this is mostly a topology/config break, not a new architectural bet.

**Risk**

- Correctness risk: low if fd passing is already smoke-tested.
- Benchmark risk: medium. FD-pass only helps if the LB implementation and accepted socket lifetime are correct under high concurrency. It may regress if control socket receiver loops or ThreadPool dispatch become bottlenecks.
- Operational break: default compose changes; local users expecting `/sockets/apiN.sock` proxy mode need the legacy compose.

**Validation**

- Compare `docker-compose.yml` vs fd-pass default for at least 5 candidate rounds.
- Track p50/p99, non-2xx/errors, and whether API CPU increases while LB CPU decreases.

### 2) Add a raw-fd API path to avoid wrapping passed fds in `Socket`

**What changes**

- Danilo added `FD_RAW=1`: passed fds can be handled by native/low-level fd read/write code instead of `new Socket(new SafeSocketHandle(...))`.
- Our `ReceiveFdLoop` always wraps the fd into `Socket` and then uses `Socket.Receive`/`Send`.
- Breaking internal option: introduce `FD_RAW=1` with P/Invoke `read`, `write`/`send`, `close`, and a fd-based `HandleConnection(int fd)` path using the same HTTP parser/scorer.

**Why it matches competitors**

- Danilo's submission has `FD_RAW=1` and its code branches before Socket allocation/wrapping.
- This is exactly targeted at the hot path exposed by FD-pass.

**Why it can be more efficient**

- Avoids `Socket` object allocation and SafeHandle overhead per connection.
- Avoids some managed socket state/configuration work.
- Keeps the synchronous raw server shape while shortening the per-accepted-connection setup.

**Risk**

- Correctness risk: medium/high. We must handle partial writes, EINTR/EAGAIN behavior, fd ownership, connection close, and Linux-only behavior correctly.
- Portability break: Linux-only path; acceptable for official Rinha containers but should keep Socket fallback for local debug.

**Validation**

- First implement behind `FD_RAW=0` default, then candidate compare `fdpass+Socket` vs `fdpass+rawfd`.
- Add a small local fd integration smoke if Docker daemon access is available; otherwise rely on GitHub benchmark workflow.

### 3) Break scorer index format into one mmapped, extension-section file

**What changes**

- Consolidate bucket, exact/risky, profile/reference fast paths, IVF metadata, and optional SoA/native sections into one versioned file similar to Danilo's `references.idx`.
- Keep sections self-describing: base vectors/labels, bucket offsets/items, profile counts/masks/fraud counts, neighbor orders, risky fine/SoA vectors, IVF orders/blocks, reference fast paths.
- This breaks current `references.bin`, `references.bucket.bin`, `references.ivf.bin` expectations and Docker envs (`EXACT_PATH`, `BUCKET_PATH`, `IVF_PATH`). Replace with `INDEX_PATH`.

**Why it matches competitors**

- Danilo's `BinaryIndex` maps one file and reads extension sections for profile/risky/native/IVF/block scan features.
- Pedro maps/prefetches dataset arrays rather than deserializing all hot data into managed arrays.

**Why it can be more efficient**

- One file descriptor and one page-cache object shared by both API containers' overlay layers.
- Allows `madvise(MADV_HUGEPAGE)` / prefaulting on one contiguous region.
- Avoids startup allocations and GC pressure from IVF managed arrays (`centroids`, `bbox`, `labels`, `ids`, `blocks`).
- Easier to add native AVX2 scan over stable memory addresses.

**Risk**

- Correctness risk: medium. Index writer/reader migration can silently alter quantization, labels, or offsets if not heavily validated.
- Build break: all data generation scripts and Dockerfile need migration.
- Memory risk: one large file may need careful section alignment and optional sections to stay under 350MB total.

**Validation**

- Add a converter self-test: for a fixed sample of requests, old split-index scorer and new unified-index scorer must produce identical `fraud_count`.
- Compare memory RSS and startup time before any benchmark run.

### 4) Fuse JSON parse + Q16 quantization, remove `FraudInput` from the hot path

**What changes**

- Instead of `FraudRequestParser.Parse(body)` returning `FraudInput` then `FraudScorer.QuantizeRequest`, parse directly into `Span<short> qv` using loaded normalization/MCC tables.
- The parser still computes `UnknownMerchant`, timestamp/hour/day, and last-transaction deltas, but writes final Q16 coordinates immediately.
- Keep the current `Utf8JsonReader` fallback only for diagnostics or behind `STRICT_JSON_FALLBACK=1`; the benchmark's canonical shape should stay on the hand parser.

**Why it matches competitors**

- Pedro's `JsonVectorizer.VectorizeJsonQ16` writes Q16 directly into a stack span and skips intermediate model objects.
- Danilo's parser returns a lightweight ref struct and then feeds query construction; the shape is similarly allocation-free.

**Why it can be more efficient**

- Removes the `FraudInput` copy/load layer from every request.
- Removes repeated divisions/conditionals split across parser and scorer by colocating normalization writes.
- Opens the door to a stricter canonical parser with fewer fallback branches.

**Risk**

- Correctness risk: medium. The current parser has safety fallbacks; direct quantization makes parse bugs immediately become scoring bugs.
- Maintainability break: scoring code becomes less separated from HTTP/request parsing.

**Validation**

- Build a vector equivalence test: old parser+quantizer vs new fused parser must match all 14 Q16 values for all sample payloads and generated fuzz variants that preserve canonical field values/order variants.

### 5) Port Pedro-style IVF two-stage/borderline expansion into our IVF path

**What changes**

- Our IVF currently supports fast/full nprobe plus boundary repair by fraud-count range, but default compose has `IVF_FAST_NPROBE=1`, `IVF_FULL_NPROBE=1`, `IVF_BOUNDARY_FULL=false`.
- Add explicit Pedro-style behavior: initial `nprobe=1`, if preliminary top-5 count is borderline (`2` or `3`) expand to `nprobe=32` and rerank a larger pool (`128`).
- Make this available for `SCORER_MODE=ivf` and possibly for hybrid fallback decisions.

**Why it matches competitors**

- Pedro's main compose runs `IVF_NPROBE=1`, `IVF_BORDERLINE_NPROBE=32`, `IVF_BORDERLINE_RERANK=128`, `IVF_EARLY_STOP=1`, `IVF_EARLY_STOP_PCT=25`, `IVF_BBOX_REPAIR=1`.

**Why it can be more efficient**

- Easy queries pay for one nearby cell only.
- Ambiguous threshold cases get more recall only when needed.
- Could replace some expensive bucket/risky exact fallback calls if IVF confidence is good.

**Risk**

- Correctness risk: medium/high if used as replacement for bucket/risky fallback; lower if used only as another fallback path.
- Performance risk: if many official queries are borderline, the expansion tail can dominate p99.

**Validation**

- Instrument distribution: percentage of requests with fraud count `2/3`, added scanned cells, and whether final output changes.
- Run multi-round benchmarks because p99 tail is sensitive.

### 6) Add native AVX2 scanner for the managed bucket/risky hot loops

**What changes**

- Our bucket/risky path is managed C# with intrinsics and pointer arithmetic.
- Add optional native C AVX2 functions for candidate scanning, similar to Danilo's `src/native/rinha_native.c`:
  - compute int16 L2 over 14 dimensions padded to 16,
  - maintain top-5 distances/labels,
  - early exit after min/max/strong decision,
  - fine risky scan with coarse/fine bounds.

**Why it matches competitors**

- Danilo uses native AVX2 for ANN/risky fine paths (`NATIVE_ANN=1`, `RISKY_NATIVE_FINE=1`).

**Why it can be more efficient**

- Removes some JIT/intrinsics overhead and bounds/check artifacts from the tightest loop.
- Native can work directly on mmapped section pointers after unified index migration.
- Easier to hand tune insertion/top-k and memory layout.

**Risk**

- Correctness risk: high. One off-by-one in candidate windows or top-k tie ordering can create false positives/negatives.
- Build break: Dockerfile and AOT/native library loading become more complex.
- Architecture break: AVX2-only linux/amd64 path; must hard-fail or fallback clearly.

**Validation**

- Differential test native vs managed on thousands/millions of generated vectors and queries.
- Only benchmark after exact top-5/fraud-count equivalence is proven for non-approx sections.

### 7) Rebalance ThreadPool/worker model around fixed per-core workers

**What changes**

- Our compose defaults to `MIN_WORKER_THREADS=64` and `ACCEPT_LOOPS=2` per 1-core API container.
- Pedro's main pins `TP_MIN_WORKERS=4`, `TP_MAX_WORKERS=4` for Kestrel on isolated single-core APIs; submission uses `64` with raw mode.
- Danilo uses `WORKERS=2`, `TP_MIN_THREADS=64`.
- Breaking experiment: make worker/ThreadPool settings mode-specific, e.g. `raw+fdpass` uses fixed receive loops and a bounded worker count; UDS proxy/debug can keep broad ThreadPool.

**Why it can be more efficient**

- Reduces context switching on 0.44-core cgroups if too many ThreadPool workers are active.
- Can reduce p99 jitter when CPU-bound scoring saturates a single API core.

**Risk**

- Performance risk: benchmark-dependent. Too few workers hurts socket concurrency; too many workers hurts p99. This must be swept, not guessed.

**Validation**

- Sweep `(ACCEPT_LOOPS, MIN_WORKER_THREADS, MAX_WORKER_THREADS)` across candidate runner: e.g. `(1,4,4)`, `(1,16,16)`, `(2,64,unset)`, `(2,64,64)`, with fd-pass fixed.

## Changes I would **not** copy blindly

1. **Pedro's Kestrel hot path as a replacement for our raw server.** It is clean and optimized, but we already have a raw server. Switching back to Kestrel is a large break with uncertain upside.
2. **Danilo's overlapping LB/API cpusets without measurement.** It may help fd-pass by colocating LB handoff with API cores, but it can also add CFS contention. Test isolated vs overlapped.
3. **Approximate-only classifier shortcuts that trade correctness for latency.** Our latest candidate run reached no FP/FN; official losses mean we need controlled accuracy diagnostics, not more unverified approximation.
4. **Native AVX2 before data/layout equivalence tests exist.** It is attractive but dangerous without a differential harness.

## Proposed execution sequence

1. **Topology first:** promote fd-pass to a candidate compose variant and run multi-round benchmark vs current main compose.
2. **Raw-fd second:** add `FD_RAW=1` behind default-off, compare only after fd-pass baseline is stable.
3. **Parser fusion:** implement direct Q16 parser with equivalence tests; no benchmark until vector equivalence is exact.
4. **IVF borderline experiment:** port Pedro-style borderline expansion behind env flags; compare accuracy deltas against current hybrid output.
5. **Unified index/native scan:** only after fast topology/parser wins are exhausted, because this is the biggest migration.

## Expected efficiency/risk matrix

| Change | Expected efficiency upside | Break size | Correctness risk | Best next proof |
|---|---:|---:|---:|---|
| FD-pass default + LB CPU 0.12-0.16 | High for p99/CPU | Medium | Low | Candidate A/B compose benchmark |
| Raw-fd API path | Medium | Medium | Medium/high | fd smoke + benchmark |
| Fused parse→Q16 | Medium | Medium | Medium | vector equivalence tests |
| Unified mmapped index | Medium/high memory/startup + enables native | High | Medium | old/new scorer equivalence |
| Pedro borderline IVF | Medium accuracy/latency balance | Medium | Medium/high | distribution + output diff |
| Native AVX2 bucket/risky | Medium/high loop speed | High | High | native/managed differential harness |
| ThreadPool bounded workers | Low/medium p99 jitter | Low | Low | parameter sweep |

## Bottom line

The best near-term breaking move is not to rewrite the scorer first. We already have a strong scorer and fd-pass support. The competitor evidence says we should **make fd-pass the default submission shape, give the LB more CPU, and then remove the remaining fd wrapping overhead**. After that, the next efficient break is **fusing parse directly into Q16**. The larger scorer/index breaks should be staged behind differential tests because they can easily recover latency while losing official accuracy.
