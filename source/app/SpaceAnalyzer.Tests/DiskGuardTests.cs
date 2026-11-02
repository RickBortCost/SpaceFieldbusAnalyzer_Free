using SpaceAnalyzer.App.Infrastructure;
using SpaceAnalyzer.App.Live;
using SpaceAnalyzer.App.Recordings;
using Xunit;

namespace SpaceAnalyzer.Tests;

/// <summary>Spazio su disco: soglie, rifiuto delle operazioni senza riserva, storico Live sospeso, archivi primari eliminabili.</summary>
public class DiskGuardTests
{
    const long Mb = 1L << 20, Gb = 1L << 30;

    [Theory]
    [InlineData(500 * Gb, 100 * Gb, "ok")]
    [InlineData(4000 * Gb, 20 * Gb, "ok")]       // large disk: absolute thresholds, not percentages
    [InlineData(64 * Gb, 4 * Gb, "low")]         // under 5 GB
    [InlineData(500 * Gb, 900 * Mb, "critical")] // under 1 GB
    [InlineData(0, 0, "ok")]                     // unknown
    public void ClassifiesFreeSpace(long total, long free, string level) => Assert.Equal(level, DiskGuard.Classify(total, free));

    [Fact]
    public void MeasuresAndRefusesWhatDoesNotFit()
    {
        using var dir = new TempDir();
        var (total, free) = DiskGuard.Measure(Path.Combine(dir.Path, "non", "ancora", "creata"));
        Assert.True(total > 0 && free >= 0);
        var error = Assert.Throws<UserError>(() => DiskGuard.Require(dir.Path, total + Gb, "una prova"));
        Assert.Contains("Spazio su disco insufficiente per una prova", error.Message);
        DiskGuard.Watch("test", () => dir.Path);
        var report = (Dictionary<string, object?>)DiskGuard.Report()["test"]!;
        Assert.Equal(dir.Path, report["folder"]);
        Assert.True((long)report["total_mb"]! > 0);
    }

    [Fact]
    public void LiveHistoryStopsInsteadOfFillingTheDisk()
    {
        using var dir = new TempDir();
        using var archive = new LiveArchive(dir.Path, spareDisk: () => 0);
        var generation = archive.NewSession();
        for (var i = 0; i < 100; i++) archive.Append(generation, "n", new LivePoint(1_700_000_000_000d + i, i, "Good"));
        for (var wait = 0; wait < 100 && archive.Error.Length == 0; wait++) Thread.Sleep(50);
        Assert.Contains("spazio su disco quasi esaurito", archive.Error);
        Assert.Equal(0, archive.Append(generation, "n", new LivePoint(1_700_000_001_000d, 1, "Good")));
    }

    [Fact]
    public void LiveHistoryWithRoomKeepsWorking()
    {
        using var dir = new TempDir();
        using var archive = new LiveArchive(dir.Path, spareDisk: () => 10 * Gb);
        var generation = archive.NewSession();
        for (var i = 0; i < 100; i++) Assert.True(archive.Append(generation, "n", new LivePoint(1_700_000_000_000d + i, i, "Good")) > 0);
        archive.Before("n", long.MaxValue, 1, out _);
        Assert.Equal("", archive.Error);
    }

    [Fact]
    public void PrimaryCapturesCanBeDeletedExceptTheOneBeingWritten()
    {
        using var dir = new TempDir();
        var conversions = new Conversions(() => Path.Combine(dir.Path, Conversions.Folder));
        var primary = Path.Combine(dir.Path, ".primary");
        Directory.CreateDirectory(primary);
        File.WriteAllBytes(Path.Combine(primary, "vecchia.sac"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(primary, "in_corso.sac.part"), new byte[16]);

        var result = conversions.DeletePrimary("vecchia.sac");
        Assert.Equal(true, result["deleted"]);
        Assert.Equal(4096L, result["freed_bytes"]);
        Assert.False(File.Exists(Path.Combine(primary, "vecchia.sac")));

        conversions.RecordingBusy = () => true;
        Assert.Throws<UserError>(() => conversions.DeletePrimary("in_corso.sac.part"));
        Assert.True(File.Exists(Path.Combine(primary, "in_corso.sac.part")));
        conversions.RecordingBusy = () => false;
        conversions.DeletePrimary("in_corso.sac.part");
        Assert.False(File.Exists(Path.Combine(primary, "in_corso.sac.part")));

        Assert.Throws<UserError>(() => conversions.DeletePrimary("../config.json"));
        Assert.Throws<UserError>(() => conversions.DeletePrimary("mancante.sac"));
    }
}
