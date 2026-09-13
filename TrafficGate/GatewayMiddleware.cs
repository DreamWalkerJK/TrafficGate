using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

namespace TrafficGate;

public sealed class GatewayPolicyMiddleware(RequestDelegate next, GatewayLimiter limiter, GatewayMetrics metrics,
    ILogger<GatewayPolicyMiddleware> logger, IAuthorizationService authorization,
    IAuthorizationPolicyProvider policyProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var proxy = context.GetReverseProxyFeature();
        var metadata = proxy.Route.Config.Metadata;
        if (metadata is null || !metadata.TryGetValue("TrafficGate:Policy", out var serialized))
        { await GatewayErrors.WriteAsync(context, 503, "policy_unavailable", context.RequestAborted); return; }
        var route = JsonSerializer.Deserialize<RouteDefinition>(serialized);
        if (route is null)
        { await GatewayErrors.WriteAsync(context, 503, "policy_unavailable", context.RequestAborted); return; }
        route.Revision = int.Parse(metadata["TrafficGate:Revision"], System.Globalization.CultureInfo.InvariantCulture);
        var clientCancellation = context.RequestAborted;

        // Route policy is part of the immutable YARP snapshot. Evaluate it here,
        // after authentication has populated HttpContext.User and before any
        // limiter slot or upstream connection is acquired.
        if (!route.AllowAnonymous)
        {
            var policy = route.AuthorizationPolicy is { Length: > 0 } named
                ? await policyProvider.GetPolicyAsync(named)
                : await policyProvider.GetDefaultPolicyAsync();
            if (policy is null)
            {
                await GatewayErrors.WriteAsync(context, 503, "policy_unavailable", clientCancellation);
                return;
            }
            var authorizationResult = await authorization.AuthorizeAsync(context.User, context, policy);
            if (!authorizationResult.Succeeded)
            {
                var authenticated = context.User.Identity?.IsAuthenticated == true;
                if (!authenticated) context.Response.Headers.WWWAuthenticate = "Bearer";
                await GatewayErrors.WriteAsync(context, authenticated ? 403 : 401,
                    authenticated ? "forbidden" : "authentication_required", clientCancellation);
                return;
            }
        }
        if (context.Request.ContentLength > route.MaxRequestBodyBytes)
        { await GatewayErrors.WriteAsync(context, 413, "request_too_large", clientCancellation); return; }
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = route.MaxRequestBodyBytes;
        var headerBytes = context.Request.Headers.Sum(h => Encoding.UTF8.GetByteCount(h.Key) + h.Value.Sum(v => Encoding.UTF8.GetByteCount(v ?? "")) + 4);
        if (headerBytes > route.MaxRequestHeaderBytes)
        { await GatewayErrors.WriteAsync(context, 431, "request_headers_too_large", clientCancellation); return; }
        if (Encoding.UTF8.GetByteCount(context.Request.PathBase + context.Request.Path + context.Request.QueryString) > route.MaxRequestLineBytes)
        { await GatewayErrors.WriteAsync(context, 414, "request_url_too_large", clientCancellation); return; }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst("sub")?.Value;
            var tenant = context.User.FindFirst("tenant")?.Value;
            if (ValidIdentity(subject)) context.Request.Headers["X-Authenticated-User"] = subject;
            if (ValidIdentity(tenant)) context.Request.Headers["X-Authenticated-Tenant"] = tenant;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(clientCancellation);
        if (route.TimeoutSeconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(route.TimeoutSeconds));
        context.RequestAborted = deadline.Token;
        var outcome = "success";
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var lease = await limiter.AcquireAsync(route, context, deadline.Token);
            if (!lease.IsAcquired)
            {
                outcome = lease.Reason ?? "rate_limited";
                if (lease.RetryAfter is { } retry) context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString();
                await GatewayErrors.WriteAsync(context, 429, outcome, clientCancellation);
                return;
            }
            await next(context);
            var failure = context.GetForwarderErrorFeature();
            if (clientCancellation.IsCancellationRequested) { outcome = "client_cancelled"; metrics.Cancelled(); return; }
            if (deadline.IsCancellationRequested)
            { outcome = "gateway_timeout"; await GatewayErrors.WriteAsync(context, 504, outcome, clientCancellation); }
            else if (failure is not null && failure.Error != ForwarderError.None)
            {
                var tooLarge = FindException<BadHttpRequestException>(failure.Exception)?.StatusCode == 413;
                outcome = tooLarge ? "request_too_large" : failure.Error switch
                {
                    ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout => "upstream_timeout",
                    ForwarderError.NoAvailableDestinations => "no_healthy_destinations",
                    ForwarderError.Request => "upstream_connection_failure",
                    _ => "upstream_transport_failure"
                };
                await GatewayErrors.WriteAsync(context, tooLarge ? 413 : outcome == "upstream_timeout" ? 504 : 503, outcome, clientCancellation);
            }
            else if (context.Response.StatusCode >= 500) outcome = "upstream_error";
            if (context.Response.StatusCode is >= 200 and < 500)
                UpstreamHealthRegistry.Shared.RecordSuccess(route.RouteId);
            else if (context.Response.StatusCode >= 500 || outcome is "no_healthy_destinations" or "upstream_connection_failure" or "upstream_transport_failure" or "upstream_timeout")
                UpstreamHealthRegistry.Shared.RecordFailure(route.RouteId, outcome);
        }
        catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
        { outcome = "client_cancelled"; metrics.Cancelled(); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { outcome = "gateway_timeout"; await GatewayErrors.WriteAsync(context, 504, outcome, clientCancellation); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        { outcome = "request_too_large"; await GatewayErrors.WriteAsync(context, 413, outcome, clientCancellation); }
        finally
        {
            context.RequestAborted = clientCancellation;
            // No raw path, query, Authorization, exception or destination is logged.
            logger.LogInformation("Proxy {Route} {Cluster} outcome {Outcome} request {RequestId} connection {ConnectionAddress} client {ClientAddress}",
                route.RouteId, route.ClusterId, outcome, context.TraceIdentifier,
                context.Items["TrafficGate:ConnectionAddress"], context.Connection.RemoteIpAddress);
            metrics.ProxyRequest(route.RouteId, outcome, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private static bool ValidIdentity(string? value) => value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl);
    private static T? FindException<T>(Exception? error) where T : Exception
    {
        while (error is not null) { if (error is T match) return match; error = error.InnerException; }
        return null;
    }
}
