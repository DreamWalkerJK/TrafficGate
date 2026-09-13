# Architecture

The `TrafficGate` host uses YARP 2.2.0 with an `IProxyConfigProvider` backed by a SQLite revision store. Requests execute against the active in-memory snapshot; management writes persist a JSON snapshot in a SQLite transaction before replacing the YARP configuration. The store is deliberately isolated from Monica/MoLibrary.

`GatewayPolicyMiddleware` enforces route limits before YARP forwarding. A token bucket is partitioned by verified `tenant`/`sub` claims, or by the connection IP for anonymous traffic, and a bounded semaphore limits route concurrency. Request cancellation is linked to the route timeout. YARP owns streaming, upgrade, and protocol handling.

The sample upstream exposes normal, delayed, error, large, SSE, and redirect endpoints. WebSocket and gRPC require HTTP/1.1 upgrade and HTTP/2 integration tests before being marked supported in deployment-specific matrices.
