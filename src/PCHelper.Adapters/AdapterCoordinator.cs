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
        CapabilityDescriptor[] resolvedCapabilities = capabilities
            .Select(capability => ApplyConflictOwnership(capability, conflicts))
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .ToArray();
        return new HardwareSnapshot(
            DateTimeOffset.UtcNow,
            DeduplicateDevices(devices),
            resolvedCapabilities,
            sensors.OrderBy(sensor => sensor.SensorId, StringComparer.Ordinal).ToArray(),
            conflicts,
            warnings,
            health);
    }

    /// <summary>
    /// Probes a single adapter and applies the same conflict-ownership resolution as a
    /// full <see cref="CaptureAsync"/>, returning just that adapter's capabilities. Used to
    /// refresh one adapter's state synchronously (for example immediately after arming GPU
    /// fan control) without the cost of re-probing every adapter.
    /// </summary>
    public static Task<IReadOnlyList<CapabilityDescriptor>> CaptureAdapterCapabilitiesAsync(
        IHardwareAdapter adapter,
        CancellationToken cancellationToken) =>
        CaptureAdapterCapabilitiesAsync(adapter, conflicts: null, cancellationToken);

    /// <summary>
    /// Seam overload: <paramref name="conflicts"/> replaces the live process scan so
    /// deterministic tests do not depend on which fan/RGB applications happen to be
    /// running on the machine. Production callers pass null for the real detector.
    /// </summary>
    public static async Task<IReadOnlyList<CapabilityDescriptor>> CaptureAdapterCapabilitiesAsync(
        IHardwareAdapter adapter,
        IReadOnlyList<ConflictDescriptor>? conflicts,
        CancellationToken cancellationToken)
    {
        AdapterProbeResult probe = await adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ConflictDescriptor> resolved = conflicts ?? ConflictDetector.Detect();
        return probe.Capabilities
            .Select(capability => ApplyConflictOwnership(capability, resolved))
            .ToArray();
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

    internal static CapabilityDescriptor ApplyConflictOwnership(
        CapabilityDescriptor capability,
        IReadOnlyList<ConflictDescriptor> conflicts)
    {
        if (capability.State is not (CapabilityAccessState.Verified or CapabilityAccessState.Experimental))
        {
            return capability;
        }

        string[] families = ResourceFamiliesFor(capability);
        ConflictDescriptor[] owners = conflicts
            .Where(conflict => conflict.IsRunning
                && conflict.ResourceFamilies.Any(family => families.Contains(family, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(conflict => conflict.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (owners.Length == 0)
        {
            return capability;
        }

        string ownerNames = string.Join(", ", owners.Select(owner => owner.DisplayName));
        string guidance = string.Join(" ", owners.Select(owner => owner.Guidance).Distinct(StringComparer.Ordinal));
        return capability with
        {
            State = CapabilityAccessState.Blocked,
            ConflictOwner = ownerNames,
            Reason = $"Overlapping control is owned by {ownerNames}. {guidance}".Trim()
        };
    }

    private static string[] ResourceFamiliesFor(CapabilityDescriptor capability) => capability.Domain switch
    {
        ControlDomain.Cooling or ControlDomain.CoolingSafety
            when capability.DeviceId.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || capability.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) => ["GpuFan"],
        ControlDomain.Cooling or ControlDomain.CoolingSafety
            when capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)
                || capability.Name.Contains("AIO", StringComparison.OrdinalIgnoreCase) => ["Aio", "UsbFan"],
        ControlDomain.Cooling or ControlDomain.CoolingSafety => ["MotherboardFan"],
        ControlDomain.Cpu => ["CpuTuning"],
        ControlDomain.Gpu => ["GpuTuning"],
        ControlDomain.Lighting => ["Lighting", "OpenRGB"],
        _ => []
    };
}
