namespace PCHelper.Adapters;

/// <summary>
/// Wire request for the out-of-process GPU power-limit session helper. Mirrors the
/// fan session's op-dispatched shape so the three families share one child-process
/// contract style.
/// </summary>
public sealed record GpuPowerSessionRequest(
    string Op,
    string ChannelId,
    uint Milliwatts,
    bool Armed);

/// <summary>
/// Wire result for the power session. <see cref="Refused"/> marks a write the
/// driver rejected (as opposed to a transport fault), which is the service's signal
/// to recycle the helper for a fresh NVAPI session and retry once.
/// </summary>
public sealed record GpuPowerSessionResult(
    bool Ok,
    bool Refused,
    string Message,
    GpuPowerLimitState? State,
    GpuPowerLimitBounds? Bounds);

/// <summary>Op discriminators for <see cref="GpuPowerSessionRequest.Op"/>.</summary>
public static class GpuPowerSessionOps
{
    public const string Ping = "ping";
    public const string ReadBounds = "read-bounds";
    public const string ReadState = "read-state";
    public const string SetLimit = "set-limit";
    public const string SetArmed = "set-armed";
}

/// <summary>Wire request for the out-of-process GPU clock-offset session helper.</summary>
public sealed record GpuClockSessionRequest(
    string Op,
    GpuClockOffsetDomain Domain,
    int OffsetKiloHertz,
    bool Armed);

/// <summary>Wire result for the clock session.</summary>
public sealed record GpuClockSessionResult(
    bool Ok,
    bool Refused,
    string Message,
    GpuClockOffsetState? State,
    GpuClockOffsetBounds? Bounds);

/// <summary>Op discriminators for <see cref="GpuClockSessionRequest.Op"/>.</summary>
public static class GpuClockSessionOps
{
    public const string Ping = "ping";
    public const string ReadBounds = "read-bounds";
    public const string ReadState = "read-state";
    public const string SetOffset = "set-offset";
    public const string RestoreOffset = "restore-offset";
    public const string SetArmed = "set-armed";
}
