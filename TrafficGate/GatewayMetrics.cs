using System.Diagnostics.Metrics;
namespace TrafficGate;
public sealed class GatewayMetrics
{
    public const string SourceName = "TrafficGate.Gateway"; public const string MeterName = "TrafficGate";
    readonly Meter meter = new(MeterName); readonly Counter<long> requests; readonly Counter<long> cancelled;
    public GatewayMetrics() { requests = meter.CreateCounter<long>("trafficgate_config_publications_total"); cancelled = meter.CreateCounter<long>("trafficgate_client_cancelled_total"); }
    public void ConfigPublished(bool success) => requests.Add(1, new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));
    public void Cancelled() => cancelled.Add(1);
    public string Snapshot() => "# TrafficGate metrics are exported through OpenTelemetry\n";
}
