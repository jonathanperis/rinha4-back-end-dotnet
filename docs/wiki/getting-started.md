# Getting Started

Restore and build the app:

```bash
dotnet restore src/WebApi/WebApi.csproj
dotnet build src/WebApi/WebApi.csproj -c Release --no-restore
```

Run local stack:

```bash
docker compose up --build
curl -i http://localhost:9999/ready
```

The default compose stack runs `SCORER_MODE=hybrid`: bucket fast path first,
then IVF fallback when needed. It allocates `0.45` CPU to each WebApi container
and `0.10` CPU to the standalone proxy.

Tune IVF image-build parameters with `IVF_CLUSTERS`, `IVF_TRAIN_SAMPLE`,
`IVF_ITERATIONS`, and `IVF_SCALE` when testing alternatives. Runtime IVF repair
controls are `IVF_FAST_NPROBE`, `IVF_FULL_NPROBE`, `IVF_BOUNDARY_FULL`,
`IVF_BBOX_REPAIR`, `IVF_REPAIR_MIN_FRAUDS`, `IVF_REPAIR_MAX_FRAUDS`,
`IVF_ZERO_FAST_APPROVE_WORST_DISTANCE`, and `IVF_FIVE_FAST_DENY_WORST_DISTANCE`.

Tune bucket runtime behavior with `BUCKET_EARLY_CANDIDATES`,
`BUCKET_MIN_CANDIDATES`, `BUCKET_MAX_CANDIDATES`, `BUCKET_PROFILE_FASTPATH`,
`BUCKET_REFERENCE_FASTPATH*`, `BUCKET_PROFILE_*_MIN_COUNT`,
`BUCKET_EXACT_FALLBACK`, and `BUCKET_AVX_CUTOFF_DIMS`.

Generate runtime data without Docker:

```bash
dotnet run --project src/DataConverter/DataConverter.csproj -c Release -- data/
```

Run focused tests:

```bash
dotnet restore tests/VectorizationTests/VectorizationTests.csproj
dotnet run --project tests/VectorizationTests/VectorizationTests.csproj -c Release --no-restore
```

Run official-like benchmark locally:

```bash
bash scripts/ci-official-benchmark.sh
```

Full compose and benchmark validation require Docker daemon access. If local
Docker is unavailable, use the GitHub Actions benchmark workflow.

Run docs locally:

```bash
cd docs
bun install
bun run dev
```
