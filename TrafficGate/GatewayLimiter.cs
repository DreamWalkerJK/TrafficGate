using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace TrafficGate;

/// <summary>
/// Process-local token buckets and FIFO route concurrency queues. Limits multiply with replicas.
/// Partition reservation and the global cap are protected by the same lock. Identity claims are
/// considered only on authenticated identities; the IP fallback uses the connection address after
/// the application's trusted forwarded-header middleware, never a raw request header.
/// </summary>
/// <remarks>
/// Each policy generation owns its concurrency limiter and identity buckets. A policy change
/// starts fresh counters for new requests; old queued and active requests retain their generation
/// until their last reference is released. Shutdown cancels queued requests and likewise defers
/// disposal of limiters used by active leases. Both partitions and generations have independent
/// hard caps, including retired generations that are still draining.
/// Idle eviction requires zero references and a completely replenished token bucket, so eviction
/// cannot grant a fresh burst before replenishment. A zero rate disables identity buckets while
/// retaining route concurrency protection. Unchanged policies preserve their counters.
/// </remarks>
public sealed class GatewayLimiter : IDisposable
{
    readonly object gate = new();
    readonly Dictionary<PolicyKey, Generation> generations = [];
    readonly Dictionary<string, CurrentPolicy> currentPolicies = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> routeEpochs = new(StringComparer.Ordinal);
    readonly int maxPartitions;
    readonly TimeSpan? idleTimeoutOverride;
    readonly TimeProvider timeProvider;
    readonly ITimer cleanupTimer;
    readonly CancellationTokenSource shutdown = new();
    int partitionCount;
    int activeLeases;
    int queuedRequests;
    long lastSweep;
    long generationSequence;
    bool disposed;
    bool shutdownSignalled;

    public GatewayLimiter(int maxPartitions = 10_000, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPartitions, 1);
        this.maxPartitions = maxPartitions;
        idleTimeoutOverride = idleTimeout;
        if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        lastSweep = this.timeProvider.GetTimestamp();
        var interval = TimeSpan.FromSeconds(Math.Clamp((idleTimeout ?? TimeSpan.FromSeconds(30)).TotalSeconds, 1, 60));
        cleanupTimer = this.timeProvider.CreateTimer(_ => SweepIdle(), null, interval, interval);
    }

    /// <summary>Returns finite aggregate diagnostics without exporting identity or IP labels.</summary>
    public LimiterSnapshot Snapshot
    {
        get { lock (gate) return new(partitionCount, generations.Count, Volatile.Read(ref activeLeases), Volatile.Read(ref queuedRequests)); }
    }

    /// <summary>
    /// Acquires one concurrency permit and then one token. Pass the immutable route snapshot's
    /// revision when available so a delayed old request cannot retire a newer policy generation.
    /// Failed concurrency and cancelled queues do not consume tokens. Dispose successful leases
    /// after forwarding completes, including streaming, cancellation, and exception paths.
    /// </summary>
    public async ValueTask<Lease> AcquireAsync(RouteDefinition route, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(route.RateLimitPerMinute);
        ArgumentOutOfRangeException.ThrowIfLessThan(route.ConcurrencyLimit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(route.QueueLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(route.MaxPartitions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(route.IdlePartitionSeconds, 1);

        var policyRevision = route.Revision;
        var key = new PolicyKey(route.RouteId, route.RateLimitPerMinute, route.ConcurrencyLimit, route.QueueLimit,
            route.RateLimitPerMinute == 0 ? "disabled" : route.RateLimitPartition,
            route.RateLimitPerMinute == 0 ? 0 : Math.Min(route.MaxPartitions, maxPartitions),
            route.RateLimitPerMinute == 0 ? TimeSpan.Zero : idleTimeoutOverride ?? TimeSpan.FromSeconds(route.IdlePartitionSeconds), 0);
        var identity = key.Rate == 0 ? null : PartitionIdentity(context, key.PartitionMode);
        Generation generation;
        Partition? partition = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (routeEpochs.TryGetValue(route.RouteId, out var epoch)) key = key with { RouteEpoch = epoch };
            // A retired generation with the same policy shape may still be draining. Give a
            // policy that returns to that shape a unique generation key instead of reviving old
            // counters or sharing its reference count with a stale snapshot.
            if (generations.TryGetValue(key, out generation!) && generation.Retired)
            {
                key = key with { GenerationId = ++generationSequence };
                generation = null!;
            }
            if (!generations.TryGetValue(key, out generation!))
            {
                if (generations.Count >= maxPartitions) SweepWhenDue();
                // Replacing an unused generation must be possible even at the cap.
                if (currentPolicies.TryGetValue(key.RouteId, out var previous) && policyRevision >= previous.Revision &&
                    generations.TryGetValue(previous.Key, out var old) && old.References == 0)
                {
                    old.Retired = true;
                    RemoveGeneration(old);
                }
                if (generations.Count >= maxPartitions) return Lease.Rejected("policy_limit");
                generation = new Generation(key, timeProvider.GetTimestamp());
                generations.Add(key, generation);
            }

            if (!currentPolicies.TryGetValue(key.RouteId, out var current) || policyRevision >= current.Revision)
            {
                if (current is not null && current.Key != key && generations.TryGetValue(current.Key, out var previous))
                {
                    previous.Retired = true;
                    if (previous.References == 0) RemoveGeneration(previous);
                }
                generation.Retired = false;
                currentPolicies[key.RouteId] = new(key, policyRevision);
            }
            else if (current.Key != key) generation.Retired = true;

            // Reserve the generation before sweeping for a partition slot so a concurrent cleanup
            // cannot dispose the concurrency limiter that this acquisition is about to use.
            generation.References++;
            if (identity is not null && !generation.Partitions.TryGetValue(identity, out partition))
            {
                if (partitionCount >= maxPartitions || generation.Partitions.Count >= key.MaxPartitions) SweepWhenDue();
                if (partitionCount >= maxPartitions || generation.Partitions.Count >= key.MaxPartitions)
                {
                    ReleaseReference(generation, null);
                    return Lease.Rejected("partition_limit");
                }
                partition = new Partition(key.Rate, timeProvider.GetTimestamp());
                generation.Partitions.Add(identity, partition);
                partitionCount++;
            }
            if (partition is not null) partition.References++;
        }

        RateLimitLease? concurrency = null;
        RateLimitLease? rate = null;
        var handedOff = false;
        var queueWait = TimeSpan.Zero;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            var acquisition = generation.Concurrency.AcquireAsync(1, cancellation.Token);
            if (!acquisition.IsCompleted)
            {
                Interlocked.Increment(ref queuedRequests);
                var started = timeProvider.GetTimestamp();
                try { concurrency = await acquisition.ConfigureAwait(false); }
                finally
                {
                    queueWait = timeProvider.GetElapsedTime(started);
                    Interlocked.Decrement(ref queuedRequests);
                }
            }
            else concurrency = await acquisition.ConfigureAwait(false);
            if (!concurrency.IsAcquired) return Lease.Rejected("concurrency_limit", queueWait: queueWait);
            cancellation.Token.ThrowIfCancellationRequested();

            if (partition is not null)
            {
                rate = partition.Bucket.AttemptAcquire(1);
                if (!rate.IsAcquired)
                {
                    TimeSpan? retryAfter = rate.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? delay : null;
                    return Lease.Rejected("rate_limited", retryAfter, queueWait);
                }
            }
            Interlocked.Increment(ref activeLeases);
            var heldConcurrency = concurrency;
            var heldRate = rate;
            var lease = new Lease(() =>
            {
                heldRate?.Dispose();
                heldConcurrency.Dispose();
                Interlocked.Decrement(ref activeLeases);
                ReleaseReference(generation, partition);
            }, null, null, queueWait);
            handedOff = true;
            return lease;
        }
        finally
        {
            if (!handedOff)
            {
                rate?.Dispose();
                concurrency?.Dispose();
                ReleaseReference(generation, partition);
            }
        }
    }

    /// <summary>
    /// Marks removed routes for retirement during configuration publication. Active requests keep
    /// their existing leases; no new configuration is read from the management database here.
    /// </summary>
    public void RetainRoutes(IEnumerable<string> routeIds)
    {
        var retained = routeIds.ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            if (disposed) return;
            foreach (var id in currentPolicies.Keys.Where(id => !retained.Contains(id)).ToArray())
            {
                routeEpochs[id] = routeEpochs.TryGetValue(id, out var epoch) ? epoch + 1 : 1;
                currentPolicies.Remove(id);
            }
            foreach (var generation in generations.Values.Where(g => !retained.Contains(g.Key.RouteId)).ToArray())
            {
                generation.Retired = true;
                if (generation.References == 0) RemoveGeneration(generation);
            }
        }
    }

    /// <summary>Reclaims fully replenished, unused partitions and empty unused route generations.</summary>
    public int SweepIdle()
    {
        lock (gate)
        {
            if (disposed) return 0;
            return SweepIdleCore();
        }
    }

    void SweepWhenDue()
    {
        // An attacker hitting a full table cannot force a complete scan on every rejected key.
        if (timeProvider.GetElapsedTime(lastSweep) >= TimeSpan.FromSeconds(1)) SweepIdleCore();
    }

    int SweepIdleCore()
    {
        var reclaimed = 0;
        lastSweep = timeProvider.GetTimestamp();
        foreach (var generation in generations.Values.ToArray())
        {
            foreach (var entry in generation.Partitions.ToArray())
            {
                var partition = entry.Value;
                if (partition.References != 0 || timeProvider.GetElapsedTime(partition.LastUsed) < generation.Key.IdleTimeout ||
                    partition.Bucket.IdleDuration is null) continue;
                generation.Partitions.Remove(entry.Key);
                partition.Bucket.Dispose();
                partitionCount--;
                reclaimed++;
            }
            if (generation.References == 0 && generation.Partitions.Count == 0 &&
                (generation.Retired || timeProvider.GetElapsedTime(generation.LastUsed) >= generation.Key.IdleTimeout)) RemoveGeneration(generation);
        }
        return reclaimed;
    }

    void ReleaseReference(Generation generation, Partition? partition)
    {
        lock (gate)
        {
            var now = timeProvider.GetTimestamp();
            if (partition is not null) { partition.References--; partition.LastUsed = now; }
            generation.References--;
            generation.LastUsed = now;
            if (generation.Retired && generation.References == 0) RemoveGeneration(generation);
        }
    }

    void RemoveGeneration(Generation generation)
    {
        generations.Remove(generation.Key);
        if (currentPolicies.TryGetValue(generation.Key.RouteId, out var current) && current.Key == generation.Key)
            currentPolicies.Remove(generation.Key.RouteId);
        foreach (var partition in generation.Partitions.Values) partition.Bucket.Dispose();
        partitionCount -= generation.Partitions.Count;
        generation.Partitions.Clear();
        generation.Concurrency.Dispose();
        if (disposed && shutdownSignalled && generations.Count == 0) shutdown.Dispose();
    }

    static string PartitionIdentity(HttpContext context, string mode)
    {
        var claimTypes = mode switch
        {
            "auto" => new[] { "tenant", "sub", "api_id" },
            "tenant" => ["tenant"],
            "user" => ["sub"],
            "api" => ["api_id"],
            "ip" => [],
            _ => throw new ArgumentException("Unknown rate-limit partition mode.", nameof(mode))
        };
        foreach (var claimType in claimTypes)
        {
            foreach (var identity in context.User.Identities)
            {
                if (!identity.IsAuthenticated) continue;
                var claim = identity.FindFirst(claimType);
                if (!string.IsNullOrWhiteSpace(claim?.Value))
                    return claimType + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claim.Value)));
            }
        }
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        return "ip:" + (address?.ToString() ?? "unknown");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            currentPolicies.Clear();
            foreach (var generation in generations.Values.ToArray())
            {
                generation.Retired = true;
                if (generation.References == 0) RemoveGeneration(generation);
            }
        }
        cleanupTimer.Dispose();
        // Cancellation invokes queue continuations, so never run it while holding the catalog lock.
        shutdown.Cancel();
        lock (gate)
        {
            shutdownSignalled = true;
            if (generations.Count == 0) shutdown.Dispose();
        }
    }

    readonly record struct PolicyKey(string RouteId, int Rate, int Concurrency, int Queue, string PartitionMode, int MaxPartitions, TimeSpan IdleTimeout, int RouteEpoch, long GenerationId = 0);
    sealed record CurrentPolicy(PolicyKey Key, int Revision);
    sealed class Generation(PolicyKey key, long now)
    {
        public PolicyKey Key { get; } = key;
        public ConcurrencyLimiter Concurrency { get; } = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = key.Concurrency,
            QueueLimit = key.Queue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        public Dictionary<string, Partition> Partitions { get; } = new(StringComparer.Ordinal);
        public long LastUsed { get; set; } = now;
        public int References { get; set; }
        public bool Retired { get; set; }
    }
    sealed class Partition(int rate, long now)
    {
        public TokenBucketRateLimiter Bucket { get; } = new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = rate,
            TokensPerPeriod = rate,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        public long LastUsed { get; set; } = now;
        public int References { get; set; }
    }

    public sealed record LimiterSnapshot(int PartitionCount, int GenerationCount, int ActiveLeaseCount, int QueuedRequestCount);

    /// <summary>An idempotent lease; only successful leases own resources.</summary>
    public sealed class Lease : IDisposable
    {
        Action? release;
        internal Lease(Action? release, string? reason, TimeSpan? retryAfter, TimeSpan queueWait)
        {
            this.release = release;
            IsAcquired = release is not null;
            Reason = reason;
            RetryAfter = retryAfter;
            QueueWait = queueWait;
        }
        internal static Lease Rejected(string reason, TimeSpan? retryAfter = null, TimeSpan queueWait = default) => new(null, reason, retryAfter, queueWait);
        public bool IsAcquired { get; }
        public string? Reason { get; }
        public TimeSpan? RetryAfter { get; }
        public TimeSpan QueueWait { get; }
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
