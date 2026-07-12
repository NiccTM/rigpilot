using LibreHardwareMonitor.Hardware;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

public sealed class LibreHardwareMonitorAdapter : IHardwareAdapter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Computer? _computer;
    private string? _lastError;

    public AdapterManifest Manifest { get; } = new(
        "librehardwaremonitor",
        "LibreHardwareMonitor",
        "0.9.6",
        "MPL-2.0",
        "Signed PawnIO for privileged motherboard access",
        AdapterExecutionContext.AdapterHost,
        ["LibreHardwareMonitor 0.9.6 supported devices"],
        ["Monitoring", "MotherboardFanReadOnly", "ControllerReadOnly"]);

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            UpdateHardware();
            List<HardwareDevice> devices = [];
            List<CapabilityDescriptor> capabilities = [];
            foreach (IHardware hardware in TraverseHardware())
            {
                HardwareDevice device = ToDevice(hardware);
                devices.Add(device);
                foreach (ISensor sensor in hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Control && sensor.Control is not null))
                {
                    capabilities.Add(new CapabilityDescriptor(
                        $"lhm.control:{sensor.Identifier}",
                        Manifest.Id,
                        device.Id,
                        sensor.Name,
                        CapabilityAccessState.ReadOnly,
                        AdapterExecutionContext.AdapterHost,
                        ControlValueKind.Numeric,
                        new NumericRange(sensor.Control!.MinSoftwareValue, sensor.Control.MaxSoftwareValue, 1),
                        "%",
                        RiskLevel.Guarded,
                        EvidenceLevel.Detected,
                        null,
                        "Control was detected but PC Helper has not qualified apply, read-back, and reset on this exact controller.",
                        CanResetToDefault: true,
                        Domain: ControlDomain.Cooling));
                }
            }

            List<DiagnosticWarning> warnings = [];
            if (!IsPawnIoInstalled())
            {
                warnings.Add(new DiagnosticWarning(
                    "PAWNIO_NOT_DETECTED",
                    "Information",
                    "Signed PawnIO was not detected. Privileged motherboard sensors may be unavailable.",
                    "Install the official signed PawnIO package only if privileged hardware access is required."));
            }

            return new AdapterProbeResult(Manifest, devices, capabilities, warnings);
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            return new AdapterProbeResult(
                Manifest,
                [],
                [],
                [new DiagnosticWarning("LHM_PROBE_FAILED", "Warning", exception.Message, "PC Helper will remain read-only and continue with Windows inventory.")]);
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
            UpdateHardware();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return TraverseHardware()
                .SelectMany(hardware => hardware.Sensors.Select(sensor => ToSample(hardware, sensor, now)))
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

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("LibreHardwareMonitor controls remain read-only until exact-controller qualification passes.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("LibreHardwareMonitor controls remain read-only until exact-controller qualification passes.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("LibreHardwareMonitor controls remain read-only until exact-controller qualification passes.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("PC Helper has not taken ownership of this control.");

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        bool healthy = _computer is not null && _lastError is null;
        return Task.FromResult(new AdapterHealth(
            Manifest.Id,
            healthy,
            DateTimeOffset.UtcNow,
            healthy ? "Hardware monitor is open." : "Hardware monitor is degraded or not initialised.",
            _lastError is null ? [] : [_lastError]));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _computer?.Close();
            _computer = null;
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
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsPowerMonitorEnabled = true
        };
        _computer.Open();
        _lastError = null;
    }

    private void UpdateHardware() => _computer!.Accept(new UpdateVisitor());

    private IEnumerable<IHardware> TraverseHardware()
    {
        foreach (IHardware hardware in _computer!.Hardware)
        {
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

    private static bool IsPawnIoInstalled() =>
        Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO") is not null;

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
