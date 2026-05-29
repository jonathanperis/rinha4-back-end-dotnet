# Architecture

```text
k6 / judge
    |
    v
rinha4-lb-yolo-mode :9999
    |
    +-- fdpass:/sockets/api1.sock.ctrl -> WebApi NativeAOT
    |
    +-- fdpass:/sockets/api2.sock.ctrl -> WebApi NativeAOT
```

## Request path

1. The standalone yolo load balancer accepts TCP on port `9999` in `fdpass` mode.
2. It selects an API instance and passes the accepted client fd over a Unix control socket.
   The compose file pins `webapi1` to cpuset `0,1`, `webapi2` to `2,3`, and
   `lb` to `0,2`. CPU quotas still total `1.00`; the overlapping cpuset keeps the proxy close to both API lanes.
3. `RawHttpServer` accepts the fd-pass control message and serves the client fd directly when `FD_RAW=1`.
4. `HttpWire` parses method, path, headers, and `Content-Length`.
5. `FraudRequestParser` reads only required JSON fields.
6. `FraudScorer` builds a normalized 14-dimensional vector.
7. `FraudScorer` runs the default IVF path, then bounded repair when configured. Bucket/hybrid fast paths are available only when `SCORER_MODE=bucket` or `SCORER_MODE=hybrid` is selected for experiments.
8. `HttpResponses` writes a prebuilt HTTP/JSON response.

## Data pipeline

`src/DataConverter` converts `data/references.json.gz` into runtime binary data during image build.

The current clean runtime uses `references.ivf.bin` for IVF search and repair.
The image also carries:

- `references.bucket.bin` for bucket or hybrid experiment modes
- `references.bin` for the explicit exact diagnostic mode used by tests and manual benchmark experiments

Only the IVF file is required by the default submission scorer; missing or invalid default scorer data fails startup instead of silently degrading correctness.

The `references.ivf.bin` file stores:

- `IVF2` magic for the current int64-distance IVF layout
- trained int16 centroids in dimension-major layout
- per-cluster int16 bounding boxes in dimension-major layout
- packed int16 vector blocks
- labels and original ids for deterministic top-five tie-breaking

## Classifier

Default submission runtime uses `SCORER_MODE=ivf` against the current official `main` dataset. It scans the two nearest IVF centroid clusters, then uses bounding-box repair to preserve exact top-five decisions while keeping the clean `6000` correctness gate.

Current defaults target IVF `nprobe=2`, bounding-box repair, rounded int16 squared L2 ranking, and tuned distance thresholds for safe `0/5` approvals and `5/5` denials. If the required IVF file is missing or invalid, startup fails instead of silently serving with a weaker classifier. The bucket/hybrid path remains available for explicit latency experiments, not as the clean default.

Runtime implementation is split into focused files:

- `BucketIndex.cs`: bucket lookup, profile/reference fast paths, risky fallback, and exact scan helpers
- `BucketSearchOptions.cs`: `BUCKET_*` runtime controls
- `IvfIndex.cs`: binary loading, validation, immutable arrays, and search dispatch
- `IvfIndex.Int64.cs`: IVF2 candidate path for `IVF_SCALE=10000`
- `FraudScorer.cs`: normalization, mode selection, and bucket/IVF orchestration

## Startup readiness

Each API process recreates its fd-pass control socket on startup in the shared
`sockets` tmpfs volume. The standalone LB consumes `/sockets/api1.sock.ctrl` and
`/sockets/api2.sock.ctrl`, then stays out of the fraud payload path after handing
off the accepted client fd.
