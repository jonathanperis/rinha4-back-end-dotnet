# Design

## Components

- `src/WebApi/Program.cs`: startup, data load, scorer selection, raw server launch.
- `src/WebApi/RawHttpServer.cs`: socket accept/read/write, keep-alive loop, route dispatch.
- `src/WebApi/HttpWire.cs`: HTTP path/content-length/header parsing and response send helpers.
- `src/WebApi/HttpResponses.cs`: prebuilt complete HTTP responses for score buckets.
- `src/WebApi/FraudRequestParser.cs`: allocation-conscious JSON field extraction.
- `src/WebApi/FraudVectorizer.cs`: timestamp and feature normalization.
- `src/WebApi/FraudScorer.cs`: vector quantization and scorer dispatch.
- `src/WebApi/BucketIndex.cs`: bucket ANN, profile/reference fast paths, risky fallback.
- `src/WebApi/IvfIndex.cs`: IVF file load and search dispatch.
- `src/WebApi/IvfIndex.Int64.cs`: int64 IVF2 candidate path, AVX2 scans, bbox repair.
- `src/WebApi/ExactIndex.cs`: exact KNN fallback/test path.
- `src/DataConverter/*`: offline conversion from reference JSON to binary indexes.
- `src/Lb/rinha-lb.c`: custom C LB experiment.

## Data Flow

```text
client/k6 -> LB :9999 -> unix:/sockets/apiN.sock -> RawHttpServer -> parse -> vectorize -> scorer -> prebuilt HTTP response
```

## Runtime Shape

- NativeAOT publish targets `linux-musl-x64`/`linux-musl-arm64` from .NET 10 SDK.
- Hot path bypasses ASP.NET middleware and handles required HTTP subset directly.
- API supports keep-alive and `Content-Length` request bodies.
- Responses are precomputed for six fraud-score buckets.
- Docker image embeds generated `references.bin`, `references.ivf.bin`, and `references.bucket.bin`.

## Scorer Shape

- Exact semantics: official labels derive from 14D Euclidean top-5 KNN over 3,000,000 references.
- Hybrid runtime uses bucket fast paths for safe decisions and IVF repair/fallback for risky cases.
- `SCORER_MODE=exact` remains test/fallback path, not p99 candidate.
- Runtime knobs live in Docker env so CI can sweep without code churn.

## Load Balancers

- Base compose defines two API services and shared socket volume.
- Overrides test nginx, HAProxy, Envoy, YARP, custom C LB, and Forevis.
- Current best-known CI candidate uses Forevis LB with `0.20 CPU / 30 MB` and two APIs at `0.40 CPU / 160 MB`.
- LB must not inspect fraud payload or answer `/fraud-score`.

## Performance Notes

- Avoid logging on hot path.
- Avoid per-request allocation where practical.
- Keep parser compatible with official payload shape.
- Prefer precomputed response bytes over serialization.
- Keep memory below cap when adding index structures.
- Benchmark same compose/image/env before claiming win.
