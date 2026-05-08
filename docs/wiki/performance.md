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
- fine-bucket lookup fallback
- nginx waits for both API socket files before serving `/ready`

## Current bottleneck

Transport is fast enough for the current target. Recent CI runs have shown `0`
HTTP errors; p99 work is now inside IVF repair and vector scan cost.

The active bottleneck is balancing full-repair accuracy with p99. Rounded IVF
matched the public benchmark locally with `0` false positives and `0` false
negatives, but the full repair path still needs CI latency improvement.

## Accuracy experiments

Exact search inside the current fine bucket was tested locally and did not justify production work: it reduced only a small number of failures while scanning tens of thousands of vectors per request on average.

The current production lane is IVF approximate nearest-neighbor search:

- build centroids and compact vector blocks from `references.json.gz`
- load `references.ivf.bin` by default with `SCORER_MODE=ivf`
- scan the nearest cluster first with `IVF_FAST_NPROBE=1`
- use bbox repair to scan clusters whose bounding box can still beat the current top-five bound
- rank candidates with rounded int16 squared L2 distance
- repair fraud counts `0..5` for the accuracy candidate

This path is implemented, unit-tested on a synthetic boundary case, and under
CI benchmarking as the submission default.

## Reverse proxy

The retained load balancer path is nginx stream. It keeps the proxy byte-oriented on port `9999` and forwards to the API containers over Unix Domain Sockets.

The benchmark workflow runs the default nginx stream compose file used by the submission.
