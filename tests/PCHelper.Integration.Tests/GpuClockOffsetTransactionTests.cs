using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Drives the real <see cref="NvidiaGpuClockOffsetAdapter"/> through the real
/// <see cref="ProfileTransactionEngine"/> so a GPU core clock-offset write obeys
/// the transaction invariants: it commits on success and restores the prior
/// offset on a mid-transaction verification failure. The device is an in-memory fake.
/// </summary>
public sealed class GpuClockOffsetTransactionTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private static string CapabilityId => $"{NvidiaGpuClockOffsetAdapter.CorePrefix}{ChannelId}";

    private static GpuClockOffsetBounds CoreBounds => new(-500_000, 250_000);

    [Fact]
    public async Task GpuClockOffsetWriteCommitsThroughTheTransactionEngine()
    {
        FakeTransport transport = new(initialCoreKiloHertz: 0);
        await using NvidiaGpuClockOffsetAdapter adapter = new(
            transport, GpuClockOffsetDomain.Core, DeviceId, ChannelId, () => true);
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        (ProfileTransaction transaction, ProfileValidationResult validation) = await engine.ApplyAsync(
            ClockProfile(megahertz: 120),
            Capabilities(CapabilityAccessState.Experimental),
            expectedRevision: 0,
            confirmExperimental: true,
            CancellationToken.None);

        Assert.True(validation.Valid);
        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
        Assert.Equal(120_000, transport.CurrentKiloHertz);
        Assert.Equal(1, engine.Revision);
    }

    [Fact]
    public async Task GpuClockOffsetWriteRollsBackToThePriorOffsetOnVerificationFailure()
    {
        // The driver silently keeps stock clocks; verification must fail and the
        // engine must restore the captured prior 0 kHz offset.
        FakeTransport transport = new(initialCoreKiloHertz: 0)
        {
            StickyReadBackKiloHertz = 0
        };
        await using NvidiaGpuClockOffsetAdapter adapter = new(
            transport, GpuClockOffsetDomain.Core, DeviceId, ChannelId, () => true);
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            ClockProfile(megahertz: 150),
            Capabilities(CapabilityAccessState.Experimental),
            expectedRevision: 0,
            confirmExperimental: true,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.RolledBack, transaction.State);
        Assert.Equal(0, transport.OffsetCommands[^1]);
        Assert.Equal(0, engine.Revision);
    }

    [Fact]
    public async Task CoreAndMemoryAdaptersRegisterTogetherInOneEngineWithoutColliding()
    {
        // Regression: the service registers one adapter instance per domain, and the
        // engine keys adapters by manifest id. Shared ids crashed service startup
        // (ArgumentException: An item with the same key has already been added).
        FakeTransport transport = new(initialCoreKiloHertz: 0);
        await using NvidiaGpuClockOffsetAdapter core = new(
            transport, GpuClockOffsetDomain.Core, DeviceId, ChannelId, () => true);
        await using NvidiaGpuClockOffsetAdapter memory = new(
            transport, GpuClockOffsetDomain.Memory, DeviceId, ChannelId, () => true);

        Assert.NotEqual(core.Manifest.Id, memory.Manifest.Id);
        using ProfileTransactionEngine engine = new([core, memory], new MemoryJournal());

        (ProfileTransaction transaction, ProfileValidationResult validation) = await engine.ApplyAsync(
            ClockProfile(megahertz: 100),
            Capabilities(CapabilityAccessState.Experimental),
            expectedRevision: 0,
            confirmExperimental: true,
            CancellationToken.None);

        Assert.True(validation.Valid);
        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
        Assert.Equal(100_000, transport.CurrentKiloHertz);
    }

    private static ProfileV1 ClockProfile(int megahertz) => new(
        ProfileV1.CurrentSchemaVersion,
        "gpu-clock-profile",
        "GPU core clock offset",
        "Experimental GPU core clock offset",
        [new ProfileAction("clock-action", NvidiaGpuClockOffsetAdapter.CoreAdapterId, CapabilityId, ControlValue.FromNumeric(megahertz), Required: true, Order: 0)],
        new SafetyLimits(),
        [],
        IsBuiltIn: false,
        IsExperimental: true);

    private static Dictionary<string, CapabilityDescriptor> Capabilities(CapabilityAccessState state) =>
        new()
        {
            [CapabilityId] = new CapabilityDescriptor(
                CapabilityId,
                NvidiaGpuClockOffsetAdapter.CoreAdapterId,
                DeviceId,
                "GPU core clock offset",
                state,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(-500, 250, 1),
                "MHz",
                RiskLevel.Experimental,
                EvidenceLevel.Detected,
                null,
                "GPU core clock offset",
                CanResetToDefault: true,
                Domain: ControlDomain.Gpu)
        };

    private sealed class FakeTransport(int? initialCoreKiloHertz) : IGpuClockOffsetTransport
    {
        public int? CurrentKiloHertz { get; private set; } = initialCoreKiloHertz;

        public List<int> OffsetCommands { get; } = [];

        /// <summary>When set, read-back always reports this offset regardless of the command.</summary>
        public int? StickyReadBackKiloHertz { get; init; }

        public Task<GpuClockOffsetBounds?> ReadBoundsAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken) =>
            Task.FromResult<GpuClockOffsetBounds?>(CoreBounds);

        public Task<GpuClockOffsetState> ReadStateAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken) =>
            Task.FromResult(new GpuClockOffsetState(StickyReadBackKiloHertz ?? CurrentKiloHertz));

        public Task SetOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
        {
            OffsetCommands.Add(offsetKiloHertz);
            CurrentKiloHertz = offsetKiloHertz;
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
