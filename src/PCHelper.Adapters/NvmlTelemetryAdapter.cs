using System.Runtime.InteropServices;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only NVML telemetry and bound discovery. NVML load and driver calls
/// are isolated to this built-in adapter; it never exposes clock, voltage,
/// power, or fan writes. Public NVML bounds are surfaced so a later,
/// separately-qualified control adapter cannot guess values.
/// </summary>
public sealed class NvmlTelemetryAdapter : IHardwareAdapter
{
    private const string AdapterId = "nvidia.nvml";
    private const uint TemperatureGpu = 0;
    private const uint ClockGraphics = 0;
    private const uint ClockMemory = 2;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NvmlApi? _api;
    private string? _availabilityMessage;
    private string? _lastError;

    public AdapterManifest Manifest { get; } = new(
        AdapterId,
        "NVIDIA Management Library telemetry",
        "0.4.0-alpha",
        "NVIDIA NVML runtime supplied by the installed display driver",
        "NVIDIA display driver",
        AdapterExecutionContext.SystemService,
        ["NVIDIA GPU with installed NVML runtime"],
        ["Telemetry", "CoolingCapabilityDiscovery", "PowerCapabilityDiscovery", "ClockOffsetCapabilityDiscovery", "GpuWritesSafetyLocked"]);

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryOpen(out NvmlApi api))
            {
                string message = _availabilityMessage ?? "The NVML runtime could not be initialised.";
                return new AdapterProbeResult(Manifest, [], [],
                [
                    new DiagnosticWarning(
                        "NVML_TELEMETRY_UNAVAILABLE",
                        "Information",
                        $"NVIDIA NVML telemetry is unavailable: {message}",
                        "RigPilot will continue with other read-only sensor adapters.")
                ]);
            }

            List<HardwareDevice> devices = [];
            List<CapabilityDescriptor> capabilities = [];
            foreach (NvmlDevice device in api.EnumerateDevices())
            {
                string deviceId = $"nvidia:{SanitiseId(device.Uuid)}";
                devices.Add(new HardwareDevice(
                    deviceId,
                    device.Name,
                    DeviceKind.Gpu,
                    "NVIDIA",
                    device.Name,
                    device.Uuid,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["uuid"] = device.Uuid,
                        ["driverVersion"] = api.DriverVersion
                    }));
                capabilities.Add(new CapabilityDescriptor(
                    $"nvml.telemetry:{deviceId}",
                    Manifest.Id,
                    deviceId,
                    "NVIDIA telemetry and cooling endpoint",
                    CapabilityAccessState.ReadOnly,
                    AdapterExecutionContext.SystemService,
                    ControlValueKind.Numeric,
                    null,
                    null,
                    RiskLevel.Guarded,
                    EvidenceLevel.Detected,
                    null,
                    $"NVML telemetry is available for this exact driver ({api.DriverVersion}). RigPilot does not expose NVIDIA tuning or direct fan writes until apply, read-back, reset, driver gate, and physical safety tests pass for the exact board.",
                    CanResetToDefault: false,
                    Domain: ControlDomain.Gpu));
                AddDiscoveredBounds(capabilities, api, device, deviceId);
            }
            return new AdapterProbeResult(Manifest, devices, capabilities, []);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            _lastError = exception.Message;
            return new AdapterProbeResult(Manifest, [], [],
            [
                new DiagnosticWarning(
                    "NVML_TELEMETRY_UNAVAILABLE",
                    "Information",
                    $"NVIDIA NVML telemetry is unavailable: {exception.Message}",
                    "RigPilot will continue with other read-only sensor adapters.")
            ]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryOpen(out NvmlApi api))
            {
                return [];
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<SensorSample> samples = [];
            foreach (NvmlDevice device in api.EnumerateDevices())
            {
                string deviceId = $"nvidia:{SanitiseId(device.Uuid)}";
                AddSample(samples, "temperature", "GPU temperature", api.TryGetTemperature(device.Handle, TemperatureGpu), "\u00B0C", deviceId, now);
                AddSample(samples, "fan", "GPU fan", api.TryGetFanSpeed(device.Handle), "%", deviceId, now);
                AddSample(samples, "power", "GPU board power", api.TryGetPowerWatts(device.Handle), "W", deviceId, now);
                AddSample(samples, "graphics-clock", "GPU core clock", api.TryGetClock(device.Handle, ClockGraphics), "MHz", deviceId, now);
                AddSample(samples, "memory-clock", "GPU memory clock", api.TryGetClock(device.Handle, ClockMemory), "MHz", deviceId, now);
                AddSample(samples, "power-limit", "GPU power limit", api.TryGetPowerLimitWatts(device.Handle), "W", deviceId, now);
                AddSample(samples, "power-default-limit", "GPU default power limit", api.TryGetDefaultPowerLimitWatts(device.Handle), "W", deviceId, now);
                AddSample(samples, "gpc-vf-offset", "GPU core VF offset", api.TryGetGpcClockVfOffset(device.Handle), "MHz", deviceId, now);
                AddSample(samples, "memory-vf-offset", "GPU memory VF offset", api.TryGetMemoryClockVfOffset(device.Handle), "MHz", deviceId, now);

                if (api.TryGetFanCount(device.Handle) is uint fanCount)
                {
                    for (uint fan = 0; fan < fanCount; fan++)
                    {
                        AddSample(
                            samples,
                            $"fan-target-{fan}",
                            $"GPU fan {fan + 1} target",
                            api.TryGetTargetFanSpeed(device.Handle, fan),
                            "%",
                            deviceId,
                            now);
                    }
                }
            }
            return samples;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            _lastError = exception.Message;
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("NVML alpha support is telemetry-only.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("NVML alpha support is telemetry-only.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("NVML alpha support is telemetry-only.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("NVML alpha support exposes no resettable control.");

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        bool healthy = _api is not null && _lastError is null;
        string message = _lastError ?? _availabilityMessage ?? "NVML telemetry has not been probed yet.";
        return Task.FromResult(new AdapterHealth(
            Manifest.Id,
            healthy,
            DateTimeOffset.UtcNow,
            message,
            healthy ? [] : [message]));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _api?.Dispose();
            _api = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private bool TryOpen(out NvmlApi api)
    {
        if (_api is not null)
        {
            api = _api;
            return true;
        }
        if (!NvmlApi.TryCreate(out NvmlApi created, out string message))
        {
            _availabilityMessage = message;
            api = null!;
            return false;
        }
        try
        {
            created.Initialize();
            _api = created;
            _availabilityMessage = $"NVML {created.DriverVersion} telemetry is available; writes are blocked in this alpha.";
            api = created;
            return true;
        }
        catch (Exception exception)
        {
            created.Dispose();
            _availabilityMessage = exception.Message;
            api = null!;
            return false;
        }
    }

    private static void AddSample(
        List<SensorSample> destination,
        string suffix,
        string name,
        double? value,
        string unit,
        string deviceId,
        DateTimeOffset timestamp)
    {
        if (value is double observed && double.IsFinite(observed))
        {
            destination.Add(new SensorSample(
                $"nvml.{suffix}:{deviceId}",
                "nvidia.nvml",
                deviceId,
                name,
                timestamp,
                observed,
                unit,
                SensorQuality.Good,
                TimeSpan.Zero));
        }
    }

    private static void AddDiscoveredBounds(
        List<CapabilityDescriptor> capabilities,
        NvmlApi api,
        NvmlDevice device,
        string deviceId)
    {
        const double ConservativeFanFloorPercent = 50;
        const string qualificationBlocker = "RigPilot discovered this public NVML range, but leaves it read-only until the exact GPU board and driver complete signed apply, read-back, default-reset, conflict, and physical safety qualification.";

        if (api.TryGetFanCount(device.Handle) is uint fanCount
            && fanCount is > 0 and <= 16
            && api.TryGetFanSpeedRange(device.Handle) is (uint fanMinimum, uint fanMaximum))
        {
            double safeMinimum = Math.Max(ConservativeFanFloorPercent, fanMinimum);
            double safeMaximum = Math.Min(100, fanMaximum);
            if (safeMinimum <= safeMaximum)
            {
                for (uint fan = 0; fan < fanCount; fan++)
                {
                    capabilities.Add(new CapabilityDescriptor(
                        $"nvml.fan:{deviceId}:{fan}",
                        AdapterId,
                        deviceId,
                        $"GPU fan {fan + 1} (safety-gated)",
                        CapabilityAccessState.ReadOnly,
                        AdapterExecutionContext.SystemService,
                        ControlValueKind.Numeric,
                        new NumericRange(safeMinimum, safeMaximum, 1),
                        "%",
                        RiskLevel.Critical,
                        EvidenceLevel.Detected,
                        null,
                        $"NVML reports a {fanMinimum}-{fanMaximum}% controller range. The conservative {ConservativeFanFloorPercent}% floor remains in effect because restart validation is incomplete. {qualificationBlocker}",
                        CanResetToDefault: false,
                        Domain: ControlDomain.Cooling));
                }
            }

            // Read-only transport-feasibility evidence for a future GPU-fan write path.
            // This reports whether the exact installed driver exports a usable manual-fan
            // setter (duty setter + control-policy setter). It never creates a write:
            // the capability stays ReadOnly and no setter delegate is ever marshalled.
            // See docs/qualification/rtx3090-fan-write-path.md.
            string transportReason = api.HasUsableFanControlTransport
                ? $"The installed NVML runtime ({api.DriverVersion}) exports both nvmlDeviceSetFanSpeed_v2 and nvmlDeviceSetFanControlPolicy, so a manual-fan write transport is feasible on this exact driver. It remains ReadOnly: apply, read-back, default-reset, conflict, and physical safety qualification are not complete. {qualificationBlocker}"
                : $"The installed NVML runtime ({api.DriverVersion}) does not export a usable manual-fan setter (SetFanSpeed_v2 present: {api.HasFanSpeedSetter}; SetFanControlPolicy present: {api.HasFanControlPolicySetter}). A GPU-fan write is not feasible through NVML on this driver; it would require a separately-audited NVAPI cooler transport.";
            capabilities.Add(new CapabilityDescriptor(
                $"nvml.fan-transport:{deviceId}",
                AdapterId,
                deviceId,
                "GPU fan write transport (feasibility)",
                CapabilityAccessState.ReadOnly,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Boolean,
                null,
                null,
                RiskLevel.Critical,
                EvidenceLevel.Detected,
                null,
                transportReason,
                CanResetToDefault: false,
                Domain: ControlDomain.Cooling));
        }

        if (api.TryGetPowerLimitRangeWatts(device.Handle) is (double powerMinimum, double powerMaximum)
            && powerMinimum > 0
            && powerMaximum >= powerMinimum)
        {
            capabilities.Add(new CapabilityDescriptor(
                $"nvml.power-limit:{deviceId}",
                AdapterId,
                deviceId,
                "GPU power limit (safety-gated)",
                CapabilityAccessState.ReadOnly,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(powerMinimum, powerMaximum, 1),
                "W",
                RiskLevel.Experimental,
                EvidenceLevel.Detected,
                null,
                $"NVML reports an exact {powerMinimum:0.#}-{powerMaximum:0.#} W range. {qualificationBlocker}",
                CanResetToDefault: false,
                Domain: ControlDomain.Gpu));
        }

        AddClockOffsetBound(
            "gpc-vf-offset",
            "GPU core VF offset (safety-gated)",
            api.TryGetGpcClockVfOffsetRange(device.Handle),
            deviceId,
            qualificationBlocker,
            capabilities);
        AddClockOffsetBound(
            "memory-vf-offset",
            "GPU memory VF offset (safety-gated)",
            api.TryGetMemoryClockVfOffsetRange(device.Handle),
            deviceId,
            qualificationBlocker,
            capabilities);
    }

    private static void AddClockOffsetBound(
        string idSuffix,
        string name,
        (int Minimum, int Maximum)? range,
        string deviceId,
        string qualificationBlocker,
        List<CapabilityDescriptor> capabilities)
    {
        if (range is not (int minimum, int maximum) || minimum > maximum)
        {
            return;
        }

        capabilities.Add(new CapabilityDescriptor(
            $"nvml.{idSuffix}:{deviceId}",
            AdapterId,
            deviceId,
            name,
            CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.SystemService,
            ControlValueKind.Numeric,
            new NumericRange(minimum, maximum, 1),
            "MHz",
            RiskLevel.Experimental,
            EvidenceLevel.Detected,
            null,
            $"NVML reports an exact {minimum} to {maximum} MHz range. {qualificationBlocker}",
            CanResetToDefault: false,
            Domain: ControlDomain.Gpu));
    }

    private static string SanitiseId(string value) => new string(value
        .Where(character => char.IsLetterOrDigit(character))
        .ToArray())
        .ToLowerInvariant();

    private sealed class NvmlApi : IDisposable
    {
        private readonly nint _library;
        private readonly NvmlInitDelegate _init;
        private readonly NvmlShutdownDelegate _shutdown;
        private readonly NvmlGetCountDelegate _getCount;
        private readonly NvmlGetHandleDelegate _getHandle;
        private readonly NvmlGetStringDelegate _getName;
        private readonly NvmlGetStringDelegate _getUuid;
        private readonly NvmlGetDriverVersionDelegate _getDriverVersion;
        private readonly NvmlGetTemperatureDelegate _getTemperature;
        private readonly NvmlGetUnsignedDelegate _getFanSpeed;
        private readonly NvmlGetUnsignedDelegate _getPowerUsage;
        private readonly NvmlGetClockDelegate _getClock;
        private readonly NvmlGetUnsignedDelegate? _getFanCount;
        private readonly NvmlGetUnsignedPairDelegate? _getFanSpeedRange;
        private readonly NvmlGetFanIndexedUnsignedDelegate? _getTargetFanSpeed;
        private readonly NvmlGetUnsignedDelegate? _getPowerLimit;
        private readonly NvmlGetUnsignedDelegate? _getDefaultPowerLimit;
        private readonly NvmlGetUnsignedPairDelegate? _getPowerLimitConstraints;
        private readonly NvmlGetSignedDelegate? _getGpcClockVfOffset;
        private readonly NvmlGetSignedPairDelegate? _getGpcClockVfOffsetRange;
        private readonly NvmlGetSignedDelegate? _getMemoryClockVfOffset;
        private readonly NvmlGetSignedPairDelegate? _getMemoryClockVfOffsetRange;
        private bool _initialised;

        private NvmlApi(
            nint library,
            NvmlInitDelegate init,
            NvmlShutdownDelegate shutdown,
            NvmlGetCountDelegate getCount,
            NvmlGetHandleDelegate getHandle,
            NvmlGetStringDelegate getName,
            NvmlGetStringDelegate getUuid,
            NvmlGetDriverVersionDelegate getDriverVersion,
            NvmlGetTemperatureDelegate getTemperature,
            NvmlGetUnsignedDelegate getFanSpeed,
            NvmlGetUnsignedDelegate getPowerUsage,
            NvmlGetClockDelegate getClock,
            NvmlGetUnsignedDelegate? getFanCount,
            NvmlGetUnsignedPairDelegate? getFanSpeedRange,
            NvmlGetFanIndexedUnsignedDelegate? getTargetFanSpeed,
            NvmlGetUnsignedDelegate? getPowerLimit,
            NvmlGetUnsignedDelegate? getDefaultPowerLimit,
            NvmlGetUnsignedPairDelegate? getPowerLimitConstraints,
            NvmlGetSignedDelegate? getGpcClockVfOffset,
            NvmlGetSignedPairDelegate? getGpcClockVfOffsetRange,
            NvmlGetSignedDelegate? getMemoryClockVfOffset,
            NvmlGetSignedPairDelegate? getMemoryClockVfOffsetRange)
        {
            _library = library;
            _init = init;
            _shutdown = shutdown;
            _getCount = getCount;
            _getHandle = getHandle;
            _getName = getName;
            _getUuid = getUuid;
            _getDriverVersion = getDriverVersion;
            _getTemperature = getTemperature;
            _getFanSpeed = getFanSpeed;
            _getPowerUsage = getPowerUsage;
            _getClock = getClock;
            _getFanCount = getFanCount;
            _getFanSpeedRange = getFanSpeedRange;
            _getTargetFanSpeed = getTargetFanSpeed;
            _getPowerLimit = getPowerLimit;
            _getDefaultPowerLimit = getDefaultPowerLimit;
            _getPowerLimitConstraints = getPowerLimitConstraints;
            _getGpcClockVfOffset = getGpcClockVfOffset;
            _getGpcClockVfOffsetRange = getGpcClockVfOffsetRange;
            _getMemoryClockVfOffset = getMemoryClockVfOffset;
            _getMemoryClockVfOffsetRange = getMemoryClockVfOffsetRange;
        }

        public string DriverVersion { get; private set; } = "unknown";

        /// <summary>
        /// True when the loaded NVML runtime exports <c>nvmlDeviceSetFanSpeed_v2</c>.
        /// This records symbol *presence* only: the setter is never marshalled to a
        /// callable delegate here, so this detection cannot issue a fan write.
        /// </summary>
        public bool HasFanSpeedSetter { get; private set; }

        /// <summary>
        /// True when the loaded NVML runtime exports <c>nvmlDeviceSetFanControlPolicy</c>,
        /// which is required both to enter manual fan control and to restore the
        /// driver automatic curve. Presence-only; never marshalled or called.
        /// </summary>
        public bool HasFanControlPolicySetter { get; private set; }

        /// <summary>
        /// A usable manual-fan transport needs both the duty setter and the control-policy
        /// setter (so a write could later be reset back to the automatic curve). Detection
        /// only; no write path is created by returning true.
        /// </summary>
        public bool HasUsableFanControlTransport => HasFanSpeedSetter && HasFanControlPolicySetter;

        public static bool TryCreate(out NvmlApi api, out string message)
        {
            foreach (string candidate in Candidates())
            {
                if (!NativeLibrary.TryLoad(candidate, out nint library))
                {
                    continue;
                }
                try
                {
                    api = new NvmlApi(
                        library,
                        Export<NvmlInitDelegate>(library, "nvmlInit_v2"),
                        Export<NvmlShutdownDelegate>(library, "nvmlShutdown"),
                        Export<NvmlGetCountDelegate>(library, "nvmlDeviceGetCount_v2"),
                        Export<NvmlGetHandleDelegate>(library, "nvmlDeviceGetHandleByIndex_v2"),
                        Export<NvmlGetStringDelegate>(library, "nvmlDeviceGetName"),
                        Export<NvmlGetStringDelegate>(library, "nvmlDeviceGetUUID"),
                        Export<NvmlGetDriverVersionDelegate>(library, "nvmlSystemGetDriverVersion"),
                        Export<NvmlGetTemperatureDelegate>(library, "nvmlDeviceGetTemperature"),
                        Export<NvmlGetUnsignedDelegate>(library, "nvmlDeviceGetFanSpeed"),
                        Export<NvmlGetUnsignedDelegate>(library, "nvmlDeviceGetPowerUsage"),
                        Export<NvmlGetClockDelegate>(library, "nvmlDeviceGetClockInfo"),
                        TryExport<NvmlGetUnsignedDelegate>(library, "nvmlDeviceGetNumFans"),
                        TryExport<NvmlGetUnsignedPairDelegate>(library, "nvmlDeviceGetMinMaxFanSpeed"),
                        TryExport<NvmlGetFanIndexedUnsignedDelegate>(library, "nvmlDeviceGetTargetFanSpeed"),
                        TryExport<NvmlGetUnsignedDelegate>(library, "nvmlDeviceGetPowerManagementLimit"),
                        TryExport<NvmlGetUnsignedDelegate>(library, "nvmlDeviceGetPowerManagementDefaultLimit"),
                        TryExport<NvmlGetUnsignedPairDelegate>(library, "nvmlDeviceGetPowerManagementLimitConstraints"),
                        TryExport<NvmlGetSignedDelegate>(library, "nvmlDeviceGetGpcClkVfOffset"),
                        TryExport<NvmlGetSignedPairDelegate>(library, "nvmlDeviceGetGpcClkMinMaxVfOffset"),
                        TryExport<NvmlGetSignedDelegate>(library, "nvmlDeviceGetMemClkVfOffset"),
                        TryExport<NvmlGetSignedPairDelegate>(library, "nvmlDeviceGetMemClkMinMaxVfOffset"));

                    // Presence-only detection of the manual-fan setter symbols. We check
                    // whether the driver exports them; we deliberately do NOT marshal them
                    // to callable delegates, so no fan-write code path can exist here.
                    api.HasFanSpeedSetter = NativeLibrary.TryGetExport(library, "nvmlDeviceSetFanSpeed_v2", out _);
                    api.HasFanControlPolicySetter = NativeLibrary.TryGetExport(library, "nvmlDeviceSetFanControlPolicy", out _);

                    message = "NVML runtime was loaded.";
                    return true;
                }
                catch (Exception exception) when (exception is EntryPointNotFoundException or InvalidOperationException)
                {
                    NativeLibrary.Free(library);
                    message = $"The installed NVML runtime is missing a required telemetry export: {exception.Message}";
                }
            }
            api = null!;
            message = "The NVIDIA NVML runtime was not found beside the installed display driver.";
            return false;
        }

        public void Initialize()
        {
            EnsureSuccess(_init(), "nvmlInit_v2");
            _initialised = true;
            DriverVersion = ReadDriverString(_getDriverVersion, 96, "nvmlSystemGetDriverVersion");
        }

        public List<NvmlDevice> EnumerateDevices()
        {
            EnsureInitialised();
            uint count = 0;
            EnsureSuccess(_getCount(out count), "nvmlDeviceGetCount_v2");
            if (count > 64)
            {
                throw new InvalidOperationException("NVML reported an unreasonable GPU count.");
            }
            List<NvmlDevice> devices = [];
            for (uint index = 0; index < count; index++)
            {
                EnsureSuccess(_getHandle(index, out IntPtr handle), "nvmlDeviceGetHandleByIndex_v2");
                string name = ReadString(_getName, handle, 96, "nvmlDeviceGetName");
                string uuid = ReadString(_getUuid, handle, 96, "nvmlDeviceGetUUID");
                if (!string.IsNullOrWhiteSpace(uuid))
                {
                    devices.Add(new NvmlDevice(handle, name, uuid));
                }
            }
            return devices;
        }

        public double? TryGetTemperature(IntPtr handle, uint sensor) => TryRead(() =>
        {
            EnsureSuccess(_getTemperature(handle, sensor, out uint value), "nvmlDeviceGetTemperature");
            return value;
        });

        public double? TryGetFanSpeed(IntPtr handle) => TryRead(() =>
        {
            EnsureSuccess(_getFanSpeed(handle, out uint value), "nvmlDeviceGetFanSpeed");
            return value;
        });

        public double? TryGetPowerWatts(IntPtr handle) => TryRead(() =>
        {
            EnsureSuccess(_getPowerUsage(handle, out uint value), "nvmlDeviceGetPowerUsage");
            return value / 1000d;
        });

        public double? TryGetClock(IntPtr handle, uint clock) => TryRead(() =>
        {
            EnsureSuccess(_getClock(handle, clock, out uint value), "nvmlDeviceGetClockInfo");
            return value;
        });

        public uint? TryGetFanCount(IntPtr handle) => TryReadUnsigned(_getFanCount, handle);

        public (uint Minimum, uint Maximum)? TryGetFanSpeedRange(IntPtr handle) => TryReadUnsignedPair(_getFanSpeedRange, handle);

        public double? TryGetTargetFanSpeed(IntPtr handle, uint fan) => TryRead(() =>
        {
            if (_getTargetFanSpeed is null)
            {
                throw new InvalidOperationException("nvmlDeviceGetTargetFanSpeed is unavailable.");
            }
            EnsureSuccess(_getTargetFanSpeed(handle, fan, out uint value), "nvmlDeviceGetTargetFanSpeed");
            return value;
        });

        public double? TryGetPowerLimitWatts(IntPtr handle) => TryRead(() =>
        {
            if (_getPowerLimit is null)
            {
                throw new InvalidOperationException("nvmlDeviceGetPowerManagementLimit is unavailable.");
            }
            EnsureSuccess(_getPowerLimit(handle, out uint value), "nvmlDeviceGetPowerManagementLimit");
            return value / 1000d;
        });

        public double? TryGetDefaultPowerLimitWatts(IntPtr handle) => TryRead(() =>
        {
            if (_getDefaultPowerLimit is null)
            {
                throw new InvalidOperationException("nvmlDeviceGetPowerManagementDefaultLimit is unavailable.");
            }
            EnsureSuccess(_getDefaultPowerLimit(handle, out uint value), "nvmlDeviceGetPowerManagementDefaultLimit");
            return value / 1000d;
        });

        public (double Minimum, double Maximum)? TryGetPowerLimitRangeWatts(IntPtr handle)
        {
            (uint Minimum, uint Maximum)? range = TryReadUnsignedPair(_getPowerLimitConstraints, handle);
            return range is (uint minimum, uint maximum)
                ? (minimum / 1000d, maximum / 1000d)
                : null;
        }

        public double? TryGetGpcClockVfOffset(IntPtr handle) => TryReadSigned(_getGpcClockVfOffset, handle);

        public (int Minimum, int Maximum)? TryGetGpcClockVfOffsetRange(IntPtr handle) => TryReadSignedPair(_getGpcClockVfOffsetRange, handle);

        public double? TryGetMemoryClockVfOffset(IntPtr handle) => TryReadSigned(_getMemoryClockVfOffset, handle);

        public (int Minimum, int Maximum)? TryGetMemoryClockVfOffsetRange(IntPtr handle) => TryReadSignedPair(_getMemoryClockVfOffsetRange, handle);

        public void Dispose()
        {
            if (_initialised)
            {
                _ = _shutdown();
                _initialised = false;
            }
            NativeLibrary.Free(_library);
        }

        private void EnsureInitialised()
        {
            if (!_initialised)
            {
                throw new InvalidOperationException("NVML was not initialised.");
            }
        }

        private static double? TryRead(Func<double> read)
        {
            try { return read(); }
            catch (InvalidOperationException) { return null; }
        }

        private static string ReadString(NvmlGetStringDelegate read, IntPtr device, int length, string operation)
        {
            nint memory = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(new byte[length], 0, memory, length);
                EnsureSuccess(read(device, memory, (uint)length), operation);
                return Marshal.PtrToStringAnsi(memory) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        private static string ReadDriverString(NvmlGetDriverVersionDelegate read, int length, string operation)
        {
            nint memory = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(new byte[length], 0, memory, length);
                EnsureSuccess(read(memory, (uint)length), operation);
                return Marshal.PtrToStringAnsi(memory) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        private static T Export<T>(nint library, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out nint address))
            {
                throw new EntryPointNotFoundException(name);
            }
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private static T? TryExport<T>(nint library, string name) where T : Delegate =>
            NativeLibrary.TryGetExport(library, name, out nint address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;

        private static uint? TryReadUnsigned(NvmlGetUnsignedDelegate? read, IntPtr handle)
        {
            if (read is null)
            {
                return null;
            }
            try
            {
                EnsureSuccess(read(handle, out uint value), "NVML optional unsigned query");
                return value;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static (uint Minimum, uint Maximum)? TryReadUnsignedPair(NvmlGetUnsignedPairDelegate? read, IntPtr handle)
        {
            if (read is null)
            {
                return null;
            }
            try
            {
                EnsureSuccess(read(handle, out uint minimum, out uint maximum), "NVML optional unsigned range query");
                return (minimum, maximum);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static double? TryReadSigned(NvmlGetSignedDelegate? read, IntPtr handle)
        {
            if (read is null)
            {
                return null;
            }
            try
            {
                EnsureSuccess(read(handle, out int value), "NVML optional signed query");
                return value;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static (int Minimum, int Maximum)? TryReadSignedPair(NvmlGetSignedPairDelegate? read, IntPtr handle)
        {
            if (read is null)
            {
                return null;
            }
            try
            {
                EnsureSuccess(read(handle, out int minimum, out int maximum), "NVML optional signed range query");
                return (minimum, maximum);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static void EnsureSuccess(int result, string operation)
        {
            if (result != 0)
            {
                throw new InvalidOperationException($"{operation} failed with NVML status {result}.");
            }
        }

        private static IEnumerable<string> Candidates()
        {
            yield return "nvml.dll";
            string systemDirectory = Environment.SystemDirectory;
            if (!string.IsNullOrWhiteSpace(systemDirectory))
            {
                yield return Path.Combine(systemDirectory, "nvml.dll");
            }
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlInitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlShutdownDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetCountDelegate(out uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetHandleDelegate(uint index, out IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetStringDelegate(IntPtr device, IntPtr value, uint length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetDriverVersionDelegate(IntPtr value, uint length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetTemperatureDelegate(IntPtr device, uint sensor, out uint value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetUnsignedDelegate(IntPtr device, out uint value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetUnsignedPairDelegate(IntPtr device, out uint first, out uint second);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetFanIndexedUnsignedDelegate(IntPtr device, uint fan, out uint value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetSignedDelegate(IntPtr device, out int value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetSignedPairDelegate(IntPtr device, out int first, out int second);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlGetClockDelegate(IntPtr device, uint clockType, out uint value);
    }

    private sealed record NvmlDevice(IntPtr Handle, string Name, string Uuid);
}
