# Performance

Hot path choices:

- NativeAOT publish
- raw socket HTTP/1
- one task per client connection
- pooled read buffers
- Unix Domain Sockets behind the standalone yolo proxy
- manual request parsing
- no model binding
- prebuilt response bytes
- rounded int16 IVF nearest-neighbor ranking
- no fraud-payload parsing in the proxy layer

## Current bottleneck

Transport is fast enough for the current target. Recent yolo-LB CI runs have
shown `0` HTTP errors; p99 work is now inside IVF repair, vector scan cost, and
CPU split between the API containers and the standalone proxy.

The latest validated main build before this cleanup used image
`ci-ecdcc3f1b0059842489ae32102763ac957cc2a36` and produced p99 `0.40ms`,
score `6000`, `0` false positives, `0` false negatives, and `0` HTTP errors in
the automatic benchmark lane. A same-matrix comparison with that image was also
correct but narrowly trailed Danilo in that run (`0.39ms` vs `0.37ms`).

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

Rejected A/Bs: AVX2 bbox repair raised p99 to `5.37ms`; a cluster-major bbox
copy raised p99 to `6.89ms`; `4096` clusters raised p99 to `16.69ms`; `1024`
clusters raised p99 to `19.78ms`; removed experiments either missed labels or
lost to the current standalone-yolo path.

## Reverse proxy

The retained load balancer path is the standalone `rinha4-lb-yolo-mode` image in
`LB_MODE=proxy`. It keeps the proxy byte-oriented on port `9999` and forwards to
the API containers over Unix Domain Sockets.

The benchmark workflow runs the canonical root `docker-compose.yml` used by the
submission. The compose file allocates `0.42 CPU / 160 MB` to each API container
and `0.16 CPU / 30 MB` to the proxy while keeping the total at `1.00 CPU / 350 MB`.
