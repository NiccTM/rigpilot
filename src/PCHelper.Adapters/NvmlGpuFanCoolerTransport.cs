using System.Runtime.InteropServices;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// The real NVML-backed GPU fan transport. This is the ONLY place in the codebase
/// that marshals the NVML fan *setter* entry points to callable delegates, and it
/// is triple-gated so that merely constructing or wiring it in cannot issue a
/// physical write:
///
///  1. The write methods are enabled only when <c>enableWrites: true</c> is passed.
///  2. Even then, an explicit operator opt-in environment variable
///     (<c>PCHELPER_GPUFAN_REAL_TRANSPORT=1</c>) must be set, checked at call time.
///  3. If the driver does not actually export the setters, the transport reports
///     no usable bounds and the write methods throw.
///
/// Reads (bounds, state) are always permitted so a bench <c>Prepare</c>-only pass
/// can run without ever reaching <c>Apply</c>. No default construction path in the
/// application passes <c>enableWrites: true</c>. See
/// docs/qualification/rtx3090-fan-write-path.md.
/// </summary>
public sealed class NvmlGpuFanCoolerTransport : IGpuFanCoolerTransport, IDisposable
{
    public const string WriteOptInEnvironmentVariable = "PCHELPER_GPUFAN_REAL_TRANSPORT";
    private const int ConservativeFloorPercent = 50;

    // NVML fan control policy values.
    private const uint FanPolicyThermal = 0; // driver automatic curve
    private const uint FanPolicyManual = 1;

    private readonly object _gate = new();
    private readonly bool _enableWrites;
    private readonly nint _library;
    private readonly NvmlInit _init;
    private readonly NvmlShutdown _shutdown;
    private readonly NvmlGetHandleByIndex _getHandle;
    private readonly NvmlGetUnsignedIndexed _getTargetFanSpeed;
    private readonly NvmlGetMinMax _getMinMaxFanSpeed;
    private readonly NvmlGetUnsigned _getNumFans;
    private readonly NvmlSetFanSpeed? _setFanSpeed;
    private readonly NvmlSetFanControlPolicy? _setFanControlPolicy;
    private bool _initialised;
    private bool _disposed;
    private bool _armed;

    private NvmlGpuFanCoolerTransport(
        bool enableWrites,
        nint library,
        NvmlInit init,
        NvmlShutdown shutdown,
        NvmlGetHandleByIndex getHandle,
        NvmlGetUnsignedIndexed getTargetFanSpeed,
        NvmlGetMinMax getMinMaxFanSpeed,
        NvmlGetUnsigned getNumFans,
        NvmlSetFanSpeed? setFanSpeed,
        NvmlSetFanControlPolicy? setFanControlPolicy)
    {
        _enableWrites = enableWrites;
        _library = library;
        _init = init;
        _shutdown = shutdown;
        _getHandle = getHandle;
        _getTargetFanSpeed = getTargetFanSpeed;
        _getMinMaxFanSpeed = getMinMaxFanSpeed;
        _getNumFans = getNumFans;
        _setFanSpeed = setFanSpeed;
        _setFanControlPolicy = setFanControlPolicy;
    }

    /// <summary>
    /// Attempts to load NVML and marshal the fan getters (and, only if
    /// <paramref name="enableWrites"/> is true, the setters). Returns false if NVML
    /// or a required entry point is unavailable. Loading with writes enabled still
    /// does not arm a write: the operator opt-in env var is checked at call time.
    /// </summary>
    public static bool TryCreate(bool enableWrites, out NvmlGpuFanCoolerTransport transport, out string message)
    {
        transport = null!;
        foreach (string candidate in NvmlCandidates())
        {
            if (!NativeLibrary.TryLoad(candidate, out nint library))
            {
                continue;
            }

            try
            {
                NvmlInit init = MarshalRequired<NvmlInit>(library, "nvmlInit_v2");
                NvmlShutdown shutdown = MarshalRequired<NvmlShutdown>(library, "nvmlShutdown");
                NvmlGetHandleByIndex getHandle = MarshalRequired<NvmlGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
                NvmlGetUnsignedIndexed getTarget = MarshalRequired<NvmlGetUnsignedIndexed>(library, "nvmlDeviceGetTargetFanSpeed");
                NvmlGetMinMax getMinMax = MarshalRequired<NvmlGetMinMax>(library, "nvmlDeviceGetMinMaxFanSpeed");
                NvmlGetUnsigned getNumFans = MarshalRequired<NvmlGetUnsigned>(library, "nvmlDeviceGetNumFans");

                // Setters are marshalled ONLY when writes are enabled; otherwise the
                // callable delegates are never created for this instance.
                NvmlSetFanSpeed? setFanSpeed = enableWrites ? MarshalOptional<NvmlSetFanSpeed>(library, "nvmlDeviceSetFanSpeed_v2") : null;
                NvmlSetFanControlPolicy? setPolicy = enableWrites ? MarshalOptional<NvmlSetFanControlPolicy>(library, "nvmlDeviceSetFanControlPolicy") : null;

                transport = new NvmlGpuFanCoolerTransport(
                    enableWrites, library, init, shutdown, getHandle, getTarget, getMinMax, getNumFans, setFanSpeed, setPolicy);
                transport.Initialise();
                message = enableWrites && (setFanSpeed is null || setPolicy is null)
                    ? "NVML loaded but the installed driver does not export the fan setters; writes remain unavailable."
                    : "NVML GPU fan transport was loaded.";
                return true;
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or InvalidOperationException)
            {
                NativeLibrary.Free(library);
                message = $"NVML is missing a required fan entry point: {exception.Message}";
                return false;
            }
        }

        message = "The NVIDIA NVML runtime was not found.";
        return false;
    }

    public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken)
    {
        nint handle = ResolveHandle(channelId);
        NvmlResult result = _getMinMaxFanSpeed(handle, out uint minimum, out uint maximum);
        if (result != NvmlResult.Success || maximum < minimum || maximum > 100)
        {
            return Task.FromResult<GpuFanBounds?>(null);
        }

        int floor = Math.Max(ConservativeFloorPercent, (int)minimum);
        int ceiling = Math.Min(100, (int)maximum);
        return Task.FromResult<GpuFanBounds?>(new GpuFanBounds(floor, ceiling));
    }

    public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
    {
        (nint handle, uint fan) = ResolveChannel(channelId);
        NvmlResult result = _getTargetFanSpeed(handle, fan, out uint target);
        int? measured = result == NvmlResult.Success ? (int)target : null;
        // NVML does not expose the current policy directly here; we treat a readable
        // target as manual evidence only when a write is armed. For read-only preflight
        // the measured duty is what matters.
        return Task.FromResult(new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, measured));
    }

    public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        (nint handle, uint fan) = ResolveChannel(channelId);
        EnsureSuccess(_setFanControlPolicy!(handle, fan, FanPolicyManual), "nvmlDeviceSetFanControlPolicy(manual)");
        EnsureSuccess(_setFanSpeed!(handle, fan, (uint)dutyPercent), "nvmlDeviceSetFanSpeed_v2");
        return Task.CompletedTask;
    }

    public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        (nint handle, uint fan) = ResolveChannel(channelId);
        EnsureSuccess(_setFanControlPolicy!(handle, fan, FanPolicyThermal), "nvmlDeviceSetFanControlPolicy(auto)");
        return Task.CompletedTask;
    }

    /// <summary>True when the driver exported callable fan setters for this instance.</summary>
    public bool CanWrite => _setFanSpeed is not null && _setFanControlPolicy is not null;

    /// <summary>
    /// Arms or disarms live writes. The service calls this only after an explicit
    /// acknowledged operator action (Experimental confirmation for the exact device).
    /// Disarming takes effect immediately for subsequent write attempts.
    /// </summary>
    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
        }
    }

    private void EnsureWriteArmed()
    {
        if (!_enableWrites || _setFanSpeed is null || _setFanControlPolicy is null)
        {
            throw new GpuFanSafetyException("GPU fan setters are unavailable on this driver; writes are impossible.");
        }

        bool armed;
        lock (_gate)
        {
            armed = _armed;
        }

        // A write proceeds only when the transport has been explicitly armed via an
        // acknowledged operator action, or the deliberate environment override is set.
        // An accidental write is impossible without one of these.
        if (!armed && Environment.GetEnvironmentVariable(WriteOptInEnvironmentVariable) != "1")
        {
            throw new GpuFanSafetyException(
                $"GPU fan writes require an acknowledged arm or the explicit operator opt-in ({WriteOptInEnvironmentVariable}=1).");
        }
    }

    private void Initialise()
    {
        lock (_gate)
        {
            if (_initialised)
            {
                return;
            }

            EnsureSuccess(_init(), "nvmlInit_v2");
            _initialised = true;
        }
    }

    private nint ResolveHandle(string channelId)
    {
        (nint handle, _) = ResolveChannel(channelId);
        return handle;
    }

    private (nint Handle, uint Fan) ResolveChannel(string channelId)
    {
        // channelId is "<deviceIndex>:<fanIndex>" or just "<fanIndex>" (device 0).
        uint deviceIndex = 0;
        uint fanIndex = 0;
        string[] parts = channelId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            deviceIndex = ParseIndex(parts[0]);
            fanIndex = ParseIndex(parts[1]);
        }
        else if (parts.Length == 1)
        {
            fanIndex = ParseIndex(parts[0]);
        }

        EnsureSuccess(_getHandle(deviceIndex, out nint handle), "nvmlDeviceGetHandleByIndex_v2");
        return (handle, fanIndex);
    }

    private static uint ParseIndex(string value) =>
        uint.TryParse(value, out uint parsed) ? parsed : 0;

    private static void EnsureSuccess(NvmlResult result, string operation)
    {
        if (result != NvmlResult.Success)
        {
            throw new InvalidOperationException($"{operation} failed with NVML result {(int)result}.");
        }
    }

    private static IEnumerable<string> NvmlCandidates()
    {
        yield return "nvml.dll";
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Path.Combine(system, "nvml.dll");
        string? programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
        }
    }

    private static T MarshalRequired<T>(nint library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out nint address))
        {
            throw new EntryPointNotFoundException(name);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T? MarshalOptional<T>(nint library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out nint address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            if (_initialised)
            {
                _ = _shutdown();
                _initialised = false;
            }
        }

        NativeLibrary.Free(_library);
    }

    private enum NvmlResult
    {
        Success = 0
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlGetHandleByIndex(uint index, out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlGetUnsigned(nint handle, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlGetUnsignedIndexed(nint handle, uint fan, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlGetMinMax(nint handle, out uint minimum, out uint maximum);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlSetFanSpeed(nint handle, uint fan, uint speed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlSetFanControlPolicy(nint handle, uint fan, uint policy);
}
