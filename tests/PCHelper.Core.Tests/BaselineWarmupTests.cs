using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Auto OC baselines are only comparable once the card has stopped heating. A
/// cold first sample produced 4.03% variation against a 3% limit while the two
/// warm samples agreed to 0.13% — and a fixed 45-second warmup sized on one
/// thermal configuration still left baselines climbing (86.4 → 88.0 °C, 4.89%)
/// on another. The warmup therefore repeats short load windows until two
/// consecutive ones agree in peak temperature, rather than trusting a duration.
/// </summary>
public sealed class BaselineWarmupTests
{
    [Fact]
    public void ConsecutiveWindowsAgreeingInTemperatureCountAsAPlateau()
    {
        Assert.True(AutoOcV3Policy.HasReachedThermalPlateau(87.5, 88.0));
        Assert.True(AutoOcV3Policy.HasReachedThermalPlateau(88.0, 88.0));
    }

    [Fact]
    public void ACardStillHeatingIsNotAtAPlateau()
    {
        // The live failure: peak temperature climbing 86.4 → 88.0 across samples.
        Assert.False(AutoOcV3Policy.HasReachedThermalPlateau(86.4, 88.0));
    }

    [Fact]
    public void TheFirstWindowNeverCountsAsAPlateau()
    {
        // One reading has nothing to agree with — a single warm-looking window
        // must not skip the warmup entirely.
        Assert.False(AutoOcV3Policy.HasReachedThermalPlateau(null, 88.0));
    }

    [Fact]
    public void MissingTemperaturesNeverCountAsAPlateau()
    {
        // Absence of evidence is not stability — the recurring bug class this
        // codebase keeps refusing to reintroduce.
        Assert.False(AutoOcV3Policy.HasReachedThermalPlateau(88.0, null));
        Assert.False(AutoOcV3Policy.HasReachedThermalPlateau(null, null));
        Assert.False(AutoOcV3Policy.HasReachedThermalPlateau(double.NaN, 88.0));
    }

    [Fact]
    public void TheWarmupBudgetIsBoundedAndMeaningful()
    {
        // At least two windows are structurally required for any plateau, and the
        // cap must keep total warmup within minutes of a screening run measured
        // in tens of minutes.
        Assert.True(AutoOcV3Policy.MaximumWarmupWindows >= 2);
        TimeSpan worstCase = AutoOcV3Policy.WarmupWindowDuration * AutoOcV3Policy.MaximumWarmupWindows;
        Assert.InRange(worstCase, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void TheObservedTransientWouldHaveBeenWaitedOut()
    {
        // Replay of the live climb: consecutive windows roughly 1.5 °C apart do
        // not plateau, and the settling that followed (0.4 °C apart) does.
        Assert.False(AutoOcV3Policy.HasReachedThermalPlateau(86.4, 87.9));
        Assert.True(AutoOcV3Policy.HasReachedThermalPlateau(87.9, 88.3));
    }
}
