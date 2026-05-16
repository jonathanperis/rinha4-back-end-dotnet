# RonieNeubauer/rinha2026 top-1 audit

Date: 2026-05-16

Competitor repo: <https://github.com/RonieNeubauer/rinha2026>
Official issue audited: <https://github.com/zanfranceschi/rinha-de-backend-2026/issues/4682>
Local audit clone: `/opt/data/github/jonathanperis/competitor-ronieneubauer-rinha2026`

## Evidence lanes — keep separated

### Official-vs-official

Official issue `#4682` closed successfully for RonieNeubauer at submission commit `557ac41`.

| Participant | Official issue | Score | p99 | FP | FN | HTTP errors |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| RonieNeubauer/rinha2026 | `#4682` | `6000` | `0.86ms` | `0` | `0` | `0` |
| jonathanperis/rinha4-back-end-dotnet | `#4665` | `5959.7` | `1.10ms` | `0` | `0` | `0` |

Current official leaderboard snapshot had Ronie at top score `6000 / 0.86ms`; Jonathan's public entry is older/stale versus our current CI candidate.

### CI-vs-CI

Do **not** compare Ronie's official `0.86ms` directly to Jonathan's GitHub Actions p99.

Most recent corrected top-global CI comparison run: <https://github.com/jonathanperis/rinha4-back-end-dotnet/actions/runs/25967304281>

| Participant | Image/config note | p99 reps | Median p99 | Score | FP/FN/HTTP |
| --- | --- | ---: | ---: | ---: | ---: |
| jonathanperis | API `ci-18f04b...`; LB `ci-b5b0e3...` | `0.30/0.34/0.31ms` | `0.31ms` | `6000` | `0/0/0` |
| RonieNeubauer/rinha2026 | `ronieneubauer/rinha2026:2.0.0-preview13` | `0.33/0.30/0.31ms` | `0.31ms` | `6000` | `0/0/0` |
| fksegundo-rust | submitted mutable `latest` API/LB tags | `0.34/0.36/0.34ms` | `0.34ms` | `6000` | `0/0/0` |

Interpretation: our current CI candidate already competes with Ronie in the CI lane. The official lane is behind because our public submission result is stale, not because the current candidate is clearly slower.

## Submission/runtime shape

Ronie's submitted `origin/submission:docker-compose.yml` uses one image for LB and APIs:

- Image: `ronieneubauer/rinha2026:2.0.0-preview13`
- LB: `0.06 CPU / 30M`
- API1/API2: `0.47 CPU / 160M` each
- API env:
  - `WORKERS=2`
  - `RINHA_CPU=0` for API1 and `RINHA_CPU=2` for API2
  - `RINHA_MAX_PROBES=20`
  - `RINHA_FAST_PROBES=5`
  - `RINHA_CLUSTER_SCORE=centroid`
  - fraud-count threshold envs `RINHA_FAST_T0..T5`
- API privileges/tuning:
  - `security_opt: seccomp:unconfined`
  - `ulimits: rtprio: 99`, `memlock: -1`, `nofile: 65535`

Important correction: `info.json` and old README text still say `io_uring`, but the audited hot path in `src/server.c` is **not** io_uring. It is blocking C + pthread-per-connection with fd passing from the LB.

## Techniques actually used

### 1. FD-passing load balancer, not byte proxying

`src/lb.c` accepts TCP connections and passes accepted client sockets to API containers through Unix domain sockets with `SCM_RIGHTS`.

Why it matters:

- LB stays tiny: only accept, health handling, `sendmsg(SCM_RIGHTS)`, close local fd.
- API writes directly to the client TCP socket; no HTTP bytes traverse LB after handoff.
- This is the same high-level shape Jonathan already uses via `rinha4-lb-yolo-mode` + `FD_RAW=1`.

Ronie's LB CPU budget is only `0.06`, while Jonathan's current compose allocates `0.16` to LB and `0.42 + 0.42` to APIs. That is an actionable CPU-split experiment for us: Ronie's top official run says `0.47/0.47/0.06` can work.

### 2. Blocking pthread-per-connection server

`src/server.c` explicitly says no io_uring. One detached pthread owns one handed-off client socket and loops:

```text
blocking recv -> HTTP frame -> parse -> search -> pre-rendered response -> send
```

Ronie's reasoning in code comments: with <=250 keep-alive connections and roughly one in-flight request per connection, a worker blocked in `recv()` is woken directly by the kernel and avoids a single shared event-loop/reaction bottleneck.

Comparable Jonathan status:

- Jonathan is already raw fd handoff, but dispatches to .NET ThreadPool work items.
- Potential experiment: cap/shape .NET worker behavior to mimic one stable worker per connection more closely, or test dedicated long-running worker dispatch for accepted fds. This must be benchmarked carefully because .NET thread scheduling and GC interactions differ from C pthreads.

### 3. Real-time scheduling / wakeup-latency experiment

Ronie attempts `SCHED_FIFO` for worker threads when allowed by container capabilities:

- Compose grants `rtprio: 99` and `seccomp:unconfined`.
- Startup probes `sched_setscheduler(0, SCHED_FIFO, priority 10)`.
- Optional `RINHA_RT_MODE=wakeup` can keep FIFO only around blocking `recv()` wakeups, then run compute/send as `SCHED_OTHER`.

This targets recv-side wakeup latency, not distance-search cost.

Jonathan status:

- Current compose has `seccomp=unconfined` but no `rtprio`/`memlock` ulimits on API containers.
- .NET has no built-in managed scheduler mapping to this, but we can P/Invoke `sched_setscheduler` or test process/thread-level native tuning. Risk: starving k6/client/softirq can make official p99 worse or break health checks. Treat as a late-stage matrix, not first lever.

### 4. Locked/warmed memory

Ronie calls:

- `mlockall(MCL_CURRENT | MCL_FUTURE)` at server startup.
- `mmap(index.bin, MAP_PRIVATE | MAP_POPULATE)`.
- `mlock(index)`, `madvise(MADV_HUGEPAGE)`, `madvise(MADV_WILLNEED)`.
- 64 warmup searches before serving.

Goal: avoid minor page faults and cold search path stalls during measured traffic.

Jonathan status:

- Bucket index uses memory-mapped file and acquired pointer, but current audited code does not visibly call Linux `mlockall`, `madvise`, or a deterministic warmup loop over search paths before ready.
- This is one of the most promising low-risk .NET experiments: P/Invoke `mlockall`/`madvise` best-effort, and add startup warmup queries for current scorer mode before readiness.

### 5. Hand-written fixed-schema parser

Ronie's `src/parse.c` is schema-specific and avoids generic JSON:

- Scans only keys needed for feature vectorization.
- Parses numbers/timestamps manually.
- Checks `known_merchants` membership by searching for quoted merchant id inside the array slice.
- Ignores unknown fields via `skip_value`.

Jonathan status:

- Jonathan already has custom parser/vectorizer code, so there may be less low-hanging fruit here.
- Useful Ronie detail: membership test avoids materializing merchant arrays entirely. If Jonathan still allocates or does more structured parsing in this path, copy this exact-slice membership pattern.

### 6. IVF index shape: 2048 clusters + bbox pruning + AVX2 pair layout

Ronie's runtime index:

- `N_DIMS=14`
- `N_CLUSTERS=2048`
- `K_NEIGHBORS=5`
- int16 scale `10000`
- pair-packed AVX2 layout: 7 dimension pairs, each vector contributes two int16 lanes; `_mm256_madd_epi16` computes pairwise squared distances.
- per-cluster bbox min/max lower bounds.
- top-k stores `(distance, original_index)` as a packed key for deterministic tie-breaking.

Search flow:

1. Optional fast pass with `RINHA_FAST_PROBES=5`.
2. If decision is uncertain by fraud-count class or threshold, rerun with `RINHA_MAX_PROBES=20`.
3. For each query, compute bbox lower bound for all 2048 clusters.
4. Repeatedly select the currently lowest lower-bound cluster.
5. Stop when lower bound cannot beat current worst top-k distance or probe cap is hit.
6. Inside cluster scan, evaluate the dimensions most useful for early rejection first: pairs 3 and 5, then more pairs only for survivors.

Jonathan status:

- Current compose defaults are bucket-heavy (`SCORER_MODE=hybrid`) with IVF present but `IVF_FAST_NPROBE=1`, `IVF_FULL_NPROBE=1`, `IVF_BOUNDARY_FULL=false`.
- Build defaults in compose show `IVF_CLUSTERS=512`, much smaller than Ronie's 2048.
- Jonathan's bucket path likely explains current strong CI results, but Ronie shows a pure IVF path can hit top-1 official if the index/probe thresholds are well tuned.

### 7. Build-time verification gate

Ronie's Dockerfile builds `index.bin` from official `references.json.gz`, then runs:

```sh
./verify 2000 index.bin
```

The source also includes debug/verify tools to compare optimized search to brute force. This is essential because all the latency wins are unsafe unless false positives/false negatives remain zero.

Jonathan status:

- Keep our exact-vs-optimized gates and replay instrumentation as mandatory for every pruning/scheduler/index experiment.
- Do not accept a p99 improvement unless CI artifacts show `FP=0`, `FN=0`, `HTTP=0` and targeted verifier/replay passes.

## What this means for Jonathan

The claim is fair: if C can top-1, the .NET candidate can be top-1 too. The strongest evidence is actually the CI lane: Jonathan and Ronie tied at median `0.31ms` in the corrected 3-repetition comparison. The official gap is a stale-promotion problem first, then a tuning problem.

## Recommended action order

1. **Promote current candidate officially before rewriting scorer.**
   - Official #4665 tested old `ci-7e742...` and scored `1.10ms`.
   - Current CI candidate `ci-18f04b...` scored `6000 / 0.29ms` in our single official-like CI and tied Ronie in repeated CI.
   - This should be tested in official lane as an immutable submission image.

2. **CPU split matrix against Ronie's top-1 shape.**
   - Current Jonathan: APIs `0.42/0.42`, LB `0.16`.
   - Ronie: APIs `0.47/0.47`, LB `0.06`.
   - Test at least:
     - `0.47/0.47/0.06`
     - `0.45/0.45/0.10`
     - current `0.42/0.42/0.16`
   - Keep CI-vs-CI only; run multiple repetitions.

3. **Memory residency/warmup experiment.**
   - Add best-effort Linux P/Invokes for `mlockall`, `madvise(MADV_WILLNEED|MADV_HUGEPAGE)` where applicable.
   - Warm the active scorer path with deterministic synthetic queries before `/ready` succeeds.
   - Verify startup still passes bot health.

4. **Thread/wakeup experiment.**
   - First test API ulimits `rtprio: 99`, `memlock: -1` without code changes.
   - Then consider native `sched_setscheduler` P/Invoke for worker threads/process.
   - Treat as risky: can starve k6 or kernel softirq and hurt official p99.

5. **IVF search experiment only after promotion/CPU/memory.**
   - Build a Ronie-like IVF variant: 2048 clusters, bbox lower-bound ordering, fast 5 probes then capped 20 full probes with fraud-class thresholds.
   - Use exact verifier/replay gates before any CI comparison.
   - Do not abandon current bucket path unless IVF beats it in repeated CI with zero correctness failures.

## Bottom line

Ronie's top-1 is not magic C syntax. It is a combination of:

- fd-pass LB with tiny CPU budget;
- direct blocking socket ownership in the API;
- resident/warmed mmap index;
- exact fixed-schema parser;
- highly tuned IVF index and AVX2 lower-bound pruning;
- optional RT wakeup scheduling;
- strict verification gates.

Jonathan already has several of the same primitives and, in CI-vs-CI, is already top-1 competitive. The next move should be official promotion of the latest immutable candidate, then CPU split and memory residency experiments before deeper scorer rewrites.
