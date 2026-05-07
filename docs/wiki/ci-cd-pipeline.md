# CI/CD Pipeline

Main build flow:

1. Build amd64 Docker image.
2. Push immutable `ci-${GITHUB_SHA}` tag to GHCR.
3. Start Docker Compose with that exact image.
4. Clone official Rinha 2026 repo.
5. Run public `test/test.js` through k6.
6. Upload raw benchmark artifacts.
7. Archive summarized JSON into `docs/public/reports`.
8. GitHub Pages deploys the docs site.

The automatic main-branch benchmark runs against the immutable image tag built in the same workflow, not a locally rebuilt image.

Manual **Official-like Benchmark** runs can archive experiment reports too.
For IVF, dispatch with `report_kind=experiment`, `BUILD_IVF=true`,
`SCORER_MODE=ivf`, `IVF_FAST_NPROBE=1`, `IVF_FULL_NPROBE=1`, bbox repair on,
exact rerank on with `IVF_RERANK_CANDIDATES=6`, and repair fraud range `1..4`.

## Report files

| File | Purpose |
| --- | --- |
| `latest.json` | latest benchmark result |
| `latest-candidate.json` | latest default submission-stack result |
| `latest-experiment.json` | latest non-default experiment result |
| `index.json` | sorted benchmark history |
| `rinha-benchmark-*.json` | immutable benchmark records |
| `rinha-benchmark-*.html` | k6 HTML reports when generated |

The report archive commit is docs-only. The build workflow ignores `docs/**`, so report commits do not trigger a new benchmark loop.

When benchmark reports change, the build workflow triggers the Pages workflow so `/reports/` refreshes without manual action.
The manual benchmark workflow does the same refresh after archiving a report.
