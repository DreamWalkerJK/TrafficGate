using System.Collections.Concurrent;

namespace TrafficGate;

/// <summary>
/// In-process passive health state for proxied routes. A response with a server
/// error or a forwarding exception counts as a failure; a successful response
/// immediately restores the route. State is bounded by the currently configured
/// route count and is intentionally not persisted with configuration revisions.
/// </summary>
public sealed class UpstreamHealthRegistry
{
    const int FailureThreshold = 3;
    readonly ConcurrentDictionary<string, HealthState> states = new(StringComparer.Ordinal);
    public static UpstreamHealthRegistry Shared { get; } = new();

    public void RecordSuccess(string routeId) => states.AddOrUpdate(routeId,
        _ => new HealthState(true, 0, DateTimeOffset.UtcNow, null),
        (_, old) => old with { Healthy = true, ConsecutiveFailures = 0, LastChangedAt = old.Healthy ? old.LastChangedAt : DateTimeOffset.UtcNow, LastFailure = null });

    public void RecordFailure(string routeId, string reason)
    {
        states.AddOrUpdate(routeId,
            _ => new HealthState(true, 1, DateTimeOffset.UtcNow, reason),
            (_, old) =>
            {
                var failures = Math.Min(FailureThreshold, old.ConsecutiveFailures + 1);
                var healthy = failures < FailureThreshold;
                return old with
                {
                    Healthy = healthy,
                    ConsecutiveFailures = failures,
                    LastChangedAt = old.Healthy != healthy ? DateTimeOffset.UtcNow : old.LastChangedAt,
                    LastFailure = reason
                };
            });
    }

    public IReadOnlyDictionary<string, HealthSnapshot> Snapshot() => states.ToDictionary(
        x => x.Key,
        x => new HealthSnapshot(x.Value.Healthy, x.Value.ConsecutiveFailures, x.Value.LastChangedAt, x.Value.LastFailure),
        StringComparer.Ordinal);

    public bool IsHealthy(string routeId) => !states.TryGetValue(routeId, out var state) || state.Healthy;

    public void RetainRoutes(IEnumerable<string> routeIds)
    {
        var allowed = routeIds.ToHashSet(StringComparer.Ordinal);
        foreach (var routeId in states.Keys) if (!allowed.Contains(routeId)) states.TryRemove(routeId, out _);
    }

    readonly record struct HealthState(bool Healthy, int ConsecutiveFailures, DateTimeOffset LastChangedAt, string? LastFailure);
}

public sealed record HealthSnapshot(bool Healthy, int ConsecutiveFailures, DateTimeOffset LastChangedAt, string? LastFailure);
