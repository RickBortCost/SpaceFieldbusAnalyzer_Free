using Microsoft.Data.Sqlite;
using SpaceAnalyzer.App.Infrastructure;
using Xunit;

namespace SpaceAnalyzer.Tests;

/// <summary>Registro eventi su disco limitato agli eventi più recenti.</summary>
public class EventStoreRetentionTests
{
    [Fact]
    public void KeepsOnlyTheNewestEvents()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "events.sqlite3");
        var store = new EventStore(path, capacity: 100_000, keep: 50);
        for (var i = 0; i < 1000; i++) Assert.True(store.Emit("prova", null, new() { ["i"] = (long)i }));
        store.Close();
        Assert.Equal("", store.Error);
        // Reopening prunes what the last batches left above the limit.
        var reopened = new EventStore(path, keep: 50);
        reopened.Emit("riapertura");
        reopened.Close();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(id) FROM events";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.InRange(reader.GetInt64(0), 1, 51);
        Assert.Equal(1001, reader.GetInt64(1));
    }
}
