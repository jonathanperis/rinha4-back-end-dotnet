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
- O(1) fine-bucket lookup
- opt-in IVF scorer experiment behind `SCORER_MODE=ivf`
- nginx waits for both API socket files before serving `/ready`

## Current bottleneck

Transport is fast enough for the current target. Recent CI candidate runs have shown `0` HTTP errors; the latest health-gated run measured `1.35ms` p99 on GitHub Actions. Earlier candidate runs on the same stack were below `1ms`, so p99 still needs watching but is not the main blocker.

The active bottleneck is detection accuracy. Latest CI failure rate is `2.35%`, driven by false positives and false negatives from the O(1) bucket classifier. Failure rate must reach `0%` to compete with the current top 10.

## Accuracy experiments

Exact search inside the current fine bucket was tested locally and did not justify production work: it reduced only a small number of failures while scanning tens of thousands of vectors per request on average.

The current experimental lane is IVF approximate nearest-neighbor search:

- build centroids and compact vector blocks from `references.json.gz`
- load `references.ivf.bin` only when `SCORER_MODE=ivf`
- scan the nearest cluster first with `IVF_FAST_NPROBE=1`
- use bbox repair to scan clusters whose bounding box can still beat the current top-five bound
- rerun boundary fraud counts `1..4` through the repair path
- keep the current bucket classifier as fallback until CI proves score improvement

This path is implemented, unit-tested on a synthetic boundary case, and still
needs full official-like CI benchmarking before it can become submission default.

## Reverse proxy

The retained load balancer path is nginx stream. It keeps the proxy byte-oriented on port `9999` and forwards to the API containers over Unix Domain Sockets.

The benchmark workflow runs the default nginx stream compose file used by the submission.
