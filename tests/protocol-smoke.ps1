$ErrorActionPreference = 'Stop'

# Exercises the checked-in Kestrel test upstream. All traffic stays on loopback.
$root = Split-Path -Parent $PSScriptRoot
$upstream = Start-Process dotnet -ArgumentList @('run', '--project', "$root\TrafficGate.TestUpstream\TrafficGate.TestUpstream.csproj", '--no-launch-profile', '--no-build') -WorkingDirectory $root -WindowStyle Hidden -PassThru
$gateway = Start-Process dotnet -ArgumentList @('run', '--project', "$root\TrafficGate\TrafficGate.csproj", '--no-launch-profile', '--no-build') -WorkingDirectory $root -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $null = Invoke-WebRequest http://127.0.0.1:5090/ -TimeoutSec 1; $ready = $true } catch { $ready = $false }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'test upstream did not start' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $null = Invoke-WebRequest http://127.0.0.1:5080/health/live -TimeoutSec 1; $gatewayReady = $true } catch { $gatewayReady = $false }
    } while (-not $gatewayReady -and [DateTime]::UtcNow -lt $deadline)
    if (-not $gatewayReady) { throw 'gateway did not start' }

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

    $http = [Net.Http.HttpClient]::new()
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
    $cancelHttp = [Net.Http.HttpClient]::new()
    $cancel = [Threading.CancellationTokenSource]::new(100)
    try { $null = $cancelHttp.GetAsync('http://127.0.0.1:5080/delay/5000', $cancel.Token).GetAwaiter().GetResult(); throw 'cancellation did not interrupt delay' } catch [OperationCanceledException] { }
    $cancel.Dispose(); $cancelHttp.Dispose()

    $countHttp = [Net.Http.HttpClient]::new()
    $before = ($countHttp.GetStringAsync('http://127.0.0.1:5090/non-idempotent/count').GetAwaiter().GetResult() | ConvertFrom-Json).calls
    $post = $countHttp.PostAsync('http://127.0.0.1:5080/non-idempotent', [Net.Http.StringContent]::new('payload')).GetAwaiter().GetResult()
    if ($post.StatusCode -ne 200) { throw "gateway POST failed: $($post.StatusCode)" }
    $after = ($countHttp.GetStringAsync('http://127.0.0.1:5090/non-idempotent/count').GetAwaiter().GetResult() | ConvertFrom-Json).calls
    if (($after - $before) -ne 1) { throw "non-idempotent request was forwarded $($after - $before) times" }
    $countHttp.Dispose()
    Write-Host 'protocol smoke passed: gateway WebSocket, HTTP/2 gRPC frame, cancellation, no retry'
} finally {
    if ($upstream -and -not $upstream.HasExited) { $upstream.Kill($true); $upstream.WaitForExit() }
    if ($gateway -and -not $gateway.HasExited) { $gateway.Kill($true); $gateway.WaitForExit() }
}
