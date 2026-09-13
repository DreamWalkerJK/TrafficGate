using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Yarp.ReverseProxy.Configuration;

namespace TrafficGate;

public static class GatewayHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args, ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(GatewayHost).Assembly.FullName
        });
        builder.Configuration.AddJsonFile("trafficgate.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables().AddCommandLine(args);
        configure?.Invoke(builder);
        var config = builder.Configuration;
        var proxy = Listener(config["TrafficGate:ListenUrl"] ?? "http://127.0.0.1:5080");
        var management = Listener(config["TrafficGate:ManagementUrl"] ?? "http://127.0.0.1:5081");
        var http2 = config["TrafficGate:Http2Url"] is { Length: > 0 } h2 ? Listener(h2) : null;
        if (proxy.Port == management.Port || http2?.Port == management.Port || http2?.Port == proxy.Port)
            throw new InvalidOperationException("Proxy and management listeners must have different ports.");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestLineSize = 16 * 1024;
            k.Limits.MaxRequestHeadersTotalSize = 64 * 1024;
            k.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
            k.Listen(Address(proxy), proxy.Port, listen =>
            {
                listen.Protocols = proxy.Scheme == "https" ? HttpProtocols.Http1AndHttp2 : HttpProtocols.Http1;
                if (proxy.Scheme == "https") listen.UseHttps();
            });
            k.Listen(Address(management), management.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                if (management.Scheme == "https") listen.UseHttps();
            });
            if (http2 is not null) k.Listen(Address(http2), http2.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                if (http2.Scheme == "https") listen.UseHttps();
            });
        });

        builder.Services.Configure<GatewayOptions>(config.GetSection("TrafficGate"));
        builder.Services.AddSingleton<GatewayConfigStore>();
        builder.Services.AddSingleton<DynamicProxyConfigProvider>();
        builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<DynamicProxyConfigProvider>());
        builder.Services.AddSingleton<GatewayLimiter>();
        builder.Services.AddSingleton<GatewayMetrics>();
        builder.Services.AddGatewayAuthentication(config, management.Port);
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-Token";
            o.Cookie.Name = "TrafficGate.Antiforgery";
            o.Cookie.Path = "/admin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddRazorComponents();
        builder.Services.AddReverseProxy()
            .ConfigureHttpClient(DynamicProxyConfigProvider.ConfigureHttpClient);

        var trustedProxies = config.GetSection("TrafficGate:TrustedProxies").Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // This middleware is enabled only when an explicit proxy is configured.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = config.GetValue("TrafficGate:ForwardLimit", 1);
            o.KnownProxies.Clear(); o.KnownIPNetworks.Clear();
            foreach (var address in trustedProxies) o.KnownProxies.Add(IPAddress.Parse(address));
        });
        builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("TrafficGate"))
            .WithTracing(t =>
            {
                t.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(config.GetValue("TrafficGate:TraceSampleRatio", 0.1))))
                    .AddSource(GatewayMetrics.SourceName);
                if (config.GetValue("TrafficGate:TelemetryConsole", false)) t.AddConsoleExporter();
            })
            .WithMetrics(m =>
            {
                m.AddMeter(GatewayMetrics.MeterName);
                if (config.GetValue("TrafficGate:TelemetryConsole", false)) m.AddConsoleExporter();
            });
        // Framework request logs include the full URL; use our bounded structured events.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.None);
        var app = builder.Build();
        var store = app.Services.GetRequiredService<GatewayConfigStore>();
        app.Services.GetRequiredService<DynamicProxyConfigProvider>().Reload(store.Current!);
        app.Use(async (context, next) =>
        {
            var isManagement = context.Connection.LocalPort == management.Port;
            context.Items["TrafficGate:Management"] = isManagement;
            context.Items["TrafficGate:ConnectionAddress"] = context.Connection.RemoteIpAddress?.ToString();
            var adminPath = context.Request.Path.StartsWithSegments("/admin");
            var healthPath = context.Request.Path == "/health/live" || context.Request.Path == "/health/ready";
            if ((!isManagement && adminPath) || (isManagement && !adminPath && !healthPath))
            { context.Response.StatusCode = 404; return; }
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            try { await next(context); }
            catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                app.Logger.LogError("Gateway local error {ErrorType} request {RequestId}", ex.GetType().Name, context.TraceIdentifier);
                context.Response.Clear();
                await GatewayErrors.WriteAsync(context, 500, "internal_error", context.RequestAborted);
            }
        });
        if (trustedProxies.Length > 0) app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            // Never pass an unverified chain or identity to a destination. YARP generates
            // its own forwarding headers from the validated connection data.
            foreach (var key in context.Request.Headers.Keys.Where(k => k.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                         k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                         k.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)).ToArray())
                context.Request.Headers.Remove(key);
            context.Request.Headers.Remove("X-Authenticated-User");
            context.Request.Headers.Remove("X-Authenticated-Tenant");
            await next(context);
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.Use(async (context, next) =>
        {
            if (context.Items["TrafficGate:Management"] is true && context.Request.Path.StartsWithSegments("/admin") &&
                !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                { await GatewayErrors.WriteAsync(context, 400, "csrf_validation_failed", context.RequestAborted); return; }
            }
            await next(context);
        });
        app.UseWebSockets();
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", revision = store.Current!.Revision })).AllowAnonymous();
        app.MapGatewayAdmin();
        app.MapRazorComponents<Components.App>();
        app.MapReverseProxy(pipeline =>
        {
            pipeline.UseMiddleware<GatewayPolicyMiddleware>();
            pipeline.UseLoadBalancing();
            pipeline.UsePassiveHealthChecks();
        });
        return app;
    }

    private static Uri Listener(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Port < 1 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0 ||
            (uri.Host != "localhost" && !IPAddress.TryParse(uri.Host, out _)))
            throw new InvalidOperationException("Listeners require an explicit HTTP(S) IP address and port.");
        return uri;
    }

    private static IPAddress Address(Uri uri) => uri.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(uri.Host);
}
