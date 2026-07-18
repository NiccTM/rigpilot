using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Pins the adaptive cooling-mode selector: threshold choices, the hysteresis
/// that stops boundary flapping, and the no-telemetry fallback.
/// </summary>
public sealed class CoolingModeSelectorTests
{
    [Theory]
    [InlineData(45, 50, CoolingCurveMode.Silent)]    // everything cool
    [InlineData(60, 65, CoolingCurveMode.Balanced)]  // middle of the band
    [InlineData(60, 80, CoolingCurveMode.Cooling)]   // hottest component decides
    [InlineData(80, 40, CoolingCurveMode.Cooling)]
    public void ChoosesByTheHottestComponent(double cpu, double gpu, CoolingCurveMode expected)
    {
        Assert.Equal(expected, CoolingModeSelector.Choose(cpu, gpu));
    }

    [Fact]
    public void NoTelemetryFallsBackToBalanced()
    {
        Assert.Equal(CoolingCurveMode.Balanced, CoolingModeSelector.Choose(null, null));
    }

    [Fact]
    public void MissingOneSensorStillUsesTheOther()
    {
        Assert.Equal(CoolingCurveMode.Cooling, CoolingModeSelector.Choose(null, 80));
        Assert.Equal(CoolingCurveMode.Silent, CoolingModeSelector.Choose(50, null));
    }

    [Fact]
    public void HysteresisKeepsTheCoolingCurveJustBelowTheHotThreshold()
    {
        // 73 °C: below the 75 °C entry threshold, but within the 4 °C
        // hysteresis band — an active Cooling curve stays.
        Assert.Equal(CoolingCurveMode.Balanced, CoolingModeSelector.Choose(73, 60, previous: null));
        Assert.Equal(CoolingCurveMode.Cooling, CoolingModeSelector.Choose(73, 60, previous: CoolingCurveMode.Cooling));
        // Clearly cooler than the band → it does step down.
        Assert.Equal(CoolingCurveMode.Balanced, CoolingModeSelector.Choose(69, 60, previous: CoolingCurveMode.Cooling));
    }

    [Fact]
    public void HysteresisKeepsTheSilentCurveJustAboveTheCoolThreshold()
    {
        Assert.Equal(CoolingCurveMode.Balanced, CoolingModeSelector.Choose(57, 50, previous: null));
        Assert.Equal(CoolingCurveMode.Silent, CoolingModeSelector.Choose(57, 50, previous: CoolingCurveMode.Silent));
        Assert.Equal(CoolingCurveMode.Balanced, CoolingModeSelector.Choose(61, 50, previous: CoolingCurveMode.Silent));
    }

    [Fact]
    public void DescriptionNamesTheTemperaturesAndTheChoice()
    {
        string description = CoolingModeSelector.Describe(CoolingCurveMode.Cooling, 80, 78);

        Assert.Contains("80", description, StringComparison.Ordinal);
        Assert.Contains("78", description, StringComparison.Ordinal);
        Assert.Contains("Cooling", description, StringComparison.Ordinal);
    }
}
