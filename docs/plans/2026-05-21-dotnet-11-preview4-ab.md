# .NET 11 Preview 4 A/B Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Build the latest `rinha4-back-end-dotnet` implementation with .NET 11 Preview 4 and run a correctness-gated A/B series against the current .NET 10 stable implementation to see whether .NET 11 improves p99/score.

**Architecture:** Keep application code, data build parameters, LB image, compose topology, CPU/memory limits, and runtime env identical. Add a .NET build axis that produces immutable .NET 10 and .NET 11 Preview 4 WebApi images from the same source commit, then benchmark those tags in same-window repeated official-like runs.

**Tech Stack:** .NET Native AOT, `src/WebApi/Dockerfile`, Docker Buildx, GHCR, `.github/workflows/build.yml`, `.github/workflows/benchmark.yml`, `scripts/ci-official-benchmark.sh`, official-like k6 artifacts.

---

## Information gathered: .NET 11 Preview 4

Sources checked:
- Microsoft announcement: `https://devblogs.microsoft.com/dotnet/dotnet-11-preview-4/`
- Microsoft download page: `https://dotnet.microsoft.com/en-us/download/dotnet/11.0`
- Release metadata: `https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/11.0/releases.json`
- Release notes: `https://raw.githubusercontent.com/dotnet/core/main/release-notes/11.0/preview/preview4/*.md`
- MCR tag API for `mcr.microsoft.com/dotnet/sdk`, `aspnet`, and `runtime-deps`

Facts:
- Release: `.NET 11.0.0-preview.4`, dated `2026-05-12`.
- SDK: `11.0.100-preview.4.26230.115`.
- Runtime / ASP.NET Core runtime: `11.0.0-preview.4.26230.115`.
- Support phase: `preview`, release type: `sts`; not production-supported.
- Language support page lists C# `14.0`, F# `10.0`, Visual Basic `17.13`.
- Local Hermes .NET install currently has only SDK `10.0.300` and runtime `10.0.8`; local .NET 11 probing needs isolated install or container-only builds.

Potentially relevant runtime items:
- Runtime libraries are built with `runtime-async=on`; potential async-throughput/library-size wins, but our current hot path is raw socket / Native AOT / mostly synchronous, so expect limited direct impact.
- JIT/codegen release notes include constant folding for `string.Equals`/`ReadOnlySpan<T>.SequenceEqual`, bounds-check elimination after empty-span guards, redundant-test elimination, better x86/x64 SIMD/floating-point cost modeling, faster `Vector128.Dot` lowering, and F16C `Half` conversions. Because we use Native AOT, normal runtime JIT wins may not translate directly, but ILC/runtime-library codegen can still change generated code.
- NativeAOT fixes include GC crash and satellite assembly handling; no direct perf promise, but build/runtime regressions should be watched.
- GC fixes include rare BGC hang and Linux `madvise()` correction.
- Breaking change: `DOTNET_RuntimeAsync` / `UNSUPPORTED_RuntimeAsync` switch is removed. Current repo search did not find it.

Container tag findings:
- Current latest repo Dockerfile uses Alpine: `mcr.microsoft.com/dotnet/sdk:10.0-alpine` and `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`.
- Verified .NET 11 Preview 4 tags exist for Alpine:
  - `mcr.microsoft.com/dotnet/sdk:11.0.100-preview.4-alpine3.23`
  - `mcr.microsoft.com/dotnet/sdk:11.0.100-preview.4-alpine3.23-aot`
  - `mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.4-alpine3.23`
- `11.0-preview-noble*` tags do not exist; that matters only for the older/stale Dockerfile shape, not current latest main.
- Prefer exact Preview 4 tags over floating `11.0-preview*` tags for A/B reproducibility.

---

## Current repo snapshot

Repo/path inspected: `/opt/data/github/jonathanperis/rinha-2026-dotnet`, latest GitHub `main` at `16bf435 docs: archive rinha benchmark`.

Relevant current files:
- `src/WebApi/Dockerfile` builds with `mcr.microsoft.com/dotnet/sdk:10.0-alpine`, restores/builds `src/DataConverter`, publishes `src/WebApi/WebApi.csproj`, and runs on `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`.
- `src/WebApi/WebApi.csproj` targets `net10.0`, has `AOT=true` path with `PublishAot`, `IlcInstructionSet=avx2`, `IlcMaxVectorTBitWidth=256`, `StaticExecutable=true`, and `ExtraOptimize` trim/runtime flags.
- `docker-compose.yml` uses `WEBAPI_IMAGE` to override the WebApi image and a fixed shared LB image `ghcr.io/jonathanperis/rinha4-lb-yolo-mode:asm-ci-0e7bbceed1eb8142dca5f83a449840381b30a785`.
- `.github/workflows/build.yml` builds and pushes `latest`, semver, `ci-${{ github.sha }}`, and `sha` tags; push ignores docs-only changes; it then runs official-like benchmark and archives reports.
- `.github/workflows/benchmark.yml` already supports manual official-like runs with `webapi_image`, `benchmark_repetitions`, `fd_raw`, CPU overrides, and artifact uploads for `results-repetition-*.json` plus `repetition-summary.json`.

Working-tree state before this plan: latest GitHub main was clean; this plan adds only `docs/plans/2026-05-21-dotnet-11-preview4-ab.md`.

---

## Decision gates

1. **Build gate:** .NET 10 and .NET 11 publish Native AOT `linux-musl-x64` successfully from the same source SHA.
2. **Smoke gate:** both image tags start via the existing `docker-compose.yml` and pass the smoke request path.
3. **Correctness gate:** score remains clean; no HTTP errors, false positives, or false negatives.
4. **Fairness gate:** only the SDK/runtime/TFM image axis changes. Keep LB image, data-generation args, IVF/search envs, CPU/memory, `FD_RAW`, and compose topology constant.
5. **Noise gate:** final decision uses 5 repetitions, parsed from every `results-repetition-*.json`; a single run is not enough.
6. **Promotion gate:** do not touch `submission` or call this official-ready without explicit approval after evidence.

---

## Implementation tasks

### Task 1: Run an isolated .NET 11 compile probe

**Objective:** Confirm current WebApi/DataConverter can build under .NET 11 Preview 4 before changing Docker/build workflows.

**Files:** none committed.

Run:
```bash
mkdir -p /opt/data/.dotnet11
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh \
  --version 11.0.100-preview.4.26230.115 \
  --install-dir /opt/data/.dotnet11 \
  --no-path
/opt/data/.dotnet11/dotnet --info
```

Then probe publish:
```bash
DOTNET_ROOT=/opt/data/.dotnet11 \
/opt/data/.dotnet11/dotnet publish src/WebApi/WebApi.csproj \
  -c Release \
  -p:AOT=true \
  -p:ExtraOptimize=true \
  -p:RinhaTargetFramework=net11.0 \
  -r linux-musl-x64 \
  -o /tmp/rinha-dotnet11-webapi \
  --self-contained
```

Expected: Native AOT binary exists at `/tmp/rinha-dotnet11-webapi/WebApi` and runs far enough to print startup/help or fail only due missing data/env.

### Task 2: Parameterize `src/WebApi/Dockerfile` for .NET version

**Objective:** Build .NET 10 by default and .NET 11 Preview 4 by explicit build args.

**Files:** modify `src/WebApi/Dockerfile` and `src/WebApi/WebApi.csproj`.

Add before the first `FROM`:
```dockerfile
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-alpine
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-alpine
ARG RINHA_TFM=net10.0

FROM ${DOTNET_SDK_IMAGE} AS build
```

Change WebApi restore/publish to pass target framework:
```dockerfile
RUN dotnet restore "WebApi/WebApi.csproj" \
    -p:Configuration=${BUILD_CONFIGURATION} \
    -p:AOT=${AOT} \
    -p:RinhaTargetFramework=${RINHA_TFM} \
    -r $(cat /tmp/runtime-id)
```

```dockerfile
RUN dotnet publish "WebApi.csproj" \
    -c $BUILD_CONFIGURATION \
    -p:AOT=${AOT} \
    -p:ExtraOptimize=${EXTRA_OPTIMIZE} \
    -p:RinhaTargetFramework=${RINHA_TFM} \
    -r $(cat /tmp/runtime-id) \
    -o /app/publish \
    --self-contained
```

Change runtime stage:
```dockerfile
FROM ${DOTNET_ASPNET_IMAGE} AS runtime
```

Verification:
```bash
docker build --platform linux/amd64 \
  -f src/WebApi/Dockerfile \
  --build-arg AOT=true \
  --build-arg EXTRA_OPTIMIZE=true \
  --build-arg BUILD_CONFIGURATION=Release \
  --build-arg CACHEBUST=net10-probe \
  -t rinha4-dotnet:net10-probe .
```

Expected: default .NET 10 build still succeeds.

### Task 3: Build the .NET 11 Preview 4 image locally

**Objective:** Catch Alpine/musl/AOT issues before CI.

Run:
```bash
docker build --platform linux/amd64 \
  -f src/WebApi/Dockerfile \
  --build-arg DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0.100-preview.4-alpine3.23-aot \
  --build-arg DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.4-alpine3.23 \
  --build-arg RINHA_TFM=net11.0 \
  --build-arg AOT=true \
  --build-arg EXTRA_OPTIMIZE=true \
  --build-arg BUILD_CONFIGURATION=Release \
  --build-arg CACHEBUST=net11-preview4-probe \
  -t rinha4-dotnet:net11-preview4-probe .
```

Expected: build succeeds. If `*-aot` SDK tag fails due duplicate/missing Alpine deps, try `mcr.microsoft.com/dotnet/sdk:11.0.100-preview.4-alpine3.23` while keeping the explicit `apk add clang lld zlib-dev musl-dev` step.

### Task 4: Add explicit build workflow axis

**Objective:** Publish same-source immutable images for .NET 10 and .NET 11.

**Files:** modify `.github/workflows/build.yml`.

Add `workflow_dispatch` inputs:
```yaml
  workflow_dispatch:
    inputs:
      dotnet_version:
        description: ".NET build axis"
        required: true
        default: "10"
        type: choice
        options:
          - "10"
          - "11-preview4"
```

In `build-amd64`, add env/steps or a matrix that maps:
- `10`:
  - suffix `net10`
  - SDK `mcr.microsoft.com/dotnet/sdk:10.0-alpine`
  - ASP.NET `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`
  - TFM `net10.0`
- `11-preview4`:
  - suffix `net11-preview4`
  - SDK `mcr.microsoft.com/dotnet/sdk:11.0.100-preview.4-alpine3.23-aot`
  - ASP.NET `mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.4-alpine3.23`
  - TFM `net11.0`

Pass build args into `docker/build-push-action@v7`:
```yaml
            DOTNET_SDK_IMAGE=${{ steps.dotnet-axis.outputs.sdk_image }}
            DOTNET_ASPNET_IMAGE=${{ steps.dotnet-axis.outputs.aspnet_image }}
            RINHA_TFM=${{ steps.dotnet-axis.outputs.tfm }}
```

Add tags without changing the meaning of production `latest`:
```yaml
            type=raw,value=${{ steps.dotnet-axis.outputs.suffix }}-ci-${{ github.sha }},enable=${{ github.event_name != 'pull_request' }}
            type=raw,value=${{ steps.dotnet-axis.outputs.suffix }}-latest,enable=${{ github.event_name != 'pull_request' }}
```

Keep `latest` and semver tags on .NET 10 only until we choose otherwise.

Validation:
```bash
python3 - <<'PY'
import yaml
for f in ['.github/workflows/build.yml', '.github/workflows/benchmark.yml']:
    yaml.safe_load(open(f))
print('workflow yaml ok')
PY
```

### Task 5: Build/push immutable candidate images

**Objective:** Produce the two exact images to compare.

Commands:
```bash
git add src/WebApi/Dockerfile .github/workflows/build.yml docs/plans/2026-05-21-dotnet-11-preview4-ab.md
git commit -m "build: add dotnet 11 preview4 ab axis"
git push github main

XDG_CONFIG_HOME=/opt/data/.config /opt/data/.local/bin/gh workflow run build.yml \
  --repo jonathanperis/rinha4-back-end-dotnet \
  -f dotnet_version=10
XDG_CONFIG_HOME=/opt/data/.config /opt/data/.local/bin/gh workflow run build.yml \
  --repo jonathanperis/rinha4-back-end-dotnet \
  -f dotnet_version=11-preview4
```

Expected tags:
- `ghcr.io/jonathanperis/rinha4-back-end-dotnet:net10-ci-<sha>`
- `ghcr.io/jonathanperis/rinha4-back-end-dotnet:net11-preview4-ci-<sha>`

Resolve digests for both tags before benchmarking; do not use mutable `latest` for conclusions.

### Task 6: Local smoke both images and stop containers

**Objective:** Confirm both image variants work through existing compose and shared LB.

Net10:
```bash
WEBAPI_IMAGE=ghcr.io/jonathanperis/rinha4-back-end-dotnet:net10-ci-<sha> \
BENCHMARK_PULL_IMAGE=true BENCHMARK_NO_BUILD=true \
docker compose up -d
# run the existing smoke path or one POST through localhost:9999
docker compose down -v
```

Net11:
```bash
WEBAPI_IMAGE=ghcr.io/jonathanperis/rinha4-back-end-dotnet:net11-preview4-ci-<sha> \
BENCHMARK_PULL_IMAGE=true BENCHMARK_NO_BUILD=true \
docker compose up -d
# run the existing smoke path or one POST through localhost:9999
docker compose down -v
```

Expected: both pass; no containers left running afterward.

### Task 7: Run official-like A/B series using existing benchmark workflow

**Objective:** Use the existing workflow instead of inventing a new harness.

Smoke run, 2 reps each image:
```bash
XDG_CONFIG_HOME=/opt/data/.config /opt/data/.local/bin/gh workflow run benchmark.yml \
  --repo jonathanperis/rinha4-back-end-dotnet \
  -f webapi_image=ghcr.io/jonathanperis/rinha4-back-end-dotnet:net10-ci-<sha> \
  -f benchmark_repetitions=2 \
  -f report_kind=experiment

XDG_CONFIG_HOME=/opt/data/.config /opt/data/.local/bin/gh workflow run benchmark.yml \
  --repo jonathanperis/rinha4-back-end-dotnet \
  -f webapi_image=ghcr.io/jonathanperis/rinha4-back-end-dotnet:net11-preview4-ci-<sha> \
  -f benchmark_repetitions=2 \
  -f report_kind=experiment
```

Decision run, 5 reps each image, same inputs and same time window:
```bash
# dispatch both promptly; record run IDs
XDG_CONFIG_HOME=/opt/data/.config /opt/data/.local/bin/gh run list \
  --repo jonathanperis/rinha4-back-end-dotnet \
  --workflow benchmark.yml --limit 10
```

If strict same-runner sequential comparison is needed, add a follow-up workflow variant only after the separate-image runs prove clean but noisy.

### Task 8: Parse all artifacts and report

**Objective:** Summarize performance without being fooled by runner noise or first-repetition-only archives.

Download artifacts:
```bash
XDG_CONFIG_HOME=/opt/data/.config /opt/data/.local/bin/gh run download <net10-run-id> -D /tmp/rinha-net10-ab
XDG_CONFIG_HOME=/opt/data/.config /opt/data/.local/bin/gh run download <net11-run-id> -D /tmp/rinha-net11-ab
```

Parse:
- every `results-repetition-*.json`
- `repetition-summary.json`
- `metadata.env`
- final `results.json` only as a summary, not as the sole source

Report table columns:
- runtime lane (`net10`, `net11-preview4`)
- source SHA
- image tag and digest
- repetition
- score
- p99
- HTTP errors
- false positives
- false negatives
- runner/run URL

Decision rules:
- Any correctness/HTTP regression: reject .NET 11 for now.
- Net11 wins all/most reps with meaningful median p99 reduction: keep as candidate and ask before submission/default promotion.
- Split/noisy results: run same-runner sequential gate before changing defaults.
- Net11 loses or ties within noise: keep .NET 10 default.

---

## Initial expectation

I expect small/no improvement rather than a guaranteed big win. This repo’s current latest implementation is Native AOT, raw-fd/socket tuned, and LB-separated; many Preview 4 wins target runtime libraries, async, JIT, and general codegen. The A/B is still worthwhile because managed parser/scorer/IVF paths and ILC-generated code may change enough to move p99.
