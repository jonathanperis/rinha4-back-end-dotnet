# Roadmap

## M1 - Spec Foundation

- `.specs` project and runtime docs exist.
- OpenCode project agent uses C# identity and C# language accent.
- Current rules, scorer defaults, and benchmark evidence recorded.

## M2 - Correctness Guardrails

- Vectorization tests cover official 14 dimensions.
- Parser tests/probes cover real payload shape, null `last_transaction`, known merchant, and MCC defaults.
- Public accuracy probe stays `0` FP, `0` FN before any accuracy-affecting change lands.
- HTTP path returns `200` for valid `/fraud-score` under load.

## M3 - Transport Candidate

- Raw server hot-path changes validated in unit/probe tests.
- Keep-alive, content-length parsing, and route matching stay compatible.
- LB readiness waits for both API socket files.
- CI benchmark archives p99/failure data for each candidate.

## M4 - Scorer Candidate

- Bucket/IVF changes preserve `0%` failures in CI candidate runs.
- Risky fallback filters are measured against public replay and CI.
- Candidate scan reductions do not alter uncertain decisions without quantified FP/FN risk.

## M5 - Submission

- GHCR image published for `linux/amd64`.
- `submission` branch contains runnable files only.
- Submitted compose matches benchmarked scorer/LB env.
- Official preview has `0%` failures.

## M6 - Iteration

- Compare variants on same CI baseline.
- Promote only measured wins.
- Sync official result into docs/state after preview.

## M7 - Competitor Gap Analysis

- Compare current candidate against Danilo and Pedro with same-CI, same-run evidence.
- Isolate infra, transport, fast-path, scorer/index, and runtime/threading layers.
- Prototype only rule-safe public-source-inspired ideas behind toggles.
- Promote only missing-factor wins that keep `0%` failures.
