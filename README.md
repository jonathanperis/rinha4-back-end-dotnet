# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026) fraud detection.

Goal: official-valid, low-p99 backend under `1 CPU / 350 MB`, with correctness protected before latency experiments. The active competitive target is the public top .NET lane.

## Current stack

- standalone `rinha4-lb-yolo-mode` reverse proxy on port `9999`
- two .NET 10 NativeAOT API instances reached through fd-pass control sockets
- raw socket HTTP/1 server, not ASP.NET Core
- manual request parsing with `Utf8JsonReader`
- prebuilt HTTP responses for fraud decisions
- build-time conversion from challenge JSON resources to compact bucket and IVF binary data
- hybrid scorer: bucket fast path first, IVF fallback for correctness-sensitive decisions
- GitHub Actions benchmark archive and GitHub Pages report history

## Contract

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/ready` | readiness probe |
| `POST` | `/fraud-score` | fraud decision |

Runtime shape:

- load balancer on port `9999`
- two API instances
- Docker bridge network
- public `linux/amd64` images for submission
- total limits <= `1 CPU / 350 MB`
- current compose split: `0.425 CPU / 165 MB` per API and `0.15 CPU / 20 MB` for the load balancer
- load balancer only distributes traffic; it does not inspect fraud payloads

## Architecture

```text
k6 / judge
    |
    v
rinha4-lb-yolo-mode :9999
    |
    +-- fdpass:/sockets/api1.sock.ctrl -> .NET NativeAOT raw HTTP server
    |
    +-- fdpass:/sockets/api2.sock.ctrl -> .NET NativeAOT raw HTTP server
```

The load balancer listens on TCP `:9999`, chooses an upstream, and passes the accepted client file descriptor over a Unix control socket. With `FD_RAW=1`, the API keeps that fd on a low-level `recv`/`send` path instead of wrapping each handoff in a managed `Socket`.

Hot path goals:

- raw socket HTTP
- HTTP/1 keep-alive
- no request-path logging
- minimal JSON scanning
- prebuilt response bytes
- compact int16 vector/index layout
- fast bucket decisions with IVF fallback/repair when accuracy needs it

## Data and classifier

Challenge data starts in `data/`:

- `references.json.gz`
- `normalization.json`
- `mcc_risk.json`

`src/DataConverter` builds runtime binary data during the Docker image build. The current hybrid runtime loads `references.bucket.bin` for the bucket fast path and `references.ivf.bin` for fallback search. `references.bin` is still generated for the explicit exact diagnostic mode used by tests and manual benchmark experiments.

The request is normalized into the official 14 fraud-vector dimensions, then classified by top-5 nearest reference labels. The response is:

```json
{ "approved": true, "fraud_score": 0.2 }
```

## Local

Restore and build:

```sh
dotnet restore src/WebApi/WebApi.csproj
dotnet build src/WebApi/WebApi.csproj -c Release --no-restore
```

Generate runtime data:

```sh
dotnet run --project src/DataConverter/DataConverter.csproj -c Release -- data/
```

Run tests:

```sh
dotnet restore tests/VectorizationTests/VectorizationTests.csproj
dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
```

Run one API locally over TCP:

```sh
DATA_DIR=data \
IVF_PATH=data/references.ivf.bin \
BUCKET_PATH=data/references.bucket.bin \
SCORER_MODE=hybrid \
  dotnet run --project src/WebApi/WebApi.csproj -c Release --no-restore
```

Run the compose stack:

```sh
docker compose up --build
curl -i http://localhost:9999/ready
```

Full compose and benchmark validation require Docker daemon access. If local Docker is unavailable, use the GitHub Actions benchmark workflow for official-like checks.

Smoke request:

```sh
curl -i -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  --data '{"id":"tx-smoke","transaction":{"amount":1,"installments":1,"requested_at":"2026-03-11T20:23:35Z"},"customer":{"avg_amount":1,"tx_count_24h":0,"known_merchants":[]},"merchant":{"id":"MERC-001","mcc":"5912","avg_amount":1},"terminal":{"is_online":false,"card_present":true,"km_from_home":0},"last_transaction":null}'
```

## Docs and reports

GitHub Pages lives under `docs/`:

- `/` home dashboard
- `/docs/` long-form system notes from `docs/wiki/*.md`
- `/reports/` benchmark archive

The site uses GitHub Linguist's C# language color (`#178600`) as its accent.

## Branches

- `main`: source, tests, docs, workflows.
- `submission`: runnable files only for official runner.
- `comparison`: isolated comparison stacks and benchmark workflow.

## Official evaluation gate

This repository's manual `Official-like Benchmark` workflow and `scripts/ci-official-benchmark.sh` run the public Rinha 2026 k6 suite pinned to `645165cbc88a637c78bd6d5cc07bae4dbe422567` by default. See `docs/official-evaluation.md` for scoring thresholds and how to run the gate locally.

## License

MIT
