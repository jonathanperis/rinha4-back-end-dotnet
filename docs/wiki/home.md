# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for Rinha de Backend 2026.

The current build is optimized for latency first:

- raw socket HTTP/1 server
- Unix Domain Sockets behind nginx stream proxy
- socket-file healthchecks before nginx starts
- manual JSON request parsing
- prebuilt HTTP responses
- rounded int16 IVF fraud classifier with an experimental IVF3 int32 scan path
- archived official-like k6 results after each main build
- one-core CI contention probe for candidate benchmarks

The project target is explicit: top-10 ranking, p99 close to 1ms, and 0% failures.

## Current signal

Latest CI benchmark history lives at `/reports/`.

The home page reads the latest official Rinha issue result from
`docs/public/official/latest.json` and the latest CI candidate result from
`docs/public/reports/latest-candidate.json`.

CI results are useful for regression tracking. They are not official Rinha
hardware results. Current candidate CI runs pin service containers to one host
CPU to reduce the gap between GitHub-hosted runners and official preview runs.

## Active lane

Transport is currently stable in CI. The active lane is the IVF
approximate-nearest-neighbor index built from the allowed reference dataset and
loaded at startup.

## Repository map

| Path | Purpose |
| --- | --- |
| `src/WebApi` | NativeAOT fraud-score server |
| `src/DataConverter` | Converts official reference data into `references.ivf.bin` |
| `data` | Allowed challenge datasets and normalization files |
| `test` | Focused validation tests |
| `scripts` | Benchmark and report archive automation |
| `docs/public/reports` | Versioned benchmark JSON history |
