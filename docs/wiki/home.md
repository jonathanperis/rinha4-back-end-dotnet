# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for Rinha de Backend 2026.

The current build is optimized for latency first:

- raw socket HTTP/1 server
- Unix Domain Sockets behind nginx stream proxy
- socket-file healthchecks before nginx starts
- manual JSON request parsing
- prebuilt HTTP responses
- rounded int16 IVF fraud classifier
- fine-bucket fallback
- archived official-like k6 results after each main build

The project target is explicit: top-10 ranking, p99 close to 1ms, and 0% failures.

## Current signal

Latest CI benchmark history lives at `/reports/`.

Recent bucket candidate signal from GitHub Actions:

- p99: `1.35ms`
- HTTP errors: `0`
- failure rate: `2.35%`
- score: `3204.75`

Those results are useful for regression tracking. They are not official Rinha hardware results.

## Active lane

Transport is currently stable in CI. The active lane is the IVF
approximate-nearest-neighbor index built from the allowed reference dataset and
loaded by default with `SCORER_MODE=ivf`.

## Repository map

| Path | Purpose |
| --- | --- |
| `src/WebApi` | NativeAOT fraud-score server |
| `src/DataConverter` | Converts official reference data into `references.bin` and `references.ivf.bin` |
| `data` | Allowed challenge datasets and normalization files |
| `test` | Focused validation tests |
| `scripts` | Benchmark and report archive automation |
| `docs/public/reports` | Versioned benchmark JSON history |
