# Performance

Hot path choices:

- NativeAOT publish
- raw socket HTTP/1
- one task per client connection
- pooled read buffers
- Unix Domain Sockets behind proxy
- manual request parsing
- no model binding
- prebuilt response bytes
- rounded int16 IVF nearest-neighbor ranking
- nginx waits for both API socket files before serving `/ready`

## Current bottleneck

Transport is fast enough for the current target. Recent CI runs have shown `0`
HTTP errors; p99 work is now inside IVF repair and vector scan cost.

The active bottleneck is balancing full-repair accuracy with p99. Rounded IVF
matched the public benchmark locally with `0` false positives and `0` false
negatives, but the full-repair path still needs CI latency improvement.
The current best zero-failure CI lane uses `2048` IVF clusters with bbox
repair: p99 `2.76ms`, score `5559.57`. The active A/B batches IVF2 bbox
lower-bound checks over 8 clusters with AVX2 while preserving int64 candidate
distances. An earlier AVX2 full-repair variant raised p99 to `5.37ms`; a
cluster-major bbox copy raised p99 to `6.89ms`; `4096` clusters raised p99 to
`16.69ms`; `1024` clusters raised p99 to `19.78ms`.
Lower-scale IVF3 int32 A/B reduced accumulation cost structurally but was not
candidate-safe on public replay: scale `1000` missed `21` labels, and scale
`4096` missed `4` labels.

## Accuracy experiments

Earlier bucket and float-rerank paths were removed from production. Rounded IVF
is the only runtime classifier now.

The current production lane is IVF approximate nearest-neighbor search:

- build centroids and compact vector blocks from `references.json.gz`
- load `references.ivf.bin` at startup
- scan the nearest cluster first with `IVF_FAST_NPROBE=1`
- use bbox repair to scan only clusters whose bounding box can still beat the current top-five bound
- rank candidates with rounded int16 squared L2 distance
- use one-pass full bbox repair for the accuracy candidate

This path is implemented, unit-tested on a synthetic boundary case, and under
CI benchmarking as the submission default.

## Reverse proxy

The retained load balancer path is nginx stream. It keeps the proxy byte-oriented on port `9999` and forwards to the API containers over Unix Domain Sockets.

The benchmark workflow runs the default nginx stream compose file used by the submission.
