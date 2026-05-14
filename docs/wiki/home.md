# rinha4-back-end-dotnet

.NET 10 NativeAOT implementation for Rinha de Backend 2026.

The current build is optimized for latency first:

- raw socket HTTP/1 server
- Unix Domain Sockets behind the standalone `rinha4-lb-yolo-mode` proxy
- manual JSON request parsing
- prebuilt HTTP responses
- rounded int16 IVF2 fraud classifier
- archived official-like k6 results after each main build
- optional one-core CI contention probe for mismatch diagnosis

The project target is explicit: lead the .NET entries, keep score `6000`, and keep 0 failures.

## Current signal

Latest CI benchmark history lives at `/reports/`.

The home page reads the latest official Rinha issue result from
`docs/public/official/latest.json` and the latest CI candidate result from
`docs/public/reports/latest-candidate.json`.

CI results are useful for regression tracking. They are not official Rinha
hardware results. Candidate CI runs keep the canonical `docker-compose.yml`
standalone-yolo layout; manual stress runs can pin all service containers to one
host CPU when diagnosing official-preview mismatch.

## Active lane

Transport is currently stable in CI. The active lane is the IVF
approximate-nearest-neighbor index built from the allowed reference dataset and
loaded at startup.

## Repository map

| Path | Purpose |
| --- | --- |
| `src/WebApi` | NativeAOT fraud-score server |
| `src/DataConverter` | Converts official reference data into `references.ivf.bin` |
| `data` | Allowed challenge datasets and normalization files copied into the API image |
| `tests` | Focused validation tests |
| `scripts` | Benchmark and report archive automation |
| `docs/public/reports` | Versioned benchmark JSON history |
