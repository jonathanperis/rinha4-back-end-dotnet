# Improvement Rounds Before Official Promotion Implementation Plan

> **For Hermes:** Use subagent-driven-development skill if this plan is later executed task-by-task. For this repo, commit directly to `main` unless Jonathan asks for a branch.

**Goal:** Improve the current `rinha4-back-end-dotnet` candidate before deciding whether to promote a new official Rinha submission.

**Architecture:** Treat the official runner as unavailable/noisy and use this repo's GitHub Actions benchmark workflow as the projection harness. Work in short, isolated experiments, each producing a pushed commit and repeated CI benchmark evidence before any official promotion decision.

**Tech Stack:** .NET NativeAOT WebApi, raw Unix-socket HTTP server, hybrid bucket/IVF scorer, C yolo-mode load balancer image, Docker Compose resource limits, GitHub Actions official-like benchmark workflow.

---

## Evidence Baseline

### Current official leaderboard state

Official data source checked: `https://rinhadebackend.com.br/` -> `https://raw.githubusercontent.com/arinhadebackend/arinhadebackend.github.io/2026-preview/results-preview.json`.

Current official entry:

| entry | official rank by p99 | p99 | score | correctness | issue |
|---|---:|---:|---:|---|---|
| `jonathanperis (jonathanperis-dotnet)` | `#30 / 211` | `1.53ms` | `5816.39` | `0 FP / 0 FN / 0 HTTP` | `zanfranceschi/rinha-de-backend-2026#4600` |

Official runtime information for the current leaderboard entry:

- official tested submission commit: `1d15593`
- WebApi image: `ghcr.io/jonathanperis/rinha4-back-end-dotnet:ci-a166a2375254bbe00e80207f56a37c50774e9b63`
- LB image: `ghcr.io/jonathanperis/rinha4-lb-yolo-mode:ci-b5b0e375ca9c9c39152950ddffbbc5ce6a7bd92e`
- API CPU: `0.42` each
- LB CPU: `0.16`
- API memory: `160MB`
- LB memory: `30MB`

### Current local candidate

Current repo state before this plan:

- `main` HEAD at inspection time: `fb80d11 docs: archive rinha benchmark`
- latest performance code commit: `46c98c8 perf: shift cpu budget back to api`
- current compose split: API `0.46` each, LB `0.08`
- current LB image: `ghcr.io/jonathanperis/rinha4-lb-yolo-mode:ci-b5b0e375ca9c9c39152950ddffbbc5ce6a7bd92e`

Current local evidence for `0.46 / 0.08`:

| evidence | p99 reps / result | correctness |
|---|---:|---|
| one-off candidate archive | `0.36ms` | clean |
| one-off calibrated archive | `0.38ms` | clean |
| 3-rep round 1 | `0.40, 0.45, 0.35ms`, median `0.40ms` | clean |
| 3-rep round 2 | `0.39, 0.41, 0.42ms`, median `0.41ms` | clean |
| combined 6 reps | median `0.405ms`, max `0.45ms` | clean |

Projection caveat: official/local multiplier from previous submission is roughly `3.73x-3.92x`, putting latest candidate around `~1.34-1.59ms` depending on whether one-off or repeated-median evidence transfers.

### Current local limitation

Docker CLI/Compose exist on this host, but Docker daemon access is unavailable from this user. Use local build/unit/guard checks plus GitHub Actions for end-to-end k6 evidence.

---

## Verified Mechanisms

### Our implementation

- `docker-compose.yml` defines two WebApi containers and one yolo-mode LB container using Unix sockets under `/sockets`.
- `src/WebApi/RawHttpServer.cs` implements a raw allocation-light HTTP/1 server with Unix socket support, configurable `ACCEPT_LOOPS`, and prebuilt responses.
- `src/WebApi/FraudScorer.cs` supports `exact`, `ivf`, `bucket`, and `hybrid`; current compose default is `hybrid`.
- Hybrid mode uses bucket fast path first and falls back to IVF when bucket cannot decide.
- `src/WebApi/BucketIndex.cs` includes profile fast-path, reference fast-path, second reference fast-path, and risky exact/fine fallback mechanisms.
- `src/WebApi/IvfIndex*.cs` is the fallback/alternative ANN path with `IVF_*` tunables.
- `scripts/ci-official-benchmark.sh` is the core official-like CI runner invoked by `.github/workflows/benchmark.yml`.
- Benchmark workflow supports explicit inputs for scorer/tunables, CPU overrides, `benchmark_repetitions`, and archive report lanes.

### Competitor/leaderboard context

Top current official references by p99:

| rank | entry | p99 | correctness |
|---:|---|---:|---|
| #1 | `fksegundo-rust` | `0.83ms` | clean |
| #2 | `crepao-da-massa/silent-index` | `0.98ms` | clean |
| #7 | `daniloitagyba/itagyba-dotnet` | `1.08ms` | clean |
| #15 | `pedrosakuma-dotnet` | `1.20ms` | clean |
| #24 | `jonathanperis-c` | `1.44ms` | clean |
| #30 | `jonathanperis-dotnet` | `1.53ms` | clean |

---

## Hypotheses to Validate, Ranked

1. **Resource split has a narrow optimum.** `0.46 / 0.08` is better than `0.42 / 0.16`, but there may be a nearby optimum around `0.44-0.48 API` and `0.04-0.12 LB`.
2. **Threading/accept-loop settings may dominate p99 at official scale.** Current defaults (`ACCEPT_LOOPS=2`, `MIN_WORKER_THREADS=64`, socket inline completions on) may be close but not proven optimal under 1 CPU total.
3. **Bucket tunables may trade a little accuracy margin for lower tail.** Candidate thresholds (`BUCKET_*`, fast-path flags, AVX cutoff, exact fallback mode) should be tested only with strict zero-error gates.
4. **Parser/vectorizer micro-optimizations may reduce tail without correctness risk.** Focus on timestamp parsing, quantization divisions, and avoiding repeated mode/scale branching on the hot path.
5. **IVF fallback thresholds may reduce expensive fallback work.** Validate `IVF_*` thresholds and repair ranges after bucket tuning; correctness risk is higher.
6. **Index layout/build changes can be high leverage but expensive.** Only attempt after cheaper CI-matrix experiments plateau.

---

## Hard Gates

### Correctness gate

Every candidate benchmark round must have:

- final score: `6000`
- false positives: `0`
- false negatives: `0`
- HTTP errors: `0`
- no official rules/resource violations

### Local CI promotion gate

A candidate can be considered for official promotion only if it satisfies one of these:

- **Strong promote:** two separate 3-rep CI rounds with median `<= 0.39ms` and max `<= 0.43ms`, all clean.
- **Moderate promote:** two separate 3-rep CI rounds with median `<= 0.40ms` and max `<= 0.45ms`, all clean, and it improves one-off candidate/calibrated archive over current `0.36/0.38` or has a compelling code-risk reason.
- **Do not promote:** any correctness failure, median `> 0.41ms`, max `> 0.46ms`, or no improvement over current `0.46 / 0.08` after repeated runs.

### Official promotion guard

Before opening or updating an official submission issue:

1. Verify `submission` branch compose/images, not just `main`.
2. Pin immutable WebApi and LB image tags; do not submit `:latest`.
3. Verify compose parses.
4. Record exact image tags, commit, and intended CPU/memory split in the report.

---

## Task 1: Refresh Baseline and Benchmark Harness

**Objective:** Re-anchor current `main`, workflow inputs, and extraction scripts before changing anything.

**Files:**
- Read: `.github/workflows/benchmark.yml`
- Read: `scripts/ci-official-benchmark.sh`
- Read: `docker-compose.yml`
- No code changes expected.

**Steps:**

1. Ensure repo is clean and current:
   ```bash
   cd /opt/data/github/jonathanperis/rinha4-back-end-dotnet
   git checkout main
   git pull --ff-only origin main
   git status --short --branch
   ```
   Expected: clean `main...origin/main`.

2. Verify compose split and images:
   ```bash
   grep -nE 'image:|pull_policy|cpus:|memory:' docker-compose.yml
   ```
   Expected: WebApi `0.46`, LB `0.08`, known immutable LB image.

3. Record current benchmark workflow inputs:
   ```bash
   GH=/opt/data/.local/share/mise/installs/gh/latest/gh_2.92.0_linux_amd64/bin/gh
   $GH workflow view "Official-like Benchmark" --yaml | sed -n '1,180p'
   ```

4. Commit: no commit unless files changed.

---

## Task 2: Run Control Round on Current Candidate

**Objective:** Establish a fresh same-day control before experimenting.

**Files:**
- No code changes expected.
- Artifacts: GitHub Actions run and downloaded `/tmp/benchmark-<run-id>` artifacts.

**Steps:**

1. Dispatch 3-rep benchmark on current `main`:
   ```bash
   GH=/opt/data/.local/share/mise/installs/gh/latest/gh_2.92.0_linux_amd64/bin/gh
   cd /opt/data/github/jonathanperis/rinha4-back-end-dotnet
   $GH workflow run "Official-like Benchmark" --ref main \
     -f compose_file=docker-compose.yml \
     -f official_ref=main \
     -f k6_image=grafana/k6:latest \
     -f webapi_image="" \
     -f scorer_mode=hybrid \
     -f report_kind=candidate \
     -f benchmark_repetitions=3
   ```

2. Watch the run:
   ```bash
   $GH run watch <RUN_ID> --interval 30 --exit-status
   ```

3. Download artifacts into separate directories and parse all `results-repetition-*.json` files.

4. Record p99 reps, median, max, correctness, run URL.

5. Gate: if control median is much worse than previous `0.405ms`, pause and run another control before experimenting.

---

## Task 3: CPU Split Micro-Matrix

**Objective:** Validate whether a nearby CPU split beats `0.46 / 0.08` without code changes.

**Files:**
- Modify per experiment: `docker-compose.yml`

**Experiment order:**

1. `0.48 API / 0.04 LB`
2. `0.45 API / 0.10 LB`
3. `0.44 API / 0.12 LB`
4. optional if needed: `0.47 API / 0.06 LB`

**Steps for each split:**

1. Patch only these lines in `docker-compose.yml`:
   - WebApi deploy limit `cpus: "<api>"`
   - LB deploy limit `cpus: "<lb>"`
   - preserve total: `2 * API + LB = 1.00`

2. Verify compose config if Docker daemon is reachable; otherwise at least syntax-inspect and rely on CI.

3. Build WebApi locally:
   ```bash
   cd /opt/data/github/jonathanperis/rinha4-back-end-dotnet
   dotnet build src/WebApi/WebApi.csproj -c Release
   ```
   Expected: success.

4. Commit directly to main:
   ```bash
   git add docker-compose.yml
   git commit -m "perf: tune cpu split to <api>-api <lb>-lb"
   git push origin main
   ```

5. Run 3-rep CI benchmark using Task 2 command.

6. Parse artifacts and compare against control.

**Decision:** Keep only splits that beat current control by repeated median and pass hard gates. If a split regresses, revert it immediately and push the revert so `main` does not drift to a bad candidate.

---

## Task 4: Threading / Accept Loop Matrix

**Objective:** Test low-risk runtime knobs that may affect tail latency under CPU quota.

**Files:**
- Modify: `docker-compose.yml`

**Experiment order:**

1. `ACCEPT_LOOPS=1`, `MIN_WORKER_THREADS=64`
2. `ACCEPT_LOOPS=2`, `MIN_WORKER_THREADS=32`
3. `ACCEPT_LOOPS=1`, `MIN_WORKER_THREADS=32`
4. optional: `KEEP_ALIVE_MAX` small cap only if logs/artifacts suggest long-lived connections cause tail spikes.

**Steps for each variant:**

1. Patch only environment defaults in both WebApi service definitions.
2. Build WebApi locally.
3. Commit/push focused change.
4. Run one 3-rep benchmark.
5. Keep only if it beats control and stays clean; otherwise revert.

**Risk note:** `ACCEPT_LOOPS` and ThreadPool settings can produce noisy results. Require two 3-rep confirmation rounds before promotion if this area wins.

---

## Task 5: Bucket Tunable CI Matrix Without Code Changes

**Objective:** Search existing `BUCKET_*` env-space before writing risky scorer code.

**Files:**
- Modify: `docker-compose.yml`
- No C# code changes.

**Experiment groups:**

1. Candidate count trims:
   - `BUCKET_EARLY_CANDIDATES=9000`, `BUCKET_MIN_CANDIDATES=15000`, `BUCKET_MAX_CANDIDATES=22500`
   - `BUCKET_EARLY_CANDIDATES=8500`, `BUCKET_MIN_CANDIDATES=14500`, `BUCKET_MAX_CANDIDATES=22000`

2. AVX cutoff:
   - `BUCKET_AVX_CUTOFF_DIMS=5`
   - `BUCKET_AVX_CUTOFF_DIMS=7`

3. Fallback mode:
   - `BUCKET_EXACT_FALLBACK=true`
   - compare against current `risky`

**Steps for each group:**

1. Change one group at a time in compose defaults.
2. Build locally.
3. Commit/push.
4. Run a 3-rep benchmark.
5. If any FP/FN appears, revert immediately and mark the area unsafe.
6. If clean and faster, run a second 3-rep confirmation round.

---

## Task 6: Hot-Path Code Review and Micro-Optimization Spike

**Objective:** Identify one or two safe C# hot-path improvements to test after config space plateaus.

**Files to inspect:**
- `src/WebApi/FraudRequestParser.cs`
- `src/WebApi/FraudVectorizer.cs`
- `src/WebApi/FraudScorer.cs`
- `src/WebApi/RawHttpServer.cs`
- `src/WebApi/HttpWire.cs`
- `src/WebApi/HttpResponses.cs`

**Candidate improvements:**

1. Hoist scorer `scale`/mode-specific delegates out of `ScoreFraudRequest` switch if measurable and NativeAOT-friendly.
2. Reduce divisions in `QuantizeRequest` by storing inverse constants in `FraudScorer` constructor.
3. Review timestamp parser for unnecessary branches or duplicate parsing.
4. Review `HttpWire.GetContentLength`/header scanning for fixed k6 header patterns.
5. Avoid any logging or exception paths in normal requests.

**Steps:**

1. Make one code change at a time.
2. Run local build:
   ```bash
   dotnet build src/WebApi/WebApi.csproj -c Release
   ```
3. Run existing probes/tests if applicable:
   ```bash
   dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release
   dotnet run --project tests/AccuracyProbe/AccuracyProbe.csproj -c Release -- --help
   ```
   Adjust `AccuracyProbe` args after inspecting its CLI.
4. Commit/push.
5. Run one 3-rep benchmark; if promising, run second 3-rep confirmation.

**Risk note:** prioritize mechanical math/branch reductions. Avoid algorithmic behavior changes unless protected by CI correctness evidence.

---

## Task 7: IVF Fallback Threshold Experiments

**Objective:** Test whether IVF fallback/repair thresholds can reduce tail while preserving zero errors.

**Files:**
- Modify: `docker-compose.yml`

**Experiment order:**

1. Raise/lower `IVF_ZERO_FAST_APPROVE_WORST_DISTANCE` around current `5000000`.
2. Raise/lower `IVF_FIVE_FAST_DENY_WORST_DISTANCE` around current `4000000`.
3. Narrow `IVF_REPAIR_MIN_FRAUDS` / `IVF_REPAIR_MAX_FRAUDS` only if earlier runs show no correctness risk.
4. Avoid increasing `IVF_FULL_NPROBE` unless correctness requires it; likely slower.

**Steps:**

1. Change one threshold family at a time.
2. Build locally.
3. Commit/push.
4. Run 3-rep CI benchmark.
5. Revert on any correctness failure.

---

## Task 8: Promotion Decision Report

**Objective:** Summarize evidence and decide whether to promote a new official candidate.

**Files:**
- Optional create: `docs/plans/2026-05-15-improvement-rounds-results.md`

**Report table must include:**

| candidate | commit | split/tunables | round 1 p99s | round 2 p99s | combined median | max | correctness | decision |
|---|---|---|---:|---:|---:|---:|---|---|

**Decision labels:**

- `promote`: beats current `0.46 / 0.08` by promotion gate and is correctness-clean.
- `hold`: no clear gain; keep current local candidate and do not submit.
- `revert`: worse candidate left on `main`; revert before doing anything else.

---

## Task 9: Prepare Official Candidate Only After Decision

**Objective:** If Jonathan approves promotion, prepare a guarded official submission update.

**Files:**
- Modify: submission branch `docker-compose.yml` only after decision.

**Steps:**

1. Build/publish or identify immutable WebApi image tag for the winning commit.
2. Fetch exact submission branch:
   ```bash
   git fetch origin submission:refs/remotes/origin/submission
   git checkout -B submission origin/submission
   ```
3. Update compose image tags and CPU split.
4. Reject accidental `:latest` images:
   ```bash
   grep -nE 'image:|pull_policy|cpus:|memory:' docker-compose.yml
   python3 - <<'PY'
   import re, pathlib, sys
   text = pathlib.Path('docker-compose.yml').read_text()
   images = re.findall(r'^\s*image:\s*(.+)$', text, re.M)
   print('\n'.join(images))
   if any(':latest' in img for img in images):
       sys.exit('refusing official submission with :latest image')
   PY
   ```
5. Commit/push `submission`.
6. Open official test issue only after Jonathan confirms.
7. After bot result, verify runtime commit/images and report official p99/score/errors.

---

## Execution Order Summary

1. Fresh control round on current `0.46 / 0.08`.
2. CPU split micro-matrix; keep/revert based on repeated medians.
3. Threading/accept-loop matrix.
4. Bucket tunable matrix.
5. Safe hot-path code micro-optimizations.
6. IVF threshold experiments.
7. Two 3-rep confirmation rounds for the best candidate.
8. Report and decide whether to promote.

Do not submit an official candidate until the report is reviewed and Jonathan explicitly approves promotion.
