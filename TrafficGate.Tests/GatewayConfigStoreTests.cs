using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TrafficGate;

namespace TrafficGate.Tests;

public class GatewayConfigStoreTests
{
    [Fact]
    public void Corrupt_snapshot_is_skipped_and_next_publish_uses_new_revision()
    {
        var path = Path.Combine(Path.GetTempPath(), "trafficgate-config-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var options = Options.Create(new GatewayOptions { DataPath = path });
            var first = new GatewayConfigStore(options, config);
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO revisions (revision, snapshot) VALUES (2, 'broken json')";
                command.ExecuteNonQuery();
            }
            var recovered = new GatewayConfigStore(options, config);
            Assert.Equal(1, recovered.Current!.Revision);
            Assert.Equal("invalid_persisted_snapshot", recovered.LastFailure!.Reason);
            Assert.True(recovered.TryPublish(new GatewayDefinition(), recovered.Current.ETag, out var revision, out _));
            Assert.Equal(3, revision);
            Assert.Equal(3, new GatewayConfigStore(options, config).Current!.Revision);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Persistence_failure_keeps_current_revision_and_records_failure()
    {
        var path = Path.Combine(Path.GetTempPath(), "trafficgate-config-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var config = new ConfigurationBuilder().Build();
            var store = new GatewayConfigStore(Options.Create(new GatewayOptions { DataPath = path }), config);
            var current = store.Current;
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "DROP TABLE revisions";
                command.ExecuteNonQuery();
            }
            Assert.False(store.TryPublish(new GatewayDefinition(), current!.ETag, out _, out var error));
            Assert.Same(current, store.Current);
            Assert.Single(store.Revisions);
            Assert.Equal("persistence_failed", error);
            Assert.Equal("persistence_failed", store.LastFailure!.Reason);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
