namespace PCHelper.Core;

/// <summary>
/// One remembered hang: the capability being screened and the offset that was applied when
/// the machine stopped responding.
/// </summary>
public sealed record AutoOcCrashRecordV1(
    int SchemaVersion,
    string CapabilityId,
    double Value,
    DateTimeOffset ObservedAt)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Keeps an Auto OC search away from an offset that has already hung this machine.
///
/// Screening detects a candidate that fails observably — a thermal excursion, a display
/// driver reset, a failed read-back. It cannot detect the candidate that hard-hangs the
/// box, because nothing in this process runs again until the machine is rebooted. The boot
/// sentinel restores the card on the next start, but without a memory of what was applied
/// the next run simply climbs the same ladder into the same offset and hangs again. That is
/// the difference between a tool that recovers from a crash and one that learns from it.
///
/// The pending-operation record already names the capability and the value under test when
/// the machine died, so a hang leaves exactly the evidence needed. This turns that evidence
/// into a ceiling: subsequent searches stop below the offset that hung, with a margin, so
/// the ladder never revisits it.
/// </summary>
public static class AutoOcCrashCeiling
{
    /// <summary>
    /// Fraction of the crashing offset that remains searchable. Backing off to 80% keeps a
    /// useful search range while staying clear of the value that actually hung the machine —
    /// the true limit is somewhere below it, not just barely below it.
    /// </summary>
    public const double SafeFractionOfCrashingValue = 0.8;

    /// <summary>
    /// How long a remembered hang constrains the search. Long enough to cover the "try it
    /// again tomorrow" case that would otherwise repeat the crash, short enough that a
    /// genuinely changed machine — new driver, new BIOS, resolved platform instability — is
    /// not capped by an old result forever.
    /// </summary>
    public static readonly TimeSpan CrashMemoryWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Returns the ceiling a search must respect for this capability, or null when no
    /// remembered hang applies. Records for other capabilities and records outside the
    /// memory window are ignored.
    /// </summary>
    public static double? CeilingFor(
        string capabilityId,
        IEnumerable<AutoOcCrashRecordV1> records,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        ArgumentNullException.ThrowIfNull(records);
        DateTimeOffset cutoff = now - CrashMemoryWindow;
        double? lowest = null;
        foreach (AutoOcCrashRecordV1 record in records)
        {
            if (!string.Equals(record.CapabilityId, capabilityId, StringComparison.Ordinal)
                || record.ObservedAt <= cutoff
                || !double.IsFinite(record.Value)
                || record.Value <= 0)
            {
                continue;
            }

            // The most conservative remembered hang wins: if two crashes are known, the
            // lower one is the better evidence about where this card actually breaks.
            double ceiling = record.Value * SafeFractionOfCrashingValue;
            lowest = lowest is double current ? Math.Min(current, ceiling) : ceiling;
        }

        return lowest;
    }

    /// <summary>
    /// Applies a remembered hang on top of whatever ceiling the caller already computed,
    /// never raising it — this only ever makes a search more conservative.
    /// </summary>
    public static double ConstrainCeiling(double proposedCeiling, double? crashCeiling) =>
        crashCeiling is double crash ? Math.Min(proposedCeiling, crash) : proposedCeiling;
}
