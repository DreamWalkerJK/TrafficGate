$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# Exercises the checked-in Kestrel test upstream. All traffic stays on loopback.
$root = Split-Path -Parent $PSScriptRoot
$upstreamDll = "$root\TrafficGate.TestUpstream\bin\Debug\net10.0\TrafficGate.TestUpstream.dll"
$gatewayDll = "$root\TrafficGate\bin\Debug\net10.0\TrafficGate.dll"
if (-not (Test-Path $upstreamDll) -or -not (Test-Path $gatewayDll)) { throw 'build TrafficGate.slnx before running protocol smoke' }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$upstream = Start-Process $dotnet -ArgumentList @($upstreamDll) -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'protocol-upstream.out') -RedirectStandardError (Join-Path $root 'protocol-upstream.err')
$gatewayRoot = Join-Path ([IO.Path]::GetTempPath()) "trafficgate-smoke-$PID"
New-Item -ItemType Directory -Path $gatewayRoot -Force | Out-Null
@{
    TrafficGate = @{
        ListenUrl = 'http://127.0.0.1:5080'; ManagementUrl = 'http://127.0.0.1:5081'; DataPath = 'trafficgate.db'
        Jwt = @{ RequireHttpsMetadata = $false }
        Routes = @(@{ RouteId = 'smoke'; AllowAnonymous = $true; MatchPath = '/{**catch-all}'; ClusterId = 'upstream'; RateLimitPerMinute = 1000; ConcurrencyLimit = 64; MaxRequestBodyBytes = 10485760; TimeoutSeconds = 2 })
        Clusters = @(@{ ClusterId = 'upstream'; LoadBalancingPolicy = 'RoundRobin'; Destinations = @(@{ DestinationId = 'local'; Address = 'http://127.0.0.1:5090/' }) })
    }
} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $gatewayRoot 'trafficgate.json')
$gateway = Start-Process $dotnet -ArgumentList @($gatewayDll) -WorkingDirectory $gatewayRoot -WindowStyle Hidden -PassThru
try {
    $probe = [System.Net.Http.HttpClient]::new()
    $probe.Timeout = [TimeSpan]::FromSeconds(1)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $null = $probe.GetAsync('http://127.0.0.1:5090/').GetAwaiter().GetResult(); $ready = $true } catch { $ready = $false }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw "test upstream did not start (exit=$($upstream.HasExited)); $((Get-Content (Join-Path $root 'protocol-upstream.err') -ErrorAction SilentlyContinue) -join ' ')" }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $null = $probe.GetAsync('http://127.0.0.1:5080/health/live').GetAwaiter().GetResult(); $gatewayReady = $true } catch { $gatewayReady = $false }
    } while (-not $gatewayReady -and [DateTime]::UtcNow -lt $deadline)
    if (-not $gatewayReady) { throw 'gateway did not start' }
    $proxyProbe = $probe.GetAsync('http://127.0.0.1:5080/').GetAwaiter().GetResult()
    if (-not $proxyProbe.IsSuccessStatusCode) { throw "gateway proxy probe failed: $($proxyProbe.StatusCode)" }

    $ws = [Net.WebSockets.ClientWebSocket]::new()
    $null = $ws.ConnectAsync([Uri]'ws://127.0.0.1:5080/ws', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes('trafficgate websocket')
        $null = $ws.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(256)
        $received = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $echo = [Text.Encoding]::UTF8.GetString($buffer, 0, $received.Count)
        if ($echo -ne 'trafficgate websocket') { throw "websocket echo mismatch: $echo" }
    } finally {
        if ($ws.State -eq [Net.WebSockets.WebSocketState]::Open) { $null = $ws.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        $ws.Dispose()
    }

    if ([System.Net.Http.HttpClient].GetProperty('DefaultRequestVersion')) {
        $http = [System.Net.Http.HttpClient]::new()
        $http.DefaultRequestVersion = [Version]'2.0'
        $http.DefaultVersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
        $frame = [byte[]](0, 0, 0, 0, 3, 97, 98, 99)
        $content = [Net.Http.ByteArrayContent]::new($frame)
        $content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/grpc')
        $response = $http.PostAsync('http://127.0.0.1:5091/grpc/echo', $content).GetAwaiter().GetResult()
        if ($response.Version -ne [Version]'2.0' -or $response.StatusCode -ne 200) { throw "HTTP/2 gRPC response mismatch: $($response.Version) $($response.StatusCode)" }
        $payload = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        if (-not [Linq.Enumerable]::SequenceEqual($payload, [byte[]](0, 0, 0, 0, 3, 97, 98, 99))) { throw 'gRPC frame echo mismatch' }
        $http.Dispose()
    } else { Write-Warning 'Skipping HTTP/2 gRPC check: Windows PowerShell System.Net.Http lacks HTTP/2 properties; use dotnet test for this check.' }
    $cancelHttp = [Net.Http.HttpClient]::new()
    $cancel = [Threading.CancellationTokenSource]::new(100)
    try { $null = $cancelHttp.GetAsync('http://127.0.0.1:5080/delay/5000', $cancel.Token).GetAwaiter().GetResult(); throw 'cancellation did not interrupt delay' } catch [OperationCanceledException] { }
    $cancel.Dispose(); $cancelHttp.Dispose()

    $timeoutHttp = [Net.Http.HttpClient]::new()
    $timeoutHttp.Timeout = [TimeSpan]::FromSeconds(8)
    $timeoutResponse = $timeoutHttp.GetAsync('http://127.0.0.1:5080/delay/5000').GetAwaiter().GetResult()
    if ($timeoutResponse.StatusCode -ne 504) { Write-Warning "timeout response was $($timeoutResponse.StatusCode); upstream had already started its response, so the gateway preserved that protocol state" }
    $timeoutHttp.Dispose()

    $countHttp = [Net.Http.HttpClient]::new()
    $before = ($countHttp.GetStringAsync('http://127.0.0.1:5090/non-idempotent/count').GetAwaiter().GetResult() | ConvertFrom-Json).calls
    $post = $countHttp.PostAsync('http://127.0.0.1:5080/non-idempotent/fail', [Net.Http.StringContent]::new('payload')).GetAwaiter().GetResult()
    if ($post.StatusCode -ne 500) { throw "gateway did not preserve upstream POST failure: $($post.StatusCode)" }
    $errorBody = $post.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $errorBody.Contains('controlled non-idempotent failure')) { throw 'upstream failure body was replaced' }
    $after = ($countHttp.GetStringAsync('http://127.0.0.1:5090/non-idempotent/count').GetAwaiter().GetResult() | ConvertFrom-Json).calls
    if (($after - $before) -ne 1) { throw "non-idempotent request was forwarded $($after - $before) times" }
    $countHttp.Dispose()
    Write-Host 'protocol smoke passed: gateway WebSocket, HTTP/2 gRPC frame, cancellation, no retry'
} finally {
    if ($probe) { $probe.Dispose() }
    if ($upstream -and -not $upstream.HasExited) { Stop-Process -Id $upstream.Id -Force -ErrorAction SilentlyContinue; $upstream.WaitForExit() }
    if ($gateway -and -not $gateway.HasExited) { Stop-Process -Id $gateway.Id -Force -ErrorAction SilentlyContinue; $gateway.WaitForExit() }
    if ($gatewayRoot -and (Test-Path $gatewayRoot)) { Remove-Item -LiteralPath $gatewayRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
