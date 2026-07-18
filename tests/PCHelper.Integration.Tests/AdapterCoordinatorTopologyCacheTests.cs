using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

public sealed class AdapterCoordinatorTopologyCacheTests
{
    [Fact]
    public async Task CachedTopologyProbesSlowlyWhileTelemetryReadsEveryTick()
    {
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));
        CountingAdapter inner = new(TimeSpan.FromSeconds(30), clock);
        await using AdapterCoordinator coordinator = new(
            [new TraceableHardwareAdapter(inner)],
            clock);

        HardwareSnapshot first = await coordinator.CaptureAsync(default);
        HardwareSnapshot second = await coordinator.CaptureAsync(default);
        clock.Advance(TimeSpan.FromSeconds(29));
        HardwareSnapshot beforeExpiry = await coordinator.CaptureAsync(default);

        Assert.Equal(1, inner.ProbeCount);
        Assert.Equal(3, inner.SensorReadCount);
        Assert.All([first, second, beforeExpiry], snapshot =>
            Assert.Equal("probe-1", Assert.Single(snapshot.Devices).Model));

        clock.Advance(TimeSpan.FromSeconds(2));
        HardwareSnapshot afterExpiry = await coordinator.CaptureAsync(default);

        Assert.Equal(2, inner.ProbeCount);
        Assert.Equal(4, inner.SensorReadCount);
        Assert.Equal("probe-2", Assert.Single(afterExpiry.Devices).Model);

        coordinator.InvalidateTopology(inner.Manifest.Id);
        HardwareSnapshot afterInvalidation = await coordinator.CaptureAsync(default);

        Assert.Equal(3, inner.ProbeCount);
        Assert.Equal(5, inner.SensorReadCount);
        Assert.Equal("probe-3", Assert.Single(afterInvalidation.Devices).Model);
    }

    [Fact]
    public async Task ZeroDurationAdapterKeepsDynamicProbeBehavior()
    {
        ManualTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        CountingAdapter inner = new(TimeSpan.Zero, clock);
        await using AdapterCoordinator coordinator = new(
            [new TraceableHardwareAdapter(inner)],
            clock);

        await coordinator.CaptureAsync(default);
        await coordinator.CaptureAsync(default);

        Assert.Equal(2, inner.ProbeCount);
        Assert.Equal(2, inner.SensorReadCount);
    }

    [Fact]
    public async Task SlowWmiAndPeripheralAdaptersOptIntoTopologyCaching()
    {
        await using LibreHardwareMonitorAdapter libreHardwareMonitor = new();
        await using NvmlTelemetryAdapter nvml = new();
        Assert.Equal(TimeSpan.FromMinutes(5), new SystemInventoryAdapter().TopologyCacheDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), new VendorControlEligibilityAdapter().TopologyCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), new WindowsPeripheralInventoryAdapter().TopologyCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), libreHardwareMonitor.TopologyCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), nvml.TopologyCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), new IntelGraphicsControlAdapter().TopologyCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), new AmdGraphicsControlAdapter().TopologyCacheDuration);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class CountingAdapter(TimeSpan cacheDuration, TimeProvider clock) : IHardwareAdapter, IAdapterTopologyCachePolicy
    {
        public int ProbeCount { get; private set; }

        public int SensorReadCount { get; private set; }

        public TimeSpan TopologyCacheDuration => cacheDuration;

        public AdapterManifest Manifest { get; } = new(
            "test.topology",
            "Topology test adapter",
            "1.0.0",
            "GPL-3.0-only",
            null,
            AdapterExecutionContext.SystemService,
            [],
            ["Monitoring"]);

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            HardwareDevice device = new(
                "test:device",
                "Test device",
                DeviceKind.Controller,
                "Test",
                $"probe-{ProbeCount}",
                null,
                new Dictionary<string, string>());
            return Task.FromResult(new AdapterProbeResult(Manifest, [device], [], []));
        }

        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SensorReadCount++;
            return Task.FromResult<IReadOnlyList<SensorSample>>(
            [
                new SensorSample(
                    "test:sensor",
                    Manifest.Id,
                    "test:device",
                    "Counter",
                    clock.GetUtcNow(),
                    SensorReadCount,
                    "count",
                    SensorQuality.Good,
                    TimeSpan.Zero)
            ]);
        }

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(
            new AdapterHealth(Manifest.Id, true, clock.GetUtcNow(), "Healthy", []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
