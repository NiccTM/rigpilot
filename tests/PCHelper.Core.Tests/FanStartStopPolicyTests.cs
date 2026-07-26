using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Pins zero-RPM idle and kickstart restart: the safety rule that a protected
/// output can never be stopped, and the stiction rule that leaving rest always
/// commands a held boost rather than the curve's low duty.
/// </summary>
public sealed class FanStartStopPolicyTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    private static FanStartStopOptions CaseFan(double start = 40, double stop = 15, double floor = 0) =>
        new(start, TimeSpan.FromSeconds(3), stop, StoppingPermitted: true, CalibratedRestartFloorPercent: floor);

    [Fact]
    public void AProtectedOutputIsNeverStoppedNoMatterTheThreshold()
    {
        // A pump or CPU fan: stop threshold set absurdly high, duty near zero.
        FanStartStopOptions pump = new(40, TimeSpan.FromSeconds(3), StopBelowPercent: 99, StoppingPermitted: false);

        FanStartStopDecision decision = FanStartStopPolicy.Evaluate(1, FanStartStopState.Running, pump, Origin);

        Assert.Equal(1, decision.DutyPercent);
        Assert.False(decision.State.Stopped);
        Assert.Contains("safety-protected", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AProtectedOutputStoredAsStoppedStillNeverCommandsZero()
    {
        FanStartStopOptions pump = new(40, TimeSpan.FromSeconds(3), 99, StoppingPermitted: false);

        // Even from a corrupt "stopped" state, a protected output must spin.
        FanStartStopDecision decision = FanStartStopPolicy.Evaluate(30, FanStartStopState.AtRest, pump, Origin);

        Assert.Equal(30, decision.DutyPercent);
        Assert.False(decision.State.Stopped);
    }

    [Fact]
    public void FallingBelowTheStopThresholdReachesTrueZeroRpm()
    {
        FanStartStopDecision decision = FanStartStopPolicy.Evaluate(10, FanStartStopState.Running, CaseFan(), Origin);

        Assert.Equal(0, decision.DutyPercent);
        Assert.True(decision.State.Stopped);
    }

    [Fact]
    public void AStoppedFanIgnoresADutyTooLowToActuallyRestartIt()
    {
        // 20% would leave the rotor stalled at 0 RPM while the curve believes
        // it is spinning — the failure this policy exists to prevent.
        FanStartStopDecision decision = FanStartStopPolicy.Evaluate(20, FanStartStopState.AtRest, CaseFan(), Origin);

        Assert.Equal(0, decision.DutyPercent);
        Assert.True(decision.State.Stopped);
        Assert.Contains("below", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeavingRestCommandsTheBoostAndHoldsItBeforeResumingTheCurve()
    {
        FanStartStopOptions options = CaseFan();

        FanStartStopDecision start = FanStartStopPolicy.Evaluate(45, FanStartStopState.AtRest, options, Origin);
        Assert.Equal(45, start.DutyPercent);
        Assert.False(start.State.Stopped);
        Assert.NotNull(start.State.KickstartUntil);

        // Mid-hold the curve drops to a duty that could stall a spinning-up
        // fan; the boost is maintained instead.
        FanStartStopDecision held = FanStartStopPolicy.Evaluate(18, start.State, options, Origin.AddSeconds(1));
        Assert.Equal(options.EffectiveStartPercent, held.DutyPercent);
        Assert.False(held.State.Stopped);

        // After the hold, normal shaping resumes — including stopping again.
        FanStartStopDecision after = FanStartStopPolicy.Evaluate(10, held.State, options, Origin.AddSeconds(5));
        Assert.Equal(0, after.DutyPercent);
        Assert.True(after.State.Stopped);
    }

    [Fact]
    public void TheMeasuredRestartFloorOverridesATooLowConfiguredStart()
    {
        // Calibration proved this fan needs 55% to restart; the user set 30%.
        FanStartStopOptions options = CaseFan(start: 30, stop: 15, floor: 55);
        Assert.Equal(55, options.EffectiveStartPercent);

        FanStartStopDecision decision = FanStartStopPolicy.Evaluate(40, FanStartStopState.AtRest, options, Origin);

        Assert.Equal(0, decision.DutyPercent); // 40% cannot restart this fan
        FanStartStopDecision restart = FanStartStopPolicy.Evaluate(60, FanStartStopState.AtRest, options, Origin);
        Assert.Equal(60, restart.DutyPercent);
    }

    [Theory]
    [InlineData(40, 15, true)]   // clear separation
    [InlineData(15, 15, false)]  // restart duty equals stop threshold — oscillates
    [InlineData(10, 20, false)]  // restart below stop threshold — oscillates
    [InlineData(40, 0, false)]   // never actually stops
    public void OscillatingConfigurationsAreRejected(double start, double stop, bool expected)
    {
        FanStartStopOptions options = new(start, TimeSpan.FromSeconds(3), stop, StoppingPermitted: true);

        Assert.Equal(expected, FanStartStopPolicy.IsStableConfiguration(options, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void AZeroLengthBoostIsRejectedBecauseItCannotBreakStiction()
    {
        FanStartStopOptions options = new(40, TimeSpan.Zero, 15, StoppingPermitted: true);

        Assert.False(FanStartStopPolicy.IsStableConfiguration(options, out string reason));
        Assert.Contains("held", reason, StringComparison.OrdinalIgnoreCase);
    }
}
