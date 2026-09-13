using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
// Keep the regular listener HTTP/1.1 so it can exercise SSE and WebSockets,
// and expose a second clear-text HTTP/2 listener for local gRPC checks.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(5090, listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    kestrel.ListenLocalhost(5091, listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});
var app = builder.Build();
app.UseWebSockets();
var nonIdempotentCalls = 0;
app.MapGet("/", () => Results.Json(new { service = "test-upstream", status = "ok" }));
app.MapGet("/delay/{milliseconds:int}", async (int milliseconds, CancellationToken ct) => { await Task.Delay(Math.Clamp(milliseconds, 0, 120000), ct); return Results.Ok(new { delayed = milliseconds }); });
app.MapGet("/error", () => Results.Problem("controlled upstream error", statusCode: 500));
app.MapGet("/large", () => Results.Text(new string('x', 1024 * 1024)));
app.MapGet("/sse", async (HttpResponse response, CancellationToken ct) => { response.ContentType = "text/event-stream"; for (var i = 0; i < 5; i++) { await response.WriteAsync($"data: {i}\n\n", ct); await response.Body.FlushAsync(ct); await Task.Delay(250, ct); } });
app.MapGet("/redirect", () => Results.Redirect("/"));
app.MapPost("/non-idempotent", () => Results.Json(new { calls = Interlocked.Increment(ref nonIdempotentCalls) }));
app.MapGet("/non-idempotent/count", () => Results.Ok(new { calls = Volatile.Read(ref nonIdempotentCalls) }));
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("expected websocket upgrade");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[16 * 1024];
    while (socket.State == WebSocketState.Open)
    {
        var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
        if (received.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
            break;
        }

        await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
    }
});

// A deliberately small gRPC wire endpoint. It validates the five-byte gRPC
// frame and echoes the payload, which is sufficient to verify HTTP/2 framing
// and trailer preservation without introducing a generated protobuf contract.
app.MapPost("/grpc/echo", async context =>
{
    if (context.Request.Protocol != "HTTP/2")
    {
        context.Response.StatusCode = 505;
        return;
    }

    var request = context.Request.Body;
    var header = new byte[5];
    await ReadExactlyAsync(request, header, context.RequestAborted);
    var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
    if (header[0] != 0 || length < 0 || length > 1024 * 1024)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var payload = new byte[length];
    await ReadExactlyAsync(request, payload, context.RequestAborted);
    context.Response.ContentType = "application/grpc";
    context.Response.DeclareTrailer("grpc-status");
    var responseHeader = new byte[5];
    responseHeader[0] = 0;
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(responseHeader.AsSpan(1), payload.Length);
    await context.Response.Body.WriteAsync(responseHeader, context.RequestAborted);
    await context.Response.Body.WriteAsync(payload, context.RequestAborted);
    context.Response.AppendTrailer("grpc-status", "0");
});

app.MapPost("/disconnect", async context =>
{
    context.Response.ContentType = "application/octet-stream";
    await context.Response.Body.WriteAsync("partial"u8.ToArray(), context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
    context.Abort();
});

app.Run();

static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
        if (read == 0) throw new EndOfStreamException("incomplete gRPC frame");
        offset += read;
    }
}
