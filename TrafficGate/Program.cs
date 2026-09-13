using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TrafficGate;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("trafficgate.json", optional: true, reloadOnChange: true);
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("TrafficGate"));
builder.Services.AddSingleton<GatewayConfigStore>();
builder.Services.AddSingleton<DynamicProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<DynamicProxyConfigProvider>());
builder.Services.AddSingleton<GatewayLimiter>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = builder.Configuration["TrafficGate:Jwt:Authority"];
    o.Audience = builder.Configuration["TrafficGate:Jwt:Audience"];
    o.RequireHttpsMetadata = builder.Configuration.GetValue("TrafficGate:Jwt:RequireHttpsMetadata", false);
});
builder.Services.AddAuthorization();
builder.Services.AddReverseProxy();
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource(GatewayMetrics.SourceName).AddAspNetCoreInstrumentation().AddConsoleExporter())
    .WithMetrics(m => m.AddMeter(GatewayMetrics.MeterName).AddAspNetCoreInstrumentation().AddConsoleExporter());
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Clear(); o.KnownNetworks.Clear();
});

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
app.Services.GetRequiredService<DynamicProxyConfigProvider>().Reload(app.Services.GetRequiredService<GatewayConfigStore>().Current!);
app.UseForwardedHeaders();
app.Use(async (context, next) => { context.Response.Headers["X-Content-Type-Options"] = "nosniff"; context.Response.Headers["X-Frame-Options"] = "DENY"; await next(); });
app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", (GatewayConfigStore store) => store.Current is null ? Results.StatusCode(503) : Results.Ok(new { status = "ready", revision = store.Current.Revision })).AllowAnonymous();
var admin = app.MapGroup("/admin").RequireAuthorization();
admin.MapGet("/ui", () => Results.Content("<!doctype html><html><head><title>TrafficGate admin</title></head><body><h1>TrafficGate</h1><p>Use the authenticated API to validate, publish, inspect revisions, and roll back configuration.</p><pre id=out></pre><script>fetch('/admin/config').then(r=>r.text()).then(t=>out.textContent=t)</script></body></html>", "text/html"));
admin.MapGet("/config", (GatewayConfigStore store) => Results.Ok(store.RedactedCurrent()));
admin.MapGet("/revisions", (GatewayConfigStore store) => Results.Ok(store.Revisions));
admin.MapGet("/metrics", (GatewayMetrics metrics) => Results.Text(metrics.Snapshot(), "text/plain"));
admin.MapPost("/config/validate", (GatewayDefinition definition) => { var errors = GatewayValidation.Validate(definition); return errors.Count == 0 ? Results.Ok(new { valid = true }) : Results.BadRequest(new { valid = false, errors }); });
admin.MapPost("/config/publish", (HttpRequest request, GatewayDefinition definition, GatewayConfigStore store, DynamicProxyConfigProvider provider, GatewayMetrics metrics) =>
{
    var errors = GatewayValidation.Validate(definition); if (errors.Count > 0) return Results.BadRequest(new { valid = false, errors });
    if (!store.TryPublish(definition, request.Headers.IfMatch.ToString(), out var revision, out var conflict)) return Results.Conflict(new { error = conflict });
    provider.Reload(store.Current!); metrics.ConfigPublished(true); return Results.Ok(new { revision, etag = store.Current!.ETag });
});
admin.MapPost("/rollback/{revision:int}", (HttpRequest request, int revision, GatewayConfigStore store, DynamicProxyConfigProvider provider, GatewayMetrics metrics) =>
{
    if (!store.TryRollback(revision, request.Headers.IfMatch.ToString(), out var next, out var error)) return Results.Conflict(new { error });
    provider.Reload(store.Current!); metrics.ConfigPublished(true); return Results.Ok(new { revision = next });
});
app.MapReverseProxy(proxyPipeline => proxyPipeline.UseMiddleware<GatewayPolicyMiddleware>());
app.Run(options.ListenUrl);

public partial class Program { }
