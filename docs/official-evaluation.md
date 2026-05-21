# Official evaluation test-suite gate

This repo uses the public Rinha de Backend 2026 test suite as an official-like rejection gate.

Current public evaluation reference:

- upstream: `zanfranceschi/rinha-de-backend-2026`
- ref: `main` unless a run explicitly documents another ref
- docs: `docs/en/EVALUATION.md`
- k6 script/data: `test/test.js`, `test/test-data.json`

The official docs state the public k6 script may differ from the final evaluator, so local/GitHub runs here are calibration evidence, not automatic official promotion. The CI default intentionally tracks upstream `main` so we do not optimize against stale preview data.

## Rules/compliance gate

The CI benchmark script validates the resolved compose before starting the stack:

- at least `webapi1`, `webapi2`, and `lb` services are present;
- `lb` publishes/listens on port `9999`;
- no service uses `privileged: true`;
- no service uses `network_mode: host`;
- every service declares CPU and memory limits;
- total service limits stay at or below `1 CPU / 350 MB`.

The benchmark may run the k6 client in native mode or in a separate one-shot Docker client container. That client is not part of the submitted solution stack and must not be counted as an implementation service.

## Score facts that matter

- `final_score = p99_score + detection_score`.
- `p99 <= 1ms` saturates `p99_score` at `3000`; sub-1ms work does not add official score.
- `p99 > 2000ms` cuts latency to `-3000`.
- Weighted detection errors are `E = 1*FP + 3*FN + 5*HTTP errors`.
- If `(FP + FN + HTTP errors) / total > 15%`, detection score is fixed at `-3000`.
- HTTP errors are the worst failure mode; prefer a valid fast fallback response over throwing/timeout paths.

## Run locally

```sh
OFFICIAL_REF=main \
BENCHMARK_REPETITIONS=3 \
BENCHMARK_K6_MODE=docker \
bash scripts/ci-official-benchmark.sh
```

For GitHub Actions, run the benchmark workflow with the default `official_ref=main`. Only use an older ref for explicitly labeled historical/scoreboard reproduction, never for current promotion evidence.

## Promotion interpretation

- Correctness/stability gate: `false_positive_detections=0`, `false_negative_detections=0`, `http_errors=0` is the desired candidate lane.
- .NET score-max target: official-like p99 must cross below `1ms`; below `~1.024ms` is the current useful threshold to beat the high .NET score lane.
- C/YOLO lanes already below `1ms` in prior official evidence; for them this gate mainly guards regressions and verifies participant-file restoration/retests.
