using System.Runtime.InteropServices;

namespace PCHelper.Adapters;

/// <summary>
/// The real NVML-backed GPU power-limit transport. This is the ONLY place in the
/// codebase that marshals <c>nvmlDeviceSetPowerManagementLimit</c>, and it is
/// triple-gated exactly like the fan transport so that merely constructing or
/// wiring it in cannot issue a physical write:
///
///  1. The setter delegate is marshalled only when <c>enableWrites: true</c>.
///  2. Even then, a write requires either the in-process armed flag (set by the
///     service only after an acknowledged operator action) or the explicit
///     operator env opt-in (<c>PCHELPER_GPUPOWER_REAL_TRANSPORT=1</c>), checked at
///     call time.
///  3. If the driver does not export the setter, writes are impossible and the
///     transport still serves read-only constraint/state queries.
/// </summary>
public sealed class NvmlGpuPowerLimitTransport : IGpuPowerLimitTransport, IDisposable
{
    public const string WriteOptInEnvironmentVariable = "PCHELPER_GPUPOWER_REAL_TRANSPORT";

    private readonly object _gate = new();
    private readonly bool _enableWrites;
    private readonly nint _library;
    private readonly NvmlInit _init;
    private readonly NvmlShutdown _shutdown;
    private readonly NvmlGetHandleByIndex _getHandle;
    private readonly NvmlGetMinMax _getConstraints;
    private readonly NvmlGetUnsigned _getDefaultLimit;
    private readonly NvmlGetUnsigned _getCurrentLimit;
    private readonly NvmlSetUnsigned? _setLimit;
    private bool _initialised;
    private bool _disposed;
    private bool _armed;

    private NvmlGpuPowerLimitTransport(
        bool enableWrites,
        nint library,
        NvmlInit init,
        NvmlShutdown shutdown,
        NvmlGetHandleByIndex getHandle,
        NvmlGetMinMax getConstraints,
        NvmlGetUnsigned getDefaultLimit,
        NvmlGetUnsigned getCurrentLimit,
        NvmlSetUnsigned? setLimit)
    {
        _enableWrites = enableWrites;
        _library = library;
        _init = init;
        _shutdown = shutdown;
        _getHandle = getHandle;
        _getConstraints = getConstraints;
        _getDefaultLimit = getDefaultLimit;
        _getCurrentLimit = getCurrentLimit;
        _setLimit = setLimit;
    }

    /// <summary>
    /// Attempts to load NVML and marshal the power-limit getters (and, only if
    /// <paramref name="enableWrites"/> is true, the setter). Returns false if NVML
    /// or a required entry point is unavailable. Loading with writes enabled still
    /// does not arm a write: the armed flag / env opt-in is checked at call time.
    /// </summary>
    public static bool TryCreate(bool enableWrites, out NvmlGpuPowerLimitTransport transport, out string message)
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
                NvmlGetMinMax getConstraints = MarshalRequired<NvmlGetMinMax>(library, "nvmlDeviceGetPowerManagementLimitConstraints");
                NvmlGetUnsigned getDefault = MarshalRequired<NvmlGetUnsigned>(library, "nvmlDeviceGetPowerManagementDefaultLimit");
                NvmlGetUnsigned getCurrent = MarshalRequired<NvmlGetUnsigned>(library, "nvmlDeviceGetPowerManagementLimit");

                // The setter is marshalled ONLY when writes are enabled; otherwise the
                // callable delegate is never created for this instance.
                NvmlSetUnsigned? setLimit = enableWrites
                    ? MarshalOptional<NvmlSetUnsigned>(library, "nvmlDeviceSetPowerManagementLimit")
                    : null;

                transport = new NvmlGpuPowerLimitTransport(
                    enableWrites, library, init, shutdown, getHandle, getConstraints, getDefault, getCurrent, setLimit);
                transport.Initialise();
                message = enableWrites && setLimit is null
                    ? "NVML loaded but the installed driver does not export the power-limit setter; writes remain unavailable."
                    : "NVML GPU power-limit transport was loaded.";
                return true;
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or InvalidOperationException)
            {
                NativeLibrary.Free(library);
                message = $"NVML is missing a required power-limit entry point: {exception.Message}";
                return false;
            }
        }

        message = "The NVIDIA NVML runtime was not found.";
        return false;
    }

    public Task<GpuPowerLimitBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken)
    {
        nint handle = ResolveHandle(channelId);
        if (_getConstraints(handle, out uint minimum, out uint maximum) != NvmlResult.Success
            || _getDefaultLimit(handle, out uint defaultLimit) != NvmlResult.Success)
        {
            return Task.FromResult<GpuPowerLimitBounds?>(null);
        }

        GpuPowerLimitBounds bounds = new(minimum, maximum, defaultLimit);
        return Task.FromResult<GpuPowerLimitBounds?>(bounds.IsValid ? bounds : null);
    }

    public Task<GpuPowerLimitState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
    {
        nint handle = ResolveHandle(channelId);
        uint? current = _getCurrentLimit(handle, out uint limit) == NvmlResult.Success ? limit : null;
        return Task.FromResult(new GpuPowerLimitState(current));
    }

    public Task SetPowerLimitAsync(string channelId, uint milliwatts, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        nint handle = ResolveHandle(channelId);
        EnsureSuccess(_setLimit!(handle, milliwatts), "nvmlDeviceSetPowerManagementLimit");
        return Task.CompletedTask;
    }

    /// <summary>True when the driver exported a callable power-limit setter for this instance.</summary>
    public bool CanWrite => _setLimit is not null;

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
        if (!_enableWrites || _setLimit is null)
        {
            throw new GpuPowerSafetyException("The GPU power-limit setter is unavailable on this driver; writes are impossible.");
        }

        bool armed;
        lock (_gate)
        {
            armed = _armed;
        }

        if (!armed && Environment.GetEnvironmentVariable(WriteOptInEnvironmentVariable) != "1")
        {
            throw new GpuPowerSafetyException(
                $"GPU power-limit writes require an acknowledged arm or the explicit operator opt-in ({WriteOptInEnvironmentVariable}=1).");
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
        // channelId is the NVML device index (default 0).
        uint deviceIndex = uint.TryParse(channelId, out uint parsed) ? parsed : 0;
        EnsureSuccess(_getHandle(deviceIndex, out nint handle), "nvmlDeviceGetHandleByIndex_v2");
        return handle;
    }

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
    private delegate NvmlResult NvmlGetMinMax(nint handle, out uint minimum, out uint maximum);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlSetUnsigned(nint handle, uint value);
}
