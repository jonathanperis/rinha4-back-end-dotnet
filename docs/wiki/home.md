# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for Rinha de Backend 2026.

The current build is optimized for latency first:

- raw socket HTTP/1 server
- Unix Domain Sockets behind nginx stream proxy
- manual JSON request parsing
- prebuilt HTTP responses
- fine-bucket fraud classifier
- archived official-like k6 results after each main build

The project target is explicit: top-10 ranking, p99 close to 1ms, and 0% failures.

## Current signal

Latest CI benchmark history lives at `/reports/`.

Those results are useful for regression tracking. They are not official Rinha hardware results.

## Repository map

| Path | Purpose |
| --- | --- |
| `src/WebApi` | NativeAOT fraud-score server |
| `src/DataConverter` | Converts official reference data into `references.bin` |
| `data` | Allowed challenge datasets and normalization files |
| `test` | Focused validation tests |
| `scripts` | Benchmark and report archive automation |
| `docs/public/reports` | Versioned benchmark JSON history |

