using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Pins continuous adaptive cooling: the threshold hold and minimum dwell that
/// stop fan thrashing, and the safety escalations that deliberately bypass
/// both. Deterministic — the clock is supplied, no hardware is involved.
/// </summary>
public sealed class AdaptiveCoolingControllerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SteadyTemperaturesHoldTheActiveModeAndNeverReapply()
    {
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Balanced, Origin);

        AdaptiveCoolingDecision decision = AdaptiveCoolingController.Evaluate(
            state, cpuCelsius: 62, gpuCelsius: 65, sensorsStale: false, Origin.AddMinutes(5));

        Assert.False(decision.ShouldApply);
        Assert.Equal(CoolingCurveMode.Balanced, decision.Mode);
        Assert.Null(decision.State.PendingMode);
    }

    [Fact]
    public void ABriefSpikeDoesNotSwitchCurvesBecauseTheHoldIsNotMet()
    {
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Balanced, Origin);

        // Hot at t+0s: starts the hold timer, does not switch.
        AdaptiveCoolingDecision spike = AdaptiveCoolingController.Evaluate(
            state, 80, 78, sensorsStale: false, Origin.AddMinutes(5));
        Assert.False(spike.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, spike.State.PendingMode);

        // Back to normal 5s later, before the 15s hold elapses: pending clears.
        AdaptiveCoolingDecision settled = AdaptiveCoolingController.Evaluate(
            spike.State, 62, 64, sensorsStale: false, Origin.AddMinutes(5).AddSeconds(5));
        Assert.False(settled.ShouldApply);
        Assert.Null(settled.State.PendingMode);
        Assert.Equal(CoolingCurveMode.Balanced, settled.State.ActiveMode);
    }

    [Fact]
    public void ASustainedChangeSwitchesOnceTheHoldAndDwellAreBothSatisfied()
    {
        // Active long enough that dwell is already satisfied.
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Balanced, Origin);
        DateTimeOffset hotAt = Origin.AddMinutes(10);

        AdaptiveCoolingDecision pending = AdaptiveCoolingController.Evaluate(
            state, 80, 78, sensorsStale: false, hotAt);
        Assert.False(pending.ShouldApply);

        // Still hot 15s later: hold satisfied, dwell satisfied → switch.
        AdaptiveCoolingDecision applied = AdaptiveCoolingController.Evaluate(
            pending.State, 80, 78, sensorsStale: false, hotAt + AdaptiveCoolingController.ThresholdHold);

        Assert.True(applied.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, applied.Mode);
        Assert.Equal(CoolingCurveMode.Cooling, applied.State.ActiveMode);
        Assert.Null(applied.State.PendingMode);
    }

    [Fact]
    public void MinimumDwellBlocksAnImmediateSecondSwitch()
    {
        // Mode became active only 5s ago; dwell is 60s.
        DateTimeOffset activeSince = Origin.AddSeconds(-5);
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Cooling, activeSince);

        // Cool readings sustained past the hold, but dwell has not elapsed.
        AdaptiveCoolingDecision first = AdaptiveCoolingController.Evaluate(
            state, 45, 48, sensorsStale: false, Origin);
        AdaptiveCoolingDecision second = AdaptiveCoolingController.Evaluate(
            first.State, 45, 48, sensorsStale: false, Origin + AdaptiveCoolingController.ThresholdHold);

        Assert.False(second.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, second.State.ActiveMode);
        Assert.Contains("must hold", second.Reason, StringComparison.OrdinalIgnoreCase);

        // Once dwell elapses the same sustained reading is accepted.
        AdaptiveCoolingDecision third = AdaptiveCoolingController.Evaluate(
            second.State, 45, 48, sensorsStale: false, activeSince + AdaptiveCoolingController.MinimumDwell);
        Assert.True(third.ShouldApply);
        Assert.Equal(CoolingCurveMode.Silent, third.Mode);
    }

    [Fact]
    public void AnEmergencyTemperatureEscalatesImmediatelyIgnoringHoldAndDwell()
    {
        // Just switched to Silent — both guards would normally block a change.
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Silent, Origin);

        AdaptiveCoolingDecision decision = AdaptiveCoolingController.Evaluate(
            state, cpuCelsius: AdaptiveCoolingController.EmergencyCelsius, gpuCelsius: 60,
            sensorsStale: false, Origin.AddSeconds(1));

        Assert.True(decision.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, decision.Mode);
        Assert.Contains("emergency", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleSensorsEscalateToCoolingAndAreNeverTreatedAsCool()
    {
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Silent, Origin);

        // No readings at all plus a stale flag: must escalate, not idle at Silent.
        AdaptiveCoolingDecision decision = AdaptiveCoolingController.Evaluate(
            state, cpuCelsius: null, gpuCelsius: null, sensorsStale: true, Origin.AddSeconds(1));

        Assert.True(decision.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, decision.Mode);
        Assert.Contains("stale", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingTemperatureReadingsEscalateAndNeverStepCoolingDown()
    {
        // Sensors report nothing at all, but nothing has flagged them stale.
        // Absent evidence must not be read as "cool".
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Silent, Origin);

        AdaptiveCoolingDecision decision = AdaptiveCoolingController.Evaluate(
            state, cpuCelsius: null, gpuCelsius: null, sensorsStale: false, Origin.AddSeconds(1));

        Assert.True(decision.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, decision.Mode);
        Assert.Contains("No CPU or GPU temperature", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LosingTelemetryWhileCoolingHoldsCoolingRatherThanReverting()
    {
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Cooling, Origin);

        // Sustained loss of readings, well past both timing guards.
        AdaptiveCoolingDecision decision = AdaptiveCoolingController.Evaluate(
            state, null, null, sensorsStale: false, Origin.AddMinutes(30));

        Assert.False(decision.ShouldApply); // already cooling; no redundant write
        Assert.Equal(CoolingCurveMode.Cooling, decision.State.ActiveMode);
        Assert.Null(decision.State.PendingMode);
    }

    [Fact]
    public void AnOngoingEmergencyDoesNotReapplyEveryTick()
    {
        AdaptiveCoolingState state = AdaptiveCoolingState.Start(CoolingCurveMode.Cooling, Origin);

        AdaptiveCoolingDecision decision = AdaptiveCoolingController.Evaluate(
            state, 90, 88, sensorsStale: false, Origin.AddSeconds(30));

        // Already at maximum-cooling policy: keep it, but do not re-issue writes.
        Assert.False(decision.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, decision.State.ActiveMode);
    }

    [Fact]
    public void EscalationIsImmediateButDeescalationAlwaysWaits()
    {
        DateTimeOffset now = Origin.AddMinutes(10);

        // Up: emergency bypasses the guards.
        AdaptiveCoolingDecision up = AdaptiveCoolingController.Evaluate(
            AdaptiveCoolingState.Start(CoolingCurveMode.Balanced, now), 92, 70, false, now);
        Assert.True(up.ShouldApply);

        // Down: the very next cool tick must not reduce cooling instantly.
        AdaptiveCoolingDecision down = AdaptiveCoolingController.Evaluate(
            up.State, 40, 42, false, now.AddSeconds(1));
        Assert.False(down.ShouldApply);
        Assert.Equal(CoolingCurveMode.Cooling, down.State.ActiveMode);
    }
}
