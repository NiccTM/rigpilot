namespace PCHelper.Core;

/// <summary>
/// Start/stop shaping for one cooling output — the two curve parameters
/// RigPilot lacked against comparable tools, and the pair behind the most
/// common real-world failures: a fan commanded to a low duty from rest that
/// never restarts (it sits stalled at 0 RPM while the curve believes it is
/// spinning), and the absence of true zero-RPM idle.
///
/// <para><b>Stopping is a privilege, not a setting.</b>
/// <see cref="StoppingPermitted"/> is false for pumps, CPU fans, and any
/// output the cooling safety supervisor protects, and this policy can never
/// return 0% for such an output regardless of
/// <see cref="StopBelowPercent"/>.</para>
///
/// <para><b>Restarting is boosted, not hoped for.</b> Leaving stop always
/// commands at least the measured restart floor for a minimum hold, so the
/// rotor actually breaks static friction before the curve's low duty
/// resumes.</para>
/// </summary>
public sealed record FanStartStopOptions(
    double StartPercent,
    TimeSpan StartHold,
    double StopBelowPercent,
    bool StoppingPermitted,
    double CalibratedRestartFloorPercent = 0)
{
    /// <summary>
    /// The duty that actually breaks stiction: the configured kickstart, never
    /// below the measured restart floor for this exact fan.
    /// </summary>
    public double EffectiveStartPercent =>
        Math.Clamp(Math.Max(StartPercent, CalibratedRestartFloorPercent), 0, 100);
}

/// <summary>Whether the output is at rest, and any kickstart still in progress.</summary>
public sealed record FanStartStopState(bool Stopped, DateTimeOffset? KickstartUntil = null)
{
    public static readonly FanStartStopState Running = new(Stopped: false);
    public static readonly FanStartStopState AtRest = new(Stopped: true);
}

public sealed record FanStartStopDecision(double DutyPercent, FanStartStopState State, string Reason);

public static class FanStartStopPolicy
{
    /// <summary>
    /// Shapes one evaluated duty. <paramref name="requestedDuty"/> is the value
    /// the curve produced after offset, avoid-bands, and the calibration floor.
    /// </summary>
    public static FanStartStopDecision Evaluate(
        double requestedDuty,
        FanStartStopState state,
        FanStartStopOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        double requested = Math.Clamp(requestedDuty, 0, 100);

        // A protected output is never stopped and never treated as at rest,
        // even if a stored state or a stop threshold says otherwise.
        if (!options.StoppingPermitted)
        {
            return new FanStartStopDecision(
                requested,
                FanStartStopState.Running,
                "This output is safety-protected: it is never stopped, so no start/stop shaping applies.");
        }

        // Finish an in-progress kickstart before honouring a lower duty.
        if (state.KickstartUntil is DateTimeOffset until && now < until)
        {
            return new FanStartStopDecision(
                Math.Max(requested, options.EffectiveStartPercent),
                state with { Stopped = false },
                $"Holding the {options.EffectiveStartPercent:0.#}% start boost until the fan is reliably spinning.");
        }

        if (state.Stopped)
        {
            // Leaving rest requires clearing the start threshold, not merely
            // the stop threshold — otherwise the output stutters either side
            // of a single boundary.
            if (requested < options.EffectiveStartPercent)
            {
                return new FanStartStopDecision(
                    0,
                    FanStartStopState.AtRest,
                    $"Staying at 0 RPM: {requested:0.#}% is below the {options.EffectiveStartPercent:0.#}% needed to restart this fan.");
            }

            return new FanStartStopDecision(
                Math.Max(requested, options.EffectiveStartPercent),
                new FanStartStopState(Stopped: false, KickstartUntil: now + options.StartHold),
                $"Restarting with a {options.EffectiveStartPercent:0.#}% boost held for {options.StartHold.TotalSeconds:0.#}s.");
        }

        if (requested < options.StopBelowPercent)
        {
            return new FanStartStopDecision(
                0,
                FanStartStopState.AtRest,
                $"Stopping: {requested:0.#}% is below the {options.StopBelowPercent:0.#}% zero-RPM threshold.");
        }

        return new FanStartStopDecision(requested, FanStartStopState.Running, "Running on the evaluated curve duty.");
    }

    /// <summary>
    /// Validates that a configuration cannot oscillate: the duty required to
    /// restart must exceed the duty that triggers a stop, or the output would
    /// stop and restart repeatedly around one threshold.
    /// </summary>
    public static bool IsStableConfiguration(FanStartStopOptions options, out string reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.StoppingPermitted)
        {
            reason = "Stopping is not permitted for this output, so no start/stop hysteresis is required.";
            return true;
        }

        if (options.StopBelowPercent <= 0)
        {
            reason = "A zero-RPM threshold of 0% never stops the fan.";
            return false;
        }

        if (options.EffectiveStartPercent <= options.StopBelowPercent)
        {
            reason = $"The restart duty ({options.EffectiveStartPercent:0.#}%) must exceed the stop threshold ({options.StopBelowPercent:0.#}%) or the fan will oscillate.";
            return false;
        }

        if (options.StartHold <= TimeSpan.Zero)
        {
            reason = "The start boost must be held for a non-zero period to break stiction.";
            return false;
        }

        reason = "Start and stop thresholds are separated and the boost is held.";
        return true;
    }
}
