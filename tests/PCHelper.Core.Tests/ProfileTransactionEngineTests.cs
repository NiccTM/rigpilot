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

        Assert.Equal(ProfileTransactionState.RollingBack, transaction.State);
        Assert.Equal(transaction.Id, (await journal.GetPendingAsync(CancellationToken.None))?.Id);
        Assert.Contains("Rollback errors", transaction.Error);
    }

    private static Dictionary<string, CapabilityDescriptor> Capabilities() =>
        new Dictionary<string, CapabilityDescriptor>
        {
            ["test.control"] = ProfileValidatorTests.Capability(CapabilityAccessState.Verified)
        };

    private sealed class FakeAdapter : IHardwareAdapter
    {
        public double CurrentValue { get; set; }

        public bool FailVerification { get; init; }

        public bool CancelDuringApply { get; init; }

        public bool FailRollback { get; init; }

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

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) => Task.CompletedTask;

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
}
