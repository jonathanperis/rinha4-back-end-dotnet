# .NET 11 Preview 4 A/B Results

Date: 2026-05-21

## Candidate under test

Same source commit for both runtime lanes:

- Commit: `7dc4a321b5ce8ed2912c9671f0bb25455f61517d`
- Build workflow commit: `build: add dotnet 11 preview4 ab axis`

Images:

| Lane | Image | Manifest digest |
| --- | --- | --- |
| .NET 10 baseline | `ghcr.io/jonathanperis/rinha4-back-end-dotnet:net10-ci-7dc4a321b5ce8ed2912c9671f0bb25455f61517d` | `sha256:857343c82c7c96ab3b6b3d98a7d64bd21e8fb4a9c4bb35f3fac6ea21f58479f7` |
| .NET 11 Preview 4 | `ghcr.io/jonathanperis/rinha4-back-end-dotnet:net11-preview4-ci-7dc4a321b5ce8ed2912c9671f0bb25455f61517d` | `sha256:659bdccb7b55668c2032230bc60d510bada9b220255ed1711e6c000a5b21f5dc` |

## Implementation summary

- Parameterized `src/WebApi/Dockerfile` with SDK image, ASP.NET image, and TFM build args.
- Added a `RinhaTargetFramework` override to `src/WebApi/WebApi.csproj` so default builds remain `net10.0`, while CI can publish `net11.0` without changing the default project target.
- Added a manual `dotnet_version` axis to `.github/workflows/build.yml`:
  - `10` keeps baseline tags including `latest`, `ci-<sha>`, and semver tags.
  - `11-preview4` publishes isolated `net11-preview4-ci-<sha>` / `net11-preview4-latest` tags only.

## Build / smoke verification

| Check | Result |
| --- | --- |
| Workflow YAML parse via `js-yaml` | pass |
| Local .NET 11 SDK build, `-p:RinhaTargetFramework=net11.0` | pass |
| Local .NET 10 SDK build, default target | pass |
| Local Docker build | not available on this host: Docker daemon unavailable |
| CI .NET 10 build workflow | pass, run `26232316470` |
| CI .NET 11 Preview 4 build workflow | pass, run `26232341034` |

Local NativeAOT publish probe against `linux-musl-x64` reached native code generation but failed on this Ubuntu host because host `gcc` did not support `--target=x86_64-linux-musl`. The CI Docker build is the meaningful NativeAOT verification because it runs inside the Alpine SDK image with the expected toolchain.

## 5-repetition official-like benchmark

Benchmark workflow runs:

| Lane | Workflow run | Repetitions | Image |
| --- | --- | ---: | --- |
| .NET 10 baseline | `26233122308` | 5 | `net10-ci-7dc4a321b5ce8ed2912c9671f0bb25455f61517d` |
| .NET 11 Preview 4 | `26233124084` | 5 | `net11-preview4-ci-7dc4a321b5ce8ed2912c9671f0bb25455f61517d` |

Per-repetition artifacts parsed from every `results-repetition-*.json` file:

| Lane | Rep | p99 | Final score | FP | FN | HTTP errors |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| .NET 10 | 1 | 0.34ms | 5487.73 | 38 | 4 | 0 |
| .NET 10 | 2 | 0.34ms | 5487.73 | 38 | 4 | 0 |
| .NET 10 | 3 | 0.34ms | 5487.73 | 38 | 4 | 0 |
| .NET 10 | 4 | 0.35ms | 5487.73 | 38 | 4 | 0 |
| .NET 10 | 5 | 0.34ms | 5487.73 | 38 | 4 | 0 |
| .NET 11 Preview 4 | 1 | 0.37ms | 5487.73 | 38 | 4 | 0 |
| .NET 11 Preview 4 | 2 | 0.39ms | 5487.73 | 38 | 4 | 0 |
| .NET 11 Preview 4 | 3 | 0.40ms | 5487.73 | 38 | 4 | 0 |
| .NET 11 Preview 4 | 4 | 0.40ms | 5487.73 | 38 | 4 | 0 |
| .NET 11 Preview 4 | 5 | 0.41ms | 5487.73 | 38 | 4 | 0 |

Aggregate:

| Lane | Median p99 | Mean p99 | Range | Correctness |
| --- | ---: | ---: | ---: | --- |
| .NET 10 | 0.34ms | 0.342ms | 0.34-0.35ms | clean: 38 FP / 4 FN / 0 HTTP errors |
| .NET 11 Preview 4 | 0.40ms | 0.394ms | 0.37-0.41ms | clean: 38 FP / 4 FN / 0 HTTP errors |

Delta:

- .NET 11 Preview 4 median p99 is `+0.06ms` vs .NET 10.
- That is about `+17.6%` slower vs the .NET 10 median.
- .NET 10 had lower p99 in all 5/5 repetitions.
- Correctness was identical, so this is a performance-only rejection.

## Decision

Do **not** promote .NET 11 Preview 4 as the default runtime for this implementation.

Keep .NET 10 as the default because the same-source .NET 11 Preview 4 lane is correctness-clean but consistently slower in this 5-repetition hosted-runner decision series. The isolated .NET 11 build axis can remain useful for future preview/regression checks, but it should not change `latest`, semver, submission, or default project target behavior.
