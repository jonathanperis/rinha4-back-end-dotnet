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
7. `FraudScorer` maps vector to a fine bucket.
8. `HttpResponses` writes a prebuilt HTTP/JSON response.

## Data pipeline

`src/DataConverter` converts `data/references.json.gz` into `data/references.bin` during image build.

The binary format stores:

- metadata header
- group offsets
- `int16` vectors with padded dimensions
- labels
- precomputed bucket response map

The runtime loads this once and avoids per-request allocation-heavy structures.

## Classifier

The hot path uses fine-bucket majority lookup.

Exact KNN modes remain available for validation, but they are not the default competition path because latency would rise.

