# rinha-2026-dotnet

.NET 10 NativeAOT implementation for [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026) — fraud detection via vector search.

## Architecture

- **Nginx** (stream L4) → **API-1** / **API-2** (Unix Domain Sockets)
- **IVF index** (K=512, nprobe=10) with two-stage scan
- **int16 quantization** (scale=10000) — 83 MB dataset
- **Pre-computed JSON responses** — zero allocation in hot path

## Performance

| Metric | Value |
|--------|-------|
| Avg latency | ~4.7 ms |
| Decision accuracy | 100% (500-sample validation) |
| Speedup vs brute-force | 35x |

## Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/ready` | GET | Health check |
| `/fraud-score` | POST | Fraud detection |

## Local Development

```bash
# Build and run DataConverter to generate references.bin
dotnet run --project src/DataConverter/DataConverter.csproj -- data/

# Run single API instance (TCP mode for local testing)
DATA_PATH=data/references.bin dotnet run --project src/WebApi/WebApi.csproj

# Test
curl http://localhost:8080/ready
curl -X POST http://localhost:8080/fraud-score \
  -H "Content-Type: application/json" \
  -d '{"id":"tx-test","transaction":{"amount":100,"installments":1,"requested_at":"2026-03-11T12:00:00Z"},"customer":{"avg_amount":200,"tx_count_24h":1,"known_merchants":["MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5411","avg_amount":150},"terminal":{"is_online":false,"card_present":true,"km_from_home":5},"last_transaction":null}'
```

## Docker (Production)

```bash
docker compose up --build
```

## Resource Limits

| Service | CPU | Memory |
|---------|-----|--------|
| nginx | 0.20 | 20 MB |
| api1 | 0.40 | 130 MB |
| api2 | 0.40 | 130 MB |
| **Total** | **1.00** | **280 MB** |

## Tech Stack

- .NET 10 / ASP.NET Core Minimal API
- NativeAOT (`PublishAot`, `ExtraOptimize`)
- Nginx stream module (L4 passthrough)
- Unix Domain Sockets
- IVF vector search with two-stage nprobe

## CI/CD

- **amd64**: Auto-build on push, semver releases
- **arm64**: Manual trigger (local Mac testing)

## License

MIT

