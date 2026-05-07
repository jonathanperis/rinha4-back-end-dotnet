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

