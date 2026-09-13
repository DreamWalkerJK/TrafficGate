using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace TrafficGate;

public sealed class GatewayConfigStore
{
    readonly object gate = new();
    readonly string connectionString;
    readonly List<ConfigRevision> revisions = [];
    readonly IConfigValidator? yarpValidator;
    readonly IAuthorizationPolicyProvider? authorizationPolicies;
    readonly DynamicProxyConfigProvider? provider;
    ConfigRevision? current;
    ConfigFailure? lastFailure;
    int highestRevision;
    public ConfigRevision? Current => Volatile.Read(ref current);
    public ConfigFailure? LastFailure => Volatile.Read(ref lastFailure);
    public IReadOnlyList<ConfigRevision> Revisions { get { lock (gate) return revisions.ToArray(); } }

    public GatewayConfigStore(IOptions<GatewayOptions> options, IConfiguration configuration,
        IConfigValidator? yarpValidator = null, IAuthorizationPolicyProvider? authorizationPolicies = null,
        DynamicProxyConfigProvider? provider = null)
    {
        this.yarpValidator = yarpValidator;
        this.authorizationPolicies = authorizationPolicies;
        this.provider = provider;
        var path = Path.GetFullPath(options.Value.DataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();
        Load(configuration);
    }

    SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    void Load(IConfiguration configuration)
    {
        using var db = Open();
        using (var command = db.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS revisions (revision INTEGER PRIMARY KEY, snapshot TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS audit (id INTEGER PRIMARY KEY AUTOINCREMENT, occurred TEXT NOT NULL, actor TEXT NOT NULL, revision INTEGER, action TEXT NOT NULL, result TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS config_state (name TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT value FROM config_state WHERE name = 'last_failure'";
            if (read.ExecuteScalar() is string failure)
            {
                try { lastFailure = JsonSerializer.Deserialize<ConfigFailure>(failure); }
                catch (JsonException) { lastFailure = new(DateTimeOffset.UtcNow, null, "invalid_persisted_failure"); }
            }
        }
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT revision, snapshot FROM revisions ORDER BY revision";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var revisionNumber = reader.GetInt32(0);
                highestRevision = Math.Max(highestRevision, revisionNumber);
                try
                {
                    var revision = JsonSerializer.Deserialize<ConfigRevision>(reader.GetString(1));
                    if (revision is null || revision.Revision != revisionNumber || revision.Revision < 1 ||
                        string.IsNullOrWhiteSpace(revision.ETag) || Validate(revision.Definition).Count > 0)
                        throw new JsonException("Invalid committed snapshot.");
                    revisions.Add(revision);
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
                {
                    lastFailure = new(DateTimeOffset.UtcNow, revisionNumber, "invalid_persisted_snapshot");
                }
            }
        }
        current = revisions.LastOrDefault();
        if (current is null)
        {
            var initial = configuration.GetSection("TrafficGate").Get<GatewayDefinition>() ?? new();
            if (Validate(initial).Count > 0)
            {
                initial = new();
                lastFailure = new(DateTimeOffset.UtcNow, null, "invalid_bootstrap_configuration");
            }
            var revision = NewRevision(initial);
            Persist(revision, "system", "bootstrap", lastFailure);
            revisions.Add(revision);
            highestRevision = revision.Revision;
            current = revision;
        }
        provider?.Reload(current);
        if (lastFailure is not null) PersistFailure();
    }

    ConfigRevision NewRevision(GatewayDefinition definition) => new(checked(highestRevision + 1),
        '"' + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + '"', DateTimeOffset.UtcNow, definition);

    /// <summary>Gateway and registered YARP validators run before any durable revision is added.</summary>
    public List<string> Validate(GatewayDefinition? definition)
    {
        var errors = GatewayValidation.Validate(definition);
        if (errors.Count != 0) return errors;
        foreach (var route in definition!.Routes)
        {
            if (route.AuthorizationPolicy is { } policy && authorizationPolicies is not null &&
                authorizationPolicies.GetPolicyAsync(policy).GetAwaiter().GetResult() is null)
                errors.Add($"unknown authorization policy: {route.RouteId}");
        }
        if (errors.Count != 0 || yarpValidator is null) return errors;
        var snapshot = DynamicProxyConfigProvider.Build(new ConfigRevision(1, "\"validation\"", DateTimeOffset.UtcNow, definition));
        foreach (var route in snapshot.Routes)
            if (yarpValidator.ValidateRouteAsync(route).GetAwaiter().GetResult().Count != 0)
                errors.Add($"YARP rejected route: {route.RouteId}");
        foreach (var cluster in snapshot.Clusters)
            if (yarpValidator.ValidateClusterAsync(cluster).GetAwaiter().GetResult().Count != 0)
                errors.Add($"YARP rejected cluster: {cluster.ClusterId}");
        return errors;
    }

    public object RedactedCurrent() => new { Current?.Revision, Current?.ETag, Current?.CreatedAt, Current?.Definition, LastFailure };

    public bool TryPublish(GatewayDefinition definition, string ifMatch, out int revision, out string? error) =>
        TryPublish(definition, ifMatch, "system", out revision, out error);

    public bool TryPublish(GatewayDefinition definition, string ifMatch, string actor, out int revision, out string? error)
    {
        lock (gate) return Publish(definition, ifMatch, actor, "publish", out revision, out error);
    }

    bool Publish(GatewayDefinition definition, string ifMatch, string actor, string action, out int revision, out string? error)
    {
        revision = 0;
        error = string.IsNullOrWhiteSpace(ifMatch) ? "etag_required" : ifMatch != current?.ETag ? "etag_conflict" : null;
        if (error is not null) { RecordAudit(actor, null, action, error); return false; }
        GatewayDefinition detached;
        try
        {
            var json = JsonSerializer.Serialize(definition);
            if (json.Length > 4 * 1024 * 1024) throw new JsonException("Configuration too large.");
            detached = JsonSerializer.Deserialize<GatewayDefinition>(json)!;
            if (Validate(detached).Count != 0) throw new JsonException("Configuration validation failed.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            error = "invalid_configuration";
            RecordFailure(null, error, actor, action);
            return false;
        }
        var candidate = NewRevision(detached);
        // Materialize the complete request-stable proxy snapshot before committing.
        // A crash after COMMIT and before notification recovers this same revision at startup.
        _ = DynamicProxyConfigProvider.Build(candidate);
        try { Persist(candidate, actor, action, null); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            error = "persistence_failed";
            RecordFailure(candidate.Revision, error, actor, action);
            return false;
        }
        revisions.Add(candidate);
        highestRevision = candidate.Revision;
        Volatile.Write(ref current, candidate);
        Volatile.Write(ref lastFailure, null);
        provider?.Reload(candidate);
        revision = candidate.Revision;
        return true;
    }

    public bool TryRollback(int target, string ifMatch, out int revision, out string? error) =>
        TryRollback(target, ifMatch, "system", out revision, out error);

    public bool TryRollback(int target, string ifMatch, string actor, out int revision, out string? error)
    {
        lock (gate)
        {
            var item = revisions.FirstOrDefault(r => r.Revision == target);
            if (item is null)
            {
                revision = 0; error = "revision_not_found";
                RecordAudit(actor, target, "rollback", error);
                return false;
            }
            return Publish(item.Definition, ifMatch, actor, "rollback", out revision, out error);
        }
    }

    public IReadOnlyList<ConfigDifference> Diff(GatewayDefinition definition)
    {
        var errors = Validate(definition);
        if (errors.Count != 0) throw new ArgumentException("Invalid configuration.", nameof(definition));
        var previous = Current!.Definition;
        var differences = new List<ConfigDifference>();
        Compare("route", previous.Routes, definition.Routes, r => r.RouteId);
        Compare("cluster", previous.Clusters, definition.Clusters, c => c.ClusterId);
        return differences;
        void Compare<T>(string kind, IEnumerable<T> oldValues, IEnumerable<T> newValues, Func<T, string> id)
        {
            var oldItems = oldValues.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
            var newItems = newValues.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
            foreach (var key in oldItems.Keys.Union(newItems.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!oldItems.TryGetValue(key, out var before)) differences.Add(new(kind, key, "added"));
                else if (!newItems.TryGetValue(key, out var after)) differences.Add(new(kind, key, "removed"));
                else if (JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after)) differences.Add(new(kind, key, "modified"));
            }
        }
    }

    void Persist(ConfigRevision revision, string actor, string action, ConfigFailure? failure)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO revisions (revision, snapshot) VALUES ($revision, $snapshot)";
            command.Parameters.AddWithValue("$revision", revision.Revision);
            command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(revision));
            command.ExecuteNonQuery();
        }
        InsertAudit(db, transaction, actor, revision.Revision, action, "success");
        WriteFailure(db, transaction, failure);
        transaction.Commit();
    }

    void RecordFailure(int? revision, string reason, string actor, string action)
    {
        Volatile.Write(ref lastFailure, new(DateTimeOffset.UtcNow, revision, reason));
        PersistFailure();
        RecordAudit(actor, revision, action, reason);
    }

    void PersistFailure()
    {
        try { using var db = Open(); WriteFailure(db, null, LastFailure); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { }
    }

    static void WriteFailure(SqliteConnection db, SqliteTransaction? transaction, ConfigFailure? failure)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = failure is null ? "DELETE FROM config_state WHERE name = 'last_failure'" :
            "INSERT INTO config_state (name, value) VALUES ('last_failure', $failure) ON CONFLICT(name) DO UPDATE SET value = excluded.value";
        if (failure is not null) command.Parameters.AddWithValue("$failure", JsonSerializer.Serialize(failure));
        command.ExecuteNonQuery();
    }

    public void RecordAudit(string actor, int? revision, string action, string result)
    {
        try { using var db = Open(); InsertAudit(db, null, actor, revision, action, result); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { }
    }

    static void InsertAudit(SqliteConnection db, SqliteTransaction? transaction, string actor, int? revision, string action, string result)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit (occurred, actor, revision, action, result) VALUES ($occurred, $actor, $revision, $action, $result)";
        command.Parameters.AddWithValue("$occurred", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actor", SafeAudit(actor));
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", SafeAudit(action));
        command.Parameters.AddWithValue("$result", SafeAudit(result));
        command.ExecuteNonQuery();
    }
    static string SafeAudit(string text) => new((text ?? "unknown").Where(c => !char.IsControl(c)).Take(128).ToArray());

    public IReadOnlyList<ConfigAudit> Audit(int take = 100)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id, occurred, actor, revision, action, result FROM audit ORDER BY id DESC LIMIT $take";
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 500));
        using var reader = command.ExecuteReader();
        var results = new List<ConfigAudit>();
        while (reader.Read()) results.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
        return results;
    }
}
