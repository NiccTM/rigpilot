using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The Performance page's clock-offset sliders start at zero on every dashboard launch;
/// they do not read back the offsets already applied to the GPU. So "save this overclock
/// for startup" taken straight from a freshly opened page would re-apply stock values,
/// undoing a live overclock, and then persist that as the saved one. The guard that stops
/// it is small enough to be easy to delete by accident, which is why it is pinned here.
/// </summary>
public sealed class GpuOcStartupSelectionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(350, 350)]
    [InlineData(-15, -15)]
    public void AControlSittingOnItsDefaultIsNotDeliberate(double value, double defaultValue)
    {
        Assert.False(GpuOcStartupSelection.IsDeliberateValue(value, defaultValue));
    }

    [Theory]
    [InlineData(45, 0)]
    [InlineData(135, 0)]
    [InlineData(280, 350)]
    public void AControlMovedOffItsDefaultIsDeliberate(double value, double defaultValue)
    {
        Assert.True(GpuOcStartupSelection.IsDeliberateValue(value, defaultValue));
    }

    /// <summary>
    /// A freshly opened page: both clock offsets at zero with zero as their default. This
    /// is the exact state that must never be saved as an overclock.
    /// </summary>
    [Fact]
    public void AFreshlyOpenedPageOffersNothingToSave()
    {
        (double Value, double? Default)[] page = [(0, 0), (0, 0)];

        Assert.DoesNotContain(page, control => GpuOcStartupSelection.IsDeliberateValue(control.Value, control.Default));
    }

    /// <summary>
    /// The state in the screenshot this was built from: core +45 MHz, memory +135 MHz.
    /// </summary>
    [Fact]
    public void AnAppliedOverclockIsOfferedForSaving()
    {
        (double Value, double? Default)[] page = [(45, 0), (135, 0)];

        Assert.All(page, control => Assert.True(
            GpuOcStartupSelection.IsDeliberateValue(control.Value, control.Default)));
    }

    /// <summary>
    /// With no reported default, zero is assumed. That is right for a clock offset, where
    /// zero is stock, and conservative everywhere else: it refuses to save rather than
    /// treating an unknown value as something the user chose.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(45, true)]
    public void AnUnknownDefaultFallsBackToZero(double value, bool expected)
    {
        Assert.Equal(expected, GpuOcStartupSelection.IsDeliberateValue(value, defaultValue: null));
    }
}
