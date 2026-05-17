# Performance

Hot path choices:

- NativeAOT publish
- raw socket HTTP/1
- one task per client connection
- pooled read buffers
- fd-pass handoff from the standalone yolo load balancer to raw API fds
- manual request parsing
- no model binding
- prebuilt response bytes
- hybrid bucket fast path with rounded int16 IVF fallback
- no fraud-payload parsing in the proxy layer

## Current bottleneck

Transport is fast enough for the current target. Recent yolo-LB CI runs have
shown `0` HTTP errors; p99 work is now mostly inside bucket fast-path coverage,
fallback frequency, IVF repair/vector scan cost, and CPU split between the API
containers and the standalone proxy.

The reports page is the source of truth for the newest archived candidate and
calibrated runs because every main build can append fresh benchmark artifacts.
One recent clean pre-audit candidate image,
`ci-ead329a626dec7a605145fd278655dfd0fa63a51`, produced p99 `0.34ms`, score
`6000`, `0` false positives, `0` false negatives, and `0` HTTP errors in the
automatic benchmark lane; its paired official-calibrated prediction run reported
p99 `0.35ms`, score `6000`, and `0` HTTP errors. These are archived CI results,
not official Rinha hardware results.

## Accuracy experiments

Earlier non-candidate classifier paths were removed from the default production
lane. The current default is hybrid bucket/IVF:

- build bucket, IVF, and exact diagnostic data from `references.json.gz`
- load `references.bucket.bin` and `references.ivf.bin` at startup for hybrid mode
- try bucket profile/reference fast paths first
- fall back to IVF when the bucket path cannot decide confidently
- scan the nearest IVF cluster first with `IVF_FAST_NPROBE=1`
- use scalar bbox repair with early exit to scan only clusters whose bounding box can still beat the current top-five bound
- skip repair for first-cluster `0/5` approval and `5/5` denial candidates below tuned distance bounds
- rank fallback candidates with rounded int16 squared L2 distance

This path is implemented, unit-tested on focused parser/vector/index cases, and
under CI benchmarking as the submission default. Exact and IVF-only modes remain
available for diagnostics and manual benchmark experiments; they are not the
canonical submission lane.

Rejected A/Bs: AVX2 bbox repair raised p99 to `5.37ms`; a cluster-major bbox
copy raised p99 to `6.89ms`; `4096` clusters raised p99 to `16.69ms`; `1024`
clusters raised p99 to `19.78ms`; removed experiments either missed labels or
lost to the current standalone-yolo path.

## Reverse proxy

The retained load balancer path is the standalone `rinha4-lb-yolo-mode` image in
`LB_MODE=fdpass`. The LB accepts the external TCP client on port `9999`, passes
the accepted socket fd to an API container over a Unix control socket, and lets
the API serve the client directly. The API default also sets `FD_RAW=1`, which
keeps the passed fd on a low-level `recv`/`send` path instead of wrapping every
handoff in a managed `Socket`; set `FD_RAW=0` to fall back to the safer managed
Socket path for diagnostics.

The benchmark workflow runs the canonical root `docker-compose.yml` used by the
submission. The compose file allocates `0.42 CPU / 160 MB` to each API container
and `0.16 CPU / 30 MB` to the LB while keeping the total at `1.00 CPU / 350 MB`.
