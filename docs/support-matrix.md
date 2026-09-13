# Protocol support matrix

| Capability | Status | Verification |
| --- | --- | --- |
| HTTP/1.1 request/response streaming | Supported | YARP forwarding smoke test |
| SSE | Supported | `/sse` keeps `text/event-stream` and flushes events |
| WebSocket | Verified with local upstream | `tests/protocol-smoke.ps1` echo test |
| gRPC over HTTP/2 cleartext in local tests | Verified with local framed endpoint | `tests/protocol-smoke.ps1` |
| gRPC over TLS/production ingress | Deployment dependent | Configure HTTP/2 Kestrel/ingress and validate end to end |
| Active health checks | Planned for next resilience milestone | YARP destination health integration pending |
| Passive health state | Implemented | Three consecutive failures mark a route unhealthy; success recovers |

YARP owns protocol parsing and streaming. The gateway does not buffer complete request or response bodies and does not transparently retry requests.
