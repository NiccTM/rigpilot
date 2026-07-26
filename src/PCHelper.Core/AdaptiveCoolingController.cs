using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// The durable state of continuous adaptive cooling between evaluations. Held
/// by the caller (the service telemetry loop) so this controller stays pure.
/// </summary>
public sealed record AdaptiveCoolingState(
    CoolingCurveMode ActiveMode,
    DateTimeOffset ActiveSince,
    CoolingCurveMode? PendingMode = null,
    DateTimeOffset? PendingSince = null)
{
    public static AdaptiveCoolingState Start(CoolingCurveMode mode, DateTimeOffset now) => new(mode, now);
}

/// <summary>Whether this tick should re-apply a curve, and why.</summary>
public sealed record AdaptiveCoolingDecision(
    AdaptiveCoolingState State,
    bool ShouldApply,
    CoolingCurveMode Mode,
    string Reason);

/// <summary>
/// Makes adaptive cooling <b>continuous</b>: evaluated on every telemetry tick
/// instead of once when a button is clicked. Mode selection reuses
/// <see cref="CoolingModeSelector"/> (thresholds + 4 °C hysteresis); this type
/// adds the two timing guards that stop a live loop from thrashing the fans:
///
/// <list type="bullet">
/// <item>a candidate mode must persist for <see cref="ThresholdHold"/> before it is accepted, so a
/// brief spike does not switch curves;</item>
/// <item>an accepted mode must stay active for <see cref="MinimumDwell"/> before another switch, so
/// the fans cannot oscillate.</item>
/// </list>
///
/// Both guards are bypassed for safety escalations only — an emergency
/// temperature or a stale temperature sensor escalates to
/// <see cref="CoolingCurveMode.Cooling"/> immediately. The guards are never
/// bypassed to reduce cooling. Pure and deterministic: the caller supplies the
/// clock, so this is exhaustively testable without hardware.
/// </summary>
public static class AdaptiveCoolingController
{
    /// <summary>A candidate mode must hold this long before it is accepted.</summary>
    public static readonly TimeSpan ThresholdHold = TimeSpan.FromSeconds(15);

    /// <summary>An accepted mode stays active at least this long before another switch.</summary>
    public static readonly TimeSpan MinimumDwell = TimeSpan.FromSeconds(60);

    /// <summary>
    /// At or above this temperature, escalate to the Cooling curve immediately,
    /// ignoring both timing guards. Set above the Cooling entry threshold
    /// (<see cref="CoolingModeSelector.HotCelsius"/>) so it fires only for a
    /// genuine thermal excursion, and below the curves' full-speed points so it
    /// still buys headroom.
    /// </summary>
    public const double EmergencyCelsius = 85;

    /// <summary>
    /// Evaluates one telemetry tick. <paramref name="sensorsStale"/> means the
    /// required temperature sensors did not report fresh values — treated as a
    /// safety escalation, never as "cool".
    /// </summary>
    public static AdaptiveCoolingDecision Evaluate(
        AdaptiveCoolingState state,
        double? cpuCelsius,
        double? gpuCelsius,
        bool sensorsStale,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        // --- Safety escalations: immediate, no hold, no dwell. ---------------
        // Absent readings are NOT a cool reading. With no temperature evidence
        // at all we must escalate exactly as for stale telemetry — never step
        // cooling down on the strength of missing data.
        double? hottest = Hottest(cpuCelsius, gpuCelsius);
        bool noTelemetry = hottest is null;
        if (sensorsStale || noTelemetry || hottest >= EmergencyCelsius)
        {
            string reason = sensorsStale || noTelemetry
                ? (noTelemetry && !sensorsStale
                    ? "No CPU or GPU temperature is being reported; escalating to the Cooling curve immediately."
                    : "Temperature telemetry is stale; escalating to the Cooling curve immediately.")
                : $"{hottest:0} °C is at or above the {EmergencyCelsius:0} °C emergency threshold; escalating to the Cooling curve immediately.";
            return state.ActiveMode == CoolingCurveMode.Cooling
                // Already cooling: hold it there and clear any pending step-down.
                ? new AdaptiveCoolingDecision(
                    state with { PendingMode = null, PendingSince = null },
                    ShouldApply: false,
                    CoolingCurveMode.Cooling,
                    reason)
                : new AdaptiveCoolingDecision(
                    AdaptiveCoolingState.Start(CoolingCurveMode.Cooling, now),
                    ShouldApply: true,
                    CoolingCurveMode.Cooling,
                    reason);
        }

        // --- Normal selection: thresholds + hysteresis against the active mode.
        CoolingCurveMode candidate = CoolingModeSelector.Choose(cpuCelsius, gpuCelsius, state.ActiveMode);
        if (candidate == state.ActiveMode)
        {
            return new AdaptiveCoolingDecision(
                state with { PendingMode = null, PendingSince = null },
                ShouldApply: false,
                state.ActiveMode,
                $"Holding {state.ActiveMode}: {CoolingModeSelector.Describe(state.ActiveMode, cpuCelsius, gpuCelsius)}");
        }

        // A different candidate: start (or continue) its hold timer.
        AdaptiveCoolingState pending = state.PendingMode == candidate
            ? state
            : state with { PendingMode = candidate, PendingSince = now };
        TimeSpan held = now - (pending.PendingSince ?? now);
        TimeSpan dwelled = now - state.ActiveSince;
        if (held < ThresholdHold)
        {
            return new AdaptiveCoolingDecision(
                pending,
                ShouldApply: false,
                state.ActiveMode,
                $"{candidate} pending for {held.TotalSeconds:0}s of {ThresholdHold.TotalSeconds:0}s before switching.");
        }

        if (dwelled < MinimumDwell)
        {
            return new AdaptiveCoolingDecision(
                pending,
                ShouldApply: false,
                state.ActiveMode,
                $"{candidate} is ready, but {state.ActiveMode} must hold {MinimumDwell.TotalSeconds:0}s (currently {dwelled.TotalSeconds:0}s).");
        }

        return new AdaptiveCoolingDecision(
            AdaptiveCoolingState.Start(candidate, now),
            ShouldApply: true,
            candidate,
            $"Switching to {candidate}: {CoolingModeSelector.Describe(candidate, cpuCelsius, gpuCelsius)}");
    }

    private static double? Hottest(double? cpuCelsius, double? gpuCelsius) => (cpuCelsius, gpuCelsius) switch
    {
        (double cpu, double gpu) => Math.Max(cpu, gpu),
        (double cpu, null) => cpu,
        (null, double gpu) => gpu,
        _ => null,
    };
}
