using TrafficGate;
using Microsoft.AspNetCore.Http;

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
}
