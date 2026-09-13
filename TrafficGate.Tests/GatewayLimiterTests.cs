using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TrafficGate;

namespace TrafficGate.Tests;

public sealed class GatewayLimiterTests
{
    [Fact]
    public async Task Real_token_bucket_enforces_quota_and_reports_retry_after()
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 2);
        using (var first = await Acquire(limiter, route)) Assert.True(first.IsAcquired);
        using (var second = await Acquire(limiter, route)) Assert.True(second.IsAcquired);
        using var rejected = await Acquire(limiter, route);
        Assert.False(rejected.IsAcquired);
        Assert.Equal("rate_limited", rejected.Reason);
        Assert.InRange(rejected.RetryAfter!.Value.TotalSeconds, 1, 60);
        Assert.Equal(0, limiter.Snapshot.ActiveLeaseCount);
    }

    [Fact]
    public async Task Unauthenticated_claims_and_raw_identity_headers_cannot_create_new_quota()
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 1);
        var first = Context("192.0.2.1", new ClaimsIdentity([new Claim("tenant", "attacker-one")]));
        first.Request.Headers["X-Tenant"] = "one";
        first.Request.Headers["X-Forwarded-For"] = "192.0.2.10";
        var second = Context("192.0.2.1", new ClaimsIdentity([new Claim("tenant", "attacker-two")]));
        second.Request.Headers["X-Tenant"] = "two";
        second.Request.Headers["X-Forwarded-For"] = "192.0.2.11";
        using (var accepted = await limiter.AcquireAsync(route, first, default)) Assert.True(accepted.IsAcquired);
        using var rejected = await limiter.AcquireAsync(route, second, default);
        Assert.Equal("rate_limited", rejected.Reason);
        Assert.Equal(1, limiter.Snapshot.PartitionCount);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("sub")]
    [InlineData("api_id")]
    public async Task Authenticated_identity_claims_partition_quota_independently_of_ip(string claimType)
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 1);
        var alice = Context("192.0.2.1", new ClaimsIdentity([new Claim(claimType, "alice")], "validated-jwt"));
        var aliceOtherIp = Context("192.0.2.2", new ClaimsIdentity([new Claim(claimType, "alice")], "validated-jwt"));
        var bob = Context("192.0.2.1", new ClaimsIdentity([new Claim(claimType, "bob")], "validated-jwt"));
        // An unauthenticated identity placed before the validated identity must never win.
        aliceOtherIp.User.AddIdentity(new ClaimsIdentity([new Claim("tenant", "unverified") ]));
        using (var accepted = await limiter.AcquireAsync(route, alice, default)) Assert.True(accepted.IsAcquired);
        using (var rejected = await limiter.AcquireAsync(route, aliceOtherIp, default)) Assert.Equal("rate_limited", rejected.Reason);
        using (var accepted = await limiter.AcquireAsync(route, bob, default)) Assert.True(accepted.IsAcquired);
        Assert.Equal(2, limiter.Snapshot.PartitionCount);
    }

    [Fact]
    public async Task Ipv4_mapped_addresses_share_the_same_anonymous_partition()
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 1);
        using (var first = await limiter.AcquireAsync(route, Context("192.0.2.1"), default)) Assert.True(first.IsAcquired);
        using var second = await limiter.AcquireAsync(route, Context("::ffff:192.0.2.1"), default);
        Assert.Equal("rate_limited", second.Reason);
    }

    [Fact]
    public async Task Route_partition_mode_and_cap_override_defaults()
    {
        using var limiter = new GatewayLimiter(maxPartitions: 10);
        var route = Route(rate: 1);
        route.RateLimitPartition = "user";
        route.MaxPartitions = 2;
        var alice = Context(identity: new ClaimsIdentity([new Claim("tenant", "shared"), new Claim("sub", "alice")], "validated-jwt"));
        var bob = Context(identity: new ClaimsIdentity([new Claim("tenant", "shared"), new Claim("sub", "bob")], "validated-jwt"));
        using (var first = await limiter.AcquireAsync(route, alice, default)) Assert.True(first.IsAcquired);
        using (var second = await limiter.AcquireAsync(route, bob, default)) Assert.True(second.IsAcquired);
        using var capped = await limiter.AcquireAsync(route, Context("192.0.2.2"), default);
        Assert.Equal("partition_limit", capped.Reason);
        Assert.Equal(2, limiter.Snapshot.PartitionCount);
    }

    [Theory]
    [InlineData("tenant", "tenant")]
    [InlineData("user", "sub")]
    [InlineData("api", "api_id")]
    public async Task Explicit_claim_partition_falls_back_to_ip_for_missing_verified_claim(string mode, string claimType)
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 1);
        route.RateLimitPartition = mode;
        using (var first = await Acquire(limiter, route)) Assert.True(first.IsAcquired);
        var unverified = Context(identity: new ClaimsIdentity([new Claim(claimType, "spoofed")]));
        using var rejected = await limiter.AcquireAsync(route, unverified, default);
        Assert.Equal("rate_limited", rejected.Reason);
    }

    [Fact]
    public async Task Concurrent_new_keys_cannot_exceed_the_partition_cap()
    {
        using var limiter = new GatewayLimiter(maxPartitions: 8);
        var route = Route(rate: 100, concurrency: 1000);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = Enumerable.Range(1, 256).Select(async value =>
        {
            await release.Task;
            return await limiter.AcquireAsync(route, Context($"198.51.{value / 256}.{value % 256}"), default);
        }).ToArray();
        release.SetResult();
        var leases = await Task.WhenAll(acquisitions);
        try
        {
            Assert.Equal(8, leases.Count(lease => lease.IsAcquired));
            Assert.All(leases.Where(lease => !lease.IsAcquired), lease => Assert.Equal("partition_limit", lease.Reason));
            Assert.Equal(8, limiter.Snapshot.PartitionCount);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task Queue_is_bounded_fifo_and_cancellation_immediately_releases_a_queue_slot()
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 100, concurrency: 1, queue: 2);
        using var first = await Acquire(limiter, route);
        using var cancellation = new CancellationTokenSource();
        var cancelled = limiter.AcquireAsync(route, Context(), cancellation.Token).AsTask();
        var oldest = Acquire(limiter, route).AsTask();
        Assert.Equal(2, limiter.Snapshot.QueuedRequestCount);
        using (var full = await Acquire(limiter, route)) Assert.Equal("concurrency_limit", full.Reason);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, limiter.Snapshot.QueuedRequestCount);
        var newest = Acquire(limiter, route).AsTask();
        Assert.Equal(2, limiter.Snapshot.QueuedRequestCount);
        first.Dispose();
        using var oldestLease = await oldest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldestLease.IsAcquired);
        Assert.False(newest.IsCompleted);
        oldestLease.Dispose();
        using var newestLease = await newest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(newestLease.IsAcquired);
        newestLease.Dispose();
        Assert.Equal(0, limiter.Snapshot.ActiveLeaseCount);
        Assert.Equal(0, limiter.Snapshot.QueuedRequestCount);
    }

    [Fact]
    public async Task Queue_cancellation_and_concurrency_rejection_do_not_consume_rate_quota()
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 2, concurrency: 1, queue: 1);
        using var first = await Acquire(limiter, route);
        using var cancellation = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(route, Context(), cancellation.Token).AsTask();
        using (var rejected = await Acquire(limiter, route)) Assert.Equal("concurrency_limit", rejected.Reason);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        using (var second = await Acquire(limiter, route)) Assert.True(second.IsAcquired);
        using (var exhausted = await Acquire(limiter, route)) Assert.Equal("rate_limited", exhausted.Reason);
    }

    [Fact]
    public async Task Policy_change_preserves_active_and_queued_old_leases_until_they_drain()
    {
        using var limiter = new GatewayLimiter();
        var oldRoute = Route(rate: 10, concurrency: 1, queue: 1, revision: 1);
        var newRoute = Route(rate: 20, concurrency: 2, queue: 1, revision: 2);
        using var oldActive = await Acquire(limiter, oldRoute);
        var oldQueued = Acquire(limiter, oldRoute).AsTask();
        using var newFirst = await Acquire(limiter, newRoute);
        using var newSecond = await Acquire(limiter, newRoute);
        Assert.True(newFirst.IsAcquired);
        Assert.True(newSecond.IsAcquired);
        Assert.Equal(2, limiter.Snapshot.GenerationCount);
        oldActive.Dispose();
        using var oldQueuedLease = await oldQueued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldQueuedLease.IsAcquired);
        Assert.Equal(2, limiter.Snapshot.GenerationCount);
        oldQueuedLease.Dispose();
        Assert.Equal(1, limiter.Snapshot.GenerationCount);
        Assert.Equal(1, limiter.Snapshot.PartitionCount);
        Assert.Equal(2, limiter.Snapshot.ActiveLeaseCount);
    }

    [Fact]
    public async Task Delayed_old_snapshot_cannot_retire_current_policy_or_reset_current_quota()
    {
        using var limiter = new GatewayLimiter();
        var old = Route(rate: 10, revision: 1);
        var current = Route(rate: 1, revision: 2);
        using var oldActive = await Acquire(limiter, old);
        using (var newRequest = await Acquire(limiter, current)) Assert.True(newRequest.IsAcquired);
        using (var delayed = await Acquire(limiter, old)) Assert.True(delayed.IsAcquired);
        using (var exhausted = await Acquire(limiter, current)) Assert.Equal("rate_limited", exhausted.Reason);
        oldActive.Dispose();
        Assert.Equal(1, limiter.Snapshot.GenerationCount);
    }

    [Fact]
    public async Task Unchanged_policy_in_new_revision_preserves_consumed_quota()
    {
        using var limiter = new GatewayLimiter();
        using (var first = await Acquire(limiter, Route(rate: 1, revision: 1))) Assert.True(first.IsAcquired);
        using var rejected = await Acquire(limiter, Route(rate: 1, revision: 2));
        Assert.Equal("rate_limited", rejected.Reason);
        Assert.Equal(1, limiter.Snapshot.GenerationCount);
    }

    [Fact]
    public async Task Idle_reclamation_removes_full_buckets_but_never_resets_unreplenished_quota()
    {
        var clock = new ManualClock();
        using var limiter = new GatewayLimiter(maxPartitions: 2, idleTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);
        var route = Route(rate: 1, concurrency: 1);
        using var active = await limiter.AcquireAsync(route, Context("192.0.2.1"), default);
        using (var rejected = await limiter.AcquireAsync(route, Context("192.0.2.2"), default)) Assert.Equal("concurrency_limit", rejected.Reason);
        Assert.Equal(2, limiter.Snapshot.PartitionCount);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, limiter.SweepIdle()); // Rejected concurrency left its bucket full.
        Assert.Equal(1, limiter.Snapshot.PartitionCount);
        active.Dispose();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, limiter.SweepIdle()); // The real token bucket has not replenished.
        using (var exhausted = await limiter.AcquireAsync(route, Context("192.0.2.1"), default)) Assert.Equal("rate_limited", exhausted.Reason);
        using (var newIdentity = await limiter.AcquireAsync(route, Context("192.0.2.3"), default)) Assert.True(newIdentity.IsAcquired);
        Assert.Equal(2, limiter.Snapshot.PartitionCount);
    }

    [Fact]
    public async Task Rate_zero_has_no_identity_partitions_and_still_limits_concurrency()
    {
        using var limiter = new GatewayLimiter(maxPartitions: 1);
        var route = Route(rate: 0, concurrency: 1);
        for (var value = 1; value <= 10; value++)
        {
            using var accepted = await limiter.AcquireAsync(route, Context($"192.0.2.{value}"), default);
            Assert.True(accepted.IsAcquired);
            using var rejected = await Acquire(limiter, route);
            Assert.Equal("concurrency_limit", rejected.Reason);
        }
        Assert.Equal(0, limiter.Snapshot.PartitionCount);
        Assert.Equal(1, limiter.Snapshot.GenerationCount);
    }

    [Fact]
    public async Task Retired_policy_generations_are_capped_and_reclaimed_after_release()
    {
        using var limiter = new GatewayLimiter(maxPartitions: 2);
        using var one = await Acquire(limiter, Route(rate: 0, concurrency: 1, revision: 1));
        using var two = await Acquire(limiter, Route(rate: 0, concurrency: 2, revision: 2));
        using var blocked = await Acquire(limiter, Route(rate: 0, concurrency: 3, revision: 3));
        Assert.Equal("policy_limit", blocked.Reason);
        Assert.Equal(2, limiter.Snapshot.GenerationCount);
        one.Dispose();
        using var three = await Acquire(limiter, Route(rate: 0, concurrency: 3, revision: 3));
        Assert.True(three.IsAcquired);
        two.Dispose();
        Assert.Equal(1, limiter.Snapshot.GenerationCount);
    }

    [Fact]
    public async Task Removing_route_and_disposing_limiter_preserve_inflight_release_and_cancel_queue()
    {
        using var limiter = new GatewayLimiter();
        var route = Route(rate: 10, concurrency: 1, queue: 1);
        using var active = await Acquire(limiter, route);
        var queued = Acquire(limiter, route).AsTask();
        limiter.RetainRoutes([]);
        Assert.Equal(1, limiter.Snapshot.GenerationCount);
        limiter.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, limiter.Snapshot.ActiveLeaseCount);
        active.Dispose();
        active.Dispose(); // Duplicate middleware cleanup cannot over-release the semaphore.
        Assert.Equal(0, limiter.Snapshot.GenerationCount);
        Assert.Equal(0, limiter.Snapshot.PartitionCount);
        Assert.Equal(0, limiter.Snapshot.ActiveLeaseCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Acquire(limiter, route).AsTask());
    }

    static RouteDefinition Route(int rate = 10, int concurrency = 64, int queue = 0, int revision = 0) => new()
    {
        RouteId = "route", ClusterId = "cluster", RateLimitPerMinute = rate,
        ConcurrencyLimit = concurrency, QueueLimit = queue, Revision = revision
    };
    static DefaultHttpContext Context(string ip = "192.0.2.1", ClaimsIdentity? identity = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (identity is not null) context.User = new ClaimsPrincipal(identity);
        return context;
    }
    static ValueTask<GatewayLimiter.Lease> Acquire(GatewayLimiter limiter, RouteDefinition route) => limiter.AcquireAsync(route, Context(), default);

    sealed class ManualClock : TimeProvider
    {
        long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new UnscheduledTimer();
        sealed class UnscheduledTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
