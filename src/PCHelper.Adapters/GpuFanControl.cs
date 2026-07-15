using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// The current fan-control policy of a GPU cooler. Restoring
/// <see cref="Automatic"/> returns the fan to the driver's built-in curve and is
/// the required reset/rollback target.
/// </summary>
public enum GpuFanControlPolicy
{
    Automatic,
    Manual
}

/// <summary>
/// Observed state of one GPU fan channel: its control policy and, when in manual
/// mode, the duty currently commanded, plus the last measured duty from telemetry.
/// </summary>
public sealed record GpuFanChannelState(
    GpuFanControlPolicy Policy,
    int? CommandedDutyPercent,
    int? MeasuredDutyPercent);

/// <summary>
/// The exact-device bounds for a GPU fan channel, discovered read-only before any
/// write. <see cref="FloorPercent"/> is the restart-validated conservative minimum;
/// no commanded duty may fall below it.
/// </summary>
public sealed record GpuFanBounds(
    int FloorPercent,
    int CeilingPercent)
{
    public bool IsValid => FloorPercent is >= 0 and <= 100
        && CeilingPercent is >= 0 and <= 100
        && CeilingPercent >= FloorPercent;
}

/// <summary>
/// The seam between the GPU-fan adapter's safety logic and the physical cooler.
/// The safety-critical adapter is tested against an in-memory fake implementation;
/// the real NVML/NVAPI transport is introduced separately, still gated, in a later
/// step. No implementation of this interface is wired to real hardware until then.
/// </summary>
public interface IGpuFanCoolerTransport
{
    /// <summary>Reads the exact bounds for the channel, or null if unavailable.</summary>
    Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>Reads the current policy and duty for the channel.</summary>
    Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>Enters manual policy and commands the given (already-clamped) duty.</summary>
    Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken);

    /// <summary>Restores the driver automatic fan curve for the channel.</summary>
    Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken);
}

/// <summary>Thrown when a request violates a GPU-fan safety bound.</summary>
public sealed class GpuFanSafetyException(string message) : Exception(message);
