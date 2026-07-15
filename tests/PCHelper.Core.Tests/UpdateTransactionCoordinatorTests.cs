using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class UpdateTransactionCoordinatorTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task AppliesAndVerifiesNonRebootUpdate()
    {
        FakeExecutor executor = new();
        FakeJournal journal = new();
        UpdateTransactionCoordinator coordinator = new(executor, journal);

        UpdateTransactionV1 result = await coordinator.ApplyAsync(Plan(requiresReboot: false), CancellationToken.None);

        Assert.Equal(UpdateTransactionState.Completed, result.State);
        Assert.True(executor.ExportedRollback);
        Assert.True(executor.Applied);
        Assert.Contains(UpdateTransactionState.Verifying, journal.States);
        Assert.Equal(UpdateTransactionState.Completed, journal.States[^1]);
    }

    [Fact]
    public async Task PersistsRebootSentinelAndCompletesAfterResume()
    {
        FakeExecutor executor = new();
        FakeJournal journal = new();
        UpdateTransactionCoordinator coordinator = new(executor, journal);

        UpdateTransactionV1 pending = await coordinator.ApplyAsync(Plan(requiresReboot: true), CancellationToken.None);

        Assert.Equal(UpdateTransactionState.PendingReboot, pending.State);
        Assert.True(executor.SentinelWritten);
        Assert.False(executor.SentinelCleared);

        UpdateTransactionV1 completed = await coordinator.ResumeAfterRebootAsync(pending, CancellationToken.None);
        Assert.Equal(UpdateTransactionState.Completed, completed.State);
        Assert.True(executor.SentinelCleared);
    }

    [Fact]
    public async Task VersionMismatchRollsBack()
    {
        FakeExecutor executor = new() { VersionMatches = false };
        FakeJournal journal = new();
        UpdateTransactionCoordinator coordinator = new(executor, journal);

        UpdateTransactionV1 result = await coordinator.ApplyAsync(Plan(requiresReboot: false), CancellationToken.None);

        Assert.Equal(UpdateTransactionState.RolledBack, result.State);
        Assert.True(executor.RolledBack);
        Assert.Contains("expected target", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackFailureEntersRecoveryRequired()
    {
        FakeExecutor executor = new() { ApplyError = new IOException("installer failed"), RollbackError = new IOException("restore failed") };
        FakeJournal journal = new();
        UpdateTransactionCoordinator coordinator = new(executor, journal);

        UpdateTransactionV1 result = await coordinator.ApplyAsync(Plan(requiresReboot: false), CancellationToken.None);

        Assert.Equal(UpdateTransactionState.RecoveryRequired, result.State);
        Assert.Contains("restore failed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidationFailureDoesNotInvokeRollbackExecutor()
    {
        FakeExecutor executor = new() { SignatureValid = false };
        UpdateTransactionCoordinator coordinator = new(executor, new FakeJournal());

        UpdateTransactionV1 result = await coordinator.ApplyAsync(Plan(requiresReboot: false), CancellationToken.None);

        Assert.Equal(UpdateTransactionState.Failed, result.State);
        Assert.False(executor.Applied);
        Assert.False(executor.RolledBack);
    }

    private static UpdatePlanV1 Plan(bool requiresReboot)
    {
        UpdateCandidateV1 candidate = new(
            UpdateCandidateV1.CurrentSchemaVersion,
            "update.driver.reference",
            UpdateKind.Driver,
            "PCI\\VEN_10DE&DEV_2204",
            "1.0.0",
            "1.1.0",
            new Uri("https://vendor.example/driver.exe"),
            Hash,
            "Reference Vendor",
            requiresReboot,
            RequiresBitLockerSuspension: false,
            RecoveryMethod: "Windows driver rollback");
        return new UpdatePlanV1(
            UpdatePlanV1.CurrentSchemaVersion,
            "plan.driver.reference",
            candidate,
            "C:\\ProgramData\\PCHelper\\Updates\\driver.exe",
            [],
            ["Restore exported driver package"],
            UserConfirmed: true);
    }

    private sealed class FakeExecutor : IUpdatePackageExecutor
    {
        public bool ExportedRollback { get; private set; }
        public bool Applied { get; private set; }
        public bool RolledBack { get; private set; }
        public bool SentinelWritten { get; private set; }
        public bool SentinelCleared { get; private set; }
        public bool VersionMatches { get; set; } = true;
        public bool SignatureValid { get; set; } = true;
        public Exception? ApplyError { get; set; }
        public Exception? RollbackError { get; set; }

        public Task<UpdateValidationContext> InspectStagedPackageAsync(UpdatePlanV1 plan, CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateValidationContext(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vendor.example" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plan.Candidate.DeviceId },
                Hash,
                plan.Candidate.ExpectedPublisher,
                PackageSignatureValid: SignatureValid,
                StablePower: true,
                BitLockerRecoveryKeyAvailable: true,
                DeveloperBuild: false));

        public Task ExportRollbackPackageAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
        {
            ExportedRollback = true;
            return Task.CompletedTask;
        }

        public Task ApplyAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
        {
            Applied = true;
            return ApplyError is null ? Task.CompletedTask : Task.FromException(ApplyError);
        }

        public Task<bool> VerifyInstalledVersionAsync(UpdateCandidateV1 candidate, CancellationToken cancellationToken) =>
            Task.FromResult(VersionMatches);

        public Task RollbackAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
        {
            RolledBack = true;
            return RollbackError is null ? Task.CompletedTask : Task.FromException(RollbackError);
        }

        public Task WriteRebootSentinelAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken)
        {
            SentinelWritten = true;
            return Task.CompletedTask;
        }

        public Task ClearRebootSentinelAsync(string transactionId, CancellationToken cancellationToken)
        {
            SentinelCleared = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJournal : IUpdateTransactionJournal
    {
        public List<UpdateTransactionState> States { get; } = [];

        public Task SaveAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken)
        {
            States.Add(transaction.State);
            return Task.CompletedTask;
        }
    }
}
