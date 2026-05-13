# State

## Current

- Runtime target: .NET 10 NativeAOT, raw HTTP/1, Unix domain sockets, prebuilt responses.
- Active scorer target: `SCORER_MODE=hybrid` with bucket fast paths plus IVF repair/fallback.
- Active LB target for best CI candidate: Forevis override (`docker-compose.forevis.yml`).
- Correctness first; p99 second.

## Known Results

- Best known same-CI candidate: run `25822565412`, p99 `1.33ms`, score `5875.65`, `0%` failures.
- Pedro same-CI reference: run `25808383580`, p99 `0.42ms`, score `6000`, `0%` failures.
- Latest known dotnet official preview: issue `#4038`, p99 `1.88ms`, score `5726.08`, `0%` failures.
- Docs `docs/public/official/latest.json` may be stale; re-sync before using as evidence.

## Decisions

- Competition rules are hard constraints.
- `0%` failures outranks p99 experiments unless user accepts accuracy risk.
- Official preview outranks CI; CI outranks local; local outranks theory.
- Do not commit generated `data/references.bucket.bin` through normal Git; file exceeds GitHub `100 MB` limit.
- Do not mix documentation/spec commits with unvalidated hot-path experiments.
- Keep benchmark changes small and reversible.

## Active Runtime Defaults

- `SCORER_MODE=hybrid`.
- `IVF_CLUSTERS=512`.
- `IVF_TRAIN_SAMPLE=65536`.
- `IVF_ITERATIONS=6`.
- `IVF_SCALE=10000`.
- `IVF_FAST_NPROBE=1`.
- `IVF_FULL_NPROBE=1`.
- `IVF_BOUNDARY_FULL=false`.
- `IVF_BBOX_REPAIR=true`.
- `IVF_ZERO_FAST_APPROVE_WORST_DISTANCE=5000000`.
- `IVF_FIVE_FAST_DENY_WORST_DISTANCE=4000000`.
- `BUCKET_REFERENCE_FASTPATH_LEGIT=false`.
- `BUCKET_REFERENCE_FASTPATH_FRAUD=true`.
- `BUCKET_REFERENCE_FASTPATH2_LEGIT=true`.
- `BUCKET_REFERENCE_FASTPATH2_FRAUD=true`.
- `ACCEPT_LOOPS=2`.
- `MIN_WORKER_THREADS=128`.

## Open Work

- Validate any local `src/WebApi/RawHttpServer.cs` hot-path experiment before committing.
- Continue transport/parser/index inspections only with focused tests or CI benchmark evidence.
- Beat Pedro on same CI before official submission attempt.
- Resolve bucket index artifact strategy if generated binary must be distributed outside image build.

## Risks

- CI p99 wins may not transfer to official runner.
- ANN/bucket fast paths can pass public replay and still create preview FP/FN.
- Parser shortcut can break if official payload shape/order shifts.
- Load balancer readiness regression can create HTTP errors and destroy score.
- Memory growth can exceed `350 MB` cap.
