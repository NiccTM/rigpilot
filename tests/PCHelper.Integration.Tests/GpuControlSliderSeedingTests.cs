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

/// <summary>
/// The Performance page opened showing 0 MHz on both clock sliders while the driver was
/// enforcing +20/+50, beside a ticked "reapply at startup" box.
///
/// <para>The slider collection is rebuilt only when the capability SET changes, so the single
/// rebuild happens the first time capabilities arrive - before any live read has landed. Every
/// slider therefore took its fallback: 0 for a clock offset, and the range maximum for power.
/// On the reference machine that power fallback is 385 W, which is ALSO the applied limit, so
/// the page looked half-correct and hid the defect. Nothing rebuilt afterwards because the
/// capability set never changes again.</para>
///
/// <para>These pin the reseed that fixes it. They fail before it exists, because without a
/// reseed the sliders keep whatever the fallback gave them.</para>
/// </summary>
public sealed class GpuControlSliderReseedTests
{
    private static MainViewModel.GpuControlSlider Slider(string capabilityId, double min, double max, double seeded) =>
        new()
        {
            CapabilityId = capabilityId,
            Name = capabilityId,
            Minimum = min,
            Maximum = max,
            Value = seeded
        };

    private static GpuOcLiveStateV1 Live(int? core, int? memory, uint? powerMilliwatts) =>
        new(true, core, memory, powerMilliwatts, "Read back from the display driver.");

    /// <summary>The exact live state and the exact fallback values observed on the machine.</summary>
    [Fact]
    public void FallbackSeededSlidersAreCorrectedFromLiveState()
    {
        MainViewModel.GpuControlSlider core = Slider("gpuclock.core:0", -1000, 1000, 0);
        MainViewModel.GpuControlSlider memory = Slider("gpuclock.memory:0", -1000, 3000, 0);
        MainViewModel.GpuControlSlider power = Slider("gpupower.limit:0", 100, 385, 385);

        bool changed = MainViewModel.ApplyLiveStateToSliders(
            [core, memory, power], Live(20, 50, 385_000));

        Assert.True(changed);
        Assert.Equal(20, core.Value);
        Assert.Equal(50, memory.Value);
        Assert.Equal(385, power.Value);
    }

    /// <summary>A negative offset must keep its sign rather than clamping to stock.</summary>
    [Fact]
    public void NegativeClockOffsetsArePreserved()
    {
        MainViewModel.GpuControlSlider core = Slider("gpuclock.core:0", -1000, 1000, 0);

        MainViewModel.ApplyLiveStateToSliders([core], Live(-75, 0, 385_000));

        Assert.Equal(-75, core.Value);
    }

    /// <summary>Zero is a real offset meaning stock; it must survive as a measured value.</summary>
    [Fact]
    public void AGenuineZeroOffsetIsKept()
    {
        MainViewModel.GpuControlSlider core = Slider("gpuclock.core:0", -1000, 1000, 120);

        MainViewModel.ApplyLiveStateToSliders([core], Live(0, 0, 385_000));

        Assert.Equal(0, core.Value);
    }

    /// <summary>
    /// An unavailable read must never be reinterpreted as a measured zero - that is the exact
    /// confusion that made the original defect invisible.
    /// </summary>
    [Fact]
    public void AnUnavailableReadingLeavesSlidersUntouched()
    {
        MainViewModel.GpuControlSlider core = Slider("gpuclock.core:0", -1000, 1000, 20);

        bool changed = MainViewModel.ApplyLiveStateToSliders(
            [core], new GpuOcLiveStateV1(false, null, null, null, "unavailable"));

        Assert.False(changed);
        Assert.Equal(20, core.Value);
        Assert.False(MainViewModel.ApplyLiveStateToSliders([core], null));
        Assert.Equal(20, core.Value);
    }

    /// <summary>A domain the driver could not report must not zero its slider.</summary>
    [Fact]
    public void AMissingDomainLeavesThatSliderAlone()
    {
        MainViewModel.GpuControlSlider core = Slider("gpuclock.core:0", -1000, 1000, 20);
        MainViewModel.GpuControlSlider memory = Slider("gpuclock.memory:0", -1000, 3000, 50);

        MainViewModel.ApplyLiveStateToSliders([core, memory], Live(20, null, 385_000));

        Assert.Equal(20, core.Value);
        Assert.Equal(50, memory.Value);
    }

    /// <summary>Reseeding an already-correct set reports no change, so bindings stay quiet.</summary>
    [Fact]
    public void ReseedingAnAlreadyCorrectSetChangesNothing()
    {
        MainViewModel.GpuControlSlider core = Slider("gpuclock.core:0", -1000, 1000, 20);
        MainViewModel.GpuControlSlider power = Slider("gpupower.limit:0", 100, 385, 385);

        Assert.False(MainViewModel.ApplyLiveStateToSliders([core, power], Live(20, 0, 385_000)));
    }

    /// <summary>A live value outside the reported bounds is clamped, never applied raw.</summary>
    [Fact]
    public void ALiveValueOutsideBoundsIsClamped()
    {
        MainViewModel.GpuControlSlider power = Slider("gpupower.limit:0", 100, 385, 385);

        MainViewModel.ApplyLiveStateToSliders([power], Live(0, 0, 500_000));

        Assert.Equal(385, power.Value);
    }
}
