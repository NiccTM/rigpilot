using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// The PBO limit parameters a Ryzen SMU tuning transport could command. Curve
/// Optimizer (per-core VF offsets) is deliberately NOT modelled here: it is a
/// stability hazard with a boot-failure mode and stays out of the seam until the
/// boot-recovery sentinel has been physically qualified. See
/// docs/qualification/cpu-tuning-and-intel-arc.md.
/// </summary>
public enum SmuTuningParameter
{
    /// <summary>Package power tracking limit, watts.</summary>
    PptWatts,

    /// <summary>Thermal design current limit, amps.</summary>
    TdcAmps,

    /// <summary>Electrical design current limit, amps.</summary>
    EdcAmps
}

/// <summary>
/// Exact-device bounds for one PBO limit parameter, discovered read-only before
/// any write. <see cref="StockValue"/> is the vendor default and the required
/// reset/rollback target; no commanded value may exceed <see cref="Maximum"/>
/// or fall below <see cref="Minimum"/>.
/// </summary>
public sealed record SmuTuningBounds(int Minimum, int Maximum, int StockValue)
{
    public bool IsValid => Minimum > 0
        && Maximum >= Minimum
        && StockValue >= Minimum
        && StockValue <= Maximum;
}

/// <summary>Observed value of one PBO limit parameter, or null when unreadable.</summary>
public sealed record SmuTuningState(int? CurrentValue);

/// <summary>
/// The seam between the CPU-tuning adapter's safety logic and the SMU mailbox.
/// The safety-critical adapter is tested against an in-memory fake ONLY. No
/// implementation of this interface performs a real SMU write anywhere in this
/// repository: per the qualification gate, a live transport requires the
/// boot-recovery sentinel, audited per-family mailbox maps, and a witnessed
/// controlled pass before it may exist. Voltage is not modelled at all — these
/// are power/current limits, and nothing here can construct a voltage command.
/// </summary>
public interface ISmuTuningTransport
{
    /// <summary>Reads the exact bounds for the parameter, or null if unavailable.</summary>
    Task<SmuTuningBounds?> ReadBoundsAsync(SmuTuningParameter parameter, CancellationToken cancellationToken);

    /// <summary>Reads the current value of the parameter.</summary>
    Task<SmuTuningState> ReadStateAsync(SmuTuningParameter parameter, CancellationToken cancellationToken);

    /// <summary>Commands the given (already-clamped) limit value.</summary>
    Task SetLimitAsync(SmuTuningParameter parameter, int value, CancellationToken cancellationToken);

    /// <summary>Restores the vendor stock value for every parameter.</summary>
    Task RestoreStockAsync(CancellationToken cancellationToken);
}

/// <summary>Thrown when a request violates a CPU-tuning safety bound or gate.</summary>
public sealed class SmuTuningSafetyException(string message) : Exception(message);
