using PCHelper.Contracts;

namespace PCHelper.Adapters;

public sealed class AdapterCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyList<IHardwareAdapter> _adapters;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly object _topologyCacheGate = new();
    private readonly Dictionary<string, CachedTopology> _topologyCache = new(StringComparer.Ordinal);

    public AdapterCoordinator(IEnumerable<IHardwareAdapter> adapters, TimeProvider? timeProvider = null)
    {
        _adapters = adapters.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<IHardwareAdapter> Adapters => _adapters;

    public async Task<HardwareSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
                    AdapterProbeResult probe = await GetTopologyAsync(adapter, cancellationToken).ConfigureAwait(false);
                    devices.AddRange(probe.Devices);
                    capabilities.AddRange(probe.Capabilities);
                    warnings.AddRange(probe.Warnings);
                    // Telemetry is deliberately never cached. Every service tick
                    // reads fresh values even when topology came from the slower
                    // metadata cache.
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
                    health.Add(new AdapterHealth(adapter.Manifest.Id, false, _timeProvider.GetUtcNow(), "Adapter failed.", [exception.Message]));
                }
            }

            IReadOnlyList<ConflictDescriptor> conflicts = ConflictDetector.Detect();
            CapabilityDescriptor[] resolvedCapabilities = capabilities
                .Select(capability => ApplyConflictOwnership(capability, conflicts))
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .ToArray();
            return new HardwareSnapshot(
                _timeProvider.GetUtcNow(),
                DeduplicateDevices(devices),
                resolvedCapabilities,
                sensors.OrderBy(sensor => sensor.SensorId, StringComparer.Ordinal).ToArray(),
                conflicts,
                warnings,
                health);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    /// <summary>
    /// Invalidates all cached topology, or one adapter's entry. Device-change
    /// listeners can call this hook without changing the telemetry or IPC shape.
    /// </summary>
    public void InvalidateTopology(string? adapterId = null)
    {
        lock (_topologyCacheGate)
        {
            if (adapterId is null)
            {
                _topologyCache.Clear();
            }
            else
            {
                _topologyCache.Remove(adapterId);
            }
        }
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
        await _captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            InvalidateTopology();
            foreach (IHardwareAdapter adapter in _adapters.Reverse())
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _captureGate.Release();
            _captureGate.Dispose();
        }
    }

    private async Task<AdapterProbeResult> GetTopologyAsync(
        IHardwareAdapter adapter,
        CancellationToken cancellationToken)
    {
        TimeSpan cacheDuration = adapter is IAdapterTopologyCachePolicy policy
            ? policy.TopologyCacheDuration
            : TimeSpan.Zero;
        if (cacheDuration <= TimeSpan.Zero)
        {
            return await adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_topologyCacheGate)
        {
            if (_topologyCache.TryGetValue(adapter.Manifest.Id, out CachedTopology? cached)
                && cached.ExpiresAt > now)
            {
                return cached.Probe;
            }
        }

        AdapterProbeResult probe = await adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
        lock (_topologyCacheGate)
        {
            _topologyCache[adapter.Manifest.Id] = new CachedTopology(probe, now + cacheDuration);
        }
        return probe;
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

    private sealed record CachedTopology(AdapterProbeResult Probe, DateTimeOffset ExpiresAt);
}
