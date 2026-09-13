var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => Results.Json(new { service = "test-upstream", status = "ok" }));
app.MapGet("/delay/{milliseconds:int}", async (int milliseconds, CancellationToken ct) => { await Task.Delay(Math.Clamp(milliseconds, 0, 120000), ct); return Results.Ok(new { delayed = milliseconds }); });
app.MapGet("/error", () => Results.Problem("controlled upstream error", statusCode: 500));
app.MapGet("/large", () => Results.Text(new string('x', 1024 * 1024)));
app.MapGet("/sse", async (HttpResponse response, CancellationToken ct) => { response.ContentType = "text/event-stream"; for (var i = 0; i < 5; i++) { await response.WriteAsync($"data: {i}\n\n", ct); await response.Body.FlushAsync(ct); await Task.Delay(250, ct); } });
app.MapGet("/redirect", () => Results.Redirect("/"));
app.Run("http://127.0.0.1:5090");
