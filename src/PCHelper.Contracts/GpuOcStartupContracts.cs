namespace PCHelper.Contracts;

/// <summary>
/// One GPU overclock output to reapply at startup: the exact capability and the numeric
/// value that was applied and verified in-session. Stored by capability id and raw value
/// so it is independent of units and survives capability-shape changes; the value is always
/// re-clamped to the live driver bounds before it is reapplied.
/// </summary>
public sealed record GpuOcStartupOutputV1(string CapabilityId, double Value);

/// <summary>
/// A persisted, risk-accepted GPU overclock to reapply automatically at service start.
///
/// This deliberately overrides the default session-only stance for GPU OC, and it is only
/// ever written after the user applies the OC, sees it verify, and explicitly accepts the
/// restart risk with an exact-device confirmation. Reapplication is never raw: it runs
/// through the same arm → apply → read-back-verify path as an interactive apply, guarded by
/// <see cref="GpuOcTrialEntryV1"/> so an OC that prevents a clean boot is reverted rather
/// than reapplied. Voltage is never part of this; only clock offsets and the power limit.
/// </summary>
public sealed record GpuOcStartupProfileV1(
    int SchemaVersion,
    string DeviceId,
    string AcceptedExactDeviceId,
    DateTimeOffset AcceptedAt,
    IReadOnlyList<GpuOcStartupOutputV1> Outputs)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// The boot-recovery journal entry. It is written to disk immediately BEFORE a persisted OC
/// is reapplied at startup and cleared only once the reapply has verified. If it is still
/// present at the next service start, the machine did not survive the reapply — the sentinel
/// then reverts every listed output to stock and disables persistence instead of trying again.
/// </summary>
public sealed record GpuOcTrialEntryV1(
    string DeviceId,
    DateTimeOffset StartedAt,
    IReadOnlyList<GpuOcStartupOutputV1> Outputs);
