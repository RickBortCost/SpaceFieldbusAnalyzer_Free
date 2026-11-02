using SpaceAnalyzer.App.Infrastructure;
using Xunit;

namespace SpaceAnalyzer.Tests;

public class MemoryGuardTests
{
    const long Mb = 1L << 20, Gb = 1L << 30;

    [Theory]
    [InlineData(8 * Gb, 4 * Gb, "ok")]
    [InlineData(8 * Gb, 1 * Gb, "low")]         // under 15 %
    [InlineData(8 * Gb, 1400 * Mb, "low")]      // over 15 % but under 1.5 GB
    [InlineData(8 * Gb, 500 * Mb, "critical")]  // under 8 % and 700 MB
    [InlineData(64 * Gb, 650 * Mb, "critical")] // under 700 MB
    [InlineData(0, 0, "ok")]                    // unknown
    public void ClassifiesAvailableMemory(long total, long available, string level) => Assert.Equal(level, MemoryGuard.Classify(total, available));

    [Fact]
    public void ReportsThePc()
    {
        MemoryGuard.Start();
        try
        {
            var report = MemoryGuard.Report();
            Assert.True((long)report["total_mb"]! > 0);
            Assert.True((long)report["process_mb"]! > 0);
            Assert.Contains((string)report["level"]!, new[] { "ok", "low", "critical" });
            Assert.Throws<UserError>(() => MemoryGuard.Require(1L << 50, "una prova"));
        }
        finally { MemoryGuard.Stop(); }
    }
}
