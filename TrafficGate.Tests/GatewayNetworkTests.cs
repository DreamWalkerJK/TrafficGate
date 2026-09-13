using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using TrafficGate;

namespace TrafficGate.Tests;

public class GatewayNetworkTests
{
    [Fact]
    public async Task Management_port_and_audience_are_isolated_and_real_antiforgery_is_required()
    {
        await using var f = await GatewayFixture.Start();
        Assert.Equal(HttpStatusCode.NotFound, (await f.Proxy.GetAsync("/admin/config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Admin.GetAsync("/echo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Admin.GetAsync("/admin/config")).StatusCode);
        f.Admin.DefaultRequestHeaders.Authorization = new("Bearer", f.Token("trafficgate-proxy", true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Admin.GetAsync("/admin/config")).StatusCode);
        f.Admin.DefaultRequestHeaders.Authorization = new("Bearer", f.Token("trafficgate-admin", false));
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Admin.GetAsync("/admin/config")).StatusCode);
        f.Admin.DefaultRequestHeaders.Authorization = new("Bearer", f.Token("trafficgate-admin", true));
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Admin.PostAsJsonAsync("/admin/config/publish", f.Definition)).StatusCode);
        // A cookie/header pair chosen by the caller is not a server-protected antiforgery token.
        using var forged = new HttpRequestMessage(HttpMethod.Post, "/admin/config/publish") { Content = JsonContent.Create(f.Definition) };
        forged.Headers.Add("Cookie", "TrafficGate.Antiforgery=attacker"); forged.Headers.Add("X-CSRF-Token", "attacker");
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Admin.SendAsync(forged)).StatusCode);
        await f.AuthenticateAdmin();
        Assert.Equal((HttpStatusCode)428, (await f.Admin.PostAsJsonAsync("/admin/config/publish", f.Definition)).StatusCode);
        var active = await f.Admin.GetAsync("/admin/config");
        f.Admin.DefaultRequestHeaders.IfMatch.ParseAdd(active.Headers.ETag!.ToString());
        var published = await f.Admin.PostAsJsonAsync("/admin/config/publish", f.Definition);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await f.Admin.PostAsJsonAsync("/admin/config/publish", f.Definition)).StatusCode);
    }

    [Fact]
    public async Task Authentication_and_trusted_headers_use_verified_claims()
    {
        await using var f = await GatewayFixture.Start(route => { route.AllowAnonymous = false; route.AuthorizationPolicy = "authenticated"; });
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Proxy.GetAsync("/headers")).StatusCode);
        f.Proxy.DefaultRequestHeaders.Authorization = new("Bearer", f.Token("trafficgate-proxy", false));
        f.Proxy.DefaultRequestHeaders.Add("X-Authenticated-User", "forged");
        f.Proxy.DefaultRequestHeaders.Add("X-Authenticated-Tenant", "forged");
        f.Proxy.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.99");
        f.Proxy.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.invalid");
        f.Proxy.DefaultRequestHeaders.Add("Forwarded", "for=198.51.100.99;host=attacker.invalid");
        var response = await f.Proxy.GetFromJsonAsync<Dictionary<string, string>>("/headers");
        Assert.Equal("test-user", response!["subject"]);
        Assert.Equal("test-tenant", response["tenant"]);
        Assert.DoesNotContain("198.51.100.99", response["forwardedFor"]);
        Assert.DoesNotContain("attacker.invalid", response["forwardedHost"]);
        Assert.Equal("", response["forwarded"]);
    }

    [Fact]
    public async Task Upstream_error_body_headers_and_binary_are_preserved_without_retry()
    {
        await using var f = await GatewayFixture.Start();
        var error = await f.Proxy.PostAsync("/error", new StringContent("operation"));
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Equal("original upstream failure", await error.Content.ReadAsStringAsync());
        Assert.Equal("preserved", error.Headers.GetValues("X-Upstream").Single());
        Assert.False(error.Headers.Contains("X-TrafficGate-Error"));
        Assert.Equal(1, f.UpstreamCalls);
        var bytes = Enumerable.Range(0, 32768).Select(x => (byte)x).ToArray();
        var echo = await f.Proxy.PostAsync("/echo", new ByteArrayContent(bytes));
        Assert.Equal(bytes, await echo.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Timeout_returns_504_and_releases_upstream_and_concurrency()
    {
        await using var f = await GatewayFixture.Start(r => { r.TimeoutSeconds = 1; r.ConcurrencyLimit = 1; });
        var response = await f.Proxy.GetAsync("/delay");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("gateway_timeout", response.Headers.GetValues("X-TrafficGate-Error").Single());
        await f.UpstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, Volatile.Read(ref f.ActiveRequests));
        Assert.Equal(HttpStatusCode.OK, (await f.Proxy.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Client_cancellation_releases_upstream_and_route_permit()
    {
        await using var f = await GatewayFixture.Start(r => r.ConcurrencyLimit = 1);
        using var cancellation = new CancellationTokenSource();
        var pending = f.Proxy.GetAsync("/delay", cancellation.Token);
        await f.UpstreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await f.UpstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, Volatile.Read(ref f.ActiveRequests));
        // Cancellation propagation through YARP completes before the slot is released.
        await f.WaitForSuccess();
    }

    [Fact]
    public async Task Rate_limit_and_concurrency_reject_with_bounded_queue()
    {
        await using var f = await GatewayFixture.Start(r => { r.RateLimitPerMinute = 2; r.ConcurrencyLimit = 1; r.QueueLimit = 0; });
        using var cancellation = new CancellationTokenSource();
        var pending = f.Proxy.GetAsync("/delay", cancellation.Token);
        await f.UpstreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var rejected = await f.Proxy.GetAsync("/");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("concurrency_limit", rejected.Headers.GetValues("X-TrafficGate-Error").Single());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await f.UpstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.WaitForSuccess();
        var exhausted = await f.Proxy.GetAsync("/");
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
        Assert.NotNull(exhausted.Headers.RetryAfter);
    }

    [Fact]
    public async Task Request_body_limits_apply_to_known_length_and_streamed_content()
    {
        await using var f = await GatewayFixture.Start(r => r.MaxRequestBodyBytes = 64);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.Proxy.PostAsync("/echo", new ByteArrayContent(new byte[65]))).StatusCode);
        using var stream = new StreamContent(new UnseekableStream(new byte[128]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.Proxy.PostAsync("/echo", stream)).StatusCode);
    }

    [Fact]
    public async Task Sse_first_event_arrives_before_later_events_and_websocket_echo_is_preserved()
    {
        await using var f = await GatewayFixture.Start(r => r.TimeoutSeconds = 0);
        using var response = await f.Proxy.GetAsync("/sse", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("data: first", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(f.SseContinue.Task.IsCompleted);
        f.SseContinue.TrySetResult();
        Assert.Equal("", await reader.ReadLineAsync());
        Assert.Equal("data: second", await reader.ReadLineAsync());
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(f.Proxy.BaseAddress!.ToString().Replace("http:", "ws:") + "ws"), CancellationToken.None);
        var bytes = Encoding.UTF8.GetBytes("websocket payload");
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var received = await ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(bytes, buffer[..received.Count]);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}

internal sealed class GatewayFixture : IAsyncDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "trafficgate-network-" + Guid.NewGuid().ToString("N"));
    private WebApplication upstream = null!;
    private WebApplication gateway = null!;
    public HttpClient Proxy { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;
    public GatewayDefinition Definition { get; private set; } = null!;
    public TaskCompletionSource UpstreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource UpstreamCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SseContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ActiveRequests;
    public int UpstreamCalls;

    public static async Task<GatewayFixture> Start(Action<RouteDefinition>? policy = null)
    {
        var fixture = new GatewayFixture();
        try { await fixture.StartAsync(policy); return fixture; }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private async Task StartAsync(Action<RouteDefinition>? policy)
    {
        Directory.CreateDirectory(directory);
        var ub = WebApplication.CreateBuilder();
        ub.Logging.ClearProviders();
        ub.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        upstream = ub.Build();
        upstream.UseWebSockets();
        upstream.MapGet("/", () => "upstream");
        upstream.MapGet("/headers", (HttpRequest r) => new Dictionary<string, string>
        {
            ["subject"] = r.Headers["X-Authenticated-User"].ToString(), ["tenant"] = r.Headers["X-Authenticated-Tenant"].ToString(),
            ["forwardedFor"] = r.Headers["X-Forwarded-For"].ToString(), ["forwardedHost"] = r.Headers["X-Forwarded-Host"].ToString(),
            ["forwarded"] = r.Headers["Forwarded"].ToString()
        });
        upstream.MapPost("/echo", async context => { context.Response.ContentType = "application/octet-stream"; await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestAborted); });
        upstream.MapPost("/error", async context =>
        {
            Interlocked.Increment(ref UpstreamCalls);
            context.Response.StatusCode = 500; context.Response.Headers["X-Upstream"] = "preserved";
            await context.Response.WriteAsync("original upstream failure");
        });
        upstream.MapGet("/delay", async context =>
        {
            Interlocked.Increment(ref ActiveRequests); UpstreamStarted.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted); }
            catch (OperationCanceledException) { }
            finally { Interlocked.Decrement(ref ActiveRequests); UpstreamCancelled.TrySetResult(); }
        });
        upstream.MapGet("/sse", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n"); await context.Response.Body.FlushAsync();
            await SseContinue.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: second\n\n");
        });
        upstream.Map("/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close)
                { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); break; }
                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
            }
        });
        await upstream.StartAsync();
        var upstreamAddress = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var route = new RouteDefinition { RouteId = "test", ClusterId = "upstream", AllowAnonymous = true, AuthorizationPolicy = null, RateLimitPerMinute = 1000 };
        policy?.Invoke(route);
        Definition = new GatewayDefinition
        {
            Routes = [route],
            Clusters = [new ClusterDefinition { ClusterId = "upstream", Destinations = [new DestinationDefinition { DestinationId = "local", Address = upstreamAddress + "/" }] }]
        };
        var proxyPort = FreePort(); var managementPort = FreePort();
        while (managementPort == proxyPort) managementPort = FreePort();
        gateway = GatewayHost.Build([], builder =>
        {
            builder.Configuration.Sources.Clear();
            var settings = new { TrafficGate = new
            {
                ListenUrl = $"http://127.0.0.1:{proxyPort}", ManagementUrl = $"http://127.0.0.1:{managementPort}",
                DataPath = Path.Combine(directory, "config.db"), Definition.Routes, Definition.Clusters
            } };
            builder.Configuration.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(settings)));
            builder.Logging.ClearProviders();
            foreach (var scheme in new[] { GatewayAuthentication.ProxyScheme, GatewayAuthentication.AdminScheme })
                builder.Services.PostConfigure<JwtBearerOptions>(scheme, o =>
                {
                    o.TokenValidationParameters = new TokenValidationParameters
                    {
                        IssuerSigningKey = new RsaSecurityKey(rsa), ValidIssuer = "trafficgate-tests",
                        ValidAudience = scheme == GatewayAuthentication.ProxyScheme ? "trafficgate-proxy" : "trafficgate-admin",
                        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.Zero,
                        NameClaimType = "sub", RoleClaimType = "role"
                    };
                });
        });
        await gateway.StartAsync();
        Proxy = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new($"http://127.0.0.1:{proxyPort}"), Timeout = TimeSpan.FromSeconds(8) };
        Admin = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = new($"http://127.0.0.1:{managementPort}"), Timeout = TimeSpan.FromSeconds(8) };
    }

    public string Token(string audience, bool admin) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "trafficgate-tests", audience,
        [new Claim("sub", "test-user"), new Claim("tenant", "test-tenant"), new Claim("role", admin ? "trafficgate.admin" : "user")],
        DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)));

    public async Task AuthenticateAdmin()
    {
        Admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("trafficgate-admin", true));
        var csrf = await Admin.GetFromJsonAsync<JsonElement>("/admin/csrf");
        Admin.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Admin.DefaultRequestHeaders.Add("X-CSRF-Token", csrf.GetProperty("token").GetString());
    }

    public async Task WaitForSuccess()
    {
        for (var i = 0; i < 40; i++)
        {
            using var response = await Proxy.GetAsync("/");
            if (response.IsSuccessStatusCode) return;
            await Task.Delay(25);
        }
        Assert.Fail("route permit was not released");
    }

    public static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        SseContinue.TrySetResult(); Proxy?.Dispose(); Admin?.Dispose();
        if (gateway is not null) { await gateway.StopAsync(); await gateway.DisposeAsync(); }
        if (upstream is not null) { await upstream.StopAsync(); await upstream.DisposeAsync(); }
        rsa.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
