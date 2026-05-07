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
- `docker-compose.yml` on `submission` uses the public GHCR image built from `main`.

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
7. Vector is quantized to `int16` with `scale=10000`.
8. Fine-bucket key is computed.
9. Precomputed bucket majority result maps directly to one of six prebuilt HTTP/JSON byte responses.

The raw HTTP server intentionally implements only the subset required by this workload:

- `GET /ready`
- `POST /fraud-score`
- HTTP/1 keep-alive
- `Content-Length` request bodies

It is not a general-purpose web server.

## Services And Limits

| Service | CPU | Memory | Notes |
| --- | ---: | ---: | --- |
| `nginx` | `0.20` | `20 MB` | default stream TCP proxy |
| `webapi1` | `0.40` | `165 MB` | .NET 10 NativeAOT |
| `webapi2` | `0.40` | `165 MB` | .NET 10 NativeAOT |
| **Total** | **1.00** | **350 MB** | competition limit |

API memory is dominated by:

- `references.bin` loaded as one byte array
- group response table
- normalization and MCC lookup tables
- NativeAOT runtime overhead

## Data Pipeline

Input data lives in `data/references.json.gz`.

`src/DataConverter` converts it to `data/references.bin` at image build time.

The binary file is embedded in the Docker image. The runtime container does not need to download or transform data before serving.

Binary format:

```text
int32 magic = "RHD6"
int32 count
int32 dims
int32 padded_dims
int32 scale
int32 group_offsets[FineGroupCount + 1]
byte labels[count]
```

Why this format:

- compact labels reduce image size and startup memory.
- `padded_dims = 16` keeps the API request-vector stride stable.
- group offsets make bucket lookup O(1).
- labels are stored separately for startup bucket-majority precomputation.

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

Default classifier:

- 16 coarse groups from:
  - last transaction present
  - online flag
  - card present flag
  - unknown merchant flag
- 32 bins for minutes since last transaction
- 32 bins for km from last transaction
- 16 bins for amount
- 16 bins for km from home

Total:

```text
16 * 32 * 32 * 16 * 16 = 4,194,304 fine buckets
```

At startup the API builds:

```text
groupResponseIndexes[group] -> response index 0..5
```

Each request does:

```text
request -> vector -> fine group -> response index -> JSON bytes
```

This is intentionally O(1) after parsing.

Tradeoff:

- This prioritizes ranking performance under load.
- It is not guaranteed to match nearest-neighbor reference scoring for every request.
- The implementation keeps only this production path in the repository.

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
- direct header and `Content-Length` parsing
- manual `Utf8JsonReader` parser
- no JSON model binding in `/fraud-score`
- precomputed full HTTP/1 JSON responses
- flat arrays for MCC risk lookup
- `stackalloc` for request vectors
- no per-request response serialization
- no hot-path logging
- grouped binary dataset
- O(1) default classifier

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

Generate binary data:

```bash
dotnet run --project src/DataConverter/DataConverter.csproj -- data/
```

Run one API locally over TCP:

```bash
DATA_PATH=data/references.bin dotnet run --project src/WebApi/WebApi.csproj
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
  - `rinha-benchmark-YYYYMMDDHHMMSS-SHA.json` immutable run result

Run from GitHub Actions:

1. Open **Actions**.
2. Select **Official-like Benchmark**.
3. Choose compose file:
   - `docker-compose.yml` for nginx stream baseline
4. Run workflow.

Manual runs can also benchmark a pushed image by filling `webapi_image`; when set,
the workflow pulls that image and starts Compose with `--no-build`.

## GitHub Pages

The repository publishes an Astro documentation site from `docs/`.

Pages structure:

- `/` home dashboard with latest official-like benchmark metrics
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
- converter creates `references.bin`.
- API is published with NativeAOT.
- runtime image contains only API binary plus data files.

Publishing:

- pushes to `main` trigger the amd64 build workflow
- successful workflow publishes `ghcr.io/jonathanperis/rinha4-back-end-dotnet:latest`
- the `submission` branch references this tag

Official preview trigger:

```text
rinha/test jonathanperis-dotnet
```

Preview issues are opened in the official Rinha repository after `main` image build and `submission` branch push finish.

## Next Work

Main blocker for top-10 target:

- keep nginx stream throughput
- remove the remaining rare EOF failures under 500 VU
- lower p99 toward single-digit milliseconds

Likely next moves:

1. Harden raw HTTP/1 connection handling until repeated full runs show 0 EOF.
2. Continue nginx stream tuning around backlog, upstream retry, and connection churn.
3. Compare with official-style load locally.

## License

MIT
