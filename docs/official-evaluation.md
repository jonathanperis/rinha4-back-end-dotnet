# Official evaluation test-suite gate

This repo uses the public Rinha de Backend 2026 test suite as an official-like rejection gate.

Pinned public evaluation reference:

- upstream: `zanfranceschi/rinha-de-backend-2026`
- ref: `645165cbc88a637c78bd6d5cc07bae4dbe422567`
- docs: `docs/en/EVALUATION.md`
- k6 script/data: `test/test.js`, `test/test-data.json`

The official docs state the public k6 script may differ from the final evaluator, so local/GitHub runs here are calibration evidence, not automatic official promotion.

## Score facts that matter

- `final_score = p99_score + detection_score`.
- `p99 <= 1ms` saturates `p99_score` at `3000`; sub-1ms work does not add official score.
- `p99 > 2000ms` cuts latency to `-3000`.
- Weighted detection errors are `E = 1*FP + 3*FN + 5*HTTP errors`.
- If `(FP + FN + HTTP errors) / total > 15%`, detection score is fixed at `-3000`.
- HTTP errors are the worst failure mode; prefer a valid fast fallback response over throwing/timeout paths.

## Run locally

```sh
OFFICIAL_REF=645165cbc88a637c78bd6d5cc07bae4dbe422567 \
BENCHMARK_REPETITIONS=3 \
BENCHMARK_K6_MODE=docker \
bash scripts/ci-official-benchmark.sh
```

For GitHub Actions, run the benchmark workflow and keep `official_ref=645165cbc88a637c78bd6d5cc07bae4dbe422567` unless intentionally checking a newer upstream test suite.

## Promotion interpretation

- Correctness/stability gate: `false_positive_detections=0`, `false_negative_detections=0`, `http_errors=0` is the desired candidate lane.
- .NET score-max target: official-like p99 must cross below `1ms`; below `~1.024ms` is the current useful threshold to beat the high .NET score lane.
- C/YOLO lanes already below `1ms` in prior official evidence; for them this gate mainly guards regressions and verifies participant-file restoration/retests.
