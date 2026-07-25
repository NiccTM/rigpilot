using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Constrains an Auto OC search to offsets a real card can plausibly survive, instead of
/// the driver's theoretical range.
///
/// NVAPI advertises what the driver will ACCEPT, not what the silicon can run: on an
/// RTX 3090 that is ±1000 MHz core and +3000 MHz memory. Screening across that range is
/// what makes an auto-overclocker hang the machine. The candidate ladder divides the range
/// into steps, so a 0..1000 MHz core search steps in ~83 MHz jumps and reaches offsets no
/// card in that class runs (typically +100-150 MHz core) within the first few candidates.
/// At those offsets the GPU does not fail gracefully — it does not render an artifact or
/// raise a recoverable driver reset that screening could observe. It hard-hangs, and once
/// the machine stops responding no in-process abort, thermal ceiling, or rollback can run.
/// The boot sentinel then recovers the card, but the user has already lost the session.
///
/// So the envelope is deliberately conservative: it is the range in which a failure is
/// most likely to present as something screening can DETECT rather than as a dead machine.
/// It bounds the automatic search only. A user driving the manual slider still has the
/// controller's full documented range, because that is a deliberate single write with
/// read-back rather than an unattended climb.
/// </summary>
public static class AutoOcSearchEnvelope
{
    /// <summary>
    /// Core-clock offset ceiling for an automatic search. Comfortably above a typical
    /// good sample's stable offset, far below the range where failure means a hard hang.
    /// </summary>
    public const double MaximumCoreOffsetMhz = 200;

    /// <summary>
    /// Memory-clock offset ceiling. GDDR6X error-correction masks instability as falling
    /// performance long before it becomes visible, and past that point the failure mode is
    /// again a hang rather than an artifact, so the automatic ladder stops well short.
    /// </summary>
    public const double MaximumMemoryOffsetMhz = 1000;

    /// <summary>
    /// Returns the offset ceiling for a clock capability, or null when the capability is not
    /// a GPU clock offset and should keep its reported range.
    /// </summary>
    public static double? CeilingFor(string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        if (capabilityId.StartsWith("gpuclock.core:", StringComparison.Ordinal))
        {
            return MaximumCoreOffsetMhz;
        }

        return capabilityId.StartsWith("gpuclock.memory:", StringComparison.Ordinal)
            ? MaximumMemoryOffsetMhz
            : null;
    }

    /// <summary>
    /// Builds the search bounds for an automatic run: never below stock (a performance
    /// search must not undervolt-by-underclock), and never above the smaller of the
    /// controller's reported maximum and this envelope's ceiling.
    /// </summary>
    public static TuneBounds Constrain(string capabilityId, NumericRange range)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        ArgumentNullException.ThrowIfNull(range);
        double floor = Math.Max(0, range.Minimum);
        double ceiling = CeilingFor(capabilityId) is double envelope
            ? Math.Min(range.Maximum, envelope)
            : range.Maximum;
        // A controller that reports a maximum below the envelope keeps its own smaller
        // range, and a degenerate range never inverts.
        return new TuneBounds(floor, Math.Max(floor, ceiling), range.Step);
    }
}
