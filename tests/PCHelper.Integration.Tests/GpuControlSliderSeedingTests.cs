using PCHelper.App;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// A GPU control slider is seeded from the live driver reading so it opens showing what the
/// card is actually doing rather than a stale zero. That seed has to be in the capability's
/// OWN unit.
///
/// <para>It was not. NVML reports the power limit in milliwatts while the capability, its
/// bounds, and the slider are in watts, so the slider opened at 385000 on a control bounded
/// 100-385. The next apply was refused with <c>PROFILE_REJECTED: Action 'gpu-slider-...'
/// value must be within 100-385 W</c> - the slider was handing the transaction a value its
/// own bounds forbid, and nothing in the UI showed why. The bounds check did its job; the
/// seed was the defect.</para>
/// </summary>
public sealed class GpuControlSliderSeedingTests
{
    private const uint ReferenceCardLimitMilliwatts = 385_000;
    private const double ReferenceCardLimitWatts = 385d;

    private static GpuOcLiveStateV1 Live(
        int? core = 0,
        int? memory = 0,
        uint? powerMilliwatts = ReferenceCardLimitMilliwatts,
        bool available = true) =>
        new(available, core, memory, powerMilliwatts, "Read back from the display driver.");

    [Fact]
    public void ThePowerSliderIsSeededInWattsNotMilliwatts()
    {
        double? seeded = MainViewModel.LiveGpuOcValue(Live(), "gpupower.limit:");

        Assert.Equal(ReferenceCardLimitWatts, seeded);
    }

    /// <summary>
    /// The property that actually broke: the seed must land inside the bounds the same
    /// capability publishes, or the transaction refuses the slider's own starting value.
    /// </summary>
    [Theory]
    [InlineData(100_000u, 100d)]
    [InlineData(250_000u, 250d)]
    [InlineData(385_000u, 385d)]
    public void ASeededPowerValueIsAlwaysInsideTheReportedBounds(uint milliwatts, double expectedWatts)
    {
        const double minimumWatts = 100d;
        const double maximumWatts = 385d;

        double seeded = MainViewModel.LiveGpuOcValue(Live(powerMilliwatts: milliwatts), "gpupower.limit:")
            ?? throw new InvalidOperationException("The power slider was not seeded at all.");

        Assert.Equal(expectedWatts, seeded);
        Assert.InRange(seeded, minimumWatts, maximumWatts);
    }

    /// <summary>
    /// Clock offsets are megahertz on both sides, so they must NOT be scaled. A blanket
    /// conversion would have traded one unit bug for another.
    /// </summary>
    [Fact]
    public void ClockOffsetsAreSeededUnscaled()
    {
        GpuOcLiveStateV1 live = Live(core: 20, memory: 50);

        Assert.Equal(20d, MainViewModel.LiveGpuOcValue(live, "gpuclock.core:"));
        Assert.Equal(50d, MainViewModel.LiveGpuOcValue(live, "gpuclock.memory:"));
    }

    [Fact]
    public void AnUnavailableReadingSeedsNothing()
    {
        Assert.Null(MainViewModel.LiveGpuOcValue(Live(available: false), "gpupower.limit:"));
        Assert.Null(MainViewModel.LiveGpuOcValue(null, "gpupower.limit:"));
    }

    [Fact]
    public void AMissingPowerReadingSeedsNothingRatherThanZeroWatts()
    {
        // Zero would be a plausible-looking value outside the reported minimum, which is the
        // same failure in the opposite direction.
        Assert.Null(MainViewModel.LiveGpuOcValue(Live(powerMilliwatts: null), "gpupower.limit:"));
    }

    [Fact]
    public void AnUnknownCapabilityPrefixSeedsNothing() =>
        Assert.Null(MainViewModel.LiveGpuOcValue(Live(), "gpufan.duty:"));
}
