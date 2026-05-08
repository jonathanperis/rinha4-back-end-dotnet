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
The best unconstrained zero-failure CI lane used `2048` IVF clusters, scalar
bbox repair, and first-cluster `5/5` fraud fast accept under a tuned int16
distance bound: p99 `1.46ms`, score `5836.34`. That did not match official
preview behavior. The newer one-core cpuset probe against the same image
produced p99 `22.52ms`, score `4647.51`, and `0%` failures, so optimization now
targets scan CPU under constrained contention.

`test/AccuracyProbe profile` showed high-confidence first-cluster `0/5`
approvals and `5/5` denials can skip bbox repair under tuned distance bounds.
Public replay stays at `0` false positives and `0` false negatives with both
guarded shortcuts, and local replay time dropped from `20.76s` to `11.71s`.

AVX2 bbox repair raised p99 to `5.37ms`; a cluster-major bbox copy raised p99
to `6.89ms`; `4096` clusters raised p99 to `16.69ms`; `1024` clusters raised
p99 to `19.78ms`.
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
- use scalar bbox repair with early exit to scan only clusters whose bounding box can still beat the current top-five bound
- skip repair for first-cluster `0/5` approval and `5/5` denial candidates below tuned distance bounds
- rank candidates with rounded int16 squared L2 distance
- use one-pass full bbox repair for the accuracy candidate

This path is implemented, unit-tested on a synthetic boundary case, and under
CI benchmarking as the submission default.

## Reverse proxy

The retained load balancer path is nginx stream. It keeps the proxy byte-oriented on port `9999` and forwards to the API containers over Unix Domain Sockets.

The benchmark workflow runs the default nginx stream compose file used by the submission.
