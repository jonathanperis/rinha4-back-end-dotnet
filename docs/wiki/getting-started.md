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

The default compose stack runs `SCORER_MODE=ivf` with `IVF_FAST_NPROBE=2`
and bounding-box repair for current-main clean `6000` scoring. It allocates
`0.425 CPU / 165 MB` to each WebApi container and `0.15 CPU / 20 MB` to the
standalone proxy, with fd-pass control sockets and `FD_RAW=1` enabled by default.

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

Run one API directly over local TCP `:8080` without the load balancer:

```bash
DATA_DIR=data \
IVF_PATH=data/references.ivf.bin \
BUCKET_PATH=data/references.bucket.bin \
SCORER_MODE=ivf \
  dotnet run --project src/WebApi/WebApi.csproj -c Release --no-restore

curl -i http://localhost:8080/ready
```

The public contest shape remains the compose stack on `:9999`; direct TCP is for
parser/scorer smoke tests and local debugging.

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

Run docs locally. CI builds the Astro 6 site with Node 24 and Bun, so use Node
24 locally when reproducing Pages failures:

```bash
cd docs
bun install
bun run dev
```

For production parity, run `bun run build` before pushing documentation changes.
