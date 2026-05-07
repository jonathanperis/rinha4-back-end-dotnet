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

## Reverse proxy

The retained load balancer path is nginx stream. It keeps the proxy byte-oriented on port `9999` and forwards to the API containers over Unix Domain Sockets.

The benchmark workflow runs the default nginx stream compose file used by the submission.
