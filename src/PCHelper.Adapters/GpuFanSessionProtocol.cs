namespace PCHelper.Adapters;

/// <summary>
/// The wire request for the out-of-process GPU-fan session helper. A single
/// op-dispatched message keeps the IPC surface minimal: the helper holds the
/// NVAPI session for its whole lifetime, and the service's
/// <c>RemoteGpuFanCoolerTransport</c> forwards each <see cref="IGpuFanCoolerTransport"/>
/// call as one of these.
/// </summary>
public sealed record GpuFanSessionRequest(
    string Op,
    string ChannelId,
    int DutyPercent,
    bool Armed);

/// <summary>
/// The wire result. <see cref="Refused"/> is set true only when an in-session
/// restore was attempted and every documented NVAPI path was refused — the
/// signal the service uses to escalate to recycling the helper process, whose
/// death releases the NVAPI session so the driver reclaims the fan.
/// </summary>
public sealed record GpuFanSessionResult(
    bool Ok,
    bool Refused,
    string Message,
    GpuFanChannelState? State,
    GpuFanBounds? Bounds);

/// <summary>Op discriminators for <see cref="GpuFanSessionRequest.Op"/>.</summary>
public static class GpuFanSessionOps
{
    public const string Ping = "ping";
    public const string ReadBounds = "read-bounds";
    public const string ReadState = "read-state";
    public const string SetManual = "set-manual";
    public const string Restore = "restore";
    public const string SetArmed = "set-armed";
}
