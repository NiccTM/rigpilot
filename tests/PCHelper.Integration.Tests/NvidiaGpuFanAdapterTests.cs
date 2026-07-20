using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class NvidiaGpuFanAdapterTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private static string CapabilityId => $"{NvidiaGpuFanAdapter.CapabilityPrefix}{ChannelId}";

    [Fact]
    public async Task ProbeIsReadOnlyByDefaultAndExperimentalOnlyWhenWritesEnabled()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));

        await using NvidiaGpuFanAdapter readOnly = new(transport, DeviceId, ChannelId);
        await using NvidiaGpuFanAdapter writable = new(transport, DeviceId, ChannelId, enableWrites: true);

        CapabilityDescriptor readOnlyCap = Assert.Single((await readOnly.ProbeAsync(default)).Capabilities);
        CapabilityDescriptor writableCap = Assert.Single((await writable.ProbeAsync(default)).Capabilities);

        Assert.Equal(CapabilityAccessState.ReadOnly, readOnlyCap.State);
        Assert.Equal(CapabilityAccessState.Experimental, writableCap.State);
        Assert.True(writableCap.CanResetToDefault);
        Assert.Equal(50, writableCap.Range!.Minimum);
        Assert.Equal(100, writableCap.Range!.Maximum);
    }

    [Fact]
    public async Task ArmingFlipsTheCapabilityFromReadOnlyToExperimentalAndPermitsWrites()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        bool armed = false;
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, () => armed);

        CapabilityDescriptor disarmed = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);
        Assert.Equal(CapabilityAccessState.ReadOnly, disarmed.State);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(Action(70), default));

        armed = true;
        CapabilityDescriptor live = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);
        Assert.Equal(CapabilityAccessState.Experimental, live.State);
        PreparedAction prepared = await adapter.PrepareAsync(Action(70), default);
        await adapter.ApplyAsync(prepared, default);
        Assert.Equal([70], transport.ManualDutyCommands);

        // Disarming again blocks further writes immediately.
        armed = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(Action(60), default));
    }

    [Fact]
    public async Task WritesAreHardBlockedWhenNotEnabled()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId);

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(Action(60), default));
        Assert.Empty(transport.ManualDutyCommands);
    }

    [Fact]
    public async Task PrepareRejectsDutyBelowTheFloor()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        await Assert.ThrowsAsync<GpuFanSafetyException>(() => adapter.PrepareAsync(Action(40), default));
        Assert.Empty(transport.ManualDutyCommands);
    }

    [Fact]
    public async Task PrepareRejectsDutyAboveTheCeiling()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 90));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        await Assert.ThrowsAsync<GpuFanSafetyException>(() => adapter.PrepareAsync(Action(95), default));
    }

    [Fact]
    public async Task ApplyWritesManualDutyAndVerifyConfirmsReadBack()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(70), default);
        await adapter.ApplyAsync(prepared, default);
        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.Equal(GpuFanControlPolicy.Manual, transport.State.Policy);
        Assert.Equal(70, transport.State.CommandedDutyPercent);
        Assert.Equal([70], transport.ManualDutyCommands);
        Assert.True(verification.Success);
    }

    [Fact]
    public async Task ApplyRefusesAnUncheckedOutOfRangeValue()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        // Bypass Prepare with a hand-built out-of-range prepared action.
        PreparedAction rogue = new(Action(20), null, DateTimeOffset.UtcNow, string.Empty);

        await Assert.ThrowsAsync<GpuFanSafetyException>(() => adapter.ApplyAsync(rogue, default));
        Assert.Empty(transport.ManualDutyCommands);
    }

    [Fact]
    public async Task VerifyFailsWhenReadBackDoesNotMatch()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(70), default);
        await adapter.ApplyAsync(prepared, default);
        transport.OverrideMeasuredDuty(40); // hardware did not reach the commanded duty

        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.False(verification.Success);
    }

    // Regression guard for the 2026-07-15 defect: NvmlGpuFanCoolerTransport.ReadStateAsync
    // hard-coded the policy to Automatic, so a manual write's Verify (which requires
    // Policy == Manual) always rolled back even though the duty matched. This locks the
    // adapter contract: a transport that reports Automatic must fail Verify regardless of
    // the read-back duty, so any transport must report the true fan-control policy.
    [Fact]
    public async Task VerifyFailsWhenPolicyReadsAutomaticEvenIfDutyMatches()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(70), default);
        await adapter.ApplyAsync(prepared, default);
        // The write reached the hardware (duty is exactly 70) but the transport reports
        // the policy as Automatic — precisely the pre-fix NVML read-back behaviour.
        transport.OverrideReportedPolicy(GpuFanControlPolicy.Automatic);

        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.False(verification.Success);
    }

    [Fact]
    public async Task RollbackRestoresAPriorManualDuty()
    {
        FakeGpuFanCoolerTransport transport = new(
            new GpuFanBounds(50, 100),
            initial: new GpuFanChannelState(GpuFanControlPolicy.Manual, 55, 55));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(80), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.RollbackAsync(prepared, default);

        Assert.Equal(GpuFanControlPolicy.Manual, transport.State.Policy);
        Assert.Equal(55, transport.State.CommandedDutyPercent);
    }

    [Fact]
    public async Task RollbackReturnsToAutomaticWhenPriorStateWasAutomatic()
    {
        FakeGpuFanCoolerTransport transport = new(
            new GpuFanBounds(50, 100),
            initial: new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, 30));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(80), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.RollbackAsync(prepared, default);

        Assert.Equal(GpuFanControlPolicy.Automatic, transport.State.Policy);
        Assert.True(transport.RestoredAutomatic);
    }

    [Fact]
    public async Task ResetToDefaultReturnsTheAutomaticCurve()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(70), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.ResetToDefaultAsync(CapabilityId, default);

        Assert.Equal(GpuFanControlPolicy.Automatic, transport.State.Policy);
        Assert.True(transport.RestoredAutomatic);
    }

    [Fact]
    public async Task ConflictBlocksTheCapabilityAndRefusesPrepare()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(
            transport,
            DeviceId,
            ChannelId,
            enableWrites: true,
            isConflicted: _ => Task.FromResult(true));

        CapabilityDescriptor cap = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);
        Assert.Equal(CapabilityAccessState.Blocked, cap.State);
        await Assert.ThrowsAsync<GpuFanSafetyException>(() => adapter.PrepareAsync(Action(70), default));
        Assert.Empty(transport.ManualDutyCommands);
    }

    [Fact]
    public async Task PrepareRefusesWhenBoundsAreUnavailable()
    {
        FakeGpuFanCoolerTransport transport = new(bounds: null);
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        await Assert.ThrowsAsync<GpuFanSafetyException>(() => adapter.PrepareAsync(Action(70), default));
    }

    private static ProfileAction Action(int duty) => new(
        "action-1",
        NvidiaGpuFanAdapter.AdapterId,
        CapabilityId,
        ControlValue.FromNumeric(duty),
        Required: true,
        Order: 0);

    private sealed class FakeGpuFanCoolerTransport(
        GpuFanBounds? bounds,
        GpuFanChannelState? initial = null) : IGpuFanCoolerTransport
    {
        private int? _measuredOverride;
        private GpuFanControlPolicy? _policyOverride;

        public GpuFanChannelState State { get; private set; } =
            initial ?? new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null);

        public List<int> ManualDutyCommands { get; } = [];

        public bool RestoredAutomatic { get; private set; }

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        public void OverrideMeasuredDuty(int measured) => _measuredOverride = measured;

        public void OverrideReportedPolicy(GpuFanControlPolicy policy) => _policyOverride = policy;

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(bounds);

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
        {
            GpuFanChannelState state = State;
            if (_measuredOverride is int measured)
            {
                state = state with { MeasuredDutyPercent = measured };
            }

            if (_policyOverride is GpuFanControlPolicy policy)
            {
                state = state with { Policy = policy };
            }

            return Task.FromResult(state);
        }

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
        {
            ManualDutyCommands.Add(dutyPercent);
            State = new GpuFanChannelState(GpuFanControlPolicy.Manual, dutyPercent, dutyPercent);
            return Task.CompletedTask;
        }

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
        {
            RestoredAutomatic = true;
            State = new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, State.MeasuredDutyPercent);
            return Task.CompletedTask;
        }
    }
}
