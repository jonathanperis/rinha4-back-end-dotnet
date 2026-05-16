# Audit, Code Cleanup, and Docs Refresh Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Audit `rinha4-back-end-dotnet`, remove stale/unused runtime and experiment paths safely, and bring `README.md` plus `docs/wiki/*.md` in line with the current source and compose setup.

**Architecture:** Treat this as a correctness-first cleanup for a competitive Rinha backend. Start with an inventory and evidence trail, then remove only code/config paths proven unused by production, tests, docs, and CI. Update docs after source cleanup so public docs describe the real default runtime rather than historical IVF-only notes.

**Tech Stack:** .NET 10 NativeAOT, raw HTTP/Unix Domain Socket server, hybrid bucket/IVF scorer, Docker Compose, Bun/Astro docs, GitHub Actions benchmark archive.

---

## Current Context from Inspection

- Repo: `/opt/data/github/jonathanperis/rinha4-back-end-dotnet`
- Current branch: `main`
- Current HEAD during planning: `691f7c32057c`
- Top-level runnable/config files include `docker-compose.yml`, `docker-compose.fdpass.yml`, `.github/workflows/*.yml`, `src/WebApi/Dockerfile`, and scripts under `scripts/`.
- Source/test shape:
  - `src/WebApi`: raw HTTP server, request parser, vectorizer, scorer, exact/IVF/bucket indexes.
  - `src/DataConverter`: builds exact, IVF, and bucket binary data.
  - `tests/VectorizationTests`: focused vectorization/parser/index tests.
  - `tests/AccuracyProbe`: probe executable.
- Current non-generated LOC snapshot, excluding reports/build/dependencies: 59 files, about 5,817 code lines; C# is about 4,487 code lines across 29 files.
- `dotnet build ... --no-restore` currently fails because `obj/project.assets.json` is absent. First verification command should restore.
- Docker daemon access may not be available in this environment; prefer local .NET/docs verification here and GitHub Actions for full compose/benchmark checks.

## Confirmed Staleness / Cleanup Signals

1. Docs mismatch current runtime:
   - `docs/wiki/architecture.md` says default and only runtime mode uses IVF.
   - `docs/wiki/performance.md` says older CPU split `0.42 API / 0.16 LB`.
   - Current `docker-compose.yml` uses `SCORER_MODE=hybrid`, API `0.45` each, LB `0.10`.
2. Production config still exposes multiple historical paths:
   - `SCORER_MODE` supports `exact`, `ivf`, `hybrid`, and implicit `bucket` fallback in `src/WebApi/FraudScorer.cs`.
   - `docker-compose.yml` still sets `EXACT_*`, `IVF_*`, and many `BUCKET_*` variables.
   - `src/DataConverter` still builds exact, IVF, and bucket data.
3. Existing docs/plans already record the newer reality:
   - `docs/plans/2026-05-16-cross-repo-rinha4-approaches.md` says current scorer is hybrid and current split is `0.45/0.10`.
   - That plan also flags stale docs around IVF-only and old CPU splits.

## Non-Goals

- Do not tune performance in this cleanup pass unless a cleanup reveals a trivial safe win.
- Do not change official runtime image tags without explicit promotion decision.
- Do not remove index/scorer modes just because they are not the default; remove only after proving they are not needed for tests, CI, benchmark comparison, or future fallback.
- Do not rely on a single GitHub-hosted benchmark run for performance conclusions.

---

## Task 1: Establish Baseline Inventory and Verification Commands

**Objective:** Capture the current runnable baseline before deleting anything.

**Files:**
- Read: `README.md`
- Read: `docker-compose.yml`
- Read: `docker-compose.fdpass.yml`
- Read: `.github/workflows/build.yml`
- Read: `.github/workflows/benchmark.yml`
- Read: `.github/workflows/pages.yml`
- Read: `src/WebApi/WebApi.csproj`
- Read: `src/DataConverter/DataConverter.csproj`
- Read: `tests/VectorizationTests/VectorizationTests.csproj`
- Read: `tests/AccuracyProbe/AccuracyProbe.csproj`
- Create/modify only if useful: `docs/plans/2026-05-16-cleanup-audit-findings.md`

**Steps:**

1. Record branch/HEAD/status:
   ```bash
   git branch --show-current
   git rev-parse --short=12 HEAD
   git status --short
   ```
   Expected: branch `main`; status either clean or only the current plan file.

2. Restore and build all local .NET projects:
   ```bash
   dotnet restore src/WebApi/WebApi.csproj
   dotnet restore src/DataConverter/DataConverter.csproj
   dotnet restore tests/VectorizationTests/VectorizationTests.csproj
   dotnet restore tests/AccuracyProbe/AccuracyProbe.csproj

   dotnet build src/WebApi/WebApi.csproj -c Release --no-restore
   dotnet build src/DataConverter/DataConverter.csproj -c Release --no-restore
   dotnet build tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
   dotnet build tests/AccuracyProbe/AccuracyProbe.csproj -c Release --no-restore
   ```
   Expected: all builds pass. If restore mutates lock/assets only under `obj/`, do not commit those generated files.

3. Run the focused test executable:
   ```bash
   dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
   ```
   Expected: success exit code.

4. Verify docs build with Bun, not npm:
   ```bash
   cd docs
   bun install
   bun run build
   ```
   Expected: Astro build succeeds. Do not commit generated `dist/` or `.astro/` artifacts unless the repo already tracks them.

5. Save or append a short audit snapshot if useful:
   ```markdown
   ## Baseline
   - branch/head:
   - dotnet restore/build:
   - vectorization tests:
   - docs build:
   - docker/local benchmark availability:
   ```

**Commit:**
- If this task only creates an audit note:
  ```bash
  git add docs/plans/2026-05-16-cleanup-audit-findings.md
  git commit -m "docs: record cleanup audit baseline"
  ```
- If no tracked files changed, no commit.

---

## Task 2: Build a Used-vs-Unused Matrix for Runtime Modes

**Objective:** Decide which scorer/index paths are production-required, test-required, experiment-only, or removable.

**Files:**
- Inspect: `src/WebApi/FraudScorer.cs`
- Inspect: `src/WebApi/ExactIndex.cs`
- Inspect: `src/WebApi/IvfIndex.cs`
- Inspect: `src/WebApi/IvfIndex.Int64.cs`
- Inspect: `src/WebApi/IvfIndex.Float.cs`
- Inspect: `src/WebApi/BucketIndex.cs`
- Inspect: `src/WebApi/BucketSearchOptions.cs`
- Inspect: `src/DataConverter/ExactIndexBuilder.cs`
- Inspect: `src/DataConverter/IvfIndexBuilder.cs`
- Inspect: `src/DataConverter/BucketIndexBuilder.cs`
- Inspect: `docker-compose.yml`
- Inspect: `src/WebApi/Dockerfile`
- Inspect: `.github/workflows/*.yml`
- Update if used: `docs/plans/2026-05-16-cleanup-audit-findings.md`

**Steps:**

1. Search mode/config references:
   ```bash
   git grep -n "SCORER_MODE\|ExactIndex\|BucketIndex\|IvfIndex\|EXACT_\|BUCKET_\|IVF_" -- . ':!docs/public/reports'
   ```

2. Produce a matrix:
   ```markdown
   | Component | Current production? | Tests? | CI/scripts? | Docs? | Decision |
   | --- | --- | --- | --- | --- | --- |
   | Hybrid mode | yes/no | ... | ... | ... | keep/remove |
   | Bucket mode | ... | ... | ... | ... | ... |
   | IVF-only mode | ... | ... | ... | ... | ... |
   | Exact mode | ... | ... | ... | ... | ... |
   | fdpass compose | ... | ... | ... | ... | ... |
   ```

3. Mark removal candidates only when all are true:
   - not selected by `docker-compose.yml` default;
   - not referenced by CI benchmark/build workflows;
   - not required by tests or accuracy probes;
   - not needed to regenerate production data;
   - no active docs/plans point to it as a near-term experiment.

4. If exact mode is experiment-only, prefer a separate task that first disables it from production config/docs before deleting source.

**Verification:**
- Matrix exists and each keep/remove decision cites concrete grep evidence.

**Commit:**
```bash
git add docs/plans/2026-05-16-cleanup-audit-findings.md
git commit -m "docs: map cleanup candidates"
```

---

## Task 3: Remove or Quarantine Proven-Stale Compose Environment Variables

**Objective:** Simplify `docker-compose.yml` without changing the default runtime behavior.

**Files:**
- Modify: `docker-compose.yml`
- Possibly modify: `README.md`
- Possibly modify: `docs/wiki/getting-started.md`

**Likely Cleanup Candidates:**
- Remove `EXACT_PATH` only if exact mode is removed or no longer configurable.
- Remove `IVF_BOUNDARY_FULL` only if no source reads it.
- Remove any `BUCKET_*` variables that are not read by `BucketSearchOptions.FromEnvironment()` or `BucketIndex`.
- Keep `SCORER_MODE=hybrid`, `IVF_PATH`, `BUCKET_PATH`, and tuned `IVF_*`/`BUCKET_*` variables that are actively read.

**Steps:**

1. For each env var in `docker-compose.yml`, verify it is read:
   ```bash
   git grep -n "ENV_VAR_NAME" -- src scripts .github docs README.md docker-compose*.yml
   ```

2. Delete only compose entries that have no source/script/CI read path or are tied to a removed scorer mode.

3. Re-run YAML sanity:
   ```bash
   docker compose config >/tmp/rinha4-compose-config.yml
   ```
   Expected: exits 0 if Docker Compose is available. If Docker daemon is unavailable, this still usually validates client-side config.

4. Re-run local .NET build/test from Task 1.

**Commit:**
```bash
git add docker-compose.yml README.md docs/wiki/getting-started.md
git commit -m "chore: remove stale compose options"
```

---

## Task 4: Remove Proven-Unused Scorer/Index Code in Small Slices

**Objective:** Delete dead source files and branches only after the matrix proves they are not needed.

**Files:**
- Potential modify: `src/WebApi/FraudScorer.cs`
- Potential delete: `src/WebApi/ExactIndex.cs`
- Potential delete: `src/DataConverter/ExactIndexBuilder.cs`
- Potential modify: `src/DataConverter/Program.cs`
- Potential modify/delete: `src/WebApi/IvfIndex.Float.cs` if obsolete and unreferenced
- Potential modify: `src/WebApi/Usings.cs`
- Potential modify: `src/DataConverter/Usings.cs`
- Test: `tests/VectorizationTests/Program.cs`
- Test: `tests/AccuracyProbe/Program.cs`

**Steps:**

1. Pick exactly one removable path, e.g. exact mode.

2. Before deleting, run grep for compile-time and reflection references:
   ```bash
   git grep -n "ExactIndex\|ScorerMode.Exact\|EXACT_PATH\|references.bin\|ExactIndexBuilder" -- . ':!docs/public/reports'
   ```

3. Remove the branch from `FraudScorer.ResolveMode()` and `ScoreFraudRequest()` only if no supported config should invoke it.

4. Delete the associated source/builder files if now unreferenced.

5. Build immediately:
   ```bash
   dotnet build src/WebApi/WebApi.csproj -c Release --no-restore
   dotnet build src/DataConverter/DataConverter.csproj -c Release --no-restore
   dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
   ```

6. Repeat for the next removable path only after the previous commit is green.

**Commit:**
```bash
git add -A src tests docker-compose.yml README.md docs/wiki
git commit -m "refactor: remove unused exact scorer path"
```

**Risk Gate:**
- If a cleanup changes generated binary format or default scoring behavior, stop and run the accuracy probe plus benchmark workflow before continuing.

---

## Task 5: Add Guard Tests for Supported Runtime Modes

**Objective:** Prevent future docs/config drift by testing the modes the repo claims to support.

**Files:**
- Modify: `tests/VectorizationTests/Program.cs`
- Possibly create: `tests/VectorizationTests/ScorerModeTests.cs` if converting the test app into multiple files is worth it.
- Modify only if needed: `src/WebApi/FraudScorer.cs` to expose parseable mode behavior internally.

**Steps:**

1. Add a small test that asserts supported mode names are exactly the intended set after cleanup.

2. Add a test or assertion for default mode resolution:
   - If keeping current behavior: default should be `bucket` only when `SCORER_MODE` is unset and no compose override exists; compose should still set `hybrid`.
   - If simplifying: default should be `hybrid` and docs should say so.

3. Run:
   ```bash
   dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
   ```

**Commit:**
```bash
git add tests/VectorizationTests src/WebApi/FraudScorer.cs
git commit -m "test: lock supported scorer modes"
```

---

## Task 6: Refresh README Runtime and Local-Run Sections

**Objective:** Make `README.md` describe the actual default stack and cleanup state.

**Files:**
- Modify: `README.md`

**Required Updates:**

1. In `Current stack`, replace historical IVF-only language with the current default:
   - standalone `rinha4-lb-yolo-mode` proxy on `9999`;
   - two .NET 10 NativeAOT API instances over Unix sockets;
   - raw socket HTTP/1 server;
   - hybrid bucket fast-path with IVF fallback, if still current after cleanup;
   - build-time data conversion to the exact set of runtime binaries that remain.

2. In `Data and classifier`, describe the retained generated files:
   - `references.bucket.bin` if bucket remains;
   - `references.ivf.bin` if IVF fallback remains;
   - remove `references.bin` if exact mode is deleted.

3. In `Local`, use restore-aware commands:
   ```bash
   dotnet restore src/WebApi/WebApi.csproj
   dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
   ```

4. Add an explicit note that full compose/benchmark validation may require Docker daemon access or GitHub Actions.

**Verification:**
- `README.md` has no claims that conflict with `docker-compose.yml` defaults.
- Search check:
  ```bash
  git grep -n "only runtime\|0.42\|0.16\|references.bin\|IVF-only\|ivf only" README.md docs/wiki || true
  ```

**Commit:**
```bash
git add README.md
git commit -m "docs: refresh readme for current runtime"
```

---

## Task 7: Refresh `docs/wiki` Architecture, Performance, Getting Started, and CI Pages

**Objective:** Bring public GitHub Pages docs up to date with the cleaned source/config.

**Files:**
- Modify: `docs/wiki/architecture.md`
- Modify: `docs/wiki/performance.md`
- Modify: `docs/wiki/getting-started.md`
- Modify: `docs/wiki/ci-cd-pipeline.md`
- Check: `docs/wiki/home.md`
- Check: `docs/wiki/challenge.md`
- Check: `docs/wiki/rules.md`

**Required Updates:**

1. `docs/wiki/architecture.md`
   - Change classifier section from IVF-only to the retained default mode.
   - Show bucket fast-path + IVF fallback if hybrid remains.
   - Keep Unix socket/LB/request path accurate.

2. `docs/wiki/performance.md`
   - Replace stale CPU split `0.42 / 0.16` with current `0.45 / 0.10` if still current.
   - Update bottleneck section to mention bucket fast-path / fallback cost rather than IVF repair only.
   - Keep benchmark claims tied to archived reports or clearly label them as historical.

3. `docs/wiki/getting-started.md`
   - Add `dotnet restore` before `--no-restore` build/test commands.
   - Ensure env var tuning list only includes retained variables.
   - Keep docs command using Bun.

4. `docs/wiki/ci-cd-pipeline.md`
   - Ensure benchmark workflow parameters match current source/compose defaults.
   - Remove stale IVF-only experiment instructions unless intentionally preserved as historical notes.

5. Run docs search after edits:
   ```bash
   git grep -n "only runtime\|0.42\|0.16\|EXACT_\|references.bin\|IVF_BOUNDARY_FULL" docs/wiki README.md docker-compose.yml src || true
   ```

**Verification:**
```bash
cd docs
bun install
bun run build
```
Expected: site builds successfully.

**Commit:**
```bash
git add docs/wiki README.md
git commit -m "docs: align wiki with current runtime"
```

---

## Task 8: Validate Generated Data Pipeline After Cleanup

**Objective:** Ensure `DataConverter` still generates exactly what runtime startup needs.

**Files:**
- Inspect/modify: `src/DataConverter/Program.cs`
- Inspect/modify: `src/WebApi/Dockerfile`
- Inspect/modify: `src/WebApi/FraudScorer.cs`
- Inspect/modify: `docker-compose.yml`
- Data outputs under `data/` should not be committed unless the repo already tracks them intentionally.

**Steps:**

1. Clean local generated binaries if they are untracked or stale:
   ```bash
   git status --short data
   ```
   Do not delete tracked required data without explicit confirmation.

2. Run converter:
   ```bash
   dotnet run --project src/DataConverter/DataConverter.csproj -c Release -- data/
   ```

3. Confirm generated files match runtime expectations:
   - If hybrid: `data/references.bucket.bin` and `data/references.ivf.bin` must exist.
   - If IVF-only: `data/references.ivf.bin` must exist.
   - If exact removed: `data/references.bin` should not be required by `docker-compose.yml`, Dockerfile, or runtime startup.

4. Run WebApi local startup if data exists:
   ```bash
   DATA_DIR=data IVF_PATH=data/references.ivf.bin BUCKET_PATH=data/references.bucket.bin SCORER_MODE=hybrid \
     dotnet run --project src/WebApi/WebApi.csproj -c Release --no-restore
   ```
   Stop the process after readiness/startup is confirmed.

**Commit:**
```bash
git add src/DataConverter src/WebApi src/WebApi/Dockerfile docker-compose.yml
git commit -m "refactor: simplify generated data pipeline"
```

---

## Task 9: Final Verification Gate

**Objective:** Prove cleanup did not break local build/tests/docs and prepare a benchmark path.

**Commands:**

```bash
git status --short

dotnet restore src/WebApi/WebApi.csproj
dotnet restore src/DataConverter/DataConverter.csproj
dotnet restore tests/VectorizationTests/VectorizationTests.csproj
dotnet restore tests/AccuracyProbe/AccuracyProbe.csproj

dotnet build src/WebApi/WebApi.csproj -c Release --no-restore
dotnet build src/DataConverter/DataConverter.csproj -c Release --no-restore
dotnet build tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
dotnet build tests/AccuracyProbe/AccuracyProbe.csproj -c Release --no-restore

dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore

cd docs
bun install
bun run build
```

**Benchmark Gate:**
- If source cleanup touches hot path or generated data format, run the official-like benchmark through GitHub Actions instead of trusting local Docker from this environment.
- Require `score=6000`, `0` false positives, `0` false negatives, and `0` HTTP errors.
- Because GitHub-hosted runner p99 is noisy, run multiple comparison rounds before claiming a performance improvement or regression.

**Commit:**
```bash
git add -A
git commit -m "chore: complete cleanup verification updates"
```
Only commit if tracked files changed.

---

## Task 10: Final Documentation/History Hygiene

**Objective:** Leave the repo easy to understand after cleanup.

**Files:**
- Modify if needed: `docs/plans/2026-05-16-cleanup-audit-findings.md`
- Modify if needed: `README.md`
- Modify if needed: `docs/wiki/performance.md`

**Steps:**

1. Add a final cleanup summary:
   ```markdown
   ## Cleanup Summary
   - Removed:
   - Kept intentionally:
   - Docs refreshed:
   - Verification:
   - Benchmark follow-up:
   ```

2. Ensure historical benchmark claims remain accurate and labeled as historical.

3. Confirm no generated docs artifacts are accidentally staged:
   ```bash
   git status --short
   git diff --stat
   ```

4. Push only after the final local verification gate passes:
   ```bash
   git push origin main
   ```

---

## Risks and Tradeoffs

- **Correctness risk:** Removing fallback/index paths can silently alter fraud decisions. Keep changes small and run tests after each slice.
- **Benchmark risk:** Cleanup can shift NativeAOT size/layout or startup behavior. Treat p99 changes as noisy until repeated CI runs confirm them.
- **Docs risk:** Existing benchmark archive and docs/plans contain historical values. Do not rewrite history; update current docs and label old plans as historical.
- **Docker limitation:** This environment may not be able to run full compose stacks locally. Use GitHub Actions for official-like benchmark validation.
- **Future experiment risk:** Some currently non-default modes may still be useful for comparison. If uncertain, quarantine/document instead of deleting.

## Recommended Execution Order

1. Baseline restore/build/test/docs build.
2. Used-vs-unused matrix.
3. Docs-only stale claim fix for obviously wrong current facts (`hybrid`, `0.45/0.10`).
4. One cleanup slice at a time: compose env vars, then dead source paths.
5. Full local verification.
6. CI benchmark only if hot path/runtime data changed.
7. Final docs summary and push.
