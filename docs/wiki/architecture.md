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
7. `FraudScorer` tries the bucket fast path and falls back to IVF search when needed.
8. `HttpResponses` writes a prebuilt HTTP/JSON response.

## Data pipeline

`src/DataConverter` converts `data/references.json.gz` into runtime binary data during image build.

The current hybrid runtime uses:

- `references.bucket.bin` for the coarse bucket fast path
- `references.ivf.bin` for IVF fallback search and repair

`references.bin` is also generated for the explicit exact diagnostic mode used by tests and manual benchmark experiments; it is not the default submission scorer.

The `references.ivf.bin` file stores:

- `IVF2` magic for the current int64-distance IVF layout
- trained int16 centroids in dimension-major layout
- per-cluster int16 bounding boxes in dimension-major layout
- packed int16 vector blocks
- labels and original ids for deterministic top-five tie-breaking

## Classifier

Default submission runtime uses `SCORER_MODE=hybrid`. Hybrid mode queries the bucket index first and returns a fast-path fraud count when the profile/reference rules are confident. If the bucket path cannot decide, `FraudScorer` falls back to the IVF index with the configured repair options.

Current defaults target bucket fast paths plus IVF `nprobe=1`, bounding-box repair, rounded int16 squared L2 ranking, and tuned distance thresholds for safe `0/5` approvals and `5/5` denials. If required bucket or IVF files are missing or invalid, startup fails instead of silently serving with a weaker classifier.

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
