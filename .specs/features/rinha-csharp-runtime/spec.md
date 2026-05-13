# Rinha C# Runtime Spec

## Requirements

- RCSR-001: API exposes `GET /ready` and `POST /fraud-score` through load balancer port `9999`.
- RCSR-002: `/ready` returns `2xx` only when both API instances can accept requests.
- RCSR-003: `/fraud-score` returns HTTP `200` with JSON containing `approved` and `fraud_score`.
- RCSR-004: Fraud score maps top-5 fraud counts to `0.0`, `0.2`, `0.4`, `0.6`, `0.8`, `1.0`.
- RCSR-005: `approved` is true only when fraud count is `0`, `1`, or `2`.
- RCSR-006: Vectorization follows official 14 dimensions and normalization constants.
- RCSR-007: `last_transaction: null` maps dimensions 6 and 7 to `-1` sentinel values.
- RCSR-008: MCC lookup uses `mcc_risk.json` values and defaults unknown MCC to `0.5`.
- RCSR-009: Unknown merchant flag is `1` when merchant id is absent from `known_merchants`, else `0`.
- RCSR-010: Load balancer distributes requests evenly without fraud logic or body transforms.
- RCSR-011: Docker topology has at least one LB and two API instances.
- RCSR-012: Declared service limits total <= `1 CPU / 350 MB`.
- RCSR-013: Images are public and `linux/amd64` compatible for submission.
- RCSR-014: `submission` branch contains runnable files only.
- RCSR-015: Runtime avoids hot-path logging and avoidable allocation.
- RCSR-016: Benchmark data records image, commit, config, p99, failure rate, and score.
- RCSR-017: Accuracy-affecting shortcuts must state FP/FN risk and pass focused validation before promotion.

## Acceptance

- Vectorization tests pass with `dotnet run --project test/VectorizationTests/VectorizationTests.csproj --no-restore`.
- Accuracy probe can replay public official data with `0` FP, `0` FN, and `0` HTTP errors for candidate configs.
- Docker Compose config remains valid under selected LB override.
- Build workflow can publish public `linux/amd64` GHCR image from `main`.
- Benchmark workflow can run official-like k6 from GitHub Actions and archive result.
- Official preview can be triggered from promoted `submission` branch.

Local verification is useful for fast debugging only. CI/official evidence decides promotion.
