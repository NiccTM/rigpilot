using LibreHardwareMonitor.Hardware;
using PCHelper.Contracts;
using System.Diagnostics;
using System.Text.Json;

namespace PCHelper.Adapters;

public sealed class LibreHardwareMonitorAdapter : IHardwareAdapter, IHardwareStateVerifier, IAdapterTopologyCachePolicy
{
    public const string AdapterId = "librehardwaremonitor";

    private readonly SemaphoreSlim _gate = new(1, 1);
    // LibreHardwareMonitor can expose a bounded control during the initial
    // hardware traversal and omit that sensor from a later traversal of the
    // same open Computer instance. Keep the exact IControl object discovered
    // during probing for the lifetime of that instance. A stale or unplugged
    // controller still fails closed when its control operation throws.
    private readonly Dictionary<string, IControl> _knownControls = new(StringComparer.Ordinal);
    // Some Super I/O implementations intermittently omit control sensors from
    // a refresh after they were successfully enumerated. Keep the matching
    // descriptor for the lifetime of this open Computer instance as well as
    // the control handle, so the service does not revoke a capability solely
    // because of that transient enumeration defect. Any actual write still
    // goes through Prepare/Apply/Verify/Reset and fails closed on the handle.
    private readonly Dictionary<string, CapabilityDescriptor> _knownControlCapabilities = new(StringComparer.Ordinal);
    private Computer? _computer;
    private string? _lastError;
    private long _probeUpdateTimestamp;
    private bool _probeUpdateAvailableForTelemetry;

    public AdapterManifest Manifest { get; } = new(
        AdapterId,
        "LibreHardwareMonitor",
        "0.9.6",
        "MPL-2.0",
        "Signed PawnIO for privileged motherboard access",
        AdapterExecutionContext.AdapterHost,
        ["LibreHardwareMonitor 0.9.6 supported devices"],
        ["Monitoring", "MotherboardFanExperimental", "GpuFanExperimental", "UsbControllerDiscoveryContainedReadOnly"]);

    public TimeSpan TopologyCacheDuration => TimeSpan.FromSeconds(30);

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            UpdateHardware();
            long probeUpdateTimestamp = Stopwatch.GetTimestamp();
            List<HardwareDevice> devices = [];
            List<CapabilityDescriptor> capabilities = [];
            bool pawnIoInstalled = IsPawnIoInstalled();
            HashSet<string> visibleDeviceIds = new(StringComparer.Ordinal);
            HashSet<string> visibleCapabilityIds = new(StringComparer.Ordinal);
            foreach (IHardware hardware in TraverseHardware())
            {
                HardwareDevice device = ToDevice(hardware);
                devices.Add(device);
                visibleDeviceIds.Add(device.Id);
                foreach (ISensor sensor in hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Control && sensor.Control is not null))
                {
                    IControl control = sensor.Control!;
                    string capabilityId = $"lhm.control:{sensor.Identifier}";
                    _knownControls[capabilityId] = control;
                    bool bounded = float.IsFinite(control.MinSoftwareValue)
                        && float.IsFinite(control.MaxSoftwareValue)
                        && control.MinSoftwareValue < control.MaxSoftwareValue;
                    bool vendorControl = hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;
                    bool writeEndpointAvailable = bounded && (vendorControl || pawnIoInstalled);
                    CapabilityAccessState state = writeEndpointAvailable
                        ? CapabilityAccessState.Experimental
                        : bounded
                            ? CapabilityAccessState.ReadOnly
                            : CapabilityAccessState.Faulted;
                    string reason = state switch
                    {
                        CapabilityAccessState.Experimental =>
                            "LibreHardwareMonitor exposes bounded software control, API read-back, rollback, and default reset. Physical response is not certified on this exact controller.",
                        CapabilityAccessState.ReadOnly =>
                            "The control is detected, but the signed PawnIO path required for motherboard writes is unavailable.",
                        _ => "The controller reported invalid software-control bounds."
                    };
                    CapabilityDescriptor descriptor = new(
                        capabilityId,
                        Manifest.Id,
                        device.Id,
                        sensor.Name,
                        state,
                        AdapterExecutionContext.AdapterHost,
                        ControlValueKind.Numeric,
                        bounded ? new NumericRange(control.MinSoftwareValue, control.MaxSoftwareValue, 1) : null,
                        "%",
                        writeEndpointAvailable ? RiskLevel.Experimental : RiskLevel.Guarded,
                        EvidenceLevel.Detected,
                        null,
                        reason,
                        CanResetToDefault: bounded,
                        Domain: ControlDomain.Cooling);
                    _knownControlCapabilities[capabilityId] = descriptor;
                    capabilities.Add(descriptor);
                    visibleCapabilityIds.Add(capabilityId);
                }
            }

            List<DiagnosticWarning> warnings = [];
            int retainedControlCount = 0;
            foreach ((string capabilityId, CapabilityDescriptor descriptor) in _knownControlCapabilities.ToArray())
            {
                if (!visibleDeviceIds.Contains(descriptor.DeviceId))
                {
                    _knownControlCapabilities.Remove(capabilityId);
                    _knownControls.Remove(capabilityId);
                    continue;
                }

                if (visibleCapabilityIds.Add(capabilityId))
                {
                    capabilities.Add(descriptor);
                    retainedControlCount++;
                }
            }

            if (retainedControlCount > 0)
            {
                warnings.Add(new DiagnosticWarning(
                    "LHM_CONTROL_VISIBILITY_TRANSIENT",
                    "Information",
                    $"Retained {retainedControlCount} previously discovered cooling control(s) after a transient LibreHardwareMonitor enumeration gap.",
                    "Writes still require prepare, read-back, and firmware/default reset in the isolated Adapter Host."));
            }
            if (!pawnIoInstalled)
            {
                warnings.Add(new DiagnosticWarning(
                    "PAWNIO_NOT_DETECTED",
                    "Information",
                    "Signed PawnIO was not detected. Privileged motherboard sensors may be unavailable.",
                    "Install the official signed PawnIO package only if privileged hardware access is required."));
            }

            // CaptureAsync reads sensors immediately after a topology refresh.
            // Let that one read reuse this update instead of walking the entire
            // hardware tree twice in the same telemetry tick.
            _probeUpdateTimestamp = probeUpdateTimestamp;
            _probeUpdateAvailableForTelemetry = true;
            return new AdapterProbeResult(Manifest, devices, capabilities, warnings);
        }
        catch (Exception exception)
        {
            _probeUpdateAvailableForTelemetry = false;
            _lastError = exception.Message;
            return new AdapterProbeResult(
                Manifest,
                [],
                [],
                [new DiagnosticWarning("LHM_PROBE_FAILED", "Warning", exception.Message, "RigPilot will remain read-only and continue with Windows inventory.")]);
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
            EnsureOpen();
            if (!TryConsumeProbeUpdate())
            {
                UpdateHardware();
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return TraverseHardware()
                .SelectMany(hardware => hardware.Sensors.Select(sensor => ToSample(hardware, sensor, TimestampFor(hardware, now))))
                .Where(sample => sample is not null)
                .Cast<SensorSample>()
                .ToArray();
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string stage = "Open";
        try
        {
            EnsureOpen();
            stage = "UpdateHardware";
            UpdateHardware();
            stage = "FindControl";
            IControl control = FindControl(action.CapabilityId);
            stage = "ValidateRequestedValue";
            float requested = ValidateRequestedValue(action, control);
            stage = "CaptureRollbackState";
            ControlRollbackState rollback = new(control.ControlMode, control.SoftwareValue);
            return new PreparedAction(
                action with { Value = ControlValue.FromNumeric(requested) },
                control.ControlMode == ControlMode.Software
                    ? ControlValue.FromNumeric(control.SoftwareValue)
                    : null,
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(rollback));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _lastError = exception.Message;
            exception.Data["PCHelper.AdapterStage"] = stage;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            IControl control = FindControl(action.Action.CapabilityId);
            float requested = ValidateRequestedValue(action.Action, control);
            control.SetSoftware(requested);
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            UpdateHardware();
            IControl control = FindControl(action.Action.CapabilityId);
            float requested = ValidateRequestedValue(action.Action, control);
            double observed = control.SoftwareValue;
            bool success = control.ControlMode == ControlMode.Software
                && Math.Abs(observed - requested) <= 0.5;
            return new ActionVerification(
                action.Action.Id,
                success,
                ControlValue.FromNumeric(observed),
                success
                    ? "LibreHardwareMonitor software-control read-back matched. Physical response remains Experimental until calibration passes."
                    : $"Expected software control {requested:0.##}%, observed {observed:0.##}% in {control.ControlMode} mode.");
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            return new ActionVerification(action.Action.Id, false, null, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            IControl control = FindControl(action.Action.CapabilityId);
            ControlRollbackState rollback = JsonSerializer.Deserialize<ControlRollbackState>(action.AdapterToken)
                ?? throw new InvalidDataException("The fan-control rollback token is invalid.");
            if (rollback.Mode == ControlMode.Software)
            {
                control.SetSoftware(rollback.SoftwareValue);
            }
            else
            {
                control.SetDefault();
            }
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            FindControl(capabilityId).SetDefault();
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HardwareStateVerification> VerifyDefaultStateAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            UpdateHardware();
            IControl control = FindControl(capabilityId);
            bool success = control.ControlMode == ControlMode.Default;
            return new HardwareStateVerification(
                Manifest.Id,
                capabilityId,
                success,
                null,
                success ? "LibreHardwareMonitor read-back confirmed firmware/default control." : $"LibreHardwareMonitor control remained in {control.ControlMode} mode.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HardwareStateVerification> VerifyRollbackStateAsync(
        PreparedAction action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            UpdateHardware();
            IControl control = FindControl(action.Action.CapabilityId);
            ControlRollbackState rollback = JsonSerializer.Deserialize<ControlRollbackState>(action.AdapterToken)
                ?? throw new InvalidDataException("The fan-control rollback token is invalid.");
            bool success = rollback.Mode == ControlMode.Software
                ? control.ControlMode == ControlMode.Software && Math.Abs(control.SoftwareValue - rollback.SoftwareValue) <= 0.5
                : control.ControlMode == ControlMode.Default;
            return new HardwareStateVerification(
                Manifest.Id,
                action.Action.CapabilityId,
                success,
                control.ControlMode == ControlMode.Software ? ControlValue.FromNumeric(control.SoftwareValue) : null,
                success ? "LibreHardwareMonitor rollback state was read back." : "LibreHardwareMonitor rollback read-back did not match the captured control mode.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        bool healthy = _computer is not null && _lastError is null;
        return Task.FromResult(new AdapterHealth(
            Manifest.Id,
            healthy,
            DateTimeOffset.UtcNow,
            healthy
                ? $"Hardware monitor is open; {_knownControlCapabilities.Count} cooling control descriptor(s) are retained in the isolated host."
                : "Hardware monitor is degraded or not initialised.",
            _lastError is null ? [] : [_lastError]));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _computer?.Close();
            _computer = null;
            _probeUpdateAvailableForTelemetry = false;
            _knownControls.Clear();
            _knownControlCapabilities.Clear();
            _hardwareUpdatedAt.Clear();
            _infrequentUpdateTimestamp = 0;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void EnsureOpen()
    {
        if (_computer is not null)
        {
            return;
        }

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            // HidSharp initialises controller discovery on a background thread, and a
            // faulty native HID dependency can terminate the whole process. This shared
            // telemetry Computer therefore keeps controllers disabled; USB/AIO discovery
            // runs separately in a disposable child via DiscoverControllersInProcess(),
            // where a crash is contained by ContainedControllerDiscovery. Discovered
            // controllers stay read-only inventory and never gain a writable capability.
            IsControllerEnabled = false,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsPowerMonitorEnabled = true
        };
        _knownControls.Clear();
        _knownControlCapabilities.Clear();
        _hardwareUpdatedAt.Clear();
        _infrequentUpdateTimestamp = 0;
        _probeUpdateAvailableForTelemetry = false;
        _computer.Open();
        _lastError = null;
    }

    /// <summary>
    /// Subsystems whose backing reads are expensive and whose values do not
    /// change meaningfully between control ticks. Storage in particular issues
    /// SMART queries per drive; refreshing it on every telemetry tick dominated
    /// capture time, which made every other sensor in the same snapshot appear
    /// stale to the cooling engine's freshness gate.
    /// </summary>
    private static readonly HardwareType[] InfrequentlyUpdatedHardware =
    [
        HardwareType.Storage,
        HardwareType.Network
    ];

    private static readonly TimeSpan InfrequentUpdateInterval = TimeSpan.FromSeconds(30);
    private long _infrequentUpdateTimestamp;

    /// <summary>
    /// The moment each hardware subsystem last actually refreshed, so a sample
    /// carries the age of the read that produced it rather than the age of the
    /// traversal that collected it. Skipping an update must never make stale
    /// data look fresh.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _hardwareUpdatedAt = new(StringComparer.Ordinal);

    private void UpdateHardware()
    {
        _probeUpdateAvailableForTelemetry = false;
        bool includeInfrequent = _infrequentUpdateTimestamp == 0
            || Stopwatch.GetElapsedTime(_infrequentUpdateTimestamp) >= InfrequentUpdateInterval;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (IHardware hardware in _computer!.Hardware)
        {
            if (!includeInfrequent && InfrequentlyUpdatedHardware.Contains(hardware.HardwareType))
            {
                continue;
            }

            UpdateHardwareTree(hardware, now);
        }

        if (includeInfrequent)
        {
            _infrequentUpdateTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void UpdateHardwareTree(IHardware hardware, DateTimeOffset now)
    {
        hardware.Update();
        _hardwareUpdatedAt[hardware.Identifier.ToString()] = now;
        foreach (IHardware sub in hardware.SubHardware)
        {
            UpdateHardwareTree(sub, now);
        }
    }

    /// <summary>
    /// The timestamp to report for a sample: when that subsystem last refreshed.
    /// An unknown subsystem falls back to the collection time, which is the
    /// behaviour before tiering and never claims data is older than it is.
    /// </summary>
    private DateTimeOffset TimestampFor(IHardware hardware, DateTimeOffset fallback) =>
        _hardwareUpdatedAt.TryGetValue(hardware.Identifier.ToString(), out DateTimeOffset updated)
            ? updated
            : fallback;

    private bool TryConsumeProbeUpdate()
    {
        bool reusable = _probeUpdateAvailableForTelemetry
            && Stopwatch.GetElapsedTime(_probeUpdateTimestamp) <= TimeSpan.FromSeconds(1);
        _probeUpdateAvailableForTelemetry = false;
        return reusable;
    }

    private IControl FindControl(string capabilityId)
    {
        if (!capabilityId.StartsWith("lhm.control:", StringComparison.Ordinal))
        {
            throw new ArgumentException("The capability is not owned by LibreHardwareMonitor.", nameof(capabilityId));
        }

        if (_knownControls.TryGetValue(capabilityId, out IControl? knownControl))
        {
            return knownControl;
        }

        ISensor? sensor = TraverseHardware()
            .SelectMany(hardware => hardware.Sensors)
            .FirstOrDefault(item => item.Control is not null
                && string.Equals($"lhm.control:{item.Identifier}", capabilityId, StringComparison.Ordinal));
        IControl control = sensor?.Control
            ?? throw new InvalidOperationException("The requested LibreHardwareMonitor control is no longer available.");
        _knownControls[capabilityId] = control;
        return control;
    }

    private static float ValidateRequestedValue(ProfileAction action, IControl control)
    {
        if (action.Value.Kind != ControlValueKind.Numeric
            || action.Value.Numeric is not double value
            || !double.IsFinite(value))
        {
            throw new ArgumentException("A finite numeric software-control value is required.", nameof(action));
        }

        if (value < control.MinSoftwareValue || value > control.MaxSoftwareValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(action),
                $"Control value {value:0.##}% is outside [{control.MinSoftwareValue:0.##}, {control.MaxSoftwareValue:0.##}]%.");
        }

        return (float)value;
    }

    private IEnumerable<IHardware> TraverseHardware()
    {
        foreach (IHardware hardware in _computer!.Hardware)
        {
            // Network interfaces are outside RigPilot's cooling/RGB/GPU/CPU domain
            // and have no controllable capabilities. Worse, Windows layers dozens
            // of pseudo-adapters on each real NIC (WFP MAC-layer filters, QoS
            // packet schedulers, WiFi filter drivers), so LibreHardwareMonitor
            // reports them as "hardware" and they flood the device inventory (43 of
            // 70 entries on a typical desktop). Skip the whole subsystem so devices,
            // sensors, and control lookup all stay limited to relevant hardware.
            if (hardware.HardwareType == HardwareType.Network)
            {
                continue;
            }

            yield return hardware;
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                yield return subHardware;
            }
        }
    }

    private static HardwareDevice ToDevice(IHardware hardware)
    {
        DeviceKind kind = hardware.HardwareType switch
        {
            HardwareType.Cpu => DeviceKind.Cpu,
            HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia => DeviceKind.Gpu,
            HardwareType.Motherboard or HardwareType.SuperIO => DeviceKind.Motherboard,
            HardwareType.Memory => DeviceKind.Memory,
            HardwareType.Storage => DeviceKind.Storage,
            HardwareType.Network => DeviceKind.Network,
            HardwareType.Cooler or HardwareType.EmbeddedController => DeviceKind.Cooling,
            _ => DeviceKind.Controller
        };
        return new HardwareDevice(
            $"lhm.device:{hardware.Identifier}",
            hardware.Name,
            kind,
            null,
            hardware.Name,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hardwareType"] = hardware.HardwareType.ToString(),
                ["identifier"] = hardware.Identifier.ToString()
            });
    }

    private SensorSample? ToSample(IHardware hardware, ISensor sensor, DateTimeOffset now)
    {
        string? unit = UnitFor(sensor.SensorType);
        if (unit is null)
        {
            return null;
        }

        double? observed = sensor.Value;
        bool finite = observed is double value && double.IsFinite(value);
        double? normalisedValue = finite ? observed : null;
        SensorQuality quality = finite ? SensorQuality.Good : SensorQuality.Unavailable;
        return new SensorSample(
            $"lhm.sensor:{sensor.Identifier}",
            Manifest.Id,
            $"lhm.device:{hardware.Identifier}",
            sensor.Name,
            now,
            normalisedValue,
            unit,
            quality,
            TimeSpan.Zero);
    }

    private static string? UnitFor(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Fan => "RPM",
        SensorType.Control or SensorType.Load or SensorType.Level => "%",
        SensorType.Clock => "MHz",
        SensorType.Power => "W",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Energy => "mWh",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Frequency => "Hz",
        SensorType.Flow => "L/h",
        SensorType.TimeSpan => "s",
        _ => null
    };

    /// <summary>
    /// Enumerates USB/AIO controllers with a dedicated, controller-only
    /// <see cref="Computer"/>. This method MUST run only inside the isolated
    /// discovery child process: enabling controllers initialises native HidSharp
    /// code that can terminate the process. A managed enumeration failure is
    /// contained here; a native crash is contained by the parent, which observes
    /// the abnormal child exit. The returned controllers are read-only inventory
    /// evidence and never carry a writable capability.
    /// </summary>
    public static ControllerDiscoveryResultV1 DiscoverControllersInProcess()
    {
        Computer computer = new()
        {
            IsCpuEnabled = false,
            IsGpuEnabled = false,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsControllerEnabled = true,
            IsNetworkEnabled = false,
            IsStorageEnabled = false,
            IsPowerMonitorEnabled = false
        };

        try
        {
            computer.Open();
            computer.Accept(new UpdateVisitor());
            List<HardwareDevice> controllers = [];
            foreach (IHardware hardware in computer.Hardware)
            {
                controllers.Add(ToDevice(hardware));
                foreach (IHardware subHardware in hardware.SubHardware)
                {
                    controllers.Add(ToDevice(subHardware));
                }
            }

            return new ControllerDiscoveryResultV1(
                ControllerDiscoveryResultV1.CurrentSchemaVersion,
                ControllerDiscoveryOutcome.Succeeded,
                controllers,
                0,
                $"Enumerated {controllers.Count} controller device(s).",
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            return ControllerDiscoveryResultV1.Contained(
                ControllerDiscoveryOutcome.EnumerationFailed,
                $"Controller enumeration failed: {exception.GetType().Name}.");
        }
        finally
        {
            try
            {
                computer.Close();
            }
            catch
            {
                // A controller stack that faulted during Open may also fault on Close;
                // the child process is disposable, so swallow to preserve the result.
            }
        }
    }

    private static bool IsPawnIoInstalled()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    private sealed record ControlRollbackState(ControlMode Mode, float SoftwareValue);

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
