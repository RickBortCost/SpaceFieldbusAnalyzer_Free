using SpaceAnalyzer.App.Web;
using Xunit;

namespace SpaceAnalyzer.Tests;

/// <summary>Copie dei file aperti dal PC: rimosse dopo 30 giorni senza uso o oltre il limite di spazio, a partire dalle più vecchie.</summary>
public class ImportRetentionTests
{
    static string Put(string dir, string name, int bytes, DateTime used)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.WriteAllText(Path.ChangeExtension(path, ".metadata.json"), "{}");
        File.SetLastWriteTimeUtc(path, used);
        File.SetLastAccessTimeUtc(path, used);
        return path;
    }

    [Fact]
    public void RemovesOldCopiesAndKeepsRecentOnes()
    {
        using var dir = new TempDir();
        var now = DateTime.UtcNow;
        var old = Put(dir.Path, "vecchio.parquet", 1000, now.AddDays(-40));
        var recent = Put(dir.Path, "recente.parquet", 1000, now.AddDays(-1));
        var part = Put(dir.Path, "parte.dat", 100, now.AddDays(-40));
        File.WriteAllText(Path.Combine(dir.Path, "seq.pda3seq"), "{\"parts\":[\"parte.dat\"]}");

        var freed = Import.Prune(dir.Path, TimeSpan.FromDays(30), long.MaxValue);

        Assert.True(freed >= 1100);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(Path.ChangeExtension(old, ".metadata.json")));
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(Path.Combine(dir.Path, "seq.pda3seq")));   // a sequence without its parts is removed
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(Path.ChangeExtension(recent, ".metadata.json")));
    }

    [Fact]
    public void KeepsTheFolderUnderTheSizeLimit()
    {
        using var dir = new TempDir();
        var now = DateTime.UtcNow;
        var first = Put(dir.Path, "a.csv", 4000, now.AddHours(-3));
        var second = Put(dir.Path, "b.csv", 4000, now.AddHours(-2));
        var third = Put(dir.Path, "c.csv", 4000, now.AddHours(-1));
        Import.Prune(dir.Path, TimeSpan.FromDays(30), 9000);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second) && File.Exists(third));
    }
}
