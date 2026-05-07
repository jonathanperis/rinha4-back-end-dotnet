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

Exact KNN modes are still present for validation and experimentation, but the default competition path is the fine-bucket classifier.

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

Binary format:

```text
int32 magic = "RHD5"
int32 count
int32 dims
int32 padded_dims
int32 scale
int32 group_offsets[FineGroupCount + 1]
int16 vectors[count * padded_dims]
byte labels[count]
```

Why this format:

- `int16` vectors reduce memory and improve cache behavior.
- `padded_dims = 16` keeps exact-search data aligned for SIMD.
- group offsets make bucket lookup O(1).
- labels are stored separately for tight exact-search scans.

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

## Exact Search Modes

Exact top-5 KNN is still available:

```bash
SEARCH_MODE=exact
SEARCH_MODE=avx2
```

Modes:

- unset: fine-bucket majority classifier
- `exact`: scalar exact KNN with pruning
- `avx2`: AVX2 exact scan when supported

Exact modes are useful for correctness studies and validator work, not for the current fastest competition path.

## Hot Path Optimizations

Implemented:

- .NET 10 NativeAOT
- raw socket HTTP/1 server
- async accept loop with one task per client connection
- keep-alive request loop
- pooled per-connection read buffers
- Unix Domain Sockets between proxy and APIs
- socket cleanup on process start
- socket chmod for non-root proxy processes like Envoy
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
- exact SIMD/pruned search retained behind env switch

## Reverse Proxy A/B

Default:

```bash
docker compose up -d --force-recreate
```

Uses:

- [nginx.conf](./nginx.conf)
- stream TCP proxy
- UDS upstreams
- `20 MB` proxy memory

Envoy test:

```bash
ENVOY_IMAGE=envoyproxy/envoy:tools-dev \
docker compose -f docker-compose.yml -f docker-compose.envoy.yml up -d --force-recreate
```

Uses:

- [envoy.yaml](./envoy.yaml)
- HTTP/1 connection manager
- UDS upstreams
- `30 MB` proxy memory
- APIs reduced to `160 MB` each

HTTP nginx test:

```bash
docker compose -f docker-compose.yml -f docker-compose.nginx-http.yml up -d --force-recreate
```

Uses:

- [nginx-http.conf](./nginx-http.conf)
- HTTP reverse proxy
- UDS upstreams

Latest local A/B observations:

| Proxy | Load | Failure | p99 / p95 | Throughput | Result |
| --- | ---: | ---: | --- | ---: | --- |
| nginx stream + raw API | 200 VU debug | `0.00%` | p95 `71.38ms` | `6.75k req/s` | current raw-server baseline |
| nginx stream + raw API | 500 VU full | `0.00%` reported, 4 EOF | p99 `107.56ms` | `6.95k req/s` | far fewer failures than Kestrel path |
| nginx stream + Kestrel parser | 200 VU debug | `0.00%` | p95 `67.56ms` | `7.2k req/s` | faster at 200 VU |
| nginx stream + Kestrel parser | 500 VU full | `0.60%` | p99 `105.19ms` | `6.8k req/s` | too many failures |
| Envoy HTTP/UDS | 200 VU debug | `0.97%` | p95 `186.96ms` | `1.25k req/s` | worse locally |
| nginx HTTP | 500 VU full | `0.00%` | p99 `308.67ms` | `3.1k req/s` | clean but too slow |

Interpretation:

- Envoy is not currently a win.
- HTTP nginx removes most EOF behavior but costs too much throughput and latency.
- raw socket API greatly reduced EOF count under 500 VU, but the target remains strict 0 failures.

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

Debug load:

```bash
k6 run --quiet benchmarks/k6/status-debug.js
```

Full local pressure test:

```bash
k6 run --quiet benchmarks/k6/fraud-score.js
```

The local k6 scripts are not the official judge. They are smoke and pressure tests used to compare changes.

## Tests And Validation

Unit-style vectorization tests live under `test/`:

```bash
dotnet run --project test/VectorizationTests/VectorizationTests.csproj --no-restore
```

Build checks:

```bash
dotnet build src/WebApi/WebApi.csproj --no-restore
dotnet build src/DataConverter/DataConverter.csproj --no-restore
dotnet build tools/Validator/Validator.csproj --no-restore
```

Offline validator:

```bash
dotnet run --project tools/Validator/Validator.csproj -- data/ 100
```

The validator compares exact KNN, same-group approximation, and bucket-majority classification over sampled reference vectors.

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

## Cysharp Library Backlog

Potential A/B candidates:

- [ZLogger](https://github.com/Cysharp/ZLogger)
- [Utf8StringInterpolation](https://github.com/Cysharp/Utf8StringInterpolation)
- [Utf8StreamReader](https://github.com/Cysharp/Utf8StreamReader)

Current expectation:

- `ZLogger`: likely low value because hot-path logging is disabled.
- `Utf8StringInterpolation`: likely low value because responses are precomputed UTF-8 bytes.
- `Utf8StreamReader`: likely low value because request parsing already uses raw socket buffers plus `Utf8JsonReader`.

Still worth benchmarking only if profiling shows logging, UTF-8 formatting, or stream decoding becomes measurable.

## Next Work

Main blocker for top-10 target:

- keep nginx stream throughput
- remove the remaining rare EOF failures under 500 VU
- lower p99 toward single-digit milliseconds

Likely next moves:

1. Harden raw HTTP/1 connection handling until repeated full runs show 0 EOF.
2. Tune stream connection behavior and per-connection balancing.
3. Compare with official-style load locally.
4. Re-run Cysharp A/B only if it targets a measured bottleneck.

## License

MIT
