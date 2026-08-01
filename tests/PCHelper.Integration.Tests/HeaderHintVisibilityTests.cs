using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The page header puts a fixed-width action strip beside a star-sized title
/// column, so the title gets whatever the strip leaves. At the 960px minimum
/// window width that was not enough, and because a Grid does not clip its
/// children the title drew straight under the mode toggle. Trimming stops the
/// overlap; collapsing the non-interactive refresh hint is what keeps the
/// trimmed title readable rather than reducing it to a few characters.
/// </summary>
public sealed class HeaderHintVisibilityTests
{
    /// <summary>
    /// The header is about 958px wide in the 1240px default window and about
    /// 662px wide at the 960px minimum.
    /// </summary>
    [Theory]
    [InlineData(958, true)]
    [InlineData(800, true)]
    [InlineData(760, true)]
    [InlineData(759, false)]
    [InlineData(662, false)]
    public void HintYieldsItsSpaceOnlyOnANarrowHeader(double headerWidth, bool expected)
    {
        Assert.Equal(expected, HeaderHintVisibilityConverter.IsHintVisible(headerWidth));
    }

    /// <summary>
    /// Visibility is bound to ActualWidth, which is zero until the first layout
    /// pass and can be NaN while a template is being applied. Treating those as
    /// "narrow" would blink the hint out of a normal-sized window on every
    /// re-layout, so an unmeasured header keeps it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnmeasuredHeaderKeepsTheHint(double headerWidth)
    {
        Assert.True(HeaderHintVisibilityConverter.IsHintVisible(headerWidth));
    }

    [Fact]
    public void ThresholdIsHonouredWhenSuppliedExplicitly()
    {
        Assert.False(HeaderHintVisibilityConverter.IsHintVisible(900, minimumWidth: 1000));
        Assert.True(HeaderHintVisibilityConverter.IsHintVisible(900, minimumWidth: 500));
    }
}
