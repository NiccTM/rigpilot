using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

// FullAutoOcEngine is deprecated in favour of FullAutoOcV3Engine but retained for
// the legacy StartAutoOc fallback, so its behaviour is still covered here.
#pragma warning disable CS0618 // Type or member is obsolete
public sealed class FullAutoOcEngineTests
{
    [Fact]
    public async Task KeepsCoreAppliedThroughMemoryAndCombinedScreenThenRestoresBoth()
    {
        FakeAdapter core = new("core.adapter", initialValue: 0);
        FakeAdapter memory = new("memory.adapter", initialValue: 0);
        CapabilityDescriptor coreCapability = Capability("gpuclock.core:0", core.Manifest.Id);
        CapabilityDescriptor memoryCapability = Capability("gpuclock.memory:0", memory.Manifest.Id);
        FakeWorkload workload = new();
        List<AutoOcWorkloadMode> screened = [];

        AutoOcResultV2 result = await FullAutoOcEngine.RunAsync(
            "nvidia:gpu-0",
            Request(coreCapability),
            coreCapability,
            core,
            Request(memoryCapability),
            memoryCapability,
            memory,
            TimeSpan.Zero,
            mode => new InspectingMonitor(() =>
            {
                screened.Add(mode);
                if (mode is AutoOcWorkloadMode.Memory or AutoOcWorkloadMode.Combined)
                {
                    Assert.Equal(10, core.CurrentValue);
                }
                if (mode == AutoOcWorkloadMode.Combined)
                {
                    Assert.Equal(10, memory.CurrentValue);
                }
            }),
            workload,
            reportProgress: null,
            CancellationToken.None);

        Assert.True(result.AllRequestedFamiliesVerified);
        Assert.True(result.PriorStateRestored);
        Assert.True(result.HardwareStateKnown);
        Assert.Equal(10, result.CoreOffsetMegahertz);
        Assert.Equal(10, result.MemoryOffsetMegahertz);
        Assert.NotNull(result.GeneratedProfile);
        Assert.Equal(2, result.GeneratedProfile.HardwareActions.Count);
        Assert.Equal(0, core.CurrentValue);
        Assert.Equal(0, memory.CurrentValue);
        Assert.Equal(AutoOcWorkloadMode.Stopped, workload.Mode);
        Assert.Contains(AutoOcWorkloadMode.Core, screened);
        Assert.Contains(AutoOcWorkloadMode.Memory, screened);
        Assert.Contains(AutoOcWorkloadMode.Combined, screened);
    }

    [Fact]
    public async Task AttemptsBothRestoresWhenOneReadBackFails()
    {
        FakeAdapter core = new("core.adapter", initialValue: 0);
        FakeAdapter memory = new("memory.adapter", initialValue: 0, failRollbackVerification: true);
        CapabilityDescriptor coreCapability = Capability("gpuclock.core:0", core.Manifest.Id);
        CapabilityDescriptor memoryCapability = Capability("gpuclock.memory:0", memory.Manifest.Id);

        await Assert.ThrowsAsync<HardwareOperationRecoveryException>(() => FullAutoOcEngine.RunAsync(
            "nvidia:gpu-0",
            Request(coreCapability),
            coreCapability,
            core,
            Request(memoryCapability),
            memoryCapability,
            memory,
            TimeSpan.Zero,
            _ => new InspectingMonitor(() => { }),
            new FakeWorkload(),
            reportProgress: null,
            CancellationToken.None));

        Assert.Equal(1, memory.RollbackAttempts);
        Assert.Equal(1, core.RollbackAttempts);
        Assert.Equal(0, core.CurrentValue);
    }

    private static StartTuneRequest Request(CapabilityDescriptor capability) => new(
        new TunePlan(
            Guid.NewGuid().ToString("N"),
            capability.DeviceId,
            TuningObjective.Performance,
            new Dictionary<string, TuneBounds>
            {
                [capability.Id] = new TuneBounds(0, 10, 10)
            },
            TimeSpan.Zero,
            83,
            null,
            Provisional: true,
            SoakStartedAt: null,
            ActiveUseRequired: TimeSpan.FromHours(10),
            ColdBootsRequired: 3),
        capability.Id,
        TuneDirection.Maximize,
        ConfirmExperimental: true,
        ConfirmDevice: true,
        CandidateScreeningTime: TimeSpan.Zero,
        MaximumCandidates: 2);

    private static CapabilityDescriptor Capability(string id, string adapterId) => new(
        id,
        adapterId,
        "nvidia:gpu-0",
        id,
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(0, 10, 10, 0),
        "MHz",
        RiskLevel.Experimental,
        EvidenceLevel.SingleSystem,
        null,
        "test",
        CanResetToDefault: true,
        Domain: ControlDomain.Gpu);

    private sealed class InspectingMonitor(Action inspect) : ITuneScreeningMonitor
    {
        public Task<TuneScreeningResult> ScreenAsync(
            CapabilityDescriptor capability,
            TunePlan plan,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            inspect();
            return Task.FromResult(new TuneScreeningResult(true, "Passed.", 60, 300, 1800));
        }
    }

    private sealed class FakeWorkload : IAutoOcWorkloadController
    {
        public AutoOcWorkloadMode Mode { get; private set; }

        public Task<WorkloadHostStatusV1> SetModeAsync(AutoOcWorkloadMode mode, CancellationToken cancellationToken)
        {
            Mode = mode;
            return Task.FromResult(Status());
        }

        public Task<WorkloadHostStatusV1> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status());

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Mode = AutoOcWorkloadMode.Stopped;
            return Task.CompletedTask;
        }

        private WorkloadHostStatusV1 Status() => new(
            WorkloadHostStatusV1.CurrentSchemaVersion,
            "test-session",
            Authenticated: true,
            Ready: true,
            Running: Mode != AutoOcWorkloadMode.Stopped,
            Mode,
            "Test GPU",
            0x10DE,
            1,
            1,
            0,
            1,
            1,
            DateTimeOffset.UtcNow,
            null);
    }

    private sealed class FakeAdapter(string id, double initialValue, bool failRollbackVerification = false) : IHardwareAdapter, IHardwareStateVerifier
    {
        public double CurrentValue { get; private set; } = initialValue;
        public int RollbackAttempts { get; private set; }

        public AdapterManifest Manifest { get; } = new(
            id, id, "1", "GPL-3.0-only", null, AdapterExecutionContext.SystemService, ["gpu"], ["test"]);

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new PreparedAction(action, ControlValue.FromNumeric(CurrentValue), DateTimeOffset.UtcNow, "test"));

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            CurrentValue = action.Action.Value.Numeric!.Value;
            return Task.CompletedTask;
        }

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionVerification(
                action.Action.Id,
                Math.Abs(CurrentValue - action.Action.Value.Numeric!.Value) < 0.001,
                ControlValue.FromNumeric(CurrentValue),
                "Verified."));

        public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            RollbackAttempts++;
            CurrentValue = action.PreviousValue!.Numeric!.Value;
            return Task.CompletedTask;
        }

        public Task<HardwareStateVerification> VerifyRollbackStateAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new HardwareStateVerification(
                Manifest.Id,
                action.Action.CapabilityId,
                !failRollbackVerification && Math.Abs(CurrentValue - action.PreviousValue!.Numeric!.Value) < 0.001,
                ControlValue.FromNumeric(CurrentValue),
                failRollbackVerification ? "Injected rollback read-back failure." : "Rollback verified."));

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
        {
            CurrentValue = 0;
            return Task.CompletedTask;
        }

        public Task<HardwareStateVerification> VerifyDefaultStateAsync(string capabilityId, CancellationToken cancellationToken) =>
            Task.FromResult(new HardwareStateVerification(Manifest.Id, capabilityId, CurrentValue == 0, ControlValue.FromNumeric(CurrentValue), "Default verified."));

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SensorSample>>([]);
        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "Healthy", []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
#pragma warning restore CS0618
