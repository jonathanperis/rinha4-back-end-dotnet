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

## Report files

| File | Purpose |
| --- | --- |
| `latest.json` | latest benchmark result |
| `index.json` | sorted benchmark history |
| `rinha-benchmark-*.json` | immutable benchmark records |

The report archive commit is docs-only. The build workflow ignores `docs/**`, so report commits do not trigger a new benchmark loop.

