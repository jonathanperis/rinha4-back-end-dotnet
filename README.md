# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026), focused on low p99 latency, high score, and 0% failures inside the official `1 CPU / 350 MB` container limit.

## Goal

Target: top-10 ranking on [rinhadebackend.com.br](https://rinhadebackend.com.br/).

Ranking pressure points:

- lower p99 latency
- higher successful request volume
- 0% failures
- valid implementation under competition rules

## Competition Contract

The application exposes the required API:

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/ready` | readiness probe |
| `POST` | `/fraud-score` | fraud score decision |

The default Docker topology follows the Rinha shape:

- reverse proxy on port `9999`
- two API instances
- Docker bridge network
- no privileged container
- total limits: `1.00 CPU / 350 MB`

Submission shape:

- `main` contains source code, tests, docs, and build workflow.
- `submission` contains only runnable files required by the official runner:
  - `docker-compose.yml`
  - `nginx.conf`
  - `info.json`
  - `LICENSE`
- `docker-compose.yml` on `submission` pins the immutable public GHCR image built from `main`.

## Current Architecture

```text
k6 / judge
    |
    v
nginx stream proxy :9999
    |
    +-- unix:/sockets/api1.sock -> WebApi NativeAOT
    |
    +-- unix:/sockets/api2.sock -> WebApi NativeAOT
```

Default path:

1. `nginx` accepts TCP on `9999`.
2. `nginx stream` forwards raw HTTP over Unix Domain Sockets.
3. WebApi handles HTTP/1 directly on a raw socket connection.
4. A small in-process HTTP parser reads headers, content length, and body.
5. A manual `Utf8JsonReader` parser extracts only fields needed by vectorization.
6. Request fields become a normalized 14-dimensional vector.
7. Vector is quantized to `int16` with the IVF header scale, default `10000`.
8. IVF scans candidate clusters and repairs with bounding-box lower bounds.
9. Rounded int16 top-five labels map directly to one of six prebuilt HTTP/JSON byte responses.

The raw HTTP server intentionally implements only the subset required by this workload:

- `GET /ready`
- `POST /fraud-score`
- HTTP/1 keep-alive
- `Content-Length` request bodies

It is not a general-purpose web server.

Docker Compose waits for both API Unix socket files before starting nginx. This prevents the reverse proxy from accepting `/ready` while upstream sockets are still missing, which was the likely cause of official `No status` health-check failures.

## Services And Limits

| Service | CPU | Memory | Notes |
| --- | ---: | ---: | --- |
| `nginx` | `0.20` | `20 MB` | default stream TCP proxy |
| `webapi1` | `0.40` | `165 MB` | .NET 10 NativeAOT |
| `webapi2` | `0.40` | `165 MB` | .NET 10 NativeAOT |
| **Total** | **1.00** | **350 MB** | competition limit |

API memory is dominated by:

- `references.ivf.bin` loaded as managed centroid, bbox, label, id, and block arrays
- normalization and MCC lookup tables
- NativeAOT runtime overhead

## Data Pipeline

Input data lives in `data/references.json.gz`.

`src/DataConverter` converts it to `data/references.ivf.bin` at image build time.

The binary file is embedded in the Docker image. The runtime container does not need to download or transform data before serving.

IVF format:

```text
int32 magic = "IVF2" | "IVF3"
int32 count
int32 clusters
int32 dims
int32 scale
int32 block_lanes
int32 total_blocks
int16 centroids[dims * clusters]    // dimension-major
int16 bbox_min[dims * clusters]     // dimension-major
int16 bbox_max[dims * clusters]     // dimension-major
int32 offsets[clusters + 1]
byte labels[padded_rows]
int32 ids[padded_rows]
int16 blocks[total_blocks * dims * block_lanes]
```

The runtime uses rounded int16 coordinates because the public labels match
rounded quantized KNN behavior. Default `IVF_SCALE=10000` writes IVF2 and uses
int64 accumulation to preserve accuracy. Experimental `IVF_SCALE<=4096` writes
IVF3 and uses int32 AVX2 centroid, bbox, and block scan accumulation for A/B.
The previous bucket fallback and float32 rerank paths were removed after IVF
became the production classifier.

Runtime IVF code is split by role:

- `IvfIndex.cs` loads and validates the binary file, stores immutable arrays, and dispatches search.
- `IvfIndex.Int64.cs` keeps the IVF2 candidate path with int64 accumulation for `IVF_SCALE=10000`.
- `IvfIndex.Int32.cs` keeps the experimental IVF3 lower-scale path with int32 AVX2 accumulation.

## Fraud Vector

The request is normalized into 14 dimensions:

| Index | Feature |
| ---: | --- |
| `0` | transaction amount |
| `1` | installments |
| `2` | amount versus customer average |
| `3` | hour of day |
| `4` | day of week |
| `5` | minutes since last transaction, or `-1` when absent |
| `6` | km from last transaction, or `-1` when absent |
| `7` | terminal km from home |
| `8` | customer transactions in last 24h |
| `9` | terminal online flag |
| `10` | card present flag |
| `11` | unknown merchant flag |
| `12` | MCC risk |
| `13` | merchant average amount |

Normalization constants are loaded from `data/normalization.json`.
MCC risk values are loaded into a flat `float[10000]` plus known-value bitmap from `data/mcc_risk.json`.

## Classifier

The production classifier:

- loads `references.ivf.bin` from `IVF_PATH` or `DATA_DIR`
- scans nearest IVF centroid clusters with `IVF_FAST_NPROBE`
- can rerun only boundary fraud counts with `IVF_FULL_NPROBE` when `IVF_BOUNDARY_FULL=true`
- current default uses one repaired pass: `IVF_BOUNDARY_FULL=false`, bbox repair on, fraud range `0..5`
- uses bounding-box lower bounds to repair missed clusters when enabled
- ranks candidates only with rounded int16 squared L2 distance
- fails startup if the IVF file is absent or invalid

Tradeoff:

- This prioritizes ranking performance under load.
- Full repair (`IVF_REPAIR_MIN_FRAUDS=0`, `IVF_REPAIR_MAX_FRAUDS=5`) matched the
  public benchmark payload locally with `0` false positives and `0` false negatives.

## Hot Path Optimizations

Implemented:

- .NET 10 NativeAOT
- raw socket HTTP/1 server
- tunable async accept loop count via `ACCEPT_LOOPS`
- one task per client connection
- keep-alive request loop
- pooled per-connection read buffers
- Unix Domain Sockets between proxy and APIs
- socket cleanup on process start
- socket chmod so nginx can reach API sockets
- Compose healthcheck waits for both API socket files before nginx starts
- direct header and `Content-Length` parsing
- manual `Utf8JsonReader` parser
- no JSON model binding in `/fraud-score`
- precomputed full HTTP/1 JSON responses
- flat arrays for MCC risk lookup
- `stackalloc` for request vectors
- no per-request response serialization
- no hot-path logging
- rounded int16 IVF classifier, with experimental IVF3 int32 scan path behind `IVF_SCALE<=4096`

## Reverse Proxy

Default:

```bash
docker compose up -d --force-recreate
```

Uses:

- [nginx.conf](./nginx.conf)
- stream TCP proxy
- UDS upstreams
- `20 MB` proxy memory

nginx stream is the retained load balancer path. It keeps the proxy layer byte-oriented, avoids HTTP parsing in the proxy, and leaves request parsing to the raw socket server.

Local benchmark caveats:

- These runs use local Docker on this machine, not the official Mac Mini runner.
- k6 rounds very low failure rates to `0.00%`; raw failed request counts still matter.
- Official preview/final scripts can have different traffic shape and payload mix.

## Local Development

Generate IVF data:

```bash
dotnet run --project src/DataConverter/DataConverter.csproj -- data/
```

Run one API locally over TCP:

```bash
DATA_DIR=data IVF_PATH=data/references.ivf.bin \
  dotnet run --project src/WebApi/WebApi.csproj
```

Check readiness:

```bash
curl -i http://localhost:8080/ready
```

Sample request:

```bash
curl -i -X POST http://localhost:8080/fraud-score \
  -H "Content-Type: application/json" \
  -d '{"id":"tx-test","transaction":{"amount":384.88,"installments":3,"requested_at":"2024-01-15T09:30:00Z"},"customer":{"avg_amount":769.76,"tx_count_24h":3,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5912","avg_amount":298.95},"terminal":{"is_online":false,"card_present":true,"km_from_home":13.7},"last_transaction":{"timestamp":"2024-01-15T09:15:00Z","km_from_current":18.8}}'
```

Run full Docker stack:

```bash
docker compose up --build
curl -i http://localhost:9999/ready
```

Docker IVF build parameters are also tunable with `IVF_CLUSTERS`,
`IVF_TRAIN_SAMPLE`, `IVF_ITERATIONS`, and `IVF_SCALE`.
The current default is `IVF_CLUSTERS=2048`, `IVF_SCALE=10000`,
`IVF_FAST_NPROBE=1`, `IVF_FULL_NPROBE=1`, and full bbox repair over fraud
counts `0..5`.

## Benchmarks

CI official-like benchmark:

- GitHub Actions workflow: `.github/workflows/benchmark.yml`
- Trigger: manual `workflow_dispatch`
- Test source: clones `zanfranceschi/rinha-de-backend-2026` and runs official `test/test.js`
- Stack start: `docker compose --compatibility` so Compose maps `deploy.resources.limits` into local container limits
- Artifacts: `benchmark-results/results.json` and `benchmark-results/docker-compose.log`
- Main build workflow also runs the same benchmark automatically after the amd64 image build/push succeeds.
- Automatic runs use immutable image tag `ci-${GITHUB_SHA}` instead of rebuilding from checkout.
- Automatic main-branch benchmark results are archived under `docs/public/reports/`, matching the historical report style used in the previous Rinha repository:
  - `index.html` browser-friendly report list
  - `index.json` machine-readable history
  - `latest.json` latest run shortcut
  - `latest-candidate.json` latest default submission-stack run
  - `rinha-benchmark-YYYYMMDDHHMMSS-SHA.json` immutable run result
  - `rinha-benchmark-YYYYMMDDHHMMSS-SHA.html` k6 HTML report when generated

Current local/CI signal:

- rounded IVF local replay over public `test-data.json`: `0` FP, `0` FN
- best zero-failure CI experiment so far uses `IVF_CLUSTERS=2048`: p99 `11.72ms`, score `4931.04`
- rejected cluster-count A/Bs: `4096` was p99 `16.69ms`, `1024` was p99 `19.78ms`, and `1536` replayed accurately but was materially slower locally
- IVF3 lower-scale local replay was tested as A/B and is not candidate-safe yet:
  `IVF_SCALE=1000` produced `10` FP and `11` FN; `IVF_SCALE=4096` produced `1` FP and `3` FN
- latest published candidate is updated by the main benchmark after each successful image build

Local replay numbers are correctness checks against the public payload. CI
benchmark numbers are official-like GitHub Actions results, not official Rinha
hardware results.

Run from GitHub Actions:

1. Open **Actions**.
2. Select **Official-like Benchmark**.
3. Choose compose file:
   - `docker-compose.yml` for nginx stream baseline
4. For IVF experiment, set `report_kind=experiment`, tune `IVF_CLUSTERS` or `IVF_SCALE`, and keep `IVF_FAST_NPROBE=1`, `IVF_FULL_NPROBE=1`, `IVF_BBOX_REPAIR=true`, `IVF_BOUNDARY_FULL=false`, and repair frauds `0..5`.
5. Run workflow.

Manual runs can also benchmark a pushed image by filling `webapi_image`; when set,
the workflow pulls that image and starts Compose with `--no-build`.
Manual workflow runs archive results under `docs/public/reports/` and refresh GitHub Pages when a report changes.

## GitHub Pages

The repository publishes an Astro documentation site from `docs/`.

Pages structure:

- `/` home dashboard with latest official Rinha issue metrics and latest CI candidate cards
- `/reports/` benchmark history from `docs/public/reports/index.json`
- `/docs/` searchable implementation notes generated from `docs/wiki/*.md`

Local docs workflow:

```bash
cd docs
bun install
bun run dev
```

## Tests And Validation

Unit-style vectorization tests live under `test/`:

```bash
dotnet run --project test/VectorizationTests/VectorizationTests.csproj --no-restore
```

Current focused tests cover timestamp parsing, request parsing, fraud-count
response mapping, IVF defaults, and a synthetic IVF boundary/bounding-box repair case.

Build checks:

```bash
dotnet build src/WebApi/WebApi.csproj --no-restore
dotnet build src/DataConverter/DataConverter.csproj --no-restore
```

## Docker Image

Image name:

```text
ghcr.io/jonathanperis/rinha4-back-end-dotnet:latest
```

Build details:

- SDK image builds converter and API.
- converter creates `references.ivf.bin`.
- image builds accept `IVF_CLUSTERS`, `IVF_TRAIN_SAMPLE`, `IVF_ITERATIONS`, and `IVF_SCALE` build args.
- API is published with NativeAOT.
- runtime image contains only API binary plus data files.

Publishing:

- pushes to `main` trigger the amd64 build workflow
- successful workflow publishes `ghcr.io/jonathanperis/rinha4-back-end-dotnet:latest`
- the `submission` branch pins an immutable `ci-${GITHUB_SHA}` tag after a successful build

Official preview trigger:

```text
rinha/test jonathanperis-dotnet
```

Preview issues are opened in the official Rinha repository after `main` image build and `submission` branch push finish.

## Next Work

Main blocker for top-10 target:

- keep rounded IVF at `0%` failures in CI and official preview
- reduce full-repair p99 below the #9 .NET reference (`1.73ms`)
- preserve `0` HTTP errors after the nginx/socket readiness hardening

Likely next moves:

1. Keep IVF2 scale 10000 as candidate default because public replay stays at `0` FP/FN.
2. Profile repair p99 and compare lower-scale IVF3 A/B against previous IVF2 results.
3. Compare new CI p99 against #9 .NET reference (`1.73ms`) while preserving `0%` failures.
4. Promote IVF submission when CI shows `0` failures and p99 below #9.
5. Track official issue `#2088` for the next preview result from the updated submission branch.

## License

MIT
