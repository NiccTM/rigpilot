using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the guided-undervolt preset math: presets anchor on the vendor default
/// (or the range maximum when no default exists), always clamp into the driver
/// range, and unknown presets refuse rather than guess. The RTX 3090 reference
/// range (100–385 W, default 350 W) is used as the realistic case.
/// </summary>
public sealed class UndervoltPresetsTests
{
    [Theory]
    [InlineData(UndervoltPresets.Quiet, 262)]     // 350 * 0.75 = 262.5 → 262 (banker's rounding)
    [InlineData(UndervoltPresets.Efficient, 298)] // 350 * 0.85 = 297.5 → 298
    [InlineData(UndervoltPresets.Stock, 350)]
    public void PresetsAnchorOnTheVendorDefault(string preset, double expectedWatts)
    {
        Assert.Equal(expectedWatts, UndervoltPresets.ComputeTargetWatts(100, 385, 350, preset));
    }

    [Fact]
    public void MissingVendorDefaultFallsBackToTheRangeMaximum()
    {
        Assert.Equal(289, UndervoltPresets.ComputeTargetWatts(100, 340, null, UndervoltPresets.Efficient));
    }

    [Fact]
    public void OutOfRangeVendorDefaultIsIgnored()
    {
        // A default outside the driver range cannot be trusted as an anchor.
        Assert.Equal(255, UndervoltPresets.ComputeTargetWatts(100, 300, 500, UndervoltPresets.Efficient));
    }

    [Fact]
    public void TargetsClampIntoTheDriverRange()
    {
        // 120 * 0.75 = 90 would undercut the driver minimum of 100.
        Assert.Equal(100, UndervoltPresets.ComputeTargetWatts(100, 385, 120, UndervoltPresets.Quiet));
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("")]
    public void UnknownPresetsRefuse(string preset)
    {
        Assert.Null(UndervoltPresets.ComputeTargetWatts(100, 385, 350, preset));
    }

    [Theory]
    [InlineData(0, 385)]   // non-positive minimum
    [InlineData(385, 100)] // inverted range
    public void InvalidRangesRefuse(double minimum, double maximum)
    {
        Assert.Null(UndervoltPresets.ComputeTargetWatts(minimum, maximum, 350, UndervoltPresets.Stock));
    }
}
