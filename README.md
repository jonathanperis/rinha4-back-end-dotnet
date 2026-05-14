# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026) fraud detection.

Goal: official-valid, low-p99 backend under `1 CPU / 350 MB`, with correctness protected before latency experiments. The active competitive target is the public top .NET lane.

## Current stack

- standalone `rinha4-lb-yolo-mode` reverse proxy on port `9999`
- two .NET 10 NativeAOT API instances over Unix Domain Sockets
- raw socket HTTP/1 server, not ASP.NET Core
- manual request parsing with `Utf8JsonReader`
- prebuilt HTTP responses for fraud decisions
- build-time conversion from challenge JSON resources to compact IVF binary data
- int16 IVF search with bounded repair for correctness-sensitive decisions
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
- load balancer only distributes traffic; it does not inspect fraud payloads

## Architecture

```text
k6 / judge
    |
    v
rinha4-lb-yolo-mode :9999
    |
    +-- unix:/sockets/api1.sock -> .NET NativeAOT raw HTTP server
    |
    +-- unix:/sockets/api2.sock -> .NET NativeAOT raw HTTP server
```

Hot path goals:

- raw socket HTTP
- HTTP/1 keep-alive
- no request-path logging
- minimal JSON scanning
- prebuilt response bytes
- compact int16 vector/index layout
- correctness-first repair/fallback around approximate nearest-neighbor search

## Data and classifier

Challenge data starts in `data/`:

- `references.json.gz`
- `normalization.json`
- `mcc_risk.json`

`src/DataConverter` builds `references.ivf.bin` during the Docker image build. Runtime containers use that binary directly; they do not download or transform data before serving.

The request is normalized into the official 14 fraud-vector dimensions, then classified by top-5 nearest reference labels. The response is:

```json
{ "approved": true, "fraud_score": 0.2 }
```

## Local

Generate IVF data:

```sh
dotnet run --project src/DataConverter/DataConverter.csproj -- data/
```

Run tests:

```sh
dotnet run --project tests/VectorizationTests/VectorizationTests.csproj --no-restore
```

Run one API locally over TCP:

```sh
DATA_DIR=data IVF_PATH=data/references.ivf.bin \
  dotnet run --project src/WebApi/WebApi.csproj
```

Run the compose stack:

```sh
docker compose up --build
curl -i http://localhost:9999/ready
```

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

## License

MIT
