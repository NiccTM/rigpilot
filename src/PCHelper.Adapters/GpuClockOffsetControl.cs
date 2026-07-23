namespace PCHelper.Adapters;

/// <summary>The two public clock domains RigPilot offsets. Voltage domains are permanently out of scope.</summary>
public enum GpuClockOffsetDomain
{
    Core,
    Memory,
}

/// <summary>
/// Driver-reported clock-offset constraints for one domain, in kilohertz. The
/// vendor default offset is always 0 kHz (stock clocks); the range comes from
/// the driver's editable pstates20 delta range and is the authoritative clamp —
/// RigPilot never widens it.
/// </summary>
public readonly record struct GpuClockOffsetBounds(int MinimumKiloHertz, int MaximumKiloHertz)
{
    public const int DefaultKiloHertz = 0;

    public bool IsValid =>
        MinimumKiloHertz <= DefaultKiloHertz
        && MaximumKiloHertz >= DefaultKiloHertz
        && MinimumKiloHertz < MaximumKiloHertz;
}

/// <summary>The currently commanded offset for one domain, in kilohertz; null when unreadable.</summary>
public readonly record struct GpuClockOffsetState(int? CurrentKiloHertz);

/// <summary>
/// Transport seam for GPU clock offsets. Implementations must issue a clock
/// write only from <see cref="SetOffsetAsync"/> and only while explicitly
/// write-enabled; reads must stay side-effect free.
/// </summary>
public interface IGpuClockOffsetTransport
{
    Task<GpuClockOffsetBounds?> ReadBoundsAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken);

    Task<GpuClockOffsetState> ReadStateAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken);

    Task SetOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a previously captured offset (or stock) back, and is the only
    /// write that does <b>not</b> require an armed transport.
    ///
    /// <para>Restoring must never be blocked by the arm gate. A restore can
    /// only move hardware toward the state it was already in, so refusing one
    /// strictly increases risk. Blocking it produced a genuine deadlock: after
    /// a disarm, the rollback write was refused, the service could not prove a
    /// default state, it entered RecoveryRequired and locked writes — which
    /// left it still unable to restore. The adapter layer already exempts
    /// rollback from its own arm check; this honours the same intent one layer
    /// down. Bounds, editability, and range checks all still apply.</para>
    /// </summary>
    Task RestoreOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken);
}

/// <summary>Raised when a GPU clock-offset operation would violate a safety bound.</summary>
public sealed class GpuClockSafetyException(string message) : InvalidOperationException(message);

/// <summary>
/// A clock write the driver rejected, carrying the domain, requested value, and
/// the driver's own delta range. The vendor status alone ("NVAPI_INVALID_USER_
/// PRIVILEGE") says nothing about what was attempted, which made an Auto OC
/// failure indistinguishable from a manual one on the reference rig.
/// </summary>
public sealed class GpuClockWriteException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
