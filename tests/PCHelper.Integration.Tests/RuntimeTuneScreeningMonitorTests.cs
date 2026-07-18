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

    private static HardwareSnapshot Snapshot(
        DateTimeOffset now,
        CapabilityDescriptor capability,
        double boundLoad,
        double unrelatedLoad) => new(
        now,
        [new HardwareDevice(capability.DeviceId, "GPU", DeviceKind.Gpu, "NVIDIA", "Test GPU", null, new Dictionary<string, string>())],
        [capability],
        [
            Sensor("temperature", "lHM:gpu:0", "GPU temperature", now, 60, "°C"),
            Sensor("bound-load", "lHM:gpu:0", "GPU load", now, boundLoad, "%"),
            Sensor("core-clock", "lHM:gpu:0", "GPU core clock", now, 2000, "MHz"),
            Sensor("memory-clock", "lHM:gpu:0", "GPU memory clock", now, 10000, "MHz"),
            Sensor("power", "nvml:gpu:uuid", "GPU power", now, 250, "W"),
            Sensor("other-load", "other:gpu", "Other GPU load", now, unrelatedLoad, "%")
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

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
