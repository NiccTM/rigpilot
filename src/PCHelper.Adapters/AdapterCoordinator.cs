using PCHelper.Contracts;

namespace PCHelper.Adapters;

public sealed class AdapterCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyList<IHardwareAdapter> _adapters;

    public AdapterCoordinator(IEnumerable<IHardwareAdapter> adapters)
    {
        _adapters = adapters.ToArray();
    }

    public IReadOnlyList<IHardwareAdapter> Adapters => _adapters;

    public async Task<HardwareSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        List<HardwareDevice> devices = [];
        List<CapabilityDescriptor> capabilities = [];
        List<SensorSample> sensors = [];
        List<DiagnosticWarning> warnings = [];
        List<AdapterHealth> health = [];

        foreach (IHardwareAdapter adapter in _adapters)
        {
            try
            {
                AdapterProbeResult probe = await adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
                devices.AddRange(probe.Devices);
                capabilities.AddRange(probe.Capabilities);
                warnings.AddRange(probe.Warnings);
                sensors.AddRange(await adapter.ReadSensorsAsync(cancellationToken).ConfigureAwait(false));
                health.Add(await adapter.GetHealthAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new DiagnosticWarning(
                    "ADAPTER_FAILED",
                    "Warning",
                    $"{adapter.Manifest.Name} failed: {exception.Message}",
                    "The adapter remains unavailable; other adapters continue."));
                health.Add(new AdapterHealth(adapter.Manifest.Id, false, DateTimeOffset.UtcNow, "Adapter failed.", [exception.Message]));
            }
        }

        IReadOnlyList<ConflictDescriptor> conflicts = ConflictDetector.Detect();
        return new HardwareSnapshot(
            DateTimeOffset.UtcNow,
            DeduplicateDevices(devices),
            capabilities.OrderBy(capability => capability.Id, StringComparer.Ordinal).ToArray(),
            sensors.OrderBy(sensor => sensor.SensorId, StringComparer.Ordinal).ToArray(),
            conflicts,
            warnings,
            health);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHardwareAdapter adapter in _adapters.Reverse())
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static HardwareDevice[] DeduplicateDevices(IEnumerable<HardwareDevice> devices) => devices
        .GroupBy(device => device.Id, StringComparer.Ordinal)
        .Select(group => group.First())
        .OrderBy(device => device.Kind)
        .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
