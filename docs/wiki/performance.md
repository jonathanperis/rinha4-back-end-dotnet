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
preview behavior. A one-core cpuset probe against the same image produced p99
`22.52ms`, score `4647.51`, and `0%` failures. After adding the first-cluster
`0/5` approval shortcut, the constrained CI candidate improved to p99
`21.03ms`, score `4677.25`, and `0%` failures on image
`ci-780d16603df535d54a0c58d1a8f5b4701d16b7b6`, so optimization now targets
remaining scan CPU under constrained contention.

`test/AccuracyProbe profile` showed high-confidence first-cluster `0/5`
approvals and `5/5` denials can skip bbox repair under tuned distance bounds.
Public replay stays at `0` false positives and `0` false negatives with both
guarded shortcuts, and local replay time dropped from `20.76s` to `11.71s`.

AVX2 bbox repair raised p99 to `5.37ms`; a cluster-major bbox copy raised p99
to `6.89ms`; `4096` clusters raised p99 to `16.69ms`; `1024` clusters raised
p99 to `19.78ms`; removed experiments either missed labels or lost to nginx.

## Accuracy experiments

Earlier non-candidate classifier paths were removed from production. Rounded
IVF2 is the only runtime classifier now.

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
The submission compose pins the proxy to cpuset `0` and the API containers to
`1,2` and `2,3`, matching the official-accepted pattern observed in the #12
.NET implementation while keeping the same `1.00 CPU / 350 MB` quota.
