# TrafficGate management UI

The management workspace is a static server rendered Blazor page at `/admin/ui`.
It is hosted by the dedicated management listener and is protected by
`trafficgate.admin` (`GatewayAuthentication.AdminPolicy`). The proxy listener
cannot route to `/admin`, and the host middleware returns a 404 when the listener
and path do not belong together.

## What operators can see

The page starts with the active revision, route count, cluster count, and the
last configuration failure. The route table shows route ID, path, host and
method matches, target cluster, anonymous or named authorization, and the rate,
concurrency, and timeout guardrails. The cluster table shows load balancing,
destination IDs and addresses, HTTP version, active/passive health settings,
and connection limits.

Authorization policy references are listed by route. Policy definitions remain
host configuration; the UI deliberately does not expose a JWT authority,
signing key, or client credential. Change a route's `AuthorizationPolicy` in
the JSON editor only when the corresponding policy is already registered in the
host.

The revision history is read from `GatewayConfigStore` and includes timestamp,
ETag, route count, and cluster count. Recent audit entries include actor,
action, revision, and result. The store truncates audit values and removes
control characters, and no form writes secrets into audit data.

## Editing and publishing

The editor contains a detached, indented `GatewayDefinition` JSON snapshot.
Use **Preview changes** to deserialize the candidate, run the same gateway and
YARP validation used for publishing, and compare route and cluster snapshots
with `GatewayConfigStore.Diff`. A preview reports added, removed, and modified
items; a valid unchanged snapshot reports no changes.

**Publish configuration** submits the candidate with the ETag that was loaded
with it. `TryPublish` checks the ETag while holding the store lock, validates
again, materializes the proxy snapshot, persists a new SQLite revision and
audit row, then atomically updates the in-memory provider. If another operator
published first, the stale ETag is rejected and the active snapshot remains in
place. Reload the page, inspect the new snapshot, and preview again before
retrying.

The three forms have explicit names (`preview-config`, `publish-config`, and
`rollback-config`) and include `<AntiforgeryToken />`. The host also validates
the antiforgery token for every mutating management request. This keeps the
static SSR workflow usable without a client-side JavaScript dependency.

## Rollback

Enter a revision number from the history table and choose **Create rollback
revision**. Rollback reads the selected immutable snapshot and calls
`TryRollback` with the current ETag. It creates a new revision and audit entry;
it never overwrites or deletes history. ETag conflicts and missing revisions
are shown as actionable errors. A persistence failure leaves the previous
active revision in memory and records the failure for diagnosis.

## Lifecycle and operational limits

The page is intentionally static SSR: each form post creates a fresh request
and rerenders from the store. It does not poll metrics or health data, so the
display is a point-in-time operations view; refresh to inspect a later state.
The active configuration is held by the process and persisted in SQLite, while
the proxy continues to use a stable YARP snapshot during a publish.

Start the application with the normal host command and open the management
listener, for example `http://127.0.0.1:5081/admin/ui`, after supplying an
administrator identity accepted by the configured admin bearer scheme. The
default JWT setup requires a signed token, issuer, audience, expiry, and the
`role=trafficgate.admin` claim. In a deployment that terminates TLS or an
identity session upstream, keep that trust boundary explicit in host
configuration and retain HTTPS for the management listener.
