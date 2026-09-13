using Microsoft.AspNetCore.Antiforgery;

namespace TrafficGate;

public static class GatewayAdmin
{
    public static void MapGatewayAdmin(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization(GatewayAuthentication.AdminPolicy);
        admin.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        });
        admin.MapGet("/config", (HttpContext context, GatewayConfigStore store) =>
        {
            context.Response.Headers.ETag = store.Current!.ETag;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(store.RedactedCurrent());
        });
        admin.MapGet("/revisions", (GatewayConfigStore store) => Results.Ok(store.Revisions));
        admin.MapGet("/audit", (GatewayConfigStore store) => Results.Ok(store.Audit()));
        admin.MapGet("/config/failure", (GatewayConfigStore store) => Results.Ok(new { failure = store.LastFailure }));
        admin.MapGet("/metrics", (GatewayMetrics metrics) => Results.Text(metrics.Snapshot(), "text/plain"));
        admin.MapGet("/health", () => Results.Ok(UpstreamHealthRegistry.Shared.Snapshot()));
        admin.MapPost("/config/validate", (GatewayDefinition definition) =>
        {
            var errors = GatewayValidation.Validate(definition);
            return errors.Count == 0 ? Results.Ok(new { valid = true }) : Results.BadRequest(new { valid = false, errors });
        });
        admin.MapPost("/config/diff", (GatewayDefinition definition, GatewayConfigStore store) =>
        {
            var errors = store.Validate(definition);
            if (errors.Count > 0) return Results.BadRequest(new { valid = false, errors });
            return Results.Ok(new { valid = true, differences = store.Diff(definition) });
        });
        admin.MapPost("/config/publish", (HttpContext context, GatewayDefinition definition, GatewayConfigStore store,
            DynamicProxyConfigProvider provider, GatewayMetrics metrics) =>
        {
            if (!context.Request.Headers.ContainsKey("If-Match")) return Results.StatusCode(428);
            var errors = GatewayValidation.Validate(definition);
            if (errors.Count > 0) { metrics.ConfigPublished(false); return Results.BadRequest(new { valid = false, errors }); }
            if (!store.TryPublish(definition, context.Request.Headers.IfMatch.ToString(), out var revision, out var error))
            {
                metrics.ConfigPublished(false);
                return Results.Json(new { error }, statusCode: error == "etag_conflict" ? 412 : 400);
            }
            provider.Reload(store.Current!);
            metrics.ConfigPublished(true);
            context.Response.Headers.ETag = store.Current!.ETag;
            return Results.Ok(new { revision, etag = store.Current.ETag });
        });
        admin.MapPost("/rollback/{revision:int}", (HttpContext context, int revision, GatewayConfigStore store,
            DynamicProxyConfigProvider provider, GatewayMetrics metrics) =>
        {
            if (!context.Request.Headers.ContainsKey("If-Match")) return Results.StatusCode(428);
            if (!store.TryRollback(revision, context.Request.Headers.IfMatch.ToString(), out var next, out var error))
                return Results.Json(new { error }, statusCode: error == "etag_conflict" ? 412 : 400);
            provider.Reload(store.Current!);
            metrics.ConfigPublished(true);
            context.Response.Headers.ETag = store.Current!.ETag;
            return Results.Ok(new { revision = next, etag = store.Current.ETag });
        });
    }
}
