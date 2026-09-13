using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrafficGate;

public sealed class GatewayOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:5080";
    public string ManagementUrl { get; set; } = "http://127.0.0.1:5081";
    public string DataPath { get; set; } = "data/trafficgate.db";
    public JwtOptions Jwt { get; set; } = new();
}

public sealed class JwtOptions
{
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
}

// Mutable input DTOs are detached before validation and never become shared runtime state.
public sealed class GatewayDefinition
{
    public List<RouteDefinition> Routes { get; set; } = [];
    public List<ClusterDefinition> Clusters { get; set; } = [];
}

public sealed class RouteDefinition
{
    public required string RouteId { get; set; }
    public string MatchPath { get; set; } = "/{**catch-all}";
    public string[] Hosts { get; set; } = [];
    public string[] Methods { get; set; } = [];
    public int Priority { get; set; }
    public required string ClusterId { get; set; }
    public bool AllowAnonymous { get; set; }
    public string? AuthorizationPolicy { get; set; }
    public int RateLimitPerMinute { get; set; } = 120;
    public string RateLimitPartition { get; set; } = "auto";
    public int ConcurrencyLimit { get; set; } = 64;
    public int QueueLimit { get; set; }
    public int MaxPartitions { get; set; } = 10000;
    public int IdlePartitionSeconds { get; set; } = 300;
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxRequestHeaderBytes { get; set; } = 32 * 1024;
    public int MaxRequestLineBytes { get; set; } = 8 * 1024;
    // Zero explicitly disables the total deadline for long-lived streaming routes.
    public int TimeoutSeconds { get; set; } = 30;
    public string? PathRemovePrefix { get; set; }
    public string? PathPrefix { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] RemoveRequestHeaders { get; set; } = [];
    [JsonIgnore] public int Revision { get; set; }
}

public sealed class ClusterDefinition
{
    public required string ClusterId { get; set; }
    public string LoadBalancingPolicy { get; set; } = "RoundRobin";
    public List<DestinationDefinition> Destinations { get; set; } = [];
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int ActivityTimeoutSeconds { get; set; } = 100;
    public int MaxConnectionsPerServer { get; set; } = 256;
    public string HttpVersion { get; set; } = "2.0";
    public string HttpVersionPolicy { get; set; } = "RequestVersionOrLower";
    public bool ActiveHealthEnabled { get; set; }
    public string HealthPath { get; set; } = "/health";
    public int HealthIntervalSeconds { get; set; } = 10;
    public int HealthTimeoutSeconds { get; set; } = 3;
    public int HealthFailureThreshold { get; set; } = 2;
    public bool PassiveHealthEnabled { get; set; } = true;
    public int PassiveReactivationSeconds { get; set; } = 30;
}

public sealed class DestinationDefinition
{
    public required string DestinationId { get; set; }
    public required string Address { get; set; }
}

/// <summary>
/// A committed revision owns a serialized definition. Consumers receive detached
/// DTOs, so neither an admin editor nor the publishing caller can mutate history.
/// </summary>
public sealed record ConfigRevision
{
    readonly string definitionJson;
    [JsonConstructor]
    public ConfigRevision(int revision, string eTag, DateTimeOffset createdAt, GatewayDefinition definition)
    {
        Revision = revision;
        ETag = eTag;
        CreatedAt = createdAt;
        definitionJson = JsonSerializer.Serialize(definition);
    }
    public int Revision { get; }
    public string ETag { get; }
    public DateTimeOffset CreatedAt { get; }
    public GatewayDefinition Definition => JsonSerializer.Deserialize<GatewayDefinition>(definitionJson)!;
}

public sealed record ConfigFailure(DateTimeOffset OccurredAt, int? Revision, string Reason);
public sealed record ConfigAudit(long Id, DateTimeOffset OccurredAt, string Actor, int? Revision, string Action, string Result);
public sealed record ConfigDifference(string Kind, string Id, string Change);
