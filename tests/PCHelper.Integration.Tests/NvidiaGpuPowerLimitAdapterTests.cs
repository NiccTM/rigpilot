using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class NvidiaGpuPowerLimitAdapterTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private static string CapabilityId => $"{NvidiaGpuPowerLimitAdapter.CapabilityPrefix}{ChannelId}";

    // Reference-system-shaped constraints: 100-385 W with a 350 W default.
    private static GpuPowerLimitBounds ReferenceBounds => new(100_000, 385_000, 350_000);

    [Fact]
    public async Task ProbeIsReadOnlyByDefaultAndExperimentalOnlyWhenWritesEnabled()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);

        await using NvidiaGpuPowerLimitAdapter readOnly = new(transport, DeviceId, ChannelId);
        await using NvidiaGpuPowerLimitAdapter writable = new(transport, DeviceId, ChannelId, enableWrites: true);

        CapabilityDescriptor readOnlyCap = Assert.Single((await readOnly.ProbeAsync(default)).Capabilities);
        CapabilityDescriptor writableCap = Assert.Single((await writable.ProbeAsync(default)).Capabilities);

        Assert.Equal(CapabilityAccessState.ReadOnly, readOnlyCap.State);
        Assert.Equal(CapabilityAccessState.Experimental, writableCap.State);
        Assert.True(writableCap.CanResetToDefault);
        Assert.Equal(ControlDomain.Gpu, writableCap.Domain);
        Assert.Equal(100, writableCap.Range!.Minimum);
        Assert.Equal(385, writableCap.Range!.Maximum);
        Assert.Equal("W", writableCap.Unit);
    }

    [Fact]
    public async Task ArmingFlipsTheCapabilityFromReadOnlyToExperimentalAndPermitsWrites()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        bool armed = false;
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, () => armed);

        CapabilityDescriptor disarmed = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);
        Assert.Equal(CapabilityAccessState.ReadOnly, disarmed.State);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(Action(300), default));

        armed = true;
        CapabilityDescriptor live = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);
        Assert.Equal(CapabilityAccessState.Experimental, live.State);
        PreparedAction prepared = await adapter.PrepareAsync(Action(300), default);
        await adapter.ApplyAsync(prepared, default);
        Assert.Equal([300_000u], transport.LimitCommands);

        // Disarming again blocks further writes immediately.
        armed = false;
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(Action(250), default));
    }

    [Fact]
    public async Task PrepareRejectsALimitBelowTheDriverMinimum()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        await Assert.ThrowsAsync<GpuPowerSafetyException>(() => adapter.PrepareAsync(Action(50), default));
        Assert.Empty(transport.LimitCommands);
    }

    [Fact]
    public async Task PrepareRejectsALimitAboveTheDriverMaximum()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        await Assert.ThrowsAsync<GpuPowerSafetyException>(() => adapter.PrepareAsync(Action(400), default));
        Assert.Empty(transport.LimitCommands);
    }

    [Fact]
    public async Task ApplyWritesTheLimitAndVerifyConfirmsReadBack()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(320), default);
        await adapter.ApplyAsync(prepared, default);
        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.Equal(320_000u, transport.CurrentMilliwatts);
        Assert.Equal([320_000u], transport.LimitCommands);
        Assert.True(verification.Success);
    }

    [Fact]
    public async Task ApplyRefusesAnUncheckedOutOfRangeValue()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        // Bypass Prepare with a hand-built out-of-range prepared action.
        PreparedAction rogue = new(Action(50), null, DateTimeOffset.UtcNow, string.Empty);

        await Assert.ThrowsAsync<GpuPowerSafetyException>(() => adapter.ApplyAsync(rogue, default));
        Assert.Empty(transport.LimitCommands);
    }

    [Fact]
    public async Task VerifyFailsWhenReadBackDoesNotMatch()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(320), default);
        await adapter.ApplyAsync(prepared, default);
        transport.OverrideCurrent(280_000); // the driver did not enforce the requested limit

        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.False(verification.Success);
    }

    [Fact]
    public async Task RollbackRestoresThePriorLimit()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds, initialMilliwatts: 350_000);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(200), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.RollbackAsync(prepared, default);

        Assert.Equal(350_000u, transport.CurrentMilliwatts);
    }

    [Fact]
    public async Task RollbackFallsBackToTheVendorDefaultWithoutACapturedPriorLimit()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds, initialMilliwatts: null);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(200), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.RollbackAsync(prepared, default);

        Assert.Equal(ReferenceBounds.DefaultMilliwatts, transport.CurrentMilliwatts);
    }

    [Fact]
    public async Task ResetToDefaultRestoresTheVendorDefaultLimit()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(200), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.ResetToDefaultAsync(CapabilityId, default);

        Assert.Equal(ReferenceBounds.DefaultMilliwatts, transport.CurrentMilliwatts);
    }

    [Fact]
    public async Task ConflictBlocksTheCapabilityAndRefusesPrepare()
    {
        FakeGpuPowerLimitTransport transport = new(ReferenceBounds);
        await using NvidiaGpuPowerLimitAdapter adapter = new(
            transport,
            DeviceId,
            ChannelId,
            enableWrites: true,
            isConflicted: _ => Task.FromResult(true));

        CapabilityDescriptor cap = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);
        Assert.Equal(CapabilityAccessState.Blocked, cap.State);
        await Assert.ThrowsAsync<GpuPowerSafetyException>(() => adapter.PrepareAsync(Action(300), default));
        Assert.Empty(transport.LimitCommands);
    }

    [Fact]
    public async Task PrepareRefusesWhenConstraintsAreUnavailable()
    {
        FakeGpuPowerLimitTransport transport = new(bounds: null);
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        await Assert.ThrowsAsync<GpuPowerSafetyException>(() => adapter.PrepareAsync(Action(300), default));
    }

    [Fact]
    public async Task ProbeExposesNoCapabilityWhenConstraintsAreInvalid()
    {
        // A default outside [min, max] is an invalid vendor report; never expose it.
        FakeGpuPowerLimitTransport transport = new(new GpuPowerLimitBounds(100_000, 385_000, 500_000));
        await using NvidiaGpuPowerLimitAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        Assert.Empty((await adapter.ProbeAsync(default)).Capabilities);
    }

    private static ProfileAction Action(int watts) => new(
        "action-1",
        NvidiaGpuPowerLimitAdapter.AdapterId,
        CapabilityId,
        ControlValue.FromNumeric(watts),
        Required: true,
        Order: 0);

    private sealed class FakeGpuPowerLimitTransport(
        GpuPowerLimitBounds? bounds,
        uint? initialMilliwatts = 350_000) : IGpuPowerLimitTransport
    {
        private uint? _currentOverride;

        public uint? CurrentMilliwatts { get; private set; } = initialMilliwatts;

        public List<uint> LimitCommands { get; } = [];

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        public void OverrideCurrent(uint milliwatts) => _currentOverride = milliwatts;

        public Task<GpuPowerLimitBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(bounds is { IsValid: true } ? bounds : null);

        public Task<GpuPowerLimitState> ReadStateAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(new GpuPowerLimitState(_currentOverride ?? CurrentMilliwatts));

        public Task SetPowerLimitAsync(string channelId, uint milliwatts, CancellationToken cancellationToken)
        {
            LimitCommands.Add(milliwatts);
            CurrentMilliwatts = milliwatts;
            return Task.CompletedTask;
        }
    }
}
