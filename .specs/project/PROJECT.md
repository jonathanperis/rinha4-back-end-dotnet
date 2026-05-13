# Project

## Vision

.NET 10 NativeAOT Rinha de Backend 2026 implementation optimized for official validity, `0%` failures, and low p99 latency under the `1 CPU / 350 MB` limit.

## Goals

- Implement required `GET /ready` and `POST /fraud-score` API.
- Preserve `0%` HTTP and detection failures before p99 experiments.
- Use C# NativeAOT runtime, raw HTTP/1 over Unix sockets, and preprocessed binary indexes.
- Keep scorer correctness aligned with official exact KNN semantics or explicitly measured ANN risk.
- Keep submission public, reproducible, and compatible with `linux/amd64`.

## Non-Goals

- No generic ASP.NET request pipeline on hot path.
- No payload lookup or correction tables from public/official test payloads.
- No fraud logic inside load balancer.
- No hidden private image or private repo dependency.
- No source code in `submission` branch.

## Branches

- `main`: source, tests, specs, docs, workflows, benchmark reports.
- `submission`: runnable official files only.
- Perf branches: isolated experiments; promote only measured wins.

## Verification

Primary ranking evidence comes from official preview. GitHub Actions predicts/regresses. Local runs debug only.

- Unit/vectorization smoke: `dotnet run --project test/VectorizationTests/VectorizationTests.csproj --no-restore`.
- Public accuracy probe: `dotnet run --project test/AccuracyProbe/AccuracyProbe.csproj --configuration Release --no-restore -- <test-data.json> data`.
- CI official-like benchmark: `bash scripts/ci-official-benchmark.sh` or `gh workflow run benchmark.yml --ref <branch>`.
- Official preview: issue text `rinha/test jonathanperis-dotnet` after `submission` promotion.

Local benchmark can guide changes, but never claim ranking from local or CI alone.
