# Getting Started

Build the app:

```bash
dotnet build src/WebApi/WebApi.csproj --no-restore
```

Run local stack:

```bash
docker compose up --build
curl -i http://localhost:9999/ready
```

Run local stack with the production IVF scorer:

```bash
docker compose up --build
```

Tune IVF image-build parameters with `IVF_CLUSTERS`, `IVF_TRAIN_SAMPLE`, and
`IVF_ITERATIONS` when testing alternatives. Runtime repair controls are
`IVF_FAST_NPROBE`, `IVF_FULL_NPROBE`, `IVF_BBOX_REPAIR`,
`IVF_REPAIR_MIN_FRAUDS`, and `IVF_REPAIR_MAX_FRAUDS`.

Generate IVF data without Docker:

```bash
BUILD_IVF=true dotnet run --project src/DataConverter/DataConverter.csproj -- data/ --ivf
```

Run focused tests:

```bash
dotnet run --project test/VectorizationTests/VectorizationTests.csproj --no-restore
```

Run official-like benchmark locally:

```bash
bash scripts/ci-official-benchmark.sh
```

Run docs locally:

```bash
cd docs
bun install
bun run dev
```
