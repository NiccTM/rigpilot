using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.GPU;

namespace PCHelper.Adapters;

/// <summary>
/// GPU fan transport backed by NVIDIA NVAPI via the NvAPIWrapper library
/// (LGPL-3.0). NVAPI's cooler API is the standard NVIDIA fan-write path and
/// complements the hand-rolled NVML transport. Writes are gated identically to
/// <see cref="NvmlGpuFanCoolerTransport"/>: they proceed only when the instance
/// has been explicitly armed via an acknowledged operator action (or the env
/// override). Constructing or wiring this transport never issues a fan write.
/// </summary>
public sealed class NvApiGpuFanCoolerTransport : IGpuFanCoolerTransport, IDisposable
{
    public const string WriteOptInEnvironmentVariable = NvmlGpuFanCoolerTransport.WriteOptInEnvironmentVariable;
    private const int ConservativeFloorPercent = 50;

    private readonly object _gate = new();
    private readonly PhysicalGPU _gpu;
    private bool _armed;
    private bool _disposed;

    private NvApiGpuFanCoolerTransport(PhysicalGPU gpu) => _gpu = gpu;

    /// <summary>
    /// Initialises NVAPI and binds to the requested GPU index. Returns false when
    /// NVAPI, an NVIDIA GPU, or a controllable cooler is unavailable. No write occurs.
    /// </summary>
    public static bool TryCreate(uint gpuIndex, out NvApiGpuFanCoolerTransport transport, out string message)
    {
        transport = null!;
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
            if (!gpu.CoolerInformation.Coolers.Any())
            {
                message = "The NVIDIA GPU exposes no NVAPI cooler for control.";
                return false;
            }

            transport = new NvApiGpuFanCoolerTransport(gpu);
            message = "NVAPI GPU fan transport was loaded.";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            message = $"NVAPI is unavailable: {exception.GetType().Name}.";
            return false;
        }
    }

    /// <summary>NVAPI cooler control is available while this instance is live.</summary>
    public bool CanWrite => !_disposed;

    /// <summary>Arms or disarms live writes. Set only after an acknowledged operator action.</summary>
    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
        }
    }

    public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken)
    {
        GPUCooler? cooler = FindCooler(channelId);
        if (cooler is null)
        {
            return Task.FromResult<GpuFanBounds?>(null);
        }

        int minimum = Math.Max(ConservativeFloorPercent, cooler.CurrentMinimumLevel);
        int maximum = Math.Min(100, cooler.CurrentMaximumLevel);
        return Task.FromResult<GpuFanBounds?>(new GpuFanBounds(minimum, maximum));
    }

    public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
    {
        GPUCooler? cooler = FindCooler(channelId);
        if (cooler is null)
        {
            return Task.FromResult(new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null));
        }

        GpuFanControlPolicy policy = cooler.CurrentPolicy == CoolerPolicy.Manual
            ? GpuFanControlPolicy.Manual
            : GpuFanControlPolicy.Automatic;
        int? commanded = policy == GpuFanControlPolicy.Manual ? cooler.CurrentLevel : null;
        return Task.FromResult(new GpuFanChannelState(policy, commanded, cooler.CurrentLevel));
    }

    public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        GPUCooler cooler = FindCooler(channelId)
            ?? throw new GpuFanSafetyException($"NVAPI cooler '{channelId}' is unavailable.");
        _gpu.CoolerInformation.SetCoolerSettings(cooler.CoolerId, CoolerPolicy.Manual, dutyPercent);
        return Task.CompletedTask;
    }

    public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        _gpu.CoolerInformation.RestoreCoolerSettingsToDefault();
        return Task.CompletedTask;
    }

    private GPUCooler? FindCooler(string channelId)
    {
        GPUCooler[] coolers = _gpu.CoolerInformation.Coolers.ToArray();
        if (coolers.Length == 0)
        {
            return null;
        }

        // channelId is a cooler index into the discovered coolers; default to the first.
        int index = int.TryParse(channelId.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out int parsed)
            ? parsed
            : 0;
        return index >= 0 && index < coolers.Length ? coolers[index] : coolers[0];
    }

    private void EnsureWriteArmed()
    {
        bool armed;
        lock (_gate)
        {
            armed = _armed;
        }

        if (!armed && Environment.GetEnvironmentVariable(WriteOptInEnvironmentVariable) != "1")
        {
            throw new GpuFanSafetyException(
                $"GPU fan writes require an acknowledged arm or the explicit operator opt-in ({WriteOptInEnvironmentVariable}=1).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            NVIDIA.Unload();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // NVAPI unload is best-effort; the process boundary is the real cleanup.
        }
    }
}
