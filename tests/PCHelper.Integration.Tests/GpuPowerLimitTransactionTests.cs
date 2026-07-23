using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Drives the real <see cref="NvidiaGpuPowerLimitAdapter"/> through the real
/// <see cref="ProfileTransactionEngine"/> so the GPU power-limit write obeys the
/// transaction invariants: it commits on success and restores the prior limit on a
/// mid-transaction verification failure. The physical device is an in-memory fake.
/// </summary>
public sealed class GpuPowerLimitTransactionTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private static string CapabilityId => $"{NvidiaGpuPowerLimitAdapter.CapabilityPrefix}{ChannelId}";

    private static GpuPowerLimitBounds ReferenceBounds => new(100_000, 385_000, 350_000);

    [Fact]
    public async Task GpuPowerLimitWriteCommitsThroughTheTransactionEngine()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds, initialMilliwatts: 350_000);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        (ProfileTransaction transaction, ProfileValidationResult validation) = await engine.ApplyAsync(
            PowerProfile(watts: 300),
            Capabilities(CapabilityAccessState.Experimental),
            expectedRevision: 0,
            confirmExperimental: true,
            CancellationToken.None);

        Assert.True(validation.Valid);
        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
        Assert.Equal(300_000u, transport.CurrentMilliwatts);
        Assert.Equal(1, engine.Revision);
    }

    [Fact]
    public async Task GpuPowerLimitWriteRollsBackToThePriorLimitOnVerificationFailure()
    {
        // The driver never enforces the requested limit; verification must fail and
        // the engine must restore the prior 350 W limit.
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds, initialMilliwatts: 350_000)
        {
            StickyReadBackMilliwatts = 350_000
        };
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            PowerProfile(watts: 200),
            Capabilities(CapabilityAccessState.Experimental),
            expectedRevision: 0,
            confirmExperimental: true,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.RolledBack, transaction.State);
        // The last command issued must be the rollback to the captured prior limit.
        Assert.Equal(350_000u, transport.LimitCommands[^1]);
        Assert.Equal(0, engine.Revision);
    }

    private static ProfileV1 PowerProfile(int watts) => new(
        ProfileV1.CurrentSchemaVersion,
        "gpu-power-profile",
        "GPU power limit",
        "Experimental GPU power limit",
        [new ProfileAction("power-action", NvidiaGpuPowerLimitAdapter.AdapterId, CapabilityId, ControlValue.FromNumeric(watts), Required: true, Order: 0)],
        new SafetyLimits(),
        [],
        IsBuiltIn: false,
        IsExperimental: true);

    private static Dictionary<string, CapabilityDescriptor> Capabilities(CapabilityAccessState state) =>
        new()
        {
            [CapabilityId] = new CapabilityDescriptor(
                CapabilityId,
                NvidiaGpuPowerLimitAdapter.AdapterId,
                DeviceId,
                "GPU power limit",
                state,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(100, 385, 1),
                "W",
                RiskLevel.Experimental,
                EvidenceLevel.Detected,
                null,
                "GPU power limit",
                CanResetToDefault: true,
                Domain: ControlDomain.Gpu)
        };

    private sealed class FakeGpuPowerLimitTransport(
        GpuPowerLimitBounds? bounds,
        uint? initialMilliwatts = 350_000) : IGpuPowerLimitTransport
    {
        public uint? CurrentMilliwatts { get; private set; } = initialMilliwatts;

        public List<uint> LimitCommands { get; } = [];

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        /// <summary>When set, read-back always reports this limit regardless of the command.</summary>
        public uint? StickyReadBackMilliwatts { get; init; }

        public Task<GpuPowerLimitBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(bounds);

        public Task<GpuPowerLimitState> ReadStateAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(new GpuPowerLimitState(StickyReadBackMilliwatts ?? CurrentMilliwatts));

        public Task SetPowerLimitAsync(string channelId, uint milliwatts, CancellationToken cancellationToken)
        {
            LimitCommands.Add(milliwatts);
            CurrentMilliwatts = milliwatts;
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
