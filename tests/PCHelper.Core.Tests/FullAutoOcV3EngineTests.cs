using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class FullAutoOcV3EngineTests
{
    [Fact]
    public async Task PerformanceRunMeasuresBaselineTunesThreeFamiliesAndRestoresPriorState()
    {
        FakeAdapter coreAdapter = new("core.adapter", 0);
        FakeAdapter memoryAdapter = new("memory.adapter", 0);
        FakeAdapter powerAdapter = new("power.adapter", 200);
        CapabilityDescriptor core = Capability("gpuclock.core:0", coreAdapter.Manifest.Id, new NumericRange(0, 20, 10, 0), "MHz");
        CapabilityDescriptor memory = Capability("gpuclock.memory:0", memoryAdapter.Manifest.Id, new NumericRange(0, 20, 10, 0), "MHz");
        CapabilityDescriptor power = Capability("gpupower.limit:0", powerAdapter.Manifest.Id, new NumericRange(150, 250, 25, 200), "W");
        AutoOcObjectiveConstraintsV3 constraints = new(
            TuningObjective.Performance,
            BaselineSampleDuration: TimeSpan.Zero,
            CandidateScreeningDuration: TimeSpan.Zero,
            FinalScreeningDuration: TimeSpan.Zero);
        FakeWorkload workload = new();

        AutoOcResultV3 result = await FullAutoOcV3Engine.RunAsync(
            "gpu:0",
            constraints,
            Fingerprint(),
            new AutoOcTuneStage(Request(core, TuneDirection.Maximize), core, coreAdapter),
            new AutoOcTuneStage(Request(memory, TuneDirection.Maximize), memory, memoryAdapter),
            new AutoOcTuneStage(Request(power, TuneDirection.Maximize, 200, 250), power, powerAdapter),
            mode => new ObjectiveMonitor(mode, coreAdapter, memoryAdapter, powerAdapter),
            workload,
            null,
            CancellationToken.None);

        Assert.Equal(AutoOcValidationState.Provisional, result.ValidationState);
        Assert.True(result.AllRequestedFamiliesVerified);
        Assert.Equal(3, result.BaselineMeasurements.Count);
        Assert.Equal(0, result.BaselineVariationPercent);
        Assert.Equal(20, result.CoreOffsetMegahertz);
        Assert.Equal(20, result.MemoryOffsetMegahertz);
        Assert.Equal(250, result.PowerLimitWatts);
        Assert.Equal(3, result.GeneratedProfile!.HardwareActions.Count);
        Assert.True(result.RestorationProof.PriorStateRestored);
        Assert.Equal(3, result.RestorationProof.Verifications.Count);
        Assert.Equal(0, coreAdapter.CurrentValue);
        Assert.Equal(0, memoryAdapter.CurrentValue);
        Assert.Equal(200, powerAdapter.CurrentValue);
        Assert.True(workload.Stopped);
    }

    [Fact]
    public async Task NoisyBaselineAppliesNoCandidatesAndReturnsRejectedWithKnownState()
    {
        FakeAdapter coreAdapter = new("core.adapter", 0);
        FakeAdapter memoryAdapter = new("memory.adapter", 0);
        CapabilityDescriptor core = Capability("gpuclock.core:0", coreAdapter.Manifest.Id, new NumericRange(0, 20, 10, 0), "MHz");
        CapabilityDescriptor memory = Capability("gpuclock.memory:0", memoryAdapter.Manifest.Id, new NumericRange(0, 20, 10, 0), "MHz");
        int baseline = 0;

        AutoOcResultV3 result = await FullAutoOcV3Engine.RunAsync(
            "gpu:0",
            new AutoOcObjectiveConstraintsV3(
                TuningObjective.Performance,
                MaximumBaselineVariationPercent: 3,
                BaselineSampleDuration: TimeSpan.Zero,
                CandidateScreeningDuration: TimeSpan.Zero,
                FinalScreeningDuration: TimeSpan.Zero),
            Fingerprint(),
            new AutoOcTuneStage(Request(core, TuneDirection.Maximize), core, coreAdapter),
            new AutoOcTuneStage(Request(memory, TuneDirection.Maximize), memory, memoryAdapter),
            null,
            mode => new DelegateMonitor((_, _, _, _) => Task.FromResult(new TuneScreeningResult(
                true,
                "pass",
                60,
                200,
                1900,
                ThroughputScore: ++baseline == 2 ? 110 : 100,
                AverageFanRpm: 1000))),
            new FakeWorkload(),
            null,
            CancellationToken.None);

        Assert.Equal(AutoOcValidationState.Rejected, result.ValidationState);
        Assert.False(result.AllRequestedFamiliesVerified);
        Assert.Empty(result.CandidateScores);
        Assert.Null(result.GeneratedProfile);
        Assert.True(result.RestorationProof.HardwareStateKnown);
        Assert.Equal(0, coreAdapter.ApplyCount);
        Assert.Equal(0, memoryAdapter.ApplyCount);
    }

    private static StartTuneRequest Request(
        CapabilityDescriptor capability,
        TuneDirection direction,
        double minimum = 0,
        double maximum = 20) => new(
            new TunePlan(
                Guid.NewGuid().ToString("N"),
                capability.DeviceId,
                TuningObjective.Performance,
                new Dictionary<string, TuneBounds> { [capability.Id] = new(minimum, maximum, capability.Range!.Step) },
                TimeSpan.Zero,
                83,
                null,
                true,
                null,
                TimeSpan.FromHours(10),
                3),
            capability.Id,
            direction,
            true,
            true,
            TimeSpan.Zero,
            MaximumCandidates: 4,
            RefinementCandidates: 0,
            SafetyMargin: 0,
            ThermalHeadroomCelsius: 0);

    private static CapabilityDescriptor Capability(string id, string adapterId, NumericRange range, string unit) => new(
        id,
        adapterId,
        "gpu:0",
        id,
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        range,
        unit,
        RiskLevel.Experimental,
        EvidenceLevel.SingleSystem,
        null,
        "test",
        true,
        ControlDomain.Gpu);

    private static HardwareFingerprintV1 Fingerprint() => new(
        HardwareFingerprintV1.CurrentSchemaVersion,
        "gpu:0",
        "GPU-test",
        "PCI\\VEN_10DE&DEV_2204",
        "95.02.42.00.A1",
        "610.62",
        new string('a', 64));

    private sealed class ObjectiveMonitor(
        AutoOcWorkloadMode mode,
        FakeAdapter core,
        FakeAdapter memory,
        FakeAdapter power) : ITuneScreeningMonitor
    {
        public Task<TuneScreeningResult> ScreenAsync(
            CapabilityDescriptor capability,
            TunePlan plan,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            double throughput = 100 + core.CurrentValue + (memory.CurrentValue / 2) + ((power.CurrentValue - 200) / 10);
            double watts = Math.Max(1, power.CurrentValue + core.CurrentValue + memory.CurrentValue);
            return Task.FromResult(new TuneScreeningResult(
                true,
                $"{mode} pass",
                60,
                watts,
                1900 + core.CurrentValue,
                throughput,
                1000));
        }
    }

    private sealed class DelegateMonitor(
        Func<CapabilityDescriptor, TunePlan, TimeSpan, CancellationToken, Task<TuneScreeningResult>> screen) : ITuneScreeningMonitor
    {
        public Task<TuneScreeningResult> ScreenAsync(
            CapabilityDescriptor capability,
            TunePlan plan,
            TimeSpan duration,
            CancellationToken cancellationToken) => screen(capability, plan, duration, cancellationToken);
    }

    private sealed class FakeWorkload : IAutoOcWorkloadController
    {
        private AutoOcWorkloadMode _mode;
        public bool Stopped { get; private set; }

        public Task<WorkloadHostStatusV1> SetModeAsync(AutoOcWorkloadMode mode, CancellationToken cancellationToken)
        {
            _mode = mode;
            return Task.FromResult(Status(running: true));
        }

        public Task<WorkloadHostStatusV1> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status(running: _mode != AutoOcWorkloadMode.Stopped));

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            _mode = AutoOcWorkloadMode.Stopped;
            return Task.CompletedTask;
        }

        private WorkloadHostStatusV1 Status(bool running) => new(
            WorkloadHostStatusV1.CurrentSchemaVersion,
            "session",
            true,
            true,
            running,
            _mode,
            "GPU",
            0x10DE,
            0x2204,
            1,
            0,
            1,
            100,
            DateTimeOffset.UtcNow,
            null);
    }

    private sealed class FakeAdapter : IHardwareAdapter, IHardwareStateVerifier
    {
        private readonly double _initial;

        public FakeAdapter(string id, double initial)
        {
            _initial = initial;
            CurrentValue = initial;
            Manifest = new AdapterManifest(id, id, "1", "test", null, AdapterExecutionContext.SystemService, ["test"], ["Gpu"]);
        }

        public AdapterManifest Manifest { get; }
        public double CurrentValue { get; private set; }
        public int ApplyCount { get; private set; }

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterProbeResult(Manifest, [], [], []));

        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SensorSample>>([]);

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new PreparedAction(action, ControlValue.FromNumeric(CurrentValue), DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")));

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            ApplyCount++;
            CurrentValue = action.Action.Value.Numeric!.Value;
            return Task.CompletedTask;
        }

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionVerification(action.Action.Id, CurrentValue == action.Action.Value.Numeric, ControlValue.FromNumeric(CurrentValue), "read back"));

        public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            CurrentValue = action.PreviousValue!.Numeric!.Value;
            return Task.CompletedTask;
        }

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
        {
            CurrentValue = _initial;
            return Task.CompletedTask;
        }

        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "healthy", []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<HardwareStateVerification> VerifyRollbackStateAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            double expected = action.PreviousValue!.Numeric!.Value;
            return Task.FromResult(new HardwareStateVerification(Manifest.Id, action.Action.CapabilityId, CurrentValue == expected, ControlValue.FromNumeric(CurrentValue), "rollback read back"));
        }

        public Task<HardwareStateVerification> VerifyDefaultStateAsync(string capabilityId, CancellationToken cancellationToken) =>
            Task.FromResult(new HardwareStateVerification(Manifest.Id, capabilityId, CurrentValue == _initial, ControlValue.FromNumeric(CurrentValue), "default read back"));
    }
}
