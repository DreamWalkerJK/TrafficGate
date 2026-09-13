namespace TrafficGate;

public static class GatewayErrors
{
    public static async Task WriteAsync(HttpContext context, int status, string code, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted || cancellationToken.IsCancellationRequested) return;
        context.Response.StatusCode = status;
        context.Response.ContentLength = null;
        context.Response.Headers["X-TrafficGate-Error"] = code;
        await context.Response.WriteAsJsonAsync(new
        {
            type = "urn:trafficgate:error:" + code, title = code, status,
            requestId = context.TraceIdentifier
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
    }
}
