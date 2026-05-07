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

## Current bottleneck

Transport is fast. Current CI p99 has been below 1ms.

The active bottleneck is detection accuracy. Failure rate must reach 0% to compete with the current top 10.

## Experiments

Proxy variants exist for direct comparison:

- default nginx stream
- nginx HTTP
- Envoy
- HAProxy

The benchmark workflow can run any compose variant manually.

