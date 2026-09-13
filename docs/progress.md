# Progress

## Milestone 1 — host, YARP proxy, controllable upstream

- Branch: `codex/trafficgate-implementation`
- Build: `dotnet build TrafficGate.slnx` passed (one ASP.NET deprecation warning).
- Commit: `bcdcd01` (`feat(proxy): add YARP gateway host`). Push attempted with `git push -u origin codex/trafficgate-implementation` and failed because the environment could not connect to `github.com:443`; retry that command when network access is available.
- Follow-up commit: `37282b4` (`docs(progress): record initial gateway delivery`) is local and unpushed for the same network reason.
- Auth fix: `2da5f9f` (`fix(auth): enforce named route policies`) passed tests; push attempted and failed with the same inability to connect to `github.com:443`.
- Delivered: .NET 10 solution, dynamic route/cluster snapshots, JWT bearer wiring, policy middleware, token bucket and concurrency controls, health endpoints, OpenTelemetry setup, local fault upstream, Docker assets, and baseline documentation.

- Health/config milestone: commits 4f65551 and 213f497 add bounded passive health state, policy snapshot metadata, invalid snapshot skipping, persistence failure rollback, and revision continuity. git ls-remote confirms codex/trafficgate-implementation at 213f497. One direct push attempt timed out against github.com:443; branch now reflects remote hash (likely concurrent retry).

- Gateway delivery milestone: commit `adab82a` (`feat(gateway): deliver traffic protection and management plane`) adds the immutable configuration store, DNS-safe YARP client callback, route authorization enforcement, bounded limiter, real Kestrel protocol tests, management API, SSR Blazor workspace, health/metrics instrumentation, Docker/Compose/Helm assets, and supporting documentation. Local validation: `dotnet test TrafficGate.slnx --no-restore` passed 37/37; `dotnet build TrafficGate.slnx --no-restore` passed with 0 warnings and 0 errors. Push attempted with `git push -u origin codex/trafficgate-implementation` and failed: unable to connect to `github.com:443`; retry when network access is available.
- Protocol smoke note: `tests/protocol-smoke.ps1` now starts the built DLLs with `dotnet`, uses portable cleanup, and passes on a clean loopback run. Windows PowerShell skips its unavailable HTTP/2 client property check (the real Kestrel HTTP/2 test remains covered by `dotnet test`) and reports when an upstream has already started a response before timeout cancellation. Helm lint/template were not run because the `helm` executable is unavailable in this environment.
- Admin observability follow-up: commit `b95360e` (`feat(admin): expose audit diff and runtime metrics`) adds authenticated audit, failed-config, diff-preview, and bounded Prometheus-style runtime metric endpoints. Push succeeded to `origin/codex/trafficgate-implementation`.
- Protocol verification follow-up: commit `912ddce` (`test(protocol): stabilize portable smoke verification`) makes the smoke script pass under Windows PowerShell while explicitly documenting its HTTP/2 client limitation and already-started-response timeout behavior. The commit is local; its push attempt failed with a transient inability to connect to `github.com:443`.
