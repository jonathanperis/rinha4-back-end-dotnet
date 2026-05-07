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
6. `FraudVectorizer` builds a normalized 14-dimensional vector.
7. `FraudScorer` maps vector to the active classifier.
8. `HttpResponses` writes a prebuilt HTTP/JSON response.

## Data pipeline

`src/DataConverter` converts `data/references.json.gz` into `data/references.bin` during image build.

When `BUILD_IVF=true` or `--ivf` is enabled, the converter also writes
`data/references.ivf.bin` for the experimental search path and
`data/references.exact.bin` for optional exact float32 rerank.

The binary format stores:

- metadata header
- `RHD7` magic
- one precomputed response index per fine bucket

The runtime loads this compact table once and avoids startup scans or per-request allocation-heavy structures.

`references.bin` is currently about `4.0 MB` because it stores bucket response indexes instead of full reference vectors.

The optional `references.ivf.bin` stores:

- `IVF1` magic
- trained centroids
- per-cluster bounding boxes
- packed int16 vector blocks
- labels and original ids for deterministic top-five tie-breaking
- optional exact float32 rows for reranking retained candidates

## Classifier

Default mode uses fine-bucket majority lookup. It remains the production fallback.

`SCORER_MODE=ivf` loads the optional IVF index and runs nearest-cluster search.
Current experiment settings target `nprobe=1`, bbox repair, and second-pass
repair on fraud counts `1..4`. When `references.exact.bin` exists, the retained
int16 candidates are reranked in float32. If the IVF file is missing or invalid,
startup falls back to bucket scoring.

## Startup readiness

Compose checks for both API Unix socket files before nginx starts. This keeps `/ready` from succeeding while nginx still has missing upstream sockets.
