using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class FanCommissioningWorkflowTests
{
    [Fact]
    public void CpuAndPumpSessionsNeverAllowFanStop()
    {
        FanCommissioningSessionV1 session = Session() with { IsCpuOrPump = true, AllowFanStop = true };

        SuiteValidationResult result = FanCommissioningWorkflow.Validate(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("CPU fans", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfirmThenCompleteRequiresPhysicalIdentityAndCalibrationEvidence()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FanCommissioningSessionV1 confirmed = FanCommissioningWorkflow.Confirm(
            Session(),
            headerConfirmed: true,
            physicalHeaderObserved: true,
            headerName: "Front intake",
            notes: "Observed with case open.",
            now: now);
        FanCommissioningSessionV1 completed = FanCommissioningWorkflow.Complete(
            confirmed,
            "lhm.control:/lpc/nct6798d/control/1",
            now: now.AddMinutes(1));

        Assert.Equal(FanCommissioningState.ReadyForCalibration, confirmed.State);
        Assert.True(confirmed.HeaderConfirmed);
        Assert.True(confirmed.PhysicalHeaderObserved);
        Assert.Equal(FanCommissioningState.Completed, completed.State);
        Assert.True(FanCommissioningWorkflow.Validate(completed).IsValid);
    }

    [Fact]
    public void CompletionCannotSkipConfirmation()
    {
        Assert.Throws<InvalidOperationException>(() => FanCommissioningWorkflow.Complete(
            Session(),
            "calibration.test",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UserDeclaredHeaderCannotFinaliseCommissioningWithoutPhysicalObservation()
    {
        FanCommissioningSessionV1 declared = FanCommissioningWorkflow.Confirm(
            Session(),
            headerConfirmed: true,
            physicalHeaderObserved: false,
            headerName: "CASE_FAN_1",
            notes: "User-declared generic mapping.",
            now: DateTimeOffset.UtcNow);

        Assert.True(declared.HeaderConfirmed);
        Assert.False(declared.PhysicalHeaderObserved);
        Assert.Throws<InvalidOperationException>(() => FanCommissioningWorkflow.Complete(
            declared,
            "calibration.test",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IdentificationPulseIsAlwaysACurrentProtocolMutation()
    {
        Assert.True(IpcCommandPolicy.IsMutation(IpcCommand.PulseFanCommissioning));
        Assert.False(IpcCommandPolicy.IsReadOnly(IpcCommand.PulseFanCommissioning));
    }

    [Fact]
    public void HistoricalOperationLookupIsReadOnly()
    {
        Assert.True(IpcCommandPolicy.IsReadOnly(IpcCommand.GetOperationById));
        Assert.False(IpcCommandPolicy.IsMutation(IpcCommand.GetOperationById));
    }

    [Fact]
    public void PersistedFanCalibrationLookupIsReadOnly()
    {
        Assert.True(IpcCommandPolicy.IsReadOnly(IpcCommand.GetFanCalibrations));
        Assert.False(IpcCommandPolicy.IsMutation(IpcCommand.GetFanCalibrations));
    }

    [Fact]
    public void NoWritePreflightStillRequiresCurrentProtocolOperatorAuthorization()
    {
        // The route persists a commissioning evidence record but cannot touch a
        // controller. Keeping it a current-protocol mutation prevents a legacy
        // or anonymous client from creating misleading qualification evidence.
        Assert.True(IpcCommandPolicy.IsMutation(IpcCommand.PreflightFanCommissioning));
        Assert.False(IpcCommandPolicy.IsReadOnly(IpcCommand.PreflightFanCommissioning));
    }

    [Fact]
    public void InteractiveNoWriteDiagnosticMessagesRemainCurrentProtocolMutations()
    {
        Assert.True(IpcCommandPolicy.IsMutation(IpcCommand.RunInteractiveFanPreflight));
        Assert.True(IpcCommandPolicy.IsMutation(IpcCommand.SubmitInteractiveFanPreflight));
        Assert.False(IpcCommandPolicy.IsReadOnly(IpcCommand.RunInteractiveFanPreflight));
    }

    [Fact]
    public void CoolingOutputRoleRegistryIsReadOnlyForQueriesAndOperatorGuardedForChanges()
    {
        Assert.True(IpcCommandPolicy.IsReadOnly(IpcCommand.GetCoolingOutputAssignments));
        Assert.False(IpcCommandPolicy.IsMutation(IpcCommand.GetCoolingOutputAssignments));
        Assert.True(IpcCommandPolicy.IsMutation(IpcCommand.SaveCoolingOutputAssignment));
        Assert.False(IpcCommandPolicy.IsReadOnly(IpcCommand.SaveCoolingOutputAssignment));
    }

    [Fact]
    public void MonitorBrightnessQueryIsLegacySafeButWritesRemainCurrentProtocolMutations()
    {
        Assert.True(IpcCommandPolicy.IsReadOnly(IpcCommand.GetMonitorBrightnesses));
        Assert.False(IpcCommandPolicy.IsMutation(IpcCommand.GetMonitorBrightnesses));
        Assert.True(IpcCommandPolicy.IsMutation(IpcCommand.SetMonitorBrightness));
        Assert.False(IpcCommandPolicy.IsReadOnly(IpcCommand.SetMonitorBrightness));
    }

    [Fact]
    public void IdentificationPulseIsBoundedAndNeverUsesZeroWhenAPositiveRangeExists()
    {
        double fullRange = FanCommissioningWorkflow.GetIdentificationPulseDuty(new NumericRange(0, 100, 5));
        double constrainedRange = FanCommissioningWorkflow.GetIdentificationPulseDuty(new NumericRange(30, 45, 5));

        Assert.Equal(60, fullRange);
        Assert.Equal(45, constrainedRange);
        Assert.Throws<InvalidOperationException>(() => FanCommissioningWorkflow.GetIdentificationPulseDuty(new NumericRange(0, 0, 5)));
    }

    [Theory]
    [InlineData("CHA_FAN1")]
    [InlineData("cha fan 2")]
    [InlineData("SYS-FAN3")]
    public void DeclaredChassisHeaderIsRecognised(string headerName)
    {
        Assert.True(FanCommissioningWorkflow.IsDeclaredChassisHeader(headerName));
    }

    [Theory]
    [InlineData("Fan #1")]
    [InlineData("CPU_FAN")]
    [InlineData("front intake")]
    public void GenericOrProtectedHeaderLabelsCannotAuthoriseAPulse(string headerName)
    {
        FanCommissioningSessionV1 session = Session() with { HeaderName = headerName };

        Assert.False(FanCommissioningWorkflow.CanIssueIdentificationPulse(session, out string? reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void CpuOrPumpSessionCannotIssueIdentificationPulseEvenWithAChassisLookingLabel()
    {
        FanCommissioningSessionV1 session = Session() with { IsCpuOrPump = true };

        Assert.False(FanCommissioningWorkflow.CanIssueIdentificationPulse(session, out string? reason));
        Assert.Contains("CPU", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedNoWritePreflightClosesTheSessionBeforeAnyPhysicalPulse()
    {
        FanCommissioningSessionV1 failed = FanCommissioningWorkflow.FailNoWritePreflight(
            Session(),
            "Adapter Prepare failed at Open.",
            DateTimeOffset.UtcNow);

        Assert.Equal(FanCommissioningState.Failed, failed.State);
        Assert.Contains("Prepare failed", failed.Error, StringComparison.Ordinal);
        Assert.False(FanCommissioningWorkflow.CanIssueIdentificationPulse(failed, out string? reason));
        Assert.Contains("awaiting", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => FanCommissioningWorkflow.Confirm(
            failed,
            headerConfirmed: true,
            physicalHeaderObserved: true,
            headerName: "CHA_FAN1",
            notes: null,
            now: DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task IdentificationPreflightPreparesButNeverWrites()
    {
        TrackingAdapter adapter = new();

        PreparedAction prepared = await FanCommissioningWorkflow.PrepareIdentificationPulseAsync(
            Capability(),
            adapter,
            CancellationToken.None);

        Assert.Equal(1, adapter.PrepareCalls);
        Assert.Equal(0, adapter.ApplyCalls);
        Assert.Equal(0, adapter.RollbackCalls);
        Assert.Equal(0, adapter.ResetCalls);
        Assert.Equal(60, prepared.Action.Value.Numeric);
    }

    [Fact]
    public async Task IdentificationPreflightFailureNeverAttemptsRecoveryOrApply()
    {
        TrackingAdapter adapter = new() { ThrowOnPrepare = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FanCommissioningWorkflow.PrepareIdentificationPulseAsync(
                Capability(),
                adapter,
                CancellationToken.None));

        Assert.Equal(1, adapter.PrepareCalls);
        Assert.Equal(0, adapter.ApplyCalls);
        Assert.Equal(0, adapter.RollbackCalls);
        Assert.Equal(0, adapter.ResetCalls);
    }

    private static FanCommissioningSessionV1 Session() => new(
        FanCommissioningSessionV1.CurrentSchemaVersion,
        "commission.test",
        "lhm.control:/lpc/nct6798d/control/1",
        "lhm.sensor:/lpc/nct6798d/fan/1",
        "CHA_FAN1",
        FanCommissioningState.AwaitingIdentification,
        IsCpuOrPump: false,
        AllowFanStop: true,
        HeaderConfirmed: false,
        CalibrationId: null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        null);

    private static CapabilityDescriptor Capability() => new(
        "lhm.control:/lpc/nct6798d/0/control/0",
        "librehardwaremonitor",
        "lhm.device:/lpc/nct6798d/0",
        "Fan #1",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.AdapterHost,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "%",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "Test control.",
        CanResetToDefault: true,
        Domain: ControlDomain.Cooling);

    private sealed class TrackingAdapter : IHardwareAdapter
    {
        public bool ThrowOnPrepare { get; init; }

        public int PrepareCalls { get; private set; }

        public int ApplyCalls { get; private set; }

        public int RollbackCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public AdapterManifest Manifest { get; } = new(
            "test.preflight",
            "Preflight test adapter",
            "1.0.0",
            "GPL-3.0-only",
            null,
            AdapterExecutionContext.AdapterHost,
            ["test"],
            ["Cooling"]);

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterProbeResult(Manifest, [], [], []));

        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SensorSample>>([]);

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return ThrowOnPrepare
                ? Task.FromException<PreparedAction>(new InvalidOperationException("Software-control preflight failed."))
                : Task.FromResult(new PreparedAction(action, null, DateTimeOffset.UtcNow, "preflight-token"));
        }

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            return Task.CompletedTask;
        }

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionVerification(action.Action.Id, true, action.Action.Value, "ok"));

        public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            RollbackCalls++;
            return Task.CompletedTask;
        }

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
        {
            ResetCalls++;
            return Task.CompletedTask;
        }

        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "ok", []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
