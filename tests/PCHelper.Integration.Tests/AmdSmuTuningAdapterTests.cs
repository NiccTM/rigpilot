using System.IO;
using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The CPU PBO tuning scaffolding is built entirely behind the qualification
/// gate (docs/qualification/cpu-tuning-and-intel-arc.md): these tests pin the
/// double gate (qualification witness AND acknowledged arm), the boot-recovery
/// journal ordering around every write, and the stock-restore recovery path.
/// Only the in-memory fake transport exists; no test (and no production code)
/// performs a real SMU write.
/// </summary>
public sealed class AmdSmuTuningAdapterTests : IDisposable
{
    private const string DeviceId = "cpu:test-5800x";
    private readonly string _journalPath = Path.Combine(
        Path.GetTempPath(), $"pchelper-cpu-tune-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_journalPath))
        {
            File.Delete(_journalPath);
        }
    }

    private static SmuTuningBounds PptBounds => new(50, 200, 142);
    private static SmuTuningBounds TdcBounds => new(40, 140, 95);
    private static SmuTuningBounds EdcBounds => new(60, 190, 140);

    [Fact]
    public async Task ProbeIsBlockedWithoutAQualificationWitnessRegardlessOfArming()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        await using AmdSmuTuningAdapter adapter = Adapter(transport, isQualified: () => false, isArmed: () => true);

        AdapterProbeResult probe = await adapter.ProbeAsync(default);

        Assert.Equal(3, probe.Capabilities.Count);
        Assert.All(probe.Capabilities, capability =>
        {
            Assert.Equal(CapabilityAccessState.Blocked, capability.State);
            Assert.Equal(ControlDomain.Cpu, capability.Domain);
            Assert.Contains("qualification gate", capability.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ProbeIsReadOnlyWhenQualifiedButDisarmedAndExperimentalWhenArmed()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        await using AmdSmuTuningAdapter disarmed = Adapter(transport, () => true, () => false);
        await using AmdSmuTuningAdapter armed = Adapter(transport, () => true, () => true);

        Assert.All((await disarmed.ProbeAsync(default)).Capabilities,
            capability => Assert.Equal(CapabilityAccessState.ReadOnly, capability.State));
        Assert.All((await armed.ProbeAsync(default)).Capabilities,
            capability => Assert.Equal(CapabilityAccessState.Experimental, capability.State));
    }

    [Fact]
    public async Task PrepareRefusesOnAnUnqualifiedSystemEvenWhenArmed()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => false, () => true);

        await Assert.ThrowsAsync<SmuTuningSafetyException>(
            () => adapter.PrepareAsync(Action(adapter, SmuTuningParameter.PptWatts, 150), default));
        Assert.Empty(transport.LimitCommands);
    }

    [Fact]
    public async Task PrepareRefusesWhenQualifiedButNotArmed()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => true, () => false);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.PrepareAsync(Action(adapter, SmuTuningParameter.PptWatts, 150), default));
        Assert.Empty(transport.LimitCommands);
    }

    [Theory]
    [InlineData(40)]  // below minimum
    [InlineData(250)] // above maximum
    public async Task PrepareAndApplyRejectValuesOutsideTheQualifiedBounds(int watts)
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => true, () => true);

        await Assert.ThrowsAsync<SmuTuningSafetyException>(
            () => adapter.PrepareAsync(Action(adapter, SmuTuningParameter.PptWatts, watts), default));

        PreparedAction rogue = new(Action(adapter, SmuTuningParameter.PptWatts, watts), null, DateTimeOffset.UtcNow, string.Empty);
        await Assert.ThrowsAsync<SmuTuningSafetyException>(() => adapter.ApplyAsync(rogue, default));
        Assert.Empty(transport.LimitCommands);
        Assert.False(File.Exists(_journalPath));
    }

    [Fact]
    public async Task ApplyJournalsBeforeTheWriteAndVerifySettlesTheJournal()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        transport.OnSetLimit = () => Assert.True(File.Exists(_journalPath),
            "The boot journal must be durable before the SMU write is commanded.");
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => true, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(adapter, SmuTuningParameter.PptWatts, 150), default);
        await adapter.ApplyAsync(prepared, default);
        Assert.True(File.Exists(_journalPath)); // still pending until verified
        ActionVerification verification = await adapter.VerifyAsync(prepared, default);

        Assert.True(verification.Success);
        Assert.Equal([(SmuTuningParameter.PptWatts, 150)], transport.LimitCommands);
        Assert.False(File.Exists(_journalPath)); // settled after read-back
    }

    [Fact]
    public async Task AFailedWriteSettlesTheJournalSoTuningIsNotWedged()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds)
        {
            FailNextSetLimit = true
        };
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => true, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(adapter, SmuTuningParameter.PptWatts, 150), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ApplyAsync(prepared, default));

        Assert.False(File.Exists(_journalPath));
    }

    [Fact]
    public async Task FailedVerifyKeepsTheJournalAndRollbackRestoresThePriorValueAndSettles()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds, initialPpt: 142);
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => true, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(adapter, SmuTuningParameter.PptWatts, 150), default);
        await adapter.ApplyAsync(prepared, default);
        transport.OverridePpt(120); // the SMU silently clamped the request

        ActionVerification verification = await adapter.VerifyAsync(prepared, default);
        Assert.False(verification.Success);
        Assert.True(File.Exists(_journalPath));

        transport.ClearPptOverride();
        await adapter.RollbackAsync(prepared, default);
        Assert.Equal(142, transport.Current(SmuTuningParameter.PptWatts));
        Assert.False(File.Exists(_journalPath));
    }

    [Fact]
    public async Task ResetToDefaultRestoresVendorStockForEveryParameter()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => true, () => true);

        PreparedAction prepared = await adapter.PrepareAsync(Action(adapter, SmuTuningParameter.EdcAmps, 170), default);
        await adapter.ApplyAsync(prepared, default);
        await adapter.ResetToDefaultAsync(adapter.CapabilityId(SmuTuningParameter.EdcAmps), default);

        Assert.True(transport.StockRestored);
        Assert.False(File.Exists(_journalPath));
    }

    [Fact]
    public async Task SentinelRecoveryRestoresStockWhenAJournalEntrySurvivesARestart()
    {
        CpuTuneBootSentinel sentinel = new(_journalPath);
        sentinel.BeginPendingTune(new CpuTuneJournalEntry("PptWatts", 150, DateTimeOffset.UtcNow));
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);

        string outcome = await sentinel.RecoverAsync(transport, default);

        Assert.True(transport.StockRestored);
        Assert.False(File.Exists(_journalPath));
        Assert.Contains("stock", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SentinelRecoveryWithoutATransportRetainsTheEvidenceAndACleanJournalIsANoOp()
    {
        CpuTuneBootSentinel sentinel = new(_journalPath);
        Assert.Contains("clean", await sentinel.RecoverAsync(transport: null, default), StringComparison.OrdinalIgnoreCase);

        sentinel.BeginPendingTune(new CpuTuneJournalEntry("PptWatts", 150, DateTimeOffset.UtcNow));
        string outcome = await sentinel.RecoverAsync(transport: null, default);

        Assert.Contains("retained", outcome, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_journalPath));
    }

    [Fact]
    public void SentinelRefusesASecondConcurrentTuneAndTreatsACorruptJournalAsUnclean()
    {
        CpuTuneBootSentinel sentinel = new(_journalPath);
        sentinel.BeginPendingTune(new CpuTuneJournalEntry("PptWatts", 150, DateTimeOffset.UtcNow));

        Assert.Throws<SmuTuningSafetyException>(
            () => sentinel.BeginPendingTune(new CpuTuneJournalEntry("TdcAmps", 100, DateTimeOffset.UtcNow)));

        File.WriteAllText(_journalPath, "{ not json");
        Assert.NotNull(sentinel.ReadPending());
    }

    [Fact]
    public async Task AdapterRejectsActionsForCapabilitiesItDoesNotOwn()
    {
        FakeSmuTuningTransport transport = new(PptBounds, TdcBounds, EdcBounds);
        await using AmdSmuTuningAdapter adapter = Adapter(transport, () => true, () => true);

        ProfileAction foreign = new(
            "action-x", AmdSmuTuningAdapter.AdapterIdValue, "gpupower.limit:0",
            ControlValue.FromNumeric(150), Required: true, Order: 0);
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.PrepareAsync(foreign, default));
        Assert.Empty(transport.LimitCommands);
    }

    private AmdSmuTuningAdapter Adapter(FakeSmuTuningTransport transport, Func<bool> isQualified, Func<bool> isArmed) =>
        new(transport, new CpuTuneBootSentinel(_journalPath), DeviceId, isQualified, isArmed);

    private static ProfileAction Action(AmdSmuTuningAdapter adapter, SmuTuningParameter parameter, int value) => new(
        "action-1",
        AmdSmuTuningAdapter.AdapterIdValue,
        adapter.CapabilityId(parameter),
        ControlValue.FromNumeric(value),
        Required: true,
        Order: 0);

    internal sealed class FakeSmuTuningTransport(
        SmuTuningBounds? ppt,
        SmuTuningBounds? tdc,
        SmuTuningBounds? edc,
        int? initialPpt = null) : ISmuTuningTransport
    {
        private readonly Dictionary<SmuTuningParameter, int?> _current = new()
        {
            [SmuTuningParameter.PptWatts] = initialPpt ?? ppt?.StockValue,
            [SmuTuningParameter.TdcAmps] = tdc?.StockValue,
            [SmuTuningParameter.EdcAmps] = edc?.StockValue,
        };
        private int? _pptOverride;

        public List<(SmuTuningParameter Parameter, int Value)> LimitCommands { get; } = [];
        public bool StockRestored { get; private set; }
        public bool FailNextSetLimit { get; set; }
        public Action? OnSetLimit { get; set; }

        public int? Current(SmuTuningParameter parameter) => _current[parameter];
        public void OverridePpt(int value) => _pptOverride = value;
        public void ClearPptOverride() => _pptOverride = null;

        public Task<SmuTuningBounds?> ReadBoundsAsync(SmuTuningParameter parameter, CancellationToken cancellationToken) =>
            Task.FromResult(parameter switch
            {
                SmuTuningParameter.PptWatts => ppt,
                SmuTuningParameter.TdcAmps => tdc,
                _ => edc
            });

        public Task<SmuTuningState> ReadStateAsync(SmuTuningParameter parameter, CancellationToken cancellationToken) =>
            Task.FromResult(new SmuTuningState(
                parameter == SmuTuningParameter.PptWatts && _pptOverride is int overridden
                    ? overridden
                    : _current[parameter]));

        public Task SetLimitAsync(SmuTuningParameter parameter, int value, CancellationToken cancellationToken)
        {
            OnSetLimit?.Invoke();
            if (FailNextSetLimit)
            {
                FailNextSetLimit = false;
                throw new InvalidOperationException("Injected SMU write failure.");
            }

            LimitCommands.Add((parameter, value));
            _current[parameter] = value;
            return Task.CompletedTask;
        }

        public Task RestoreStockAsync(CancellationToken cancellationToken)
        {
            StockRestored = true;
            _current[SmuTuningParameter.PptWatts] = ppt?.StockValue;
            _current[SmuTuningParameter.TdcAmps] = tdc?.StockValue;
            _current[SmuTuningParameter.EdcAmps] = edc?.StockValue;
            return Task.CompletedTask;
        }
    }
}
