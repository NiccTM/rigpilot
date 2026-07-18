using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ProfileTransactionEngineTests
{
    [Fact]
    public async Task SuccessfulTransactionCommitsAndAdvancesRevision()
    {
        FakeAdapter adapter = new();
        MemoryJournal journal = new();
        using ProfileTransactionEngine engine = new([adapter], journal);

        (ProfileTransaction transaction, ProfileValidationResult validation) = await engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        Assert.True(validation.Valid);
        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
        Assert.Equal(50, adapter.CurrentValue);
        Assert.Equal(1, engine.Revision);
        Assert.Null(await journal.GetPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VerificationFailureRollsBackAppliedValue()
    {
        FakeAdapter adapter = new() { FailVerification = true, CurrentValue = 25 };
        MemoryJournal journal = new();
        using ProfileTransactionEngine engine = new([adapter], journal);

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.RolledBack, transaction.State);
        Assert.Equal(25, adapter.CurrentValue);
        Assert.Contains("rollback", adapter.Calls);
        Assert.Equal(0, engine.Revision);
    }

    [Fact]
    public async Task StaleRevisionThrowsBeforePreparing()
    {
        FakeAdapter adapter = new();
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal(), initialRevision: 4);

        await Assert.ThrowsAsync<StateRevisionException>(() => engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 3,
            confirmExperimental: false,
            CancellationToken.None));
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task AppliesDomainsInSafetyOrderBeforeUserOrder()
    {
        FakeAdapter adapter = new();
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());
        ProfileAction power = ProfileValidatorTests.Action(required: true) with
        {
            Id = "power",
            CapabilityId = "power.control",
            Order = 0
        };
        ProfileAction coolingSafety = ProfileValidatorTests.Action(required: true) with
        {
            Id = "cooling-safety",
            CapabilityId = "cooling.safety",
            Order = 99
        };
        ProfileV1 profile = ProfileValidatorTests.Profile(power, experimental: false) with
        {
            Actions = [power, coolingSafety]
        };
        Dictionary<string, CapabilityDescriptor> capabilities = new()
        {
            [power.CapabilityId] = ProfileValidatorTests.Capability(CapabilityAccessState.Verified) with
            {
                Id = power.CapabilityId,
                Domain = ControlDomain.Power
            },
            [coolingSafety.CapabilityId] = ProfileValidatorTests.Capability(CapabilityAccessState.Verified) with
            {
                Id = coolingSafety.CapabilityId,
                Domain = ControlDomain.CoolingSafety
            }
        };

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            profile,
            capabilities,
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
        Assert.Equal(["cooling-safety", "power"], adapter.AppliedActionIds);
    }

    [Fact]
    public async Task CancellationAfterPartialWriteRollsBackImmediately()
    {
        FakeAdapter adapter = new() { CurrentValue = 25, CancelDuringApply = true };
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.RolledBack, transaction.State);
        Assert.Equal(25, adapter.CurrentValue);
        Assert.Contains("rollback", adapter.Calls);
    }

    [Fact]
    public async Task FailedRollbackRemainsPendingForBootRecovery()
    {
        FakeAdapter adapter = new() { FailVerification = true, FailRollback = true };
        MemoryJournal journal = new();
        using ProfileTransactionEngine engine = new([adapter], journal);

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.RecoveryRequired, transaction.State);
        Assert.Equal(transaction.Id, (await journal.GetPendingAsync(CancellationToken.None))?.Id);
        Assert.Contains("Rollback errors", transaction.Error);
    }

    [Fact]
    public async Task MutationTimeoutRemainsRecoveryRequiredEvenWhenRollbackReadsBack()
    {
        FakeAdapter adapter = new() { CurrentValue = 25, UnknownDuringApply = true };
        MemoryJournal journal = new();
        using ProfileTransactionEngine engine = new([adapter], journal);

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        Assert.Equal(ProfileTransactionState.RecoveryRequired, transaction.State);
        Assert.Equal(25, adapter.CurrentValue);
        Assert.Equal(transaction.Id, (await journal.GetPendingAsync(CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task CommittedTransactionPersistsControlLeaseBeforePendingIsCleared()
    {
        FakeAdapter adapter = new();
        MemoryJournal journal = new();
        MemorySuiteStore suiteStore = new();
        using ProfileTransactionEngine engine = new(
            [adapter],
            journal,
            suiteStore: suiteStore,
            serviceInstanceId: "service-test");

        (ProfileTransaction transaction, _) = await engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        HardwareControlLeaseV1? lease = await suiteStore.GetSuiteEntityAsync<HardwareControlLeaseV1>(
            SuiteEntityKind.HardwareControlLease,
            HardwareControlLeaseV1.DefaultId,
            CancellationToken.None);
        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
        Assert.NotNull(lease);
        Assert.False(lease.CleanShutdown);
        Assert.False(lease.DefaultsVerified);
        HardwareControlLeaseItemV1 control = Assert.Single(lease.Controls);
        Assert.Equal("test.adapter", control.AdapterId);
        Assert.Equal("test.control", control.CapabilityId);
    }

    [Fact]
    public async Task RestoreDefaultsFailsClosedWhenReadBackDoesNotMatch()
    {
        FakeAdapter adapter = new() { CurrentValue = 50, FailDefaultVerification = true };
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal());

        HardwareRecoveryResult result = await engine.RestoreDefaultsAsync(
            [new HardwareControlLeaseItemV1("test.adapter", "test.control")],
            CancellationToken.None);

        Assert.False(result.AllDefaultsVerified);
        Assert.Single(result.Errors);
        Assert.False(Assert.Single(result.Verifications).Success);
    }

    [Fact]
    public async Task SuppliedMutationGateSerializesExternalHardwareTransactions()
    {
        FakeAdapter adapter = new();
        using SemaphoreSlim sharedGate = new(1, 1);
        using ProfileTransactionEngine engine = new([adapter], new MemoryJournal(), mutationLock: sharedGate);
        await sharedGate.WaitAsync();
        Task<(ProfileTransaction Transaction, ProfileValidationResult Validation)> apply = engine.ApplyAsync(
            ProfileValidatorTests.Profile(ProfileValidatorTests.Action(required: true), experimental: false),
            Capabilities(),
            expectedRevision: 0,
            confirmExperimental: false,
            CancellationToken.None);

        await Task.Delay(50);
        Assert.Empty(adapter.Calls);
        sharedGate.Release();
        (ProfileTransaction transaction, _) = await apply;

        Assert.Equal(ProfileTransactionState.Committed, transaction.State);
    }

    private static Dictionary<string, CapabilityDescriptor> Capabilities() =>
        new Dictionary<string, CapabilityDescriptor>
        {
            ["test.control"] = ProfileValidatorTests.Capability(CapabilityAccessState.Verified)
        };

    private sealed class FakeAdapter : IHardwareAdapter, IHardwareStateVerifier
    {
        public double CurrentValue { get; set; }

        public bool FailVerification { get; init; }

        public bool CancelDuringApply { get; init; }

        public bool FailRollback { get; init; }

        public bool FailRollbackVerification { get; init; }

        public bool FailDefaultVerification { get; init; }

        public bool UnknownDuringApply { get; init; }

        public List<string> Calls { get; } = [];

        public List<string> AppliedActionIds { get; } = [];

        public AdapterManifest Manifest { get; } = new(
            "test.adapter", "Test", "1", "GPL-3.0-only", null, AdapterExecutionContext.AdapterHost, ["test"], ["test"]);

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SensorSample>>([]);

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
        {
            Calls.Add($"prepare:{action.Id}");
            return Task.FromResult(new PreparedAction(
                action,
                ControlValue.FromNumeric(CurrentValue),
                DateTimeOffset.UtcNow,
                "token"));
        }

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            Calls.Add("apply");
            AppliedActionIds.Add(action.Action.Id);
            CurrentValue = action.Action.Value.Numeric!.Value;
            if (CancelDuringApply)
            {
                throw new OperationCanceledException("Injected cancellation after a partial write.");
            }

            if (UnknownDuringApply)
            {
                throw new HardwareStateUnknownException(Manifest.Id, "Apply", "Injected mutation timeout.");
            }

            return Task.CompletedTask;
        }

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            Calls.Add("verify");
            return Task.FromResult(new ActionVerification(
                action.Action.Id,
                !FailVerification,
                ControlValue.FromNumeric(CurrentValue),
                FailVerification ? "Injected failure." : "Verified."));
        }

        public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            Calls.Add("rollback");
            if (FailRollback)
            {
                throw new InvalidOperationException("Injected rollback failure.");
            }

            CurrentValue = action.PreviousValue!.Numeric!.Value;
            return Task.CompletedTask;
        }

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
        {
            Calls.Add("reset-default");
            CurrentValue = 0;
            return Task.CompletedTask;
        }

        public Task<HardwareStateVerification> VerifyDefaultStateAsync(string capabilityId, CancellationToken cancellationToken) =>
            Task.FromResult(new HardwareStateVerification(
                Manifest.Id,
                capabilityId,
                !FailDefaultVerification && CurrentValue == 0,
                ControlValue.FromNumeric(CurrentValue),
                FailDefaultVerification ? "Injected default read-back failure." : "Default read back."));

        public Task<HardwareStateVerification> VerifyRollbackStateAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            double expected = action.PreviousValue!.Numeric!.Value;
            bool success = !FailRollbackVerification && CurrentValue == expected;
            return Task.FromResult(new HardwareStateVerification(
                Manifest.Id,
                action.Action.CapabilityId,
                success,
                ControlValue.FromNumeric(CurrentValue),
                success ? "Rollback read back." : "Injected rollback read-back failure."));
        }

        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "Healthy", []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class MemorySuiteStore : ISuiteStateStore
    {
        private readonly Dictionary<(SuiteEntityKind Kind, string Id), object> _entities = [];

        public Task<IReadOnlyList<T>> GetSuiteEntitiesAsync<T>(SuiteEntityKind kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<T>>(_entities
                .Where(pair => pair.Key.Kind == kind)
                .Select(pair => (T)pair.Value)
                .ToArray());

        public Task<T?> GetSuiteEntityAsync<T>(SuiteEntityKind kind, string id, CancellationToken cancellationToken) =>
            Task.FromResult(_entities.TryGetValue((kind, id), out object? value) ? (T?)value : default);

        public Task SaveSuiteEntityAsync<T>(SuiteEntityKind kind, string id, T entity, CancellationToken cancellationToken)
        {
            _entities[(kind, id)] = entity!;
            return Task.CompletedTask;
        }

        public Task DeleteSuiteEntityAsync(SuiteEntityKind kind, string id, CancellationToken cancellationToken)
        {
            _entities.Remove((kind, id));
            return Task.CompletedTask;
        }
    }
}
