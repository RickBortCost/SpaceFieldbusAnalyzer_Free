using System.Runtime;
using SpaceAnalyzer.Engine.Core;
using SpaceAnalyzer.Engine.Drivers.Ads;
using Xunit;

public class AcquisitionTuningTests
{
    [Theory]
    [InlineData(0, 1, 50)]     // automatic: 50 ms at 1 ms
    [InlineData(0, 10, 50)]
    [InlineData(0, 100, 100)]  // never below one period
    [InlineData(20, 1, 20)]    // configured
    [InlineData(5, 10, 10)]
    [InlineData(1000, 1, 1000)]
    public void Notification_max_delay_batches_at_least_one_period(int batch, int period, int expected)
    {
        Assert.Equal(expected, AdsNotifyDriver.MaxDelayMs(batch, period));
    }

    [Fact]
    public void Acquisition_keeps_the_default_gc_mode()
    {
        var before = GCSettings.LatencyMode;
        SystemTuning.SetAcquiring(true);
        try
        {
            // SustainedLowLatency postponed full collections and let the memory of long acquisitions grow.
            Assert.Equal(before, GCSettings.LatencyMode);
            Assert.NotEqual(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
            SystemTuning.SetAcquiring(true); // idempotent
            var report = SystemTuning.Report();
            Assert.True(report.ContainsKey("process_priority") && report.ContainsKey("receive_thread") && report.ContainsKey("power_plan"));
        }
        finally { SystemTuning.SetAcquiring(false); }
        Assert.Equal(before, GCSettings.LatencyMode);
    }
}
