using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing.Patterns;

namespace TrafficGate;

public static partial class GatewayValidation
{
    static readonly HashSet<string> BalancingPolicies = new(StringComparer.OrdinalIgnoreCase)
        { "RoundRobin", "PowerOfTwoChoices", "LeastRequests", "Random", "FirstAlphabetical" };
    static readonly HashSet<string> Partitions = new(StringComparer.Ordinal)
        { "auto", "user", "tenant", "ip", "api" };
    static readonly HashSet<string> ProtectedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "Forwarded", "Connection", "Upgrade", "Content-Length", "Transfer-Encoding", "TE", "Trailer", "Keep-Alive", "Proxy-Connection", "X-Original-For", "X-Original-Host", "X-Original-Proto" };

    public static List<string> Validate(GatewayDefinition? definition)
    {
        var errors = new List<string>();
        if (definition?.Routes is null || definition.Clusters is null)
            return ["routes and clusters must be arrays"];
        if (definition.Routes.Count > 256 || definition.Clusters.Count > 128)
            errors.Add("configuration exceeds the route or cluster count limit");
        var clusterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in definition.Clusters)
        {
            if (cluster is null) { errors.Add("cluster must not be null"); continue; }
            if (!ValidId(cluster.ClusterId)) errors.Add("invalid cluster id");
            if (!clusterIds.Add(cluster.ClusterId ?? string.Empty)) errors.Add($"duplicate cluster: {SafeId(cluster.ClusterId)}");
            if (!BalancingPolicies.Contains(cluster.LoadBalancingPolicy ?? string.Empty)) errors.Add($"invalid load balancing policy: {SafeId(cluster.ClusterId)}");
            if (cluster.Destinations is null || cluster.Destinations.Count is < 1 or > 64)
                errors.Add($"cluster needs between 1 and 64 destinations: {SafeId(cluster.ClusterId)}");
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var destination in cluster.Destinations ?? [])
            {
                if (destination is null) { errors.Add("destination must not be null"); continue; }
                if (!ValidId(destination.DestinationId) || !destinations.Add(destination.DestinationId ?? string.Empty))
                    errors.Add($"invalid or duplicate destination id in cluster: {SafeId(cluster.ClusterId)}");
                if (!ValidDestination(destination.Address))
                    errors.Add($"invalid destination in cluster: {SafeId(cluster.ClusterId)}");
            }
            if (cluster.ConnectTimeoutSeconds is < 1 or > 300 || cluster.ActivityTimeoutSeconds is < 1 or > 86400 ||
                cluster.MaxConnectionsPerServer is < 1 or > 10000)
                errors.Add($"invalid connection limits: {SafeId(cluster.ClusterId)}");
            if (cluster.HttpVersion is not ("1.1" or "2.0") ||
                cluster.HttpVersionPolicy is not ("RequestVersionExact" or "RequestVersionOrLower" or "RequestVersionOrHigher"))
                errors.Add($"invalid HTTP version policy: {SafeId(cluster.ClusterId)}");
            if (!ValidLiteralPath(cluster.HealthPath) || cluster.HealthIntervalSeconds is < 1 or > 3600 ||
                cluster.HealthTimeoutSeconds is < 1 or > 300 || cluster.HealthTimeoutSeconds > cluster.HealthIntervalSeconds ||
                cluster.HealthFailureThreshold is < 1 or > 100 || cluster.PassiveReactivationSeconds is < 1 or > 3600)
                errors.Add($"invalid health check settings: {SafeId(cluster.ClusterId)}");
        }
        var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validPatterns = new List<(RouteDefinition Route, RoutePattern Pattern)>();
        foreach (var route in definition.Routes)
        {
            if (route is null) { errors.Add("route must not be null"); continue; }
            if (!ValidId(route.RouteId)) errors.Add("invalid route id");
            if (!routeIds.Add(route.RouteId ?? string.Empty)) errors.Add($"duplicate route: {SafeId(route.RouteId)}");
            if (!clusterIds.Contains(route.ClusterId ?? string.Empty)) errors.Add($"missing cluster: {SafeId(route.ClusterId)}");
            if (route.Priority is < -10000 or > 10000 || route.RateLimitPerMinute is < 0 or > 1000000 ||
                route.ConcurrencyLimit is < 1 or > 10000 || route.QueueLimit is < 0 or > 10000 ||
                route.MaxPartitions is < 1 or > 10000 || route.IdlePartitionSeconds is < 1 or > 86400 ||
                route.TimeoutSeconds is < 0 or > 86400 || route.MaxRequestBodyBytes is < 1 or > 1073741824 ||
                route.MaxRequestHeaderBytes is < 1024 or > 65536 || route.MaxRequestLineBytes is < 256 or > 16384 ||
                !Partitions.Contains(route.RateLimitPartition ?? string.Empty))
                errors.Add($"invalid limits: {SafeId(route.RouteId)}");
            if (route.AllowAnonymous && route.AuthorizationPolicy is not null)
                errors.Add($"anonymous route cannot reference an authorization policy: {SafeId(route.RouteId)}");
            if (route.AuthorizationPolicy is not null && (!ValidId(route.AuthorizationPolicy) ||
                string.Equals(route.AuthorizationPolicy, "anonymous", StringComparison.OrdinalIgnoreCase)))
                errors.Add($"invalid authorization reference: {SafeId(route.RouteId)}");
            if (route.Hosts is null || route.Hosts.Length > 16 || route.Hosts.Any(h => !ValidHostPattern(h)))
                errors.Add($"invalid host match: {SafeId(route.RouteId)}");
            if (route.Methods is null || route.Methods.Length > 16 || route.Methods.Any(m => m is null || m.Length > 20 || !Token().IsMatch(m)))
                errors.Add($"invalid HTTP method: {SafeId(route.RouteId)}");
            if (string.IsNullOrEmpty(route.MatchPath) || route.MatchPath.Length > 2048 || !route.MatchPath.StartsWith('/') ||
                route.MatchPath.StartsWith("//", StringComparison.Ordinal) || route.MatchPath.Any(char.IsControl) || route.MatchPath.Contains('\\') || route.MatchPath.Contains('#'))
                errors.Add($"invalid route path: {SafeId(route.RouteId)}");
            else
            {
                try { validPatterns.Add((route, RoutePatternFactory.Parse(route.MatchPath))); }
                catch (RoutePatternException) { errors.Add($"invalid route pattern: {SafeId(route.RouteId)}"); }
            }
            if (route.PathRemovePrefix is not null && !ValidLiteralPath(route.PathRemovePrefix) ||
                route.PathPrefix is not null && !ValidLiteralPath(route.PathPrefix))
                errors.Add($"invalid path transform: {SafeId(route.RouteId)}");
            if (route.RequestHeaders is null || route.RemoveRequestHeaders is null ||
                (route.RequestHeaders?.Count ?? 0) + (route.RemoveRequestHeaders?.Length ?? 0) > 16)
                errors.Add($"invalid header transform count: {SafeId(route.RouteId)}");
            foreach (var header in route.RequestHeaders ?? [])
                if (!ValidTransformHeader(header.Key) || header.Value is null || header.Value.Length > 512 || header.Value.Any(char.IsControl))
                    errors.Add($"invalid request header transform: {SafeId(route.RouteId)}");
            foreach (var header in route.RemoveRequestHeaders ?? [])
                if (!ValidTransformHeader(header)) errors.Add($"invalid request header removal: {SafeId(route.RouteId)}");
        }
        for (var first = 0; first < validPatterns.Count; first++)
        for (var second = first + 1; second < validPatterns.Count; second++)
        {
            var (a, ap) = validPatterns[first]; var (b, bp) = validPatterns[second];
            // ASP.NET chooses order then path precedence. Only equal-precedence
            // overlapping routes can be ambiguous; different explicit priorities resolve it.
            if (a.Priority == b.Priority && ap.InboundPrecedence == bp.InboundPrecedence &&
                MethodsOverlap(a.Methods, b.Methods) && HostsOverlap(a.Hosts, b.Hosts) && PathsOverlap(ap, bp))
                errors.Add($"route conflict: {SafeId(a.RouteId)} and {SafeId(b.RouteId)}; use distinct match criteria or priorities");
        }
        return errors;
    }

    static bool PathsOverlap(RoutePattern a, RoutePattern b)
    {
        var count = Math.Min(a.PathSegments.Count, b.PathSegments.Count);
        for (var i = 0; i < count; i++)
        {
            var x = a.PathSegments[i]; var y = b.PathSegments[i];
            if (x.Parts.Count == 1 && y.Parts.Count == 1 && x.Parts[0] is RoutePatternLiteralPart xl && y.Parts[0] is RoutePatternLiteralPart yl &&
                !string.Equals(xl.Content, yl.Content, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    static bool MethodsOverlap(string[]? a, string[]? b) => a is null || b is null || a.Length == 0 || b.Length == 0 || a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();
    static bool HostsOverlap(string[]? a, string[]? b) => a is null || b is null || a.Length == 0 || b.Length == 0 ||
        a.Any(x => b.Any(y => HostOverlap(x, y)));
    static bool HostOverlap(string? a, string? b)
    {
        if (a is null || b is null || a == "*" || b == "*") return true;
        var (ah, ap) = SplitHost(a); var (bh, bp) = SplitHost(b);
        if (ap is not null && bp is not null && ap != bp) return false;
        if (string.Equals(ah, bh, StringComparison.OrdinalIgnoreCase)) return true;
        if (ah.StartsWith("*.", StringComparison.Ordinal) && bh.EndsWith(ah[1..], StringComparison.OrdinalIgnoreCase)) return true;
        return bh.StartsWith("*.", StringComparison.Ordinal) && ah.EndsWith(bh[1..], StringComparison.OrdinalIgnoreCase);
    }
    static (string Host, string? Port) SplitHost(string value)
    {
        var index = value.LastIndexOf(':');
        return index > 0 && !value.EndsWith(']') ? (value[..index], value[(index + 1)..]) : (value, null);
    }
    static bool ValidHostPattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || value.Any(char.IsWhiteSpace) || value.Contains('@') || value.Contains('/') || value.Contains('\\')) return false;
        if (value == "*") return true;
        var (host, port) = SplitHost(value);
        if (port is not null && (!int.TryParse(port, out var number) || number is < 1 or > 65535)) return false;
        if (host.StartsWith("*.", StringComparison.Ordinal)) host = host[2..];
        return Uri.CheckHostName(host.Trim('[', ']')) is not UriHostNameType.Unknown;
    }
    static bool ValidTransformHeader(string? name) => name is { Length: > 0 and <= 64 } && Token().IsMatch(name) &&
        !ProtectedHeaders.Contains(name) && !name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("X-Authenticated-", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase);
    static bool ValidId(string? value) => value is { Length: > 0 and <= 64 } && Identifier().IsMatch(value);
    static string SafeId(string? value) => ValidId(value) ? value! : "[invalid]";
    static bool ValidLiteralPath(string? value)
    {
        if (value is not { Length: > 0 and <= 2048 } || !value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)) return false;
        var decoded = Uri.UnescapeDataString(value);
        return !decoded.Any(char.IsControl) && !decoded.ContainsAny('?', '#', '{', '}', '\\') &&
            !decoded.Split('/').Any(p => p is "." or "..");
    }
    static bool ContainsAny(this string value, params char[] characters) => value.IndexOfAny(characters) >= 0;

    public static bool ValidDestination(string? address)
    {
        if (address is null || address.Length > 2048 || address.Any(char.IsControl) || address.Contains('\\') ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is < 1 or > 65535 || IsDangerousHost(uri.IdnHost)) return false;
        // Dot segments are normalized by Uri; rejecting them in the original input
        // avoids concealing a target path boundary change from the configuration review.
        var authorityEnd = address.IndexOf('/', address.IndexOf("://", StringComparison.Ordinal) + 3);
        return authorityEnd < 0 || ValidLiteralPath(address[authorityEnd..]);
    }
    public static bool IsDangerousHost(string host)
    {
        var normalized = host.Trim('[', ']').TrimEnd('.');
        if (normalized.Contains('%') || normalized.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("metadata.azure.internal", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("instance-data.ec2.internal", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(normalized, out var address) && IsDangerousAddress(address);
    }
    public static bool IsDangerousAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254 ||
                address.Equals(IPAddress.Parse("100.100.100.200")) || address.Equals(IPAddress.Parse("168.63.129.16"));
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;
        return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            address.Equals(IPAddress.Parse("fd00:ec2::254"));
    }
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
