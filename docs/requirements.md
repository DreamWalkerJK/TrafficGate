# TrafficGate requirements

TrafficGate is a standalone .NET 10 ASP.NET Core and YARP gateway. It accepts administrator supplied route and cluster snapshots, forwards HTTP traffic to explicitly configured destinations, and applies authentication, rate, concurrency, size, and timeout policies. Quotas are process local in v1; replicas multiply the effective allowance. Redis global limiting is intentionally a future extension.

The first release keeps proxy retries disabled, preserves upstream status/body/headers, and emits identifiable gateway errors for policy and timeout failures. The management API publishes immutable revisions with ETag checks and retains the last valid snapshot.
