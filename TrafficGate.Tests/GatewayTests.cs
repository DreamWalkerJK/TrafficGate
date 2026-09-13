using TrafficGate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace TrafficGate.Tests;
public class GatewayTests
{
    [Fact]
    public void Validation_rejects_missing_cluster_and_bad_destination()
    {
        var errors = GatewayValidation.Validate(new GatewayDefinition
        {
            Routes = [new RouteDefinition { RouteId = "r", ClusterId = "missing" }],
            Clusters = [new ClusterDefinition { ClusterId = "c", Destinations = [new DestinationDefinition { DestinationId = "d", Address = "file:///etc/passwd" }] }]
        });
        Assert.Contains(errors, x => x.Contains("missing cluster")); Assert.Contains(errors, x => x.Contains("invalid destination"));
    }

    [Fact]
    public async Task Limiter_rejects_second_concurrent_request()
    {
        var limiter = new GatewayLimiter(); var route = new RouteDefinition { RouteId = "r", ClusterId = "c", RateLimitPerMinute = 100, ConcurrencyLimit = 1 };
        var context = new DefaultHttpContext(); using var first = await limiter.AcquireAsync(route, context, CancellationToken.None); using var second = await limiter.AcquireAsync(route, context, CancellationToken.None);
        Assert.True(first.IsAcquired); Assert.False(second.IsAcquired); Assert.Equal("concurrency_limit", second.Reason);
    }

    [Fact]
    public void Passive_health_requires_three_failures_and_recovers_on_success()
    {
        var health = new UpstreamHealthRegistry();
        Assert.True(health.IsHealthy("route"));
        health.RecordFailure("route", "upstream_unavailable");
        health.RecordFailure("route", "upstream_unavailable");
        Assert.True(health.IsHealthy("route"));
        health.RecordFailure("route", "upstream_unavailable");
        var failed = Assert.Single(health.Snapshot());
        Assert.False(failed.Value.Healthy);
        Assert.Equal(3, failed.Value.ConsecutiveFailures);
        health.RecordSuccess("route");
        Assert.True(health.IsHealthy("route"));
        Assert.Equal(0, health.Snapshot()["route"].ConsecutiveFailures);
    }

    [Fact]
    public void Config_store_rejects_stale_etag_and_creates_rollback_revision()
    {
        var file = Path.Combine(Path.GetTempPath(), "trafficgate-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var store = new GatewayConfigStore(Options.Create(new GatewayOptions { DataPath = file }), config);
            var d = new GatewayDefinition { Clusters = [new ClusterDefinition { ClusterId = "c", Destinations = [new DestinationDefinition { DestinationId = "d", Address = "http://127.0.0.1:5090" }] }], Routes = [new RouteDefinition { RouteId = "r", ClusterId = "c" }] };
            Assert.True(store.TryPublish(d, store.Current!.ETag, out var revision, out _));
            Assert.False(store.TryPublish(d, "\"stale\"", out _, out var error)); Assert.Equal("etag_conflict", error);
            Assert.True(store.TryRollback(1, store.Current!.ETag, out var rollback, out _)); Assert.True(rollback > revision);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void Dynamic_provider_attaches_immutable_policy_snapshot_metadata()
    {
        var provider = new DynamicProxyConfigProvider();
        var revision = new ConfigRevision(7, "\"etag\"", DateTimeOffset.UtcNow, new GatewayDefinition
        {
            Routes = [new RouteDefinition { RouteId = "r", ClusterId = "c", AuthorizationPolicy = "admin", RateLimitPerMinute = 11, ConcurrencyLimit = 2, TimeoutSeconds = 9 }],
            Clusters = [new ClusterDefinition { ClusterId = "c", Destinations = [new DestinationDefinition { DestinationId = "d", Address = "http://127.0.0.1:5090/" }] }]
        });
        provider.Reload(revision);
        var route = Assert.Single(provider.GetConfig().Routes);
        Assert.Equal("7", route.Metadata!["TrafficGate:Revision"]);
        var policy = DynamicProxyConfigProvider.ReadPolicy(route);
        Assert.NotNull(policy);
        Assert.Equal("admin", policy!.AuthorizationPolicy);
        Assert.Equal(11, policy.RateLimitPerMinute);
        Assert.Equal(2, policy.ConcurrencyLimit);
        Assert.Equal(9, policy.TimeoutSeconds);
        policy.ConcurrencyLimit = 1000;
        Assert.Equal(2, DynamicProxyConfigProvider.ReadPolicy(route)!.ConcurrencyLimit);
    }

    [Fact]
    public void Validation_rejects_ambiguous_equal_priority_routes_and_unsafe_destination_metadata()
    {
        var errors = GatewayValidation.Validate(new GatewayDefinition
        {
            Routes = [
                new RouteDefinition { RouteId = "a", ClusterId = "c", MatchPath = "/items/{id}" },
                new RouteDefinition { RouteId = "b", ClusterId = "c", MatchPath = "/items/{name}" }],
            Clusters = [new ClusterDefinition { ClusterId = "c", Destinations = [
                new DestinationDefinition { DestinationId = "d", Address = "http://user:pass@127.0.0.1:5090/?token=secret" }] }]
        });
        Assert.Contains(errors, x => x.Contains("route conflict"));
        Assert.Contains(errors, x => x.Contains("invalid destination"));
    }

    [Fact]
    public void Publish_requires_if_match_even_for_first_update()
    {
        var file = Path.Combine(Path.GetTempPath(), "trafficgate-etag-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new GatewayConfigStore(Options.Create(new GatewayOptions { DataPath = file }), new ConfigurationBuilder().Build());
            Assert.False(store.TryPublish(new GatewayDefinition(), "", out _, out var error));
            Assert.Equal("etag_required", error);
            Assert.Equal(1, store.Current!.Revision);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }
}
