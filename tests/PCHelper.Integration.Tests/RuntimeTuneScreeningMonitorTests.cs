using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class RuntimeTuneScreeningMonitorTests
{
    [Fact]
    public async Task UsesOnlyBoundGpuLoadAndEnforcesAutoOcThreshold()
    {
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero));
        CapabilityDescriptor capability = Capability();
        TuneSensorBindingV2 binding = Binding();
        RuntimeTuneScreeningMonitor monitor = new(
            () => Snapshot(clock.GetUtcNow(), capability, boundLoad: 65, unrelatedLoad: 100),
            capability,
            clock,
            (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            _ => null,
            binding,
            new HealthyWorkload(clock),
            AutoOcWorkloadMode.Core,
            requiredAverageLoadPercent: 70);

        TuneScreeningResult result = await monitor.ScreenAsync(
            capability,
            Plan(capability),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("70%", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsWrongAuthenticatedWorkloadModeBeforeClaimingStability()
    {
        ManualTimeProvider clock = new(DateTimeOffset.UtcNow);
        CapabilityDescriptor capability = Capability();
        RuntimeTuneScreeningMonitor monitor = new(
            () => Snapshot(clock.GetUtcNow(), capability, boundLoad: 95, unrelatedLoad: 0),
            capability,
            clock,
            (_, _) => Task.CompletedTask,
            _ => null,
            Binding(),
            new HealthyWorkload(clock, AutoOcWorkloadMode.Memory),
            AutoOcWorkloadMode.Core,
            requiredAverageLoadPercent: 70);

        TuneScreeningResult result = await monitor.ScreenAsync(
            capability,
            Plan(capability),
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("workload host", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportsMeasuredDispatchThroughputAndBoundDeviceFanRpm()
    {
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero));
        CapabilityDescriptor capability = Capability();
        RuntimeTuneScreeningMonitor monitor = new(
            () => Snapshot(clock.GetUtcNow(), capability, boundLoad: 95, unrelatedLoad: 0),
            capability,
            clock,
            (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            _ => null,
            Binding(),
            new AdvancingWorkload(clock),
            AutoOcWorkloadMode.Core,
            requiredAverageLoadPercent: 70);

        TuneScreeningResult result = await monitor.ScreenAsync(
            capability,
            Plan(capability),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(100, result.ThroughputScore);
        Assert.Equal(1200, result.AverageFanRpm);
    }

    private static CapabilityDescriptor Capability() => new(
        "gpuclock.core:0",
        "nvidia.clock",
        "nvidia:gpu-0",
        "GPU core offset",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(-500, 500, 5, 0),
        "MHz",
        RiskLevel.Experimental,
        EvidenceLevel.SingleSystem,
        null,
        "test",
        true,
        ControlDomain.Gpu);

    private static TunePlan Plan(CapabilityDescriptor capability) => new(
        "screen",
        capability.DeviceId,
        TuningObjective.Performance,
        new Dictionary<string, TuneBounds> { [capability.Id] = new(-500, 500, 5) },
        TimeSpan.Zero,
        83,
        350,
        true,
        null,
        TimeSpan.FromHours(10),
        3);

    private static TuneSensorBindingV2 Binding() => new(
        TuneSensorBindingV2.CurrentSchemaVersion,
        "nvidia:gpu-0",
        ["lhm:gpu:0", "nvml:gpu:uuid"],
        ["temperature"],
        "bound-load",
        "core-clock",
        "memory-clock",
        "power");

    [Fact]
    public async Task ASingleLateSnapshotDoesNotAbortScreening()
    {
        // The service refreshes sensors about once a second, but a refresh can overrun that
        // while the screening workload is loading the card. One late snapshot used to abort
        // the whole Auto OC run with "No fresh temperature source"; it must now be ridden out.
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
        CapabilityDescriptor capability = Capability();
        int poll = 0;
        RuntimeTuneScreeningMonitor monitor = new(
            () =>
            {
                // The third poll returns sensors stamped well beyond the freshness window.
                DateTimeOffset stamp = ++poll == 3 ? clock.GetUtcNow() - TimeSpan.FromSeconds(8) : clock.GetUtcNow();
                return Snapshot(clock.GetUtcNow(), capability, boundLoad: 95, unrelatedLoad: 0, sensorStamp: stamp);
            },
            capability,
            clock,
            (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            _ => null,
            Binding(),
            new HealthyWorkload(clock),
            AutoOcWorkloadMode.Core,
            requiredAverageLoadPercent: 70);

        TuneScreeningResult result = await monitor.ScreenAsync(
            capability,
            Plan(capability),
            TimeSpan.FromSeconds(6),
            CancellationToken.None);

        Assert.DoesNotContain("No fresh temperature source", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SustainedTemperatureStalenessStillFailsClosed()
    {
        // Riding out a blip must not become screening blind: when no usable temperature
        // appears for the whole grace window, the run is still rejected.
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
        CapabilityDescriptor capability = Capability();
        RuntimeTuneScreeningMonitor monitor = new(
            () => Snapshot(
                clock.GetUtcNow(),
                capability,
                boundLoad: 95,
                unrelatedLoad: 0,
                sensorStamp: clock.GetUtcNow() - TimeSpan.FromSeconds(30)),
            capability,
            clock,
            (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            _ => null,
            Binding(),
            new HealthyWorkload(clock),
            AutoOcWorkloadMode.Core,
            requiredAverageLoadPercent: 70);

        TuneScreeningResult result = await monitor.ScreenAsync(
            capability,
            Plan(capability),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("No fresh temperature source", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HardwareSnapshot Snapshot(
        DateTimeOffset now,
        CapabilityDescriptor capability,
        double boundLoad,
        double unrelatedLoad,
        DateTimeOffset? sensorStamp = null) => new(
        now,
        [new HardwareDevice(capability.DeviceId, "GPU", DeviceKind.Gpu, "NVIDIA", "Test GPU", null, new Dictionary<string, string>())],
        [capability],
        [
            Sensor("temperature", "lHM:gpu:0", "GPU temperature", sensorStamp ?? now, 60, "°C"),
            Sensor("bound-load", "lHM:gpu:0", "GPU load", sensorStamp ?? now, boundLoad, "%"),
            Sensor("core-clock", "lHM:gpu:0", "GPU core clock", sensorStamp ?? now, 2000, "MHz"),
            Sensor("memory-clock", "lHM:gpu:0", "GPU memory clock", sensorStamp ?? now, 10000, "MHz"),
            Sensor("power", "nvml:gpu:uuid", "GPU power", sensorStamp ?? now, 250, "W"),
            Sensor("fan", "lhm:gpu:0", "GPU fan", sensorStamp ?? now, 1200, "RPM"),
            Sensor("other-load", "other:gpu", "Other GPU load", sensorStamp ?? now, unrelatedLoad, "%")
        ],
        [],
        [],
        []);

    private static SensorSample Sensor(
        string id,
        string deviceId,
        string name,
        DateTimeOffset now,
        double value,
        string unit) => new(id, "test", deviceId, name, now, value, unit, SensorQuality.Good, TimeSpan.Zero);

    [Fact]
    public async Task RejectsACandidateThatReturnedCorruptedValuesEvenWhileEverythingElseLooksHealthy()
    {
        // The whole point of artifact detection: load, temperature, and dispatch progress all
        // look fine, and the card is still returning wrong answers. On GDDR6X that is the
        // normal presentation of instability, because error correction stops it faulting.
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        CapabilityDescriptor capability = Capability();
        RuntimeTuneScreeningMonitor monitor = new(
            () => Snapshot(clock.GetUtcNow(), capability, boundLoad: 99, unrelatedLoad: 10),
            capability,
            clock,
            (delay, _) =>
            {
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            _ => null,
            Binding(),
            new CorruptingWorkload(clock),
            AutoOcWorkloadMode.Core,
            requiredAverageLoadPercent: 70);

        TuneScreeningResult result = await monitor.ScreenAsync(
            capability,
            Plan(capability),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("corrupted", result.Message, StringComparison.OrdinalIgnoreCase);
        // The message has to name the failure mode, or a rejection with healthy-looking
        // telemetry reads as a false positive.
        Assert.Contains("silent data corruption", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A host that is healthy in every respect except that the pattern check failed.</summary>
    private sealed class CorruptingWorkload(TimeProvider clock) : IAutoOcWorkloadController
    {
        public Task<WorkloadHostStatusV1> SetModeAsync(AutoOcWorkloadMode requested, CancellationToken cancellationToken) =>
            Task.FromResult(Status(requested));

        public Task<WorkloadHostStatusV1> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status(AutoOcWorkloadMode.Core));

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private WorkloadHostStatusV1 Status(AutoOcWorkloadMode current) => new(
            WorkloadHostStatusV1.CurrentSchemaVersion,
            "session",
            true,
            true,
            true,
            current,
            "Test GPU",
            0x10DE,
            1,
            1,
            0,
            1,
            1,
            clock.GetUtcNow(),
            null,
            ArtifactErrorCount: 7);
    }

    private sealed class HealthyWorkload(
        TimeProvider clock,
        AutoOcWorkloadMode mode = AutoOcWorkloadMode.Core) : IAutoOcWorkloadController
    {
        public Task<WorkloadHostStatusV1> SetModeAsync(AutoOcWorkloadMode requested, CancellationToken cancellationToken) =>
            Task.FromResult(Status(requested));

        public Task<WorkloadHostStatusV1> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status(mode));

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private WorkloadHostStatusV1 Status(AutoOcWorkloadMode current) => new(
            WorkloadHostStatusV1.CurrentSchemaVersion,
            "session",
            true,
            true,
            true,
            current,
            "Test GPU",
            0x10DE,
            1,
            1,
            0,
            1,
            1,
            clock.GetUtcNow(),
            null);
    }

    private sealed class AdvancingWorkload(TimeProvider clock) : IAutoOcWorkloadController
    {
        private long _dispatchCount;

        public Task<WorkloadHostStatusV1> SetModeAsync(AutoOcWorkloadMode requested, CancellationToken cancellationToken) =>
            Task.FromResult(Status(requested));

        public Task<WorkloadHostStatusV1> GetStatusAsync(CancellationToken cancellationToken)
        {
            _dispatchCount += 100;
            return Task.FromResult(Status(AutoOcWorkloadMode.Core));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private WorkloadHostStatusV1 Status(AutoOcWorkloadMode mode) => new(
            WorkloadHostStatusV1.CurrentSchemaVersion,
            "session",
            true,
            true,
            true,
            mode,
            "Test GPU",
            0x10DE,
            1,
            1,
            0,
            1,
            _dispatchCount,
            clock.GetUtcNow(),
            null);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
