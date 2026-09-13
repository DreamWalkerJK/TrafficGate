# TrafficGate

TrafficGate is a standalone API gateway for .NET 10. It uses [YARP](https://github.com/dotnet/yarp) for forwarding and adds versioned configuration, JWT authentication, route authorization, process-local traffic protection, request limits, health handling, structured errors, OpenTelemetry, and an authenticated management plane.

The service is independent of Monica and MoLibrary. It is designed for explicit administrator-configured destinations and does not provide an open proxy or a full WAF.

## What is included

- Host, path, and method based routing to named upstream clusters.
- Round-robin and other bounded YARP load-balancing policies.
- Streaming HTTP responses, SSE, WebSocket upgrades, and HTTP/2 forwarding.
- JWT Bearer authentication with separate proxy and management schemes.
- Explicit anonymous routes, default authenticated routes, and named authorization policies.
- Token-bucket rate limits partitioned by verified claims or connection IP.
- FIFO concurrency queues, queue cancellation, Retry-After responses, partition caps, idle cleanup, and safe policy generations during hot reloads.
- Request body, header, URL, connection, and activity limits.
- Active and passive YARP health configuration plus observable in-process passive route state.
- SQLite WAL configuration revisions with atomic publish, ETag conflict detection, audit records, diff preview, rollback, and last-good-snapshot recovery.
- Management API and server-side rendered Blazor workspace on a separate listener.
- OpenTelemetry traces and bounded metrics labels.
- Docker, Compose, and Helm deployment assets.
- A local Kestrel upstream and real network tests for failure and protocol behavior.

## Run locally

The repository pins .NET SDK `10.0.102` in [`global.json`](global.json).

```powershell
dotnet build TrafficGate.slnx
dotnet run --project TrafficGate.TestUpstream
dotnet run --project TrafficGate
```

The default listeners are:

| Listener | Address | Purpose |
| --- | --- | --- |
| Proxy | `http://127.0.0.1:5080` | Public API traffic |
| Management | `http://127.0.0.1:5081` | Authenticated API and `/admin/ui` |
| Test upstream | `http://127.0.0.1:5090` | Local fixture |
| Test upstream HTTP/2 | `http://127.0.0.1:5091` | Cleartext HTTP/2 fixture |

The sample configuration is in [`TrafficGate/trafficgate.json`](TrafficGate/trafficgate.json). Set `TrafficGate__ListenUrl`, `TrafficGate__ManagementUrl`, `TrafficGate__DataPath`, or the corresponding JSON values for another environment. Keep the management listener on loopback or a private network.

## Management API

Management endpoints are available only on port `5081` and require the admin JWT policy. Mutating calls require both a server-issued antiforgery token and an `If-Match` header containing the current ETag.

| Endpoint | Function |
| --- | --- |
| `GET /admin/config` | Redacted current snapshot, ETag, and last failure |
| `GET /admin/revisions` | Revision history |
| `GET /admin/audit` | Recent management audit records |
| `GET /admin/config/failure` | Last persisted configuration failure |
| `GET /admin/health` | Observed passive route health |
| `GET /admin/metrics` | Bounded Prometheus-style counters |
| `POST /admin/config/validate` | Validate a candidate definition |
| `POST /admin/config/diff` | Validate and compare a candidate definition |
| `POST /admin/config/publish` | Persist and atomically activate a new revision |
| `POST /admin/rollback/{revision}` | Create a new revision from an older snapshot |
| `GET /admin/ui` | Blazor operations workspace |

See [`docs/jwt-example.json`](docs/jwt-example.json) for the shape of local JWT configuration. Production deployments should use a real issuer, audience, signing key, and TLS trust chain. Do not commit private keys or upstream credentials.

## Configuration model

Routes point to one named cluster. A route can set `AllowAnonymous` explicitly; when it is false, the gateway evaluates either the configured `AuthorizationPolicy` or the default authenticated policy before consuming a limiter slot. Destinations must be explicit HTTP(S) URLs. The validator rejects userinfo, query strings, fragments, dangerous metadata addresses, malformed paths, unsafe header transforms, duplicate IDs, and ambiguous equal-priority matches.

Runtime requests use the immutable YARP route metadata snapshot selected for that request. Proxy traffic never queries SQLite for route policy. Configuration is persisted before the new snapshot becomes current, so a restart recovers the last valid committed revision.

Rate limits are process local. After scaling to multiple replicas, each replica has its own counters; Kubernetes scaling does not create an exact global quota. A distributed limiter is a future extension.

## Tests and protocol checks

Run the complete suite with:

```powershell
dotnet test TrafficGate.slnx --no-restore
```

The current suite contains 37 tests covering configuration recovery and concurrency, route validation, limiter generations and queues, authentication and trusted headers, error preservation, timeout and cancellation, request limits, SSE, WebSocket, and HTTP/2 framed gRPC on real Kestrel hosts.

The portable loopback smoke test starts the built DLLs and checks WebSocket echo, cancellation, timeout state handling, and non-idempotent no-retry behavior:

```powershell
dotnet build TrafficGate.slnx --no-restore
powershell -ExecutionPolicy Bypass -File tests/protocol-smoke.ps1
```

Windows PowerShell versions whose `System.Net.Http` lacks HTTP/2 request properties explicitly skip that one client-side check; the HTTP/2 path is covered by the .NET network test suite. See [`docs/test-report.md`](docs/test-report.md) and [`docs/support-matrix.md`](docs/support-matrix.md).

## Containers and Helm

```powershell
docker compose config
docker compose up --build
```

Compose exposes proxy port `5080`, binds management port `5081` to host loopback, and persists SQLite data in a named volume. The chart is under [`deploy/helm/trafficgate`](deploy/helm/trafficgate). It includes resource requests/limits, readiness/liveness probes, a values schema, and a Helm test Pod. Helm lint/template and cluster runtime validation require a local Helm binary and Kubernetes cluster; neither is assumed by the repository.

## Security and operational boundaries

Forwarded headers and injected identity headers are cleared unless trusted proxies are explicitly configured. Upstream TLS verification remains enabled. DNS answers are checked before a numeric-address connection is opened to reduce DNS rebinding risk. Logs avoid authorization headers, request bodies, raw query strings, destination URLs, and exception details.

TrafficGate protects application-layer routing and resource usage. It does not claim to absorb link saturation or arbitrary-scale DDoS traffic. Retrying arbitrary proxy requests is disabled, especially for non-idempotent requests and requests whose responses have started.

Operational procedures, the threat model, support matrix, UI behavior, and performance test plan are documented in [`docs/`](docs/).

## Current delivery state

The implementation branch is `codex/trafficgate-implementation`. See [`docs/progress.md`](docs/progress.md) for commit and push evidence, including the known Helm and performance-validation limits.
