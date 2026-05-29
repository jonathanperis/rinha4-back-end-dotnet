# Tuning Knobs

This page is source-backed by `docker-compose.yml`, `src/WebApi`, `src/DataConverter`, `src/WebApi/Dockerfile`, `.github/workflows/build.yml`, `.github/workflows/benchmark.yml`, and `scripts/ci-official-benchmark.sh`.

Default candidate posture: keep `SCORER_MODE=ivf`, `FD_RAW=1`, `IVF_FAST_NPROBE=2`, and bbox repair enabled unless a run is explicitly labeled as an experiment.

## Default submission stack

| Surface | Default | Source of truth |
| --- | --- | --- |
| public port | `9999` on `lb` | `docker-compose.yml` |
| API instances | `webapi1`, `webapi2` | `docker-compose.yml` |
| API limits | `0.425 CPU / 165 MB` each | `docker-compose.yml` |
| LB limits | `0.15 CPU / 20 MB` | `docker-compose.yml` |
| API cpusets | `0,1` and `2,3` | `docker-compose.yml` |
| LB cpuset | `0,2` | `docker-compose.yml` |
| scorer mode | `SCORER_MODE=ivf` | `docker-compose.yml`, `FraudScorer.cs` |
| handoff mode | `LB_MODE=fdpass`, `FD_RAW=1` | `docker-compose.yml`, `RawHttpServer.cs` |

The load balancer passes accepted client file descriptors over Unix control sockets. It does not inspect fraud payloads or proxy fraud-response bytes after handoff.

## Runtime scorer controls

| Variable | Default | Applies to | Purpose |
| --- | --- | --- | --- |
| `SCORER_MODE` | `ivf` | runtime | Chooses `ivf`, `bucket`, `hybrid`, or `exact`. IVF is the clean candidate default. |
| `DATA_DIR` | `/data` | runtime | Directory for generated binary data and JSON resources. |
| `IVF_PATH` | `/data/references.ivf.bin` | runtime | IVF index loaded by default mode. Startup fails if the selected scorer data is invalid. |
| `BUCKET_PATH` | `/data/references.bucket.bin` | runtime | Bucket index for bucket/hybrid experiments. |
| `EXACT_PATH` | `/data/references.bin` | runtime | Exact diagnostic index for tests/manual experiments. |
| `SUBMITTED_FAST_PATH` | `1` | hybrid runtime | Enables the submitted hybrid fast path when `SCORER_MODE=hybrid`; set `0` for diagnostics. |

## IVF search controls

| Variable | Default | Purpose |
| --- | --- | --- |
| `IVF_FAST_NPROBE` | `2` | Number of nearest centroid clusters scanned in the first pass. |
| `IVF_FULL_NPROBE` | `1` | Secondary/boundary pass probe count when enabled. |
| `IVF_BOUNDARY_FULL` | `false` | Enables a broader boundary second pass. |
| `IVF_BBOX_REPAIR` | `true` | Enables bounding-box repair to preserve exact top-five decisions. |
| `IVF_REPAIR_MIN_FRAUDS` | `0` | Inclusive fraud-count lower bound for repair. |
| `IVF_REPAIR_MAX_FRAUDS` | `5` | Inclusive fraud-count upper bound for repair. |
| `IVF_ZERO_FAST_APPROVE_WORST_DISTANCE` | `5000000` in compose | Distance threshold that lets clean `0/5` approvals skip repair. |
| `IVF_FIVE_FAST_DENY_WORST_DISTANCE` | `2000000` in compose | Distance threshold that lets clean `5/5` denials skip repair. |
| `IVF_PEDRO_DECISION_PATH` | `0`/`false` | Enables Pedro-style borderline expansion for first-pass counts. |
| `IVF_BORDERLINE_MIN_FRAUDS` | `2` | Lower fraud-count bound for borderline expansion. |
| `IVF_BORDERLINE_MAX_FRAUDS` | `3` | Upper fraud-count bound for borderline expansion. |
| `IVF_BORDERLINE_NPROBE` | `32` | Probe count for borderline expansion. |
| `IVF_BORDERLINE_RERANK` | `128` | Rerank candidate count for borderline expansion. |
| `IVF_BBOX_ORDERED` | `false` | Experimental ordered bbox repair path. |
| `IVF_BBOX_ORDERED_MAX_PROBES` | `0` | Optional cap for ordered bbox probes; `0` means uncapped by this setting. |
| `IVF_FLOAT_AVX2` | unset/false | Experimental float AVX2 path guard in `IvfIndex.cs`. |

## Bucket and hybrid controls

| Variable | Default | Purpose |
| --- | --- | --- |
| `BUCKET_EARLY_CANDIDATES` | `9800` | Early candidate target for bucket scoring. |
| `BUCKET_MIN_CANDIDATES` | `16150` | Minimum bucket candidate count. |
| `BUCKET_MAX_CANDIDATES` | `24200` | Maximum bucket candidate count. |
| `BUCKET_PROFILE_FASTPATH` | `true` | Enables profile-level bucket fast paths. |
| `BUCKET_PROFILE_MIN_COUNT` | `15` | Legacy/default profile count used as fallback by bucket options. |
| `BUCKET_PROFILE_LEGIT_MIN_COUNT` | `5` | Legit profile fast-path minimum count. |
| `BUCKET_PROFILE_FRAUD_MIN_COUNT` | `15` | Fraud profile fast-path minimum count. |
| `BUCKET_REFERENCE_FASTPATH` | `true` | Enables reference fast paths. |
| `BUCKET_REFERENCE_FASTPATH_LEGIT` | `false` | Enables first reference legit fast path. |
| `BUCKET_REFERENCE_FASTPATH_FRAUD` | `true` | Enables first reference fraud fast path. |
| `BUCKET_REFERENCE_FASTPATH2_LEGIT` | `true` | Enables second reference legit fast path. |
| `BUCKET_REFERENCE_FASTPATH2_FRAUD` | `true` | Enables second reference fraud fast path. |
| `BUCKET_EXACT_FALLBACK` | `risky` | Controls exact fallback behavior: off/false, uncertain/exact, or risky. |
| `BUCKET_AVX_CUTOFF_DIMS` | `6` | Dimension cutoff for AVX-assisted bucket comparisons. |
| `BUCKET_RISKY_FALLBACK` and `BUCKET_RISKY_*` | source defaults | Risky fallback thresholds for amount/installments/ratio/distance/merchant averages. |

Bucket and hybrid modes are useful for latency experiments. They are not the default clean candidate lane while `SCORER_MODE=ivf` remains the compose default.

## Transport and process controls

| Variable | Default | Purpose |
| --- | --- | --- |
| `BIND_ADDR` | `fd:/sockets/api*.sock.ctrl` in compose; unset locally | `fd:` means fd-pass control socket; unset means local TCP `:8080`. |
| `FD_RAW` | `1` | Keeps passed fds on low-level `recv`/`send`; `0` wraps in managed `Socket`. |
| `FD_TCP_NODELAY` | `0` | Applies `TCP_NODELAY` to passed TCP fds when enabled. |
| `FD_TCP_QUICKACK` | `0` | Applies `TCP_QUICKACK` where available. |
| `FD_TCP_TUNE` | unset/false | Enables both TCP tuning flags in `RawHttpServer`. |
| `FD_SET_BLOCKING` | unset/false | Forces passed fds to blocking mode for diagnostics. |
| `FD_BUSY_POLL_US` | `0` | Optional Linux socket busy-poll value in microseconds. |
| `ACCEPT_LOOPS` | `1` | Number of accept/control loops. |
| `KEEP_ALIVE_MAX` | `0` | Optional keep-alive request cap; `0` means no cap in the server setting. |
| `THREADPOOL_PREFER_LOCAL` | `0` | Enables thread-pool local preference path. |
| `MIN_WORKER_THREADS` | `128` | Minimum .NET worker threads set at startup. |
| `MAX_WORKER_THREADS` | unset | Optional maximum worker threads override. |
| `MAX_IO_THREADS` | unset | Optional maximum IO completion threads override. |
| `MLOCKALL` | unset/false | Attempts Linux `mlockall` when enabled. |

The compose file also sets .NET runtime switches such as `DOTNET_PROCESSOR_COUNT=1`, invariant globalization, socket inline completions, and socket thread count. Treat those as part of the submission baseline unless a run is explicitly a runtime experiment.

## Image-build and data-conversion controls

| Variable / build arg | Default | Purpose |
| --- | --- | --- |
| `DOTNET_SDK_IMAGE` | `mcr.microsoft.com/dotnet/sdk:10.0-alpine` | SDK image used by the Dockerfile; workflows can select .NET 10 or 11 preview images. |
| `DOTNET_ASPNET_IMAGE` | `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` | Runtime base image. |
| `RINHA_TFM` | `net10.0` | Target framework passed to `WebApi.csproj`; workflow dispatch can use `net11.0` preview. |
| `AOT` | `true` in compose/workflow | Enables NativeAOT publish. |
| `EXTRA_OPTIMIZE` | `true` in compose/workflow | Enables trimming/runtime feature removals and optimization switches. |
| `BUILD_CONFIGURATION` | `Release` | Build configuration. |
| `EXACT_MAX_REFS` | `100000` | Caps exact diagnostic references; non-positive means all rows in converter logic. |
| `IVF_CLUSTERS` | `512` in compose/workflow | IVF k-means cluster count for generated index. |
| `IVF_TRAIN_SAMPLE` | `65536` | Training sample size. |
| `IVF_ITERATIONS` | `6` | K-means iterations. |
| `IVF_SCALE` | `10000` | Quantization scale for rounded int16 IVF vectors. |
| `BUCKET_SCALE` | falls back to `IVF_SCALE` | Bucket quantization scale. |
| `BUCKET_ONLY` | `false` | Converter shortcut to write only bucket data. |

## Benchmark and CI controls

| Variable / input | Default | Purpose |
| --- | --- | --- |
| `official_ref` / `OFFICIAL_REF` | `main` | Official Rinha repo ref for the public evaluation suite. |
| `webapi_image` / `WEBAPI_IMAGE` | empty or compose default | Prebuilt API image. Empty means build from checkout in the manual workflow. |
| `k6_image` / `K6_IMAGE` | `grafana/k6:latest` | k6 Docker image when Docker k6 mode is used. |
| `BENCHMARK_K6_MODE` | `native` in workflows, `docker` script default | Selects native k6 or Docker k6 execution. |
| `BENCHMARK_REPETITIONS` | `1` | Number of k6 repetitions; report archive uses median p99 when greater than one. |
| `report_kind` / `BENCHMARK_REPORT_KIND` | `experiment` manual default | Archive lane: candidate, official-calibrated, or experiment. |
| `BENCHMARK_API_CPUSET` / `benchmark_api_cpuset` | unset | Overrides API cpusets for calibration/diagnostics. |
| `BENCHMARK_PROXY_CPUSET` / `benchmark_proxy_cpuset` | unset | Overrides proxy cpuset. |
| `BENCHMARK_STACK_CPUSET` | unset | Applies one cpuset to the whole stack when specific overrides are absent. |
| `BENCHMARK_API_CPUS` / `benchmark_api_cpus` | unset | Overrides per-API CPU quota. |
| `BENCHMARK_PROXY_CPUS` / `benchmark_proxy_cpus` | unset | Overrides proxy CPU quota. |
| `BENCHMARK_API_MEMORY` | unset | Script-level API memory override. |
| `BENCHMARK_PROXY_MEMORY` | unset | Script-level proxy memory override. |
| `BENCHMARK_K6_CPUSET` | unset | Optional k6 client cpuset. |
| `BENCHMARK_PULL_IMAGE` | derived from `webapi_image` | Pull a supplied prebuilt image before benchmark. |
| `BENCHMARK_NO_BUILD` | derived from `webapi_image` | Skip local image build when using a supplied image. |

The benchmark script validates the resolved compose before startup: required services, port `9999`, no privileged mode, no host networking, declared CPU/memory limits, and aggregate limits at or below `1 CPU / 350 MB`.
