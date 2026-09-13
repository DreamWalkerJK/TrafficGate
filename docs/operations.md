# Operations

Run the controllable upstream with `dotnet run --project TrafficGate.TestUpstream` and the gateway with `dotnet run --project TrafficGate`. The proxy listens on `http://127.0.0.1:5080`; the management API listens on loopback `http://127.0.0.1:5081`; the sample upstream listens on `http://127.0.0.1:5090`. Check `/health/live`, `/health/ready`, and OpenTelemetry console output. Obtain a CSRF token from `/admin/csrf`, then publish revisions through `/admin/config/publish` with matching `X-CSRF-Token` and `If-Match` headers and roll back using `/admin/rollback/{revision}`.

For production, keep the management API bound to loopback/private network, supply a real JWT authority/audience, mount `data/` on durable storage, and terminate TLS at a trusted proxy. Single-instance rate limits are process local and are not exact global limits after Kubernetes scaling.
