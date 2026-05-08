# Architecture

```text
k6 / judge
    |
    v
nginx stream :9999
    |
    +-- unix:/sockets/api1.sock -> WebApi NativeAOT
    |
    +-- unix:/sockets/api2.sock -> WebApi NativeAOT
```

## Request path

1. nginx accepts TCP on port `9999`.
2. nginx stream forwards bytes to API instances over Unix Domain Sockets.
3. `RawHttpServer` accepts the socket connection.
4. `HttpWire` parses method, path, headers, and `Content-Length`.
5. `FraudRequestParser` reads only required JSON fields.
6. `FraudScorer` builds a normalized 14-dimensional vector.
7. `FraudScorer` maps vector to the IVF classifier.
8. `HttpResponses` writes a prebuilt HTTP/JSON response.

## Data pipeline

`src/DataConverter` converts `data/references.json.gz` into `data/references.ivf.bin` during image build.

The `references.ivf.bin` file stores:

- `IVF2` magic
- trained int16 centroids in dimension-major layout
- per-cluster int16 bounding boxes in dimension-major layout
- packed int16 vector blocks
- labels and original ids for deterministic top-five tie-breaking

## Classifier

Default and only runtime mode uses IVF.

Startup loads the IVF index and runs nearest-cluster search. Current settings
target `nprobe=1`, one-pass full bbox repair, and rounded int16 squared L2
ranking. If the IVF file is missing or invalid, startup fails.

## Startup readiness

Compose checks for both API Unix socket files before nginx starts. This keeps `/ready` from succeeding while nginx still has missing upstream sockets.
