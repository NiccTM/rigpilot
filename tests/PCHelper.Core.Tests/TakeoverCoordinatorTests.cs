using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class TakeoverCoordinatorTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExecutesExactConsentForceStopResetLeaseAndGiveBack()
    {
        TakeoverProcessIdentity identity = Identity(Hash);
        FakeProcesses processes = new(identity) { GracefulResult = false };
        FakeStartup startup = new();
        FakeHardware hardware = new();
        FakeJournal journal = new();
        TakeoverCoordinator coordinator = new(processes, startup, hardware, journal);
        TakeoverPlanV1 plan = Plan(identity, ["gpu.fan", "gpu.power"]);
        OwnershipConsentV1 consent = TakeoverConsentValidator.Create(identity, true, true, DateTimeOffset.UtcNow);

        (TakeoverTransactionV1 transaction, OwnershipLeaseV1? lease) = await coordinator.ExecuteAsync(
            plan,
            [consent],
            CancellationToken.None);

        Assert.Equal(TakeoverTransactionState.Completed, transaction.State);
        Assert.NotNull(lease);
        Assert.True(processes.ForceStopped);
        Assert.Equal(["gpu.fan", "gpu.power"], hardware.ResetControls);
        Assert.True(startup.Disabled);
        Assert.Contains(journal.States, state => state == TakeoverTransactionState.ResettingHardware);

        TakeoverTransactionV1 released = await coordinator.GiveControlBackAsync(transaction, lease, CancellationToken.None);
        Assert.Equal(TakeoverTransactionState.Released, released.State);
        Assert.True(startup.Restored);
        Assert.True(hardware.Released);
        Assert.Equal(4, hardware.ResetControls.Count);
    }

    [Fact]
    public async Task HardwareResetFailureRestoresStartupAndDoesNotAcquireLease()
    {
        TakeoverProcessIdentity identity = Identity(Hash);
        FakeProcesses processes = new(identity) { GracefulResult = true };
        FakeStartup startup = new();
        FakeHardware hardware = new() { FailResetFor = "gpu.power" };
        FakeJournal journal = new();
        TakeoverCoordinator coordinator = new(processes, startup, hardware, journal);

        (TakeoverTransactionV1 transaction, OwnershipLeaseV1? lease) = await coordinator.ExecuteAsync(
            Plan(identity, ["gpu.fan", "gpu.power"]),
            [TakeoverConsentValidator.Create(identity, true, true, DateTimeOffset.UtcNow)],
            CancellationToken.None);

        Assert.Equal(TakeoverTransactionState.RolledBack, transaction.State);
        Assert.Null(lease);
        Assert.True(startup.Restored);
        Assert.False(hardware.Acquired);
        Assert.Contains("reset failed", transaction.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GracefulOnlyConsentCanCompleteWithoutForceTermination()
    {
        TakeoverProcessIdentity identity = Identity(Hash);
        FakeProcesses processes = new(identity) { GracefulResult = true };
        FakeStartup startup = new();
        FakeHardware hardware = new();
        TakeoverCoordinator coordinator = new(processes, startup, hardware, new FakeJournal());

        (TakeoverTransactionV1 transaction, OwnershipLeaseV1? lease) = await coordinator.ExecuteAsync(
            Plan(identity, ["gpu.fan"]),
            [TakeoverConsentValidator.Create(identity, allowForceTermination: false, disableStartup: false, DateTimeOffset.UtcNow)],
            CancellationToken.None);

        Assert.Equal(TakeoverTransactionState.Completed, transaction.State);
        Assert.NotNull(lease);
        Assert.False(processes.ForceStopped);
        Assert.False(startup.Disabled);
    }

    [Fact]
    public async Task ChangedBinaryIsRejectedBeforeStartupOrProcessMutation()
    {
        TakeoverProcessIdentity preview = Identity(Hash);
        TakeoverProcessIdentity current = preview with { Sha256 = new string('b', 64) };
        FakeProcesses processes = new(current);
        FakeStartup startup = new();
        FakeHardware hardware = new();
        TakeoverCoordinator coordinator = new(processes, startup, hardware, new FakeJournal());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.ExecuteAsync(
            Plan(preview, ["gpu.fan"]),
            [TakeoverConsentValidator.Create(preview, true, true, DateTimeOffset.UtcNow)],
            CancellationToken.None));

        Assert.False(startup.Disabled);
        Assert.False(processes.ForceStopped);
        Assert.Empty(hardware.ResetControls);
    }

    [Fact]
    public async Task CancellationAfterStartupMutationRestoresStartupWithSafetyToken()
    {
        TakeoverProcessIdentity identity = Identity(Hash);
        FakeStartup startup = new() { CancelDuringDisable = true };
        FakeJournal journal = new();
        TakeoverCoordinator coordinator = new(new FakeProcesses(identity), startup, new FakeHardware(), journal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(
            Plan(identity, ["gpu.fan"]),
            [TakeoverConsentValidator.Create(identity, true, true, DateTimeOffset.UtcNow)],
            CancellationToken.None));

        Assert.True(startup.Disabled);
        Assert.True(startup.Restored);
        Assert.Equal(TakeoverTransactionState.RolledBack, journal.States[^1]);
    }

    private static TakeoverProcessIdentity Identity(string hash) => new(
        "MSI Afterburner",
        Path.Combine(Path.GetTempPath(), "MSIAfterburner.exe"),
        "MSI Afterburner",
        "MICRO-STAR INTERNATIONAL CO., LTD.",
        "00112233445566778899AABBCCDDEEFF00112233",
        hash,
        "MSIAfterburner",
        ["GpuFan", "GpuTuning"]);

    private static TakeoverPlanV1 Plan(TakeoverProcessIdentity identity, IReadOnlyList<string> controls) => new(
        TakeoverPlanV1.CurrentSchemaVersion,
        "takeover.plan",
        DateTimeOffset.UtcNow,
        [identity],
        ["HKCU:Run:MSIAfterburner"],
        [],
        controls,
        []);

    private sealed class FakeProcesses(TakeoverProcessIdentity current) : ITakeoverProcessController
    {
        public bool GracefulResult { get; init; }
        public bool ForceStopped { get; private set; }
        private bool Running { get; set; } = true;

        public Task<TakeoverProcessIdentity?> GetCurrentIdentityAsync(string executablePath, CancellationToken cancellationToken) =>
            Task.FromResult<TakeoverProcessIdentity?>(current);

        public Task<bool> RequestGracefulStopAsync(TakeoverProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (GracefulResult) Running = false;
            return Task.FromResult(GracefulResult);
        }

        public Task ForceStopAsync(TakeoverProcessIdentity identity, CancellationToken cancellationToken)
        {
            ForceStopped = true;
            Running = false;
            return Task.CompletedTask;
        }

        public Task<bool> IsRunningAsync(TakeoverProcessIdentity identity, CancellationToken cancellationToken) => Task.FromResult(Running);
    }

    private sealed class FakeStartup : ITakeoverStartupController
    {
        public bool CancelDuringDisable { get; init; }
        public bool Disabled { get; private set; }
        public bool Restored { get; private set; }

        public Task<IReadOnlyList<StartupEntryBackupV1>> BackupAsync(TakeoverProcessIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StartupEntryBackupV1>>(
                [new StartupEntryBackupV1("run", "HKCU", identity.DisplayName, identity.ExecutablePath, true)]);

        public Task DisableAsync(IReadOnlyList<StartupEntryBackupV1> entries, CancellationToken cancellationToken)
        {
            Disabled = true;
            if (CancelDuringDisable) throw new OperationCanceledException("cancelled during startup mutation");
            return Task.CompletedTask;
        }

        public Task RestoreAsync(IReadOnlyList<StartupEntryBackupV1> entries, CancellationToken cancellationToken)
        {
            Restored = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHardware : ITakeoverHardwareController
    {
        public string? FailResetFor { get; init; }
        public List<string> ResetControls { get; } = [];
        public bool Acquired { get; private set; }
        public bool Released { get; private set; }

        public Task ResetAndVerifyAsync(string capabilityId, CancellationToken cancellationToken)
        {
            if (capabilityId == FailResetFor) throw new InvalidOperationException("reset failed");
            ResetControls.Add(capabilityId);
            return Task.CompletedTask;
        }

        public Task<OwnershipLeaseV1> AcquireAsync(IReadOnlyList<string> resourceFamilies, CancellationToken cancellationToken)
        {
            Acquired = true;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OwnershipLeaseV1(1, "lease", "PC Helper", resourceFamilies, now, now.AddMinutes(5), OwnershipState.OwnedByPcHelper, "test"));
        }

        public Task ReleaseAsync(OwnershipLeaseV1 lease, CancellationToken cancellationToken)
        {
            Released = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJournal : ITakeoverJournal
    {
        public List<TakeoverTransactionState> States { get; } = [];
        public Task SaveAsync(TakeoverTransactionV1 transaction, CancellationToken cancellationToken)
        {
            States.Add(transaction.State);
            return Task.CompletedTask;
        }
    }
}
