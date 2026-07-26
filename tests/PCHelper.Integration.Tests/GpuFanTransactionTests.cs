using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Drives the real <see cref="NvidiaGpuFanAdapter"/> through the real
/// <see cref="ProfileTransactionEngine"/> so the GPU-fan write obeys the
/// transaction invariants: it commits on success and rolls the fan back to its
/// prior state on a mid-transaction verification failure. The physical cooler is
/// an in-memory fake — no hardware is touched.
/// </summary>
public sealed class GpuFanTransactionTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private static string CapabilityId => $"{NvidiaGpuFanAdapter.CapabilityPrefix}{ChannelId}";

    [Fact]
    public async Task GpuFanWriteCommitsThroughTheTransactionEngine()
    {
        FakeGpuFanCoolerTransport transport = new(
            new GpuFanBounds(50, 100),
            new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, 30));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        (ProfileTransaction transaction, ProfileValidationResult validation) = await engine.ApplyAsync(
            FanProfile(dutyPercent: 70),
            Capabilities(CapabilityAccessState.Experimental),
            expectedRevision: 0,
            confirmExperimental: true,
            CancellationToken.None);

        Assert.True(validation.Valid);
        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
        Assert.Equal(GpuFanControlPolicy.Manual, transport.State.Policy);
        Assert.Equal(70, transport.State.CommandedDutyPercent);
        Assert.Equal(1, engine.Revision);
    }

    [Fact]
    public async Task GpuFanWriteRollsBackToAutomaticOnVerificationFailure()
    {
        // Fan does not reach the commanded duty; verification must fail and the
        // engine must roll the fan back toward its prior automatic curve.
        FakeGpuFanCoolerTransport transport = new(
            new GpuFanBounds(50, 100),
            new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, 30))
        {
            StickyMeasuredDuty = 30
        };
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            FanProfile(dutyPercent: 80),
            Capabilities(CapabilityAccessState.Experimental),
            expectedRevision: 0,
            confirmExperimental: true,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.RolledBack, transaction.State);
        Assert.Equal(GpuFanControlPolicy.Automatic, transport.State.Policy);
        Assert.True(transport.RestoredAutomatic);
        Assert.Equal(0, engine.Revision);
    }

    private static ProfileV1 FanProfile(int dutyPercent) => new(
        ProfileV1.CurrentSchemaVersion,
        "gpu-fan-profile",
        "GPU fan",
        "Experimental GPU fan duty",
        [new ProfileAction("fan-action", NvidiaGpuFanAdapter.AdapterId, CapabilityId, ControlValue.FromNumeric(dutyPercent), Required: true, Order: 0)],
        new SafetyLimits(),
        [],
        IsBuiltIn: false,
        IsExperimental: true);

    private static Dictionary<string, CapabilityDescriptor> Capabilities(CapabilityAccessState state) =>
        new()
        {
            [CapabilityId] = new CapabilityDescriptor(
                CapabilityId,
                NvidiaGpuFanAdapter.AdapterId,
                DeviceId,
                "GPU fan duty",
                state,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(50, 100, 1),
                "%",
                RiskLevel.Experimental,
                EvidenceLevel.Detected,
                null,
                "GPU fan duty",
                CanResetToDefault: true,
                Domain: ControlDomain.Cooling)
        };

    private sealed class FakeGpuFanCoolerTransport(
        GpuFanBounds? bounds,
        GpuFanChannelState? initial = null) : IGpuFanCoolerTransport
    {
        public GpuFanChannelState State { get; private set; } =
            initial ?? new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null);

        public bool RestoredAutomatic { get; private set; }

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        /// <summary>When set, read-back always reports this measured duty regardless of the command.</summary>
        public int? StickyMeasuredDuty { get; init; }

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(bounds);

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
        {
            GpuFanChannelState state = StickyMeasuredDuty is int measured
                ? State with { MeasuredDutyPercent = measured }
                : State;
            return Task.FromResult(state);
        }

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
        {
            State = new GpuFanChannelState(GpuFanControlPolicy.Manual, dutyPercent, StickyMeasuredDuty ?? dutyPercent);
            return Task.CompletedTask;
        }

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
        {
            RestoredAutomatic = true;
            State = new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, State.MeasuredDutyPercent);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryJournal : IProfileTransactionJournal
    {
        private ProfileTransaction? _pending;

        public Task SaveAsync(ProfileTransaction transaction, CancellationToken cancellationToken)
        {
            _pending = transaction.State is ProfileTransactionState.Committed or ProfileTransactionState.RolledBack or ProfileTransactionState.Failed
                ? null
                : transaction;
            return Task.CompletedTask;
        }

        public Task<ProfileTransaction?> GetPendingAsync(CancellationToken cancellationToken) => Task.FromResult(_pending);

        public Task ClearPendingAsync(string transactionId, CancellationToken cancellationToken)
        {
            _pending = null;
            return Task.CompletedTask;
        }
    }
}
