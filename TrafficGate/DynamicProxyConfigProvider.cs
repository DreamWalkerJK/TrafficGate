using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;

namespace TrafficGate;

public sealed class DynamicProxyConfigProvider : IProxyConfigProvider
{
    public const string PolicyMetadataKey = "TrafficGate:Policy";
    public const string RevisionMetadataKey = "TrafficGate:Revision";
    public const string ConnectTimeoutMetadataKey = "TrafficGate:ConnectTimeoutSeconds";
    readonly object gate = new();
    readonly InMemoryConfigProvider provider = new([], []);
    int revisionNumber;
    public IProxyConfig GetConfig() => provider.GetConfig();
    public int PublishedRevision => Volatile.Read(ref revisionNumber);

    public void Reload(ConfigRevision revision)
    {
        var snapshot = Build(revision);
        lock (gate)
        {
            // An older admin response must not roll the provider back after a newer commit.
            if (revision.Revision <= revisionNumber) return;
            provider.Update(snapshot.Routes, snapshot.Clusters, revision.ETag);
            Volatile.Write(ref revisionNumber, revision.Revision);
        }
    }

    public static ProxySnapshot Build(ConfigRevision revision)
    {
        var definition = revision.Definition;
        var routes = definition.Routes.Select(r =>
        {
            var transforms = new List<IReadOnlyDictionary<string, string>>();
            void Add(Dictionary<string, string> value) => transforms.Add(value.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
            if (r.PathRemovePrefix is not null) Add(new() { ["PathRemovePrefix"] = r.PathRemovePrefix });
            if (r.PathPrefix is not null) Add(new() { ["PathPrefix"] = r.PathPrefix });
            foreach (var header in r.RemoveRequestHeaders) Add(new() { ["RequestHeaderRemove"] = header });
            foreach (var header in r.RequestHeaders) Add(new() { ["RequestHeader"] = header.Key, ["Set"] = header.Value });
            // Authorization is enforced by the gateway middleware, allowing its own stable
            // problem response while the same ASP.NET authorization service evaluates policies.
            return new RouteConfig
            {
                RouteId = r.RouteId,
                ClusterId = r.ClusterId,
                Order = r.Priority,
                Match = new RouteMatch { Path = r.MatchPath, Hosts = r.Hosts.ToImmutableArray(), Methods = r.Methods.ToImmutableArray() },
                Transforms = transforms.ToImmutableArray(),
                Metadata = new Dictionary<string, string>
                {
                    [RevisionMetadataKey] = revision.Revision.ToString(CultureInfo.InvariantCulture),
                    [PolicyMetadataKey] = JsonSerializer.Serialize(r)
                }.ToFrozenDictionary(StringComparer.Ordinal)
            };
        }).ToImmutableArray();
        var clusters = definition.Clusters.Select(c => new ClusterConfig
        {
            ClusterId = c.ClusterId,
            LoadBalancingPolicy = c.LoadBalancingPolicy,
            Destinations = c.Destinations.ToFrozenDictionary(d => d.DestinationId,
                d => new DestinationConfig { Address = d.Address }, StringComparer.OrdinalIgnoreCase),
            HttpClient = new HttpClientConfig { DangerousAcceptAnyServerCertificate = false,
                MaxConnectionsPerServer = c.MaxConnectionsPerServer, EnableMultipleHttp2Connections = true },
            HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(c.ActivityTimeoutSeconds),
                Version = Version.Parse(c.HttpVersion), VersionPolicy = Enum.Parse<HttpVersionPolicy>(c.HttpVersionPolicy), AllowResponseBuffering = false },
            HealthCheck = new HealthCheckConfig
            {
                AvailableDestinationsPolicy = "HealthyAndUnknown",
                Active = new ActiveHealthCheckConfig { Enabled = c.ActiveHealthEnabled, Path = c.HealthPath,
                    Interval = TimeSpan.FromSeconds(c.HealthIntervalSeconds), Timeout = TimeSpan.FromSeconds(c.HealthTimeoutSeconds), Policy = "ConsecutiveFailures" },
                Passive = new PassiveHealthCheckConfig { Enabled = c.PassiveHealthEnabled, Policy = "TransportFailureRate",
                    ReactivationPeriod = TimeSpan.FromSeconds(c.PassiveReactivationSeconds) }
            },
            Metadata = new Dictionary<string, string>
            {
                [ConnectTimeoutMetadataKey] = c.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                [ConsecutiveFailuresHealthPolicyOptions.ThresholdMetadataName] = c.HealthFailureThreshold.ToString(CultureInfo.InvariantCulture)
            }.ToFrozenDictionary(StringComparer.Ordinal)
        }).ToImmutableArray();
        return new(routes, clusters);
    }

    public static RouteDefinition? ReadPolicy(RouteConfig route)
    {
        if (route.Metadata is not { } metadata || !metadata.TryGetValue(PolicyMetadataKey, out var json)) return null;
        var policy = JsonSerializer.Deserialize<RouteDefinition>(json);
        if (policy is not null && metadata.TryGetValue(RevisionMetadataKey, out var revision))
            policy.Revision = int.Parse(revision, CultureInfo.InvariantCulture);
        return policy;
    }

    /// <summary>Register with AddReverseProxy().ConfigureHttpClient to guard every new socket, including health probes.</summary>
    public static void ConfigureHttpClient(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        handler.ConnectTimeout = TimeSpan.FromSeconds(int.Parse(context.NewMetadata![ConnectTimeoutMetadataKey], CultureInfo.InvariantCulture));
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.UseProxy = false;
        handler.AllowAutoRedirect = false;
        handler.ConnectCallback = ConnectAsync;
    }

    static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        if (GatewayValidation.IsDangerousHost(host)) throw new HttpRequestException("Destination network boundary rejected the connection.");
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        // Check the complete DNS answer, then connect the validated numeric address. A
        // second hostname resolution by the socket would permit DNS rebinding.
        if (addresses.Length == 0 || addresses.Any(GatewayValidation.IsDangerousAddress))
            throw new HttpRequestException("Destination network boundary rejected the resolved address.");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                last = ex;
            }
        }
        throw new HttpRequestException("Could not establish an upstream connection.", last);
    }
}

public sealed record ProxySnapshot(IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters);
