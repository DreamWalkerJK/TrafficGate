using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Yarp.ReverseProxy.Model;
using Microsoft.AspNetCore.Authorization;

namespace TrafficGate;
public sealed class GatewayPolicyMiddleware(RequestDelegate next, GatewayConfigStore store, GatewayLimiter limiter, GatewayMetrics metrics, IAuthorizationService authorization)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route.Config.RouteId;
        var route = store.Current?.Definition.Routes.FirstOrDefault(r => r.RouteId == routeId);
        if (route is null) { await next(context); return; }
        if (route.MaxRequestBodyBytes > 0 && context.Request.ContentLength > route.MaxRequestBodyBytes) { await WriteError(context, 413, "request_too_large"); return; }
        if (route.AuthorizationPolicy is not null)
        {
            var result = await authorization.AuthorizeAsync(context.User, null, route.AuthorizationPolicy);
            if (!result.Succeeded) { await WriteError(context, context.User.Identity?.IsAuthenticated == true ? 403 : 401, "authorization_required"); return; }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(route.TimeoutSeconds, 1, 600)));
        var lease = await limiter.AcquireAsync(route, context, timeout.Token);
        if (!lease.IsAcquired) { context.Response.Headers.RetryAfter = "1"; await WriteError(context, 429, lease.Reason ?? "rate_limited"); return; }
        try { await next(context); } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { metrics.Cancelled(); } catch (OperationCanceledException) { if (!context.Response.HasStarted) await WriteError(context, 504, "gateway_timeout"); } finally { lease.Dispose(); }
    }
    static async Task WriteError(HttpContext c, int status, string code) { if (c.Response.HasStarted) return; c.Response.StatusCode = status; await c.Response.WriteAsJsonAsync(new { type = "https://trafficgate.dev/errors/" + code, title = code, status }); }
}
public sealed class GatewayLimiter
{
    const int MaxPartitions = 10000;
    readonly ConcurrentDictionary<string, SemaphoreSlim> semaphores = new(); readonly ConcurrentDictionary<string, TokenBucketRateLimiter> rates = new();
    public async ValueTask<Lease> AcquireAsync(RouteDefinition route, HttpContext context, CancellationToken ct)
    { var key = route.RouteId + ":" + (context.User.FindFirst("tenant")?.Value ?? context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"); if (!rates.ContainsKey(key) && rates.Count >= MaxPartitions) return new Lease(null, "partition_limit"); var rate = rates.GetOrAdd(key, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions { TokenLimit = Math.Max(1, route.RateLimitPerMinute), TokensPerPeriod = Math.Max(1, route.RateLimitPerMinute), ReplenishmentPeriod = TimeSpan.FromMinutes(1), AutoReplenishment = true, QueueLimit = 0 })); var r = await rate.AcquireAsync(1, ct); if (!r.IsAcquired) return new Lease(null, "rate_limited"); var sem = semaphores.GetOrAdd(route.RouteId, _ => new SemaphoreSlim(route.ConcurrencyLimit, route.ConcurrencyLimit)); if (!await sem.WaitAsync(TimeSpan.Zero, ct)) { r.Dispose(); return new Lease(null, "concurrency_limit"); } return new Lease(() => { sem.Release(); r.Dispose(); }, null); }
    public sealed class Lease(Action? release, string? reason) : IDisposable { public bool IsAcquired => release is not null; public string? Reason => reason; public void Dispose() => release?.Invoke(); }
}
