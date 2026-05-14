# Architecture

```text
k6 / judge
    |
    v
rinha4-lb-yolo-mode :9999
    |
    +-- unix:/sockets/api1.sock -> WebApi NativeAOT
    |
    +-- unix:/sockets/api2.sock -> WebApi NativeAOT
```

## Request path

1. The standalone yolo load balancer accepts TCP on port `9999` in `proxy` mode.
2. It forwards bytes to API instances over Unix Domain Sockets.
   The compose file lets `webapi1` run on cpuset `0,1`, `webapi2` on `2,3`, and
   the lightweight `lb` on `0,2`. CPU quotas still total `1.00`; cpuset reduces scheduler
   contention under the official host.
3. `RawHttpServer` accepts the socket connection.
4. `HttpWire` parses method, path, headers, and `Content-Length`.
5. `FraudRequestParser` reads only required JSON fields.
6. `FraudScorer` builds a normalized 14-dimensional vector.
7. `FraudScorer` maps vector to the IVF classifier.
8. `HttpResponses` writes a prebuilt HTTP/JSON response.

## Data pipeline

`src/DataConverter` converts `data/references.json.gz` into `data/references.ivf.bin` during image build.

The `references.ivf.bin` file stores:

- `IVF2` magic for the candidate default
- trained int16 centroids in dimension-major layout
- per-cluster int16 bounding boxes in dimension-major layout
- packed int16 vector blocks
- labels and original ids for deterministic top-five tie-breaking

## Classifier

Default and only runtime mode uses IVF.

Startup loads the IVF index and runs nearest-cluster search. Current settings
target `nprobe=1`, one-pass full bbox repair, and rounded int16 squared L2
ranking. IVF2 uses int64 accumulation for accuracy. If the IVF file is missing
or invalid, startup fails.

Runtime implementation is split into focused partial files:

- `IvfIndex.cs`: binary loading, validation, immutable arrays, and search dispatch
- `IvfIndex.Int64.cs`: IVF2 candidate path for `IVF_SCALE=10000`

## Startup readiness

Each API process recreates its Unix socket file on startup in the shared
`sockets` tmpfs volume. The standalone LB consumes `/sockets/api1.sock` and
`/sockets/api2.sock` and keeps the proxy layer byte-oriented.
