# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for Rinha de Backend 2026.

The current build is optimized for latency first:

- raw socket HTTP/1 server
- Unix Domain Sockets behind nginx stream proxy
- socket-file healthchecks before nginx starts
- manual JSON request parsing
- prebuilt HTTP responses
- fine-bucket fraud classifier
- opt-in IVF scorer experiment
- archived official-like k6 results after each main build

The project target is explicit: top-10 ranking, p99 close to 1ms, and 0% failures.

## Current signal

Latest CI benchmark history lives at `/reports/`.

Latest candidate signal from GitHub Actions:

- p99: `1.35ms`
- HTTP errors: `0`
- failure rate: `2.35%`
- score: `3204.75`

Those results are useful for regression tracking. They are not official Rinha hardware results.

## Active lane

Transport is currently stable in CI. The main ranking gap is classifier accuracy.
The active experiment is an IVF approximate-nearest-neighbor index built from
the allowed reference dataset and loaded only with `SCORER_MODE=ivf`.

## Repository map

| Path | Purpose |
| --- | --- |
| `src/WebApi` | NativeAOT fraud-score server |
| `src/DataConverter` | Converts official reference data into `references.bin` and optional `references.ivf.bin` |
| `data` | Allowed challenge datasets and normalization files |
| `test` | Focused validation tests |
| `scripts` | Benchmark and report archive automation |
| `docs/public/reports` | Versioned benchmark JSON history |
