using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace PCHelper.Adapters;

/// <summary>
/// GPU power-limit transport backed by NVIDIA NVAPI client power policies via
/// NvAPIWrapper (LGPL-3.0) — the same surface Afterburner-class tools use, and
/// the one the clock-offset control already writes successfully on this GPU.
/// </summary>
/// <remarks>
/// <para>
/// NVML's <c>nvmlDeviceSetPowerManagementLimit</c> returns NVML_ERROR_NO_PERMISSION
/// on this class of GeForce card, so the NVML transport reported valid constraints
/// yet every write was refused. NVAPI expresses the P0 power target in
/// <c>PCM</c> (per-cent-mille — percentage of the default TDP × 1000, so 100% =
/// 100000), not absolute watts. The <see cref="IGpuPowerLimitTransport"/> contract
/// is milliwatts, so a milliwatt anchor for the 100% point is required: the default
/// TDP in milliwatts, which is read reliably from NVML (only NVML <em>writes</em>
/// are refused; reads work) and injected here. Everything else — bounds, current
/// target, and the write — goes through NVAPI, so read-back after a write is
/// self-consistent in one API.
/// </para>
/// <para>
/// Only the P0 (3D performance) entry is read or written; no voltage or other
/// pstate is touched, in keeping with the repository rule that RigPilot never
/// raises a voltage. Writes proceed only while the transport is explicitly armed
/// (or the operator env opt-in is set). A power limit is a driver-enforced cap: it
/// cannot destabilise the GPU the way a clock offset can, and a refused or clamped
/// write fails closed.
/// </para>
/// </remarks>
public sealed class NvApiGpuPowerLimitTransport : IGpuPowerLimitTransport
{
    public const string WriteOptInEnvironmentVariable = NvmlGpuPowerLimitTransport.WriteOptInEnvironmentVariable;

    private readonly object _gate = new();
    private readonly PhysicalGPU _gpu;
    private readonly uint _defaultTdpMilliwatts;
    private readonly bool _enableWrites;
    private bool _armed;
    private bool _disposed;

    private NvApiGpuPowerLimitTransport(PhysicalGPU gpu, uint defaultTdpMilliwatts, bool enableWrites)
    {
        _gpu = gpu;
        _defaultTdpMilliwatts = defaultTdpMilliwatts;
        _enableWrites = enableWrites;
    }

    /// <summary>
    /// Initialises NVAPI, binds to the requested GPU, and confirms it exposes a P0
    /// client power policy. <paramref name="defaultTdpMilliwatts"/> anchors the
    /// PCM↔milliwatt conversion (the milliwatt value of the 100% point) and must be
    /// a real, non-zero default TDP — read it from NVML. No write occurs here.
    /// </summary>
    public static bool TryCreate(
        uint gpuIndex,
        uint defaultTdpMilliwatts,
        bool enableWrites,
        out NvApiGpuPowerLimitTransport transport,
        out string message)
    {
        transport = null!;
        if (defaultTdpMilliwatts == 0)
        {
            message = "A non-zero default TDP anchor is required to express NVAPI PCM power limits in milliwatts.";
            return false;
        }

        try
        {
            NVIDIA.Initialize();
            PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
            if (gpuIndex >= gpus.Length)
            {
                message = $"NVAPI found {gpus.Length} GPU(s); index {gpuIndex} is out of range.";
                return false;
            }

            PhysicalGPU gpu = gpus[gpuIndex];
            if (FindP0Info(gpu) is null)
            {
                message = "The NVIDIA driver exposes no P0 client power policy for control.";
                return false;
            }

            transport = new NvApiGpuPowerLimitTransport(gpu, defaultTdpMilliwatts, enableWrites);
            message = "NVAPI GPU power-limit transport was loaded.";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            message = $"NVAPI power policies are unavailable: {exception.GetType().Name}.";
            return false;
        }
    }

    public bool CanWrite => _enableWrites && !_disposed;

    /// <summary>Arms or disarms live writes. Set only after an acknowledged operator action.</summary>
    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
        }
    }

    public Task<GpuPowerLimitBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken)
    {
        PrivatePowerPoliciesInfoV1.PowerPolicyInfoEntry? p0 = FindP0Info(_gpu);
        if (p0 is not { } info)
        {
            return Task.FromResult<GpuPowerLimitBounds?>(null);
        }

        // The default TDP (from NVML) is the milliwatt value of the DefaultPowerInPCM
        // point; min and max scale from it by their PCM ratio.
        uint defaultPcm = info.DefaultPowerInPCM;
        GpuPowerLimitBounds bounds = new(
            PcmToMilliwatts(info.MinimumPowerInPCM, defaultPcm, _defaultTdpMilliwatts),
            PcmToMilliwatts(info.MaximumPowerInPCM, defaultPcm, _defaultTdpMilliwatts),
            PcmToMilliwatts(defaultPcm, defaultPcm, _defaultTdpMilliwatts));
        return Task.FromResult<GpuPowerLimitBounds?>(bounds);
    }

    public Task<GpuPowerLimitState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
    {
        uint? currentPcm = FindP0Target(_gpu);
        uint? defaultPcm = FindP0Info(_gpu)?.DefaultPowerInPCM;
        uint? milliwatts = currentPcm is uint pcm && defaultPcm is uint reference
            ? PcmToMilliwatts(pcm, reference, _defaultTdpMilliwatts)
            : null;
        return Task.FromResult(new GpuPowerLimitState(milliwatts));
    }

    public Task SetPowerLimitAsync(string channelId, uint milliwatts, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        PrivatePowerPoliciesInfoV1.PowerPolicyInfoEntry info = FindP0Info(_gpu)
            ?? throw new GpuPowerSafetyException("The NVIDIA driver exposes no P0 client power policy to write.");

        uint targetPcm = MilliwattsToPcm(milliwatts, info.DefaultPowerInPCM, _defaultTdpMilliwatts);
        // Defence in depth: the driver's own PCM window is the final clamp, so a
        // milliwatt value that slipped past the adapter cannot ask for a target the
        // driver never advertised.
        uint clampedPcm = Math.Clamp(targetPcm, info.MinimumPowerInPCM, info.MaximumPowerInPCM);

        PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry entry = new(clampedPcm);
        PrivatePowerPoliciesStatusV1 status = new([entry]);
        lock (_gate)
        {
            GPUApi.ClientPowerPoliciesSetStatus(_gpu.Handle, status);
        }

        return Task.CompletedTask;
    }

    /// <summary>Milliwatts for a PCM value, given the milliwatt value of the default point.</summary>
    internal static uint PcmToMilliwatts(uint pcm, uint defaultPcm, uint defaultTdpMilliwatts) =>
        defaultPcm == 0 ? 0u : (uint)Math.Round((double)pcm * defaultTdpMilliwatts / defaultPcm);

    /// <summary>PCM for a milliwatt value, the inverse of <see cref="PcmToMilliwatts"/>.</summary>
    internal static uint MilliwattsToPcm(uint milliwatts, uint defaultPcm, uint defaultTdpMilliwatts) =>
        defaultTdpMilliwatts == 0 ? 0u : (uint)Math.Round((double)milliwatts * defaultPcm / defaultTdpMilliwatts);

    private static PrivatePowerPoliciesInfoV1.PowerPolicyInfoEntry? FindP0Info(PhysicalGPU gpu)
    {
        PrivatePowerPoliciesInfoV1 info = GPUApi.ClientPowerPoliciesGetInfo(gpu.Handle);
        foreach (PrivatePowerPoliciesInfoV1.PowerPolicyInfoEntry entry in info.PowerPolicyInfoEntries)
        {
            if (entry.PerformanceStateId == PerformanceStateId.P0_3DPerformance)
            {
                return entry;
            }
        }

        return null;
    }

    private static uint? FindP0Target(PhysicalGPU gpu)
    {
        PrivatePowerPoliciesStatusV1 status = GPUApi.ClientPowerPoliciesGetStatus(gpu.Handle);
        foreach (PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry entry in status.PowerPolicyStatusEntries)
        {
            if (entry.PerformanceStateId == PerformanceStateId.P0_3DPerformance)
            {
                return entry.PowerTargetInPCM;
            }
        }

        return null;
    }

    private void EnsureWriteArmed()
    {
        if (!_enableWrites || _disposed)
        {
            throw new GpuPowerSafetyException("The GPU power-limit transport is not write-enabled.");
        }

        bool armed;
        lock (_gate)
        {
            armed = _armed;
        }

        if (!armed && Environment.GetEnvironmentVariable(WriteOptInEnvironmentVariable) != "1")
        {
            throw new GpuPowerSafetyException("GPU power-limit writes require an acknowledged arm.");
        }
    }

    public void Dispose() => _disposed = true;
}
