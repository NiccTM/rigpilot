using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// A whole-domain clock offset shifts every point on the V/F curve, but instability does not
/// live at every point. Screening at the stock power limit leaves the card power-bound, in a
/// comparatively low boost bin at high voltage — the most forgiving place to test an offset.
/// Reducing the power limit forces the opposite: higher boost bins at lower voltage, which is
/// where a shifted curve breaks and the state a game produces whenever it is not loading the
/// GPU flat out.
///
/// These tests pin the reduced target and the evidence the rejection carries.
/// </summary>
public sealed class AutoOcLowPowerValidationTests
{
    // Milliwatts, matching the controller's units on the reference RTX 3090.
    private const double Stock = 350_000;
    private const double Minimum = 100_000;

    [Fact]
    public void TheValidationTargetSitsMeaningfullyBelowStock()
    {
        double target = AutoOcV3Policy.LowPowerValidationTarget(Stock, Minimum);

        Assert.True(target < Stock, "the point of the phase is to leave the power-bound state");
        Assert.Equal(Stock * AutoOcV3Policy.LowPowerValidationFraction, target);
    }

    [Fact]
    public void TheTargetIsNeverBelowTheControllersOwnMinimum()
    {
        // A controller with a high floor must not be driven under it just to hit the fraction.
        double target = AutoOcV3Policy.LowPowerValidationTarget(stockValue: 200_000, minimumValue: 180_000);

        Assert.Equal(180_000, target);
    }

    [Fact]
    public void TheReductionIsSubstantialEnoughToChangeTheBoostState()
    {
        // A token reduction would leave the card power-bound and prove nothing.
        Assert.InRange(AutoOcV3Policy.LowPowerValidationFraction, 0.5, 0.9);
    }

    [Fact]
    public void TheScreenRunsLongEnoughToSettleIntoTheHigherBins()
    {
        Assert.InRange(AutoOcV3Policy.LowPowerValidationDuration.TotalSeconds, 20, 120);
    }

    [Fact]
    public void ARejectionExplainsWhyAFullLoadPassWasNotEnough()
    {
        string message = AutoOcV3Policy.DescribeLowPowerFailure(280_000, "display driver reset observed");

        Assert.Contains("280 W", message, StringComparison.Ordinal);
        Assert.Contains("display driver reset observed", message, StringComparison.Ordinal);
        // Without the reasoning this reads as rejecting an overclock the user just watched
        // pass, so the physics has to be in the message.
        Assert.Contains("higher boost clocks at lower voltage", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no profile was generated", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARejectionWithoutDetailStillReadsAsACompleteSentence()
    {
        Assert.Contains(
            "the screening monitor reported a failure",
            AutoOcV3Policy.DescribeLowPowerFailure(280_000, null),
            StringComparison.OrdinalIgnoreCase);
    }
}
