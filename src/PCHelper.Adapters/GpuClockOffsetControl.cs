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
}

/// <summary>Raised when a GPU clock-offset operation would violate a safety bound.</summary>
public sealed class GpuClockSafetyException(string message) : InvalidOperationException(message);
