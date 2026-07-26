using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The climb stops once a passing candidate is within the headroom margin of a
/// thermal limit. It judged the PEAK of every bound sensor against the core
/// ceiling, and the bound set includes memory junction — which on the reference
/// 3090 sits near the 83 °C core ceiling under load purely because GDDR6X runs
/// hot. The search therefore halted after the very first candidate and Auto OC
/// reported a 0 MHz "result" from a completed run. Same max-of-all-sensors-
/// versus-one-ceiling error that <see cref="GpuThermalCeilings"/> fixes for the
/// ceiling itself, one call site away.
/// </summary>
public sealed class ThermalHeadroomStopTests
{
    private const double Ceiling = 83;
    private const double Headroom = 4;

    [Fact]
    public void AHotMemoryJunctionNoLongerStopsTheClimb()
    {
        // The live case: memory junction at 90 °C against its own 95 °C ceiling is
        // 5 °C of margin — outside the 4 °C headroom, so the climb continues. The
        // old peak-versus-core-ceiling test saw 90 >= 79 and stopped.
        TuneScreeningResult screening = Screening(peak: 90, smallestMargin: 5);

        Assert.False(GpuAutoOcSearch.ReachedThermalMargin(screening, Ceiling, Headroom));
        // The reading that used to stop it.
        Assert.True(GpuAutoOcSearch.ReachedThermalHeadroom(90d, Ceiling, Headroom));
    }

    [Fact]
    public void AGenuinelyCloseSensorStillStopsTheClimb()
    {
        // The protective direction must survive: 2 °C of margin is inside the 4 °C
        // headroom, so climbing further would trade away real thermal headroom.
        Assert.True(GpuAutoOcSearch.ReachedThermalMargin(Screening(peak: 81, smallestMargin: 2), Ceiling, Headroom));
    }

    [Fact]
    public void MarginExactlyAtTheHeadroomStops()
    {
        Assert.True(GpuAutoOcSearch.ReachedThermalMargin(Screening(peak: 79, smallestMargin: Headroom), Ceiling, Headroom));
    }

    [Fact]
    public void AZeroHeadroomRequestNeverStopsEarly()
    {
        // 0 means "climb to the stability edge"; a margin must not end the search.
        Assert.False(GpuAutoOcSearch.ReachedThermalMargin(Screening(peak: 82, smallestMargin: 0.5), Ceiling, headroomCelsius: 0));
    }

    [Fact]
    public void WithoutPerSensorDataTheOldPeakRuleStillApplies()
    {
        // Callers that supply no margin (cooling calibration) must keep working.
        TuneScreeningResult screening = Screening(peak: 80, smallestMargin: null);

        Assert.True(GpuAutoOcSearch.ReachedThermalMargin(screening, Ceiling, Headroom));
        Assert.False(GpuAutoOcSearch.ReachedThermalMargin(Screening(peak: 60, smallestMargin: null), Ceiling, Headroom));
    }

    private static TuneScreeningResult Screening(double peak, double? smallestMargin) => new(
        true,
        "ok",
        peak,
        AveragePowerWatts: 300,
        AverageClockMegahertz: 1900,
        ThroughputScore: 100,
        AverageFanRpm: 1500,
        SmallestThermalMarginCelsius: smallestMargin);
}
