using System.Diagnostics.Metrics;
namespace TrafficGate;
public sealed class GatewayMetrics
{
    public const string SourceName = "TrafficGate.Gateway"; public const string MeterName = "TrafficGate";
    readonly Meter meter = new(MeterName); readonly Counter<long> requests; readonly Counter<long> cancelled;
    readonly Counter<long> proxyRequests; readonly Counter<long> proxyErrors; readonly Counter<long> throttled; readonly Counter<long> forwardingFailures;
    readonly Histogram<double> gatewayDuration; readonly Histogram<double> upstreamDuration;
    public GatewayMetrics()
    {
        requests = meter.CreateCounter<long>("trafficgate_config_publications_total");
        cancelled = meter.CreateCounter<long>("trafficgate_client_cancelled_total");
        proxyRequests = meter.CreateCounter<long>("trafficgate_proxy_requests_total");
        proxyErrors = meter.CreateCounter<long>("trafficgate_proxy_errors_total");
        throttled = meter.CreateCounter<long>("trafficgate_throttled_requests_total");
        forwardingFailures = meter.CreateCounter<long>("trafficgate_forwarding_failures_total");
        gatewayDuration = meter.CreateHistogram<double>("trafficgate_gateway_duration_ms");
        upstreamDuration = meter.CreateHistogram<double>("trafficgate_upstream_duration_ms");
    }
    public void ConfigPublished(bool success) => requests.Add(1, new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));
    public void Cancelled() => cancelled.Add(1);
    public void ProxyRequest(string route, string outcome, double gatewayMs, double upstreamMs)
    {
        var tags = new KeyValuePair<string, object?>[] { new("route", route), new("outcome", outcome) };
        proxyRequests.Add(1, tags); gatewayDuration.Record(gatewayMs, tags); upstreamDuration.Record(upstreamMs, tags);
        if (outcome is "rate_limited" or "concurrency_limit" or "partition_limit") throttled.Add(1, tags);
        if (outcome is not "success" and not "upstream_error") { proxyErrors.Add(1, tags); if (outcome.StartsWith("upstream_", StringComparison.Ordinal) || outcome == "no_healthy_destinations") forwardingFailures.Add(1, tags); }
    }
    public string Snapshot() => "# TrafficGate metrics are exported through OpenTelemetry\n";
}
