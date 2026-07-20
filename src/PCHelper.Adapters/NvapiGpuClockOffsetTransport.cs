using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;

namespace PCHelper.Adapters;

/// <summary>
/// GPU clock-offset transport backed by NVIDIA NVAPI performance states 2.0 via
/// NvAPIWrapper (LGPL-3.0) — the same driver surface Afterburner-class tools
/// use. Only the P0 (3D performance) frequency delta is ever touched; voltage
/// entries and overvolting settings are never read for writing, constructed, or
/// submitted, in keeping with the repository rule that RigPilot never raises a
/// voltage. Reads are side-effect free; <see cref="SetOffsetAsync"/> is the only
/// member that calls <c>SetPerformanceStates20</c>, and it proceeds only while
/// the transport is explicitly armed (or the operator env opt-in is set).
/// </summary>
public sealed class NvapiGpuClockOffsetTransport : IGpuClockOffsetTransport, IDisposable
{
    public const string WriteOptInEnvironmentVariable = "PCHELPER_GPUCLOCK_REAL_TRANSPORT";

    private readonly object _gate = new();
    private readonly PhysicalGPU _gpu;
    private readonly bool _enableWrites;
    private bool _armed;
    private bool _disposed;

    private NvapiGpuClockOffsetTransport(PhysicalGPU gpu, bool enableWrites)
    {
        _gpu = gpu;
        _enableWrites = enableWrites;
    }

    /// <summary>
    /// Initialises NVAPI and binds to the requested GPU index. Returns false when
    /// NVAPI is unavailable or the driver marks the performance states non-editable.
    /// No write occurs here or anywhere else before an explicit arm.
    /// </summary>
    public static bool TryCreate(uint gpuIndex, bool enableWrites, out NvapiGpuClockOffsetTransport transport, out string message)
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
            IPerformanceStates20Info info = GPUApi.GetPerformanceStates20(gpu.Handle);
            if (!info.IsEditable)
            {
                message = "The NVIDIA driver reports its performance states as non-editable; clock offsets stay unavailable.";
                return false;
            }

            transport = new NvapiGpuClockOffsetTransport(gpu, enableWrites);
            message = "NVAPI GPU clock-offset transport was loaded.";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            message = $"NVAPI is unavailable: {exception.GetType().Name}.";
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

    public Task<GpuClockOffsetBounds?> ReadBoundsAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken)
    {
        IPerformanceStates20ClockEntry? entry = FindP0Entry(domain);
        if (entry is null || !entry.IsEditable)
        {
            return Task.FromResult<GpuClockOffsetBounds?>(null);
        }

        PerformanceStates20ParameterDelta delta = entry.FrequencyDeltaInkHz;
        return Task.FromResult<GpuClockOffsetBounds?>(
            new GpuClockOffsetBounds(delta.DeltaRange.Minimum, delta.DeltaRange.Maximum));
    }

    public Task<GpuClockOffsetState> ReadStateAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken)
    {
        IPerformanceStates20ClockEntry? entry = FindP0Entry(domain);
        return Task.FromResult(new GpuClockOffsetState(entry?.FrequencyDeltaInkHz.DeltaValue));
    }

    public Task SetOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        return WriteOffsetAsync(domain, offsetKiloHertz, cancellationToken);
    }

    /// <summary>
    /// Restores a captured offset without requiring an armed transport. Every
    /// other guard — transport enabled, not disposed, editable P0 entry, driver
    /// delta range — still applies; only the arm gate is bypassed, because a
    /// restore returns hardware toward its prior state and refusing one leaves
    /// the machine stranded in whatever state tuning last applied.
    /// </summary>
    public Task RestoreOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
    {
        if (!_enableWrites || _disposed)
        {
            throw new GpuClockSafetyException("The GPU clock-offset transport is not write-enabled.");
        }

        return WriteOffsetAsync(domain, offsetKiloHertz, cancellationToken);
    }

    private Task WriteOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
    {
        IPerformanceStates20ClockEntry entry = FindP0Entry(domain)
            ?? throw new GpuClockSafetyException($"The NVIDIA driver exposes no editable P0 {domain} clock entry.");
        if (!entry.IsEditable)
        {
            throw new GpuClockSafetyException($"The NVIDIA driver marks the P0 {domain} clock entry as non-editable.");
        }
        PerformanceStates20ParameterDelta current = entry.FrequencyDeltaInkHz;
        if (offsetKiloHertz < current.DeltaRange.Minimum || offsetKiloHertz > current.DeltaRange.Maximum)
        {
            throw new GpuClockSafetyException(
                $"Offset {offsetKiloHertz} kHz is outside the driver delta range [{current.DeltaRange.Minimum}, {current.DeltaRange.Maximum}] kHz.");
        }

        // Submit exactly one clock entry for P0 — no voltage entries, no
        // overvolting settings, no other pstates. The V1 payload cannot even
        // carry an overvolting section.
        PerformanceStates20ClockEntryV1 clock = new(
            ToPublicDomain(domain),
            new PerformanceStates20ParameterDelta(offsetKiloHertz));
        PerformanceStates20InfoV1.PerformanceState20 state = new(
            PerformanceStateId.P0_3DPerformance,
            [clock],
            []);
        PerformanceStates20InfoV1 payload = new([state], 1, 0);
        try
        {
            GPUApi.SetPerformanceStates20(_gpu.Handle, payload);
        }
        catch (Exception exception)
        {
            // A bare driver status ("NVAPI_INVALID_USER_PRIVILEGE") names neither
            // the domain nor the value, so an Auto OC failure cannot be told apart
            // from a manual one, and the adapter trace that would carry the detail
            // is a bounded buffer that per-second sensor polling flushes within
            // seconds. Carry the whole request in the message that reaches the
            // durable operation record.
            throw new GpuClockWriteException(
                $"NVAPI refused a {domain} clock write of {offsetKiloHertz} kHz "
                + $"({ToMegaHertzText(offsetKiloHertz)}), driver delta range "
                + $"[{current.DeltaRange.Minimum}, {current.DeltaRange.Maximum}] kHz: {exception.Message}",
                exception);
        }

        return Task.CompletedTask;
    }

    private static string ToMegaHertzText(int offsetKiloHertz) =>
        $"{offsetKiloHertz / 1000d:0.###} MHz";

    private IPerformanceStates20ClockEntry? FindP0Entry(GpuClockOffsetDomain domain)
    {
        IPerformanceStates20Info info = GPUApi.GetPerformanceStates20(_gpu.Handle);
        PublicClockDomain publicDomain = ToPublicDomain(domain);
        if (!info.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out IPerformanceStates20ClockEntry[]? entries))
        {
            return null;
        }

        return entries.FirstOrDefault(entry => entry.DomainId == publicDomain);
    }

    private static PublicClockDomain ToPublicDomain(GpuClockOffsetDomain domain) => domain switch
    {
        GpuClockOffsetDomain.Core => PublicClockDomain.Graphics,
        GpuClockOffsetDomain.Memory => PublicClockDomain.Memory,
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unsupported clock domain."),
    };

    private void EnsureWriteArmed()
    {
        if (!_enableWrites || _disposed)
        {
            throw new GpuClockSafetyException("The GPU clock-offset transport is not write-enabled.");
        }

        bool armed;
        lock (_gate)
        {
            armed = _armed;
        }

        if (!armed && Environment.GetEnvironmentVariable(WriteOptInEnvironmentVariable) != "1")
        {
            throw new GpuClockSafetyException(
                $"GPU clock-offset writes require an acknowledged arm or the explicit operator opt-in ({WriteOptInEnvironmentVariable}=1).");
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
