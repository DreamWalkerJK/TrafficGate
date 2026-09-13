using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Microsoft.Data.Sqlite;

namespace TrafficGate;
public sealed class GatewayOptions { public string ListenUrl { get; set; } = "http://127.0.0.1:5080"; public string DataPath { get; set; } = "data/trafficgate.db"; public JwtOptions Jwt { get; set; } = new(); }
public sealed class JwtOptions { public string? Authority { get; set; } public string? Audience { get; set; } public bool RequireHttpsMetadata { get; set; } }
public sealed class GatewayDefinition { public List<RouteDefinition> Routes { get; set; } = []; public List<ClusterDefinition> Clusters { get; set; } = []; }
public sealed class RouteDefinition { public required string RouteId { get; set; } public string MatchPath { get; set; } = "/{**catch-all}"; public string[] Hosts { get; set; } = []; public string[] Methods { get; set; } = []; public int Priority { get; set; } public required string ClusterId { get; set; } public string? AuthorizationPolicy { get; set; } public int RateLimitPerMinute { get; set; } = 120; public int ConcurrencyLimit { get; set; } = 64; public int QueueLimit { get; set; } public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024; public int TimeoutSeconds { get; set; } = 30; }
public sealed class ClusterDefinition { public required string ClusterId { get; set; } public string LoadBalancingPolicy { get; set; } = "RoundRobin"; public List<DestinationDefinition> Destinations { get; set; } = []; }
public sealed class DestinationDefinition { public required string DestinationId { get; set; } public required string Address { get; set; } }
public sealed record ConfigRevision(int Revision, string ETag, DateTimeOffset CreatedAt, GatewayDefinition Definition);
public sealed class GatewayConfigStore
{
    readonly object gate = new(); readonly string path; public ConfigRevision? Current { get; private set; } public List<ConfigRevision> Revisions { get; } = [];
    readonly IConfiguration configuration;
    public GatewayConfigStore(IOptions<GatewayOptions> options, IConfiguration configuration) { this.configuration = configuration; path = options.Value.DataPath; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); Load(); }
    void Load() { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using (var create = db.CreateCommand()) { create.CommandText = "CREATE TABLE IF NOT EXISTS revisions (revision INTEGER PRIMARY KEY, snapshot TEXT NOT NULL)"; create.ExecuteNonQuery(); } using (var read = db.CreateCommand()) { read.CommandText = "SELECT snapshot FROM revisions ORDER BY revision"; using var reader = read.ExecuteReader(); while (reader.Read()) { var revision = JsonSerializer.Deserialize<ConfigRevision>(reader.GetString(0)); if (revision is not null) Revisions.Add(revision); } } Current = Revisions.LastOrDefault(); if (Current is null) { var initial = configuration.GetSection("TrafficGate").Get<GatewayDefinition>() ?? new(); Current = new(1, "\"bootstrap\"", DateTimeOffset.UtcNow, initial); Revisions.Add(Current); Persist(); } }
    void Persist() { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using var transaction = db.BeginTransaction(); foreach (var revision in Revisions) { using var cmd = db.CreateCommand(); cmd.Transaction = transaction; cmd.CommandText = "INSERT OR IGNORE INTO revisions (revision, snapshot) VALUES ($revision, $snapshot)"; cmd.Parameters.AddWithValue("$revision", revision.Revision); cmd.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(revision)); cmd.ExecuteNonQuery(); } transaction.Commit(); }
    public object RedactedCurrent() => new { Current?.Revision, Current?.ETag, Current?.CreatedAt, Current?.Definition };
    public bool TryPublish(GatewayDefinition definition, string ifMatch, out int revision, out string? error) { lock (gate) { if (Current is not null && !string.IsNullOrEmpty(ifMatch) && ifMatch != Current.ETag) { revision = 0; error = "etag_conflict"; return false; } revision = (Current?.Revision ?? 0) + 1; var etag = '"' + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + '"'; Current = new(revision, etag, DateTimeOffset.UtcNow, definition); Revisions.Add(Current); Persist(); error = null; return true; } }
    public bool TryRollback(int target, string ifMatch, out int revision, out string? error) { lock (gate) { var item = Revisions.FirstOrDefault(x => x.Revision == target); if (item is null) { revision = 0; error = "revision_not_found"; return false; } return TryPublish(item.Definition, ifMatch, out revision, out error); } }
}
public sealed class DynamicProxyConfigProvider : IProxyConfigProvider
{
    volatile InMemoryProxyConfig config; CancellationTokenSource change = new(); public IProxyConfig GetConfig() => config;
    public DynamicProxyConfigProvider() => config = new InMemoryProxyConfig([], [], change.Token);
    public void Reload(ConfigRevision revision) { var routes = revision.Definition.Routes.Select(r => new RouteConfig { RouteId = r.RouteId, ClusterId = r.ClusterId, Order = r.Priority, Match = new RouteMatch { Path = r.MatchPath, Hosts = r.Hosts, Methods = r.Methods }, Metadata = new Dictionary<string, string>() }).ToList(); var clusters = revision.Definition.Clusters.Select(c => new ClusterConfig { ClusterId = c.ClusterId, LoadBalancingPolicy = c.LoadBalancingPolicy, Destinations = c.Destinations.ToDictionary(d => d.DestinationId, d => new DestinationConfig { Address = d.Address }) }).ToList(); var old = change; change = new(); config = new InMemoryProxyConfig(routes, clusters, change.Token); old.Cancel(); old.Dispose(); }
    sealed class InMemoryProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters, CancellationToken token) : IProxyConfig { public IReadOnlyList<RouteConfig> Routes { get; } = routes; public IReadOnlyList<ClusterConfig> Clusters { get; } = clusters; public IChangeToken ChangeToken => new CancellationChangeToken(token); }
}
public static class GatewayValidation
{
    public static List<string> Validate(GatewayDefinition d) { var e = new List<string>(); var routeIds = new HashSet<string>(); foreach (var r in d.Routes) { if (!routeIds.Add(r.RouteId)) e.Add($"duplicate route: {r.RouteId}"); if (!d.Clusters.Any(c => c.ClusterId == r.ClusterId)) e.Add($"missing cluster: {r.ClusterId}"); if (r.Priority < 0 || r.RateLimitPerMinute < 0 || r.ConcurrencyLimit < 1 || r.QueueLimit < 0) e.Add($"invalid limits: {r.RouteId}"); } var clusterIds = new HashSet<string>(); foreach (var c in d.Clusters) { if (!clusterIds.Add(c.ClusterId)) e.Add($"duplicate cluster: {c.ClusterId}"); foreach (var x in c.Destinations) if (!Uri.TryCreate(x.Address, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) e.Add($"invalid destination: {x.Address}"); } return e; }
}
