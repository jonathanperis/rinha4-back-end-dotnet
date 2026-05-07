# Challenge

Rinha de Backend 2026 scores implementations by latency, correctness, and request survival under the official k6 workload.

Required endpoints:

| Method | Path | Role |
| --- | --- | --- |
| `GET` | `/ready` | readiness probe |
| `POST` | `/fraud-score` | fraud decision |

Default topology:

- reverse proxy on `9999`
- two API instances
- total container budget: `1.00 CPU / 350 MB`
- no privileged container
- runnable through Docker Compose

Ranking pressure:

- lower p99 improves p99 score
- 0% failures preserves detection score
- HTTP errors destroy score quickly

This repository currently wins on transport speed in CI-like runs, but still loses ranking score through fraud classification errors. Accuracy work is the main remaining lane.

