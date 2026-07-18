using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class NvidiaGpuClockOffsetAdapterTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private static string CoreCapabilityId => $"{NvidiaGpuClockOffsetAdapter.CorePrefix}{ChannelId}";
    private static string MemoryCapabilityId => $"{NvidiaGpuClockOffsetAdapter.MemoryPrefix}{ChannelId}";

    // Driver-shaped delta ranges: core ±500 MHz would be unusual, so model an
    // asymmetric core range and a wide memory range as real drivers report them.
    private static GpuClockOffsetBounds CoreBounds => new(-500_000, 250_000);
    private static GpuClockOffsetBounds MemoryBounds => new(-2_000_000, 2_000_000);

    [Fact]
    public async Task ProbeIsReadOnlyByDefaultAndExperimentalWhenArmed()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);

        await using NvidiaGpuClockOffsetAdapter readOnly = CoreAdapter(transport, () => false);
        await using NvidiaGpuClockOffsetAdapter armed = CoreAdapter(transport, () => true);

        CapabilityDescriptor readOnlyCap = Assert.Single((await readOnly.ProbeAsync(default)).Capabilities);
        CapabilityDescriptor armedCap = Assert.Single((await armed.ProbeAsync(default)).Capabilities);

        Assert.Equal(CapabilityAccessState.ReadOnly, readOnlyCap.State);
        Assert.Equal(CapabilityAccessState.Experimental, armedCap.State);
        Assert.True(armedCap.CanResetToDefault);
        Assert.Equal(ControlDomain.Gpu, armedCap.Domain);
        Assert.Equal(-500, armedCap.Range!.Minimum);
        Assert.Equal(250, armedCap.Range!.Maximum);
        Assert.Equal("MHz", armedCap.Unit);
    }

    [Fact]
    public async Task CoreAndMemoryAdaptersExposeDistinctCapabilitiesAndRejectEachOthersActions()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);
        await using NvidiaGpuClockOffsetAdapter core = CoreAdapter(transport, () => true);
        await using NvidiaGpuClockOffsetAdapter memory = MemoryAdapter(transport, () => true);

        Assert.Equal(CoreCapabilityId, core.CapabilityId);
        Assert.Equal(MemoryCapabilityId, memory.CapabilityId);
        await Assert.ThrowsAsync<ArgumentException>(() => core.PrepareAsync(Action(100, MemoryCapabilityId), default));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.PrepareAsync(Action(100, CoreCapabilityId), default));
        Assert.Empty(transport.OffsetCommands);
    }

    [Fact]
    public async Task ArmingGatesPrepareAndDisarmingBlocksImmediately()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);
        bool armed = false;
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => armed);

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(Action(100), default));

        armed = true;
        PreparedAction prepared = await adapter.PrepareAsync(Action(100), default);
        await adapter.ApplyAsync(prepared, default);
        Assert.Equal([(GpuClockOffsetDomain.Core, 100_000)], transport.OffsetCommands);

        armed = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(Action(50), default));
    }

    [Theory]
    [InlineData(-600)] // below the driver minimum
    [InlineData(300)]  // above the driver maximum
    public async Task PrepareRejectsAnOffsetOutsideTheDriverDeltaRange(int megahertz)
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        await Assert.ThrowsAsync<GpuClockSafetyException>(() => adapter.PrepareAsync(Action(megahertz), default));
        Assert.Empty(transport.OffsetCommands);
    }

    [Fact]
    public async Task ApplyWritesTheOffsetAndVerifyConfirmsReadBack()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(150), default);
        await adapter.ApplyAsync(prepared, default);
        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.Equal(150_000, transport.CurrentKiloHertz(GpuClockOffsetDomain.Core));
        Assert.True(verification.Success);
    }

    [Fact]
    public async Task ApplyRefusesAnUncheckedOutOfRangeValue()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        PreparedAction rogue = new(Action(2000), null, DateTimeOffset.UtcNow, string.Empty);

        await Assert.ThrowsAsync<GpuClockSafetyException>(() => adapter.ApplyAsync(rogue, default));
        Assert.Empty(transport.OffsetCommands);
    }

    [Fact]
    public async Task VerifyFailsWhenReadBackDoesNotMatch()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(150), default);
        await adapter.ApplyAsync(prepared, default);
        transport.OverrideCurrent(GpuClockOffsetDomain.Core, 0); // the driver silently dropped the offset

        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.False(verification.Success);
    }

    [Fact]
    public async Task RollbackRestoresThePriorOffset()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds, initialCoreKiloHertz: 50_000);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(200), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.RollbackAsync(prepared, default);

        Assert.Equal(50_000, transport.CurrentKiloHertz(GpuClockOffsetDomain.Core));
    }

    [Fact]
    public async Task RollbackFallsBackToStockClocksWithoutACapturedPriorOffset()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds, initialCoreKiloHertz: null);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(200), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.RollbackAsync(prepared, default);

        Assert.Equal(0, transport.CurrentKiloHertz(GpuClockOffsetDomain.Core));
    }

    [Fact]
    public async Task ResetToDefaultReturnsToStockClocks()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds, initialCoreKiloHertz: 50_000);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(200), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.ResetToDefaultAsync(CoreCapabilityId, default);

        Assert.Equal(0, transport.CurrentKiloHertz(GpuClockOffsetDomain.Core));
    }

    [Fact]
    public async Task ConflictBlocksTheCapabilityAndRefusesPrepare()
    {
        FakeGpuClockOffsetTransport transport = new(CoreBounds, MemoryBounds);
        await using NvidiaGpuClockOffsetAdapter adapter = new(
            transport,
            GpuClockOffsetDomain.Core,
            DeviceId,
            ChannelId,
            () => true,
            isConflicted: _ => Task.FromResult(true));

        CapabilityDescriptor cap = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);
        Assert.Equal(CapabilityAccessState.Blocked, cap.State);
        await Assert.ThrowsAsync<GpuClockSafetyException>(() => adapter.PrepareAsync(Action(100), default));
        Assert.Empty(transport.OffsetCommands);
    }

    [Fact]
    public async Task ProbeExposesNoCapabilityAndPrepareRefusesWhenTheDeltaRangeIsUnavailable()
    {
        FakeGpuClockOffsetTransport transport = new(coreBounds: null, memoryBounds: null);
        await using NvidiaGpuClockOffsetAdapter adapter = CoreAdapter(transport, () => true);

        Assert.Empty((await adapter.ProbeAsync(default)).Capabilities);
        await Assert.ThrowsAsync<GpuClockSafetyException>(() => adapter.PrepareAsync(Action(100), default));
    }

    private static NvidiaGpuClockOffsetAdapter CoreAdapter(IGpuClockOffsetTransport transport, Func<bool> isArmed) =>
        new(transport, GpuClockOffsetDomain.Core, DeviceId, ChannelId, isArmed);

    private static NvidiaGpuClockOffsetAdapter MemoryAdapter(IGpuClockOffsetTransport transport, Func<bool> isArmed) =>
        new(transport, GpuClockOffsetDomain.Memory, DeviceId, ChannelId, isArmed);

    private static ProfileAction Action(int megahertz, string? capabilityId = null) => new(
        "action-1",
        NvidiaGpuClockOffsetAdapter.CoreAdapterId,
        capabilityId ?? CoreCapabilityId,
        ControlValue.FromNumeric(megahertz),
        Required: true,
        Order: 0);

    internal sealed class FakeGpuClockOffsetTransport(
        GpuClockOffsetBounds? coreBounds,
        GpuClockOffsetBounds? memoryBounds,
        int? initialCoreKiloHertz = 0) : IGpuClockOffsetTransport
    {
        private readonly Dictionary<GpuClockOffsetDomain, int?> _current = new()
        {
            [GpuClockOffsetDomain.Core] = initialCoreKiloHertz,
            [GpuClockOffsetDomain.Memory] = 0,
        };
        private readonly Dictionary<GpuClockOffsetDomain, int?> _overrides = [];

        public List<(GpuClockOffsetDomain Domain, int KiloHertz)> OffsetCommands { get; } = [];

        public int? CurrentKiloHertz(GpuClockOffsetDomain domain) => _current[domain];

        public void OverrideCurrent(GpuClockOffsetDomain domain, int kiloHertz) => _overrides[domain] = kiloHertz;

        public Task<GpuClockOffsetBounds?> ReadBoundsAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken)
        {
            GpuClockOffsetBounds? bounds = domain == GpuClockOffsetDomain.Core ? coreBounds : memoryBounds;
            return Task.FromResult(bounds is { IsValid: true } ? bounds : null);
        }

        public Task<GpuClockOffsetState> ReadStateAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken) =>
            Task.FromResult(new GpuClockOffsetState(
                _overrides.TryGetValue(domain, out int? overridden) ? overridden : _current[domain]));

        public Task SetOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
        {
            OffsetCommands.Add((domain, offsetKiloHertz));
            _current[domain] = offsetKiloHertz;
            return Task.CompletedTask;
        }

        /// <summary>Records restores separately so tests can prove rollback used the un-gated path.</summary>
        public Task RestoreOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
        {
            RestoreCommands.Add((domain, offsetKiloHertz));
            OffsetCommands.Add((domain, offsetKiloHertz));
            _current[domain] = offsetKiloHertz;
            return Task.CompletedTask;
        }

        public List<(GpuClockOffsetDomain Domain, int OffsetKiloHertz)> RestoreCommands { get; } = [];
    }
}
