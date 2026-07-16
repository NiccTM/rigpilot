namespace PCHelper.Adapters;

/// <summary>
/// The exact-device power-limit constraints for a GPU, discovered read-only before
/// any write. All values are milliwatts as reported by the vendor runtime; no
/// commanded limit may fall outside [<see cref="MinimumMilliwatts"/>,
/// <see cref="MaximumMilliwatts"/>], and <see cref="DefaultMilliwatts"/> is the
/// guaranteed reset target.
/// </summary>
public sealed record GpuPowerLimitBounds(
    uint MinimumMilliwatts,
    uint MaximumMilliwatts,
    uint DefaultMilliwatts)
{
    public bool IsValid => MinimumMilliwatts > 0
        && MaximumMilliwatts >= MinimumMilliwatts
        && DefaultMilliwatts >= MinimumMilliwatts
        && DefaultMilliwatts <= MaximumMilliwatts;
}

/// <summary>Observed state of one GPU power limit: the currently enforced value.</summary>
public sealed record GpuPowerLimitState(uint? CurrentMilliwatts);

/// <summary>
/// The seam between the GPU power-limit adapter's safety logic and the physical
/// device. The safety-critical adapter is tested against an in-memory fake; the
/// real NVML transport is the only implementation that touches hardware and is
/// separately arm-gated.
/// </summary>
public interface IGpuPowerLimitTransport
{
    /// <summary>Reads the vendor constraints and default, or null if unavailable.</summary>
    Task<GpuPowerLimitBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>Reads the currently enforced power limit.</summary>
    Task<GpuPowerLimitState> ReadStateAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>Applies the given (already-clamped) power limit in milliwatts.</summary>
    Task SetPowerLimitAsync(string channelId, uint milliwatts, CancellationToken cancellationToken);
}

/// <summary>Thrown when a request violates a GPU power-limit safety bound.</summary>
public sealed class GpuPowerSafetyException(string message) : Exception(message);
