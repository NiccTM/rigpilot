namespace PCHelper.Core;

/// <summary>
/// Refuses to begin an Auto OC run on a machine that is already reporting hardware
/// instability.
///
/// Auto OC screens candidate clock offsets by applying them and watching for faults, and
/// it already invalidates a profile when a WHEA event arrives mid-run. That is useless if
/// the machine was ALREADY throwing machine-check exceptions before the run started: the
/// faults are not caused by the candidate offset, so every screening verdict is
/// meaningless, and driving an unstable machine harder risks a hard hang that no software
/// abort can catch (the abort logic never runs once the box stops responding).
///
/// A WHEA machine-check exception is a CPU/memory/interconnect fault. RigPilot performs no
/// CPU tuning at all, so these are pre-existing platform conditions — an unstable memory
/// profile, an aggressive undervolt, marginal voltage, or failing hardware — that must be
/// resolved by the owner before an overclock screening result can mean anything.
/// </summary>
public static class AutoOcPreflightPolicy
{
    /// <summary>
    /// How far back a machine-check signal still counts against starting a run. Long enough
    /// to catch "it crashed earlier today", short enough that a machine fixed days ago is
    /// not blocked forever.
    /// </summary>
    public static readonly TimeSpan InstabilityLookback = TimeSpan.FromHours(24);

    /// <summary>
    /// Returns a refusal reason when Auto OC must not start, or null when it may proceed.
    /// <paramref name="signals"/> is any recent health-signal set; only WHEA signals inside
    /// the lookback block a run. Display-driver resets do not: those are the fault class
    /// Auto OC is designed to observe and recover from during screening.
    /// </summary>
    public static string? DescribeBlockingInstability(
        IEnumerable<HealthSystemSignal> signals,
        DateTimeOffset now,
        bool overrideInstabilityBlock = false)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (overrideInstabilityBlock)
        {
            return null;
        }

        DateTimeOffset cutoff = now - InstabilityLookback;
        HealthSystemSignal[] recent = [.. signals
            .Where(signal => signal.Kind == HealthSystemSignalKind.Whea && signal.Timestamp > cutoff)];
        if (recent.Length == 0)
        {
            return null;
        }

        DateTimeOffset mostRecent = recent.Max(signal => signal.Timestamp);
        return $"This machine reported {recent.Length} hardware error{(recent.Length == 1 ? string.Empty : "s")} "
            + $"(WHEA machine check) in the last {InstabilityLookback.TotalHours:0} hours, most recently at "
            + $"{mostRecent.ToLocalTime():yyyy-MM-dd HH:mm}. These are CPU/memory/interconnect faults that RigPilot "
            + "does not cause and cannot tune away, so an overclock screened now would be measuring the platform's "
            + "instability rather than the GPU. Resolve the underlying cause first — commonly an unstable memory "
            + "profile (EXPO/XMP), an aggressive Curve Optimizer or PBO undervolt, or marginal SoC voltage.";
    }
}
