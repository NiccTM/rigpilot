using PCHelper.Contracts;

namespace PCHelper.Core;

public interface IUpdatePackageExecutor
{
    Task<UpdateValidationContext> InspectStagedPackageAsync(UpdatePlanV1 plan, CancellationToken cancellationToken);

    Task ExportRollbackPackageAsync(UpdatePlanV1 plan, CancellationToken cancellationToken);

    Task ApplyAsync(UpdatePlanV1 plan, CancellationToken cancellationToken);

    Task<bool> VerifyInstalledVersionAsync(UpdateCandidateV1 candidate, CancellationToken cancellationToken);

    Task RollbackAsync(UpdatePlanV1 plan, CancellationToken cancellationToken);

    Task WriteRebootSentinelAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken);

    Task ClearRebootSentinelAsync(string transactionId, CancellationToken cancellationToken);
}

public interface IUpdateTransactionJournal
{
    Task SaveAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken);
}

public sealed class UpdateTransactionCoordinator(
    IUpdatePackageExecutor executor,
    IUpdateTransactionJournal journal)
{
    public async Task<UpdateTransactionV1> ApplyAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UpdateTransactionV1 transaction = new(
            UpdateTransactionV1.CurrentSchemaVersion,
            $"update.{Guid.NewGuid():N}",
            plan,
            UpdateTransactionState.Planned,
            now,
            now,
            plan.Candidate.CurrentVersion,
            null);
        bool applyStarted = false;
        await SaveAsync(UpdateTransactionState.Planned).ConfigureAwait(false);
        try
        {
            UpdateValidationContext context = await executor.InspectStagedPackageAsync(plan, cancellationToken).ConfigureAwait(false);
            SuiteValidationResult validation = UpdatePlanValidator.Validate(plan, context);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(string.Join(" ", validation.Errors));
            }
            await SaveAsync(UpdateTransactionState.Validated).ConfigureAwait(false);
            await executor.ExportRollbackPackageAsync(plan, cancellationToken).ConfigureAwait(false);
            await SaveAsync(UpdateTransactionState.Staged).ConfigureAwait(false);
            await SaveAsync(UpdateTransactionState.Applying).ConfigureAwait(false);
            applyStarted = true;
            await executor.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Candidate.RequiresReboot)
            {
                await SaveAsync(UpdateTransactionState.PendingReboot).ConfigureAwait(false);
                await executor.WriteRebootSentinelAsync(transaction, cancellationToken).ConfigureAwait(false);
                return transaction;
            }
            return await VerifyOrRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            transaction = transaction with { Error = "Update operation was cancelled." };
            await MarkFailedAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            if (applyStarted)
            {
                await RollbackAfterFailureAsync(transaction with { State = UpdateTransactionState.Failed }, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception)
        {
            transaction = transaction with { Error = exception.Message };
            if (transaction.State == UpdateTransactionState.Completed)
            {
                UpdateTransactionV1 recovery = transaction with
                {
                    State = UpdateTransactionState.RecoveryRequired,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await journal.SaveAsync(recovery, CancellationToken.None).ConfigureAwait(false);
                return recovery;
            }
            await MarkFailedAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            transaction = transaction with { State = UpdateTransactionState.Failed };
            if (!applyStarted)
            {
                return transaction;
            }
            return await RollbackAfterFailureAsync(transaction, CancellationToken.None).ConfigureAwait(false);
        }

        async Task SaveAsync(UpdateTransactionState state)
        {
            if (transaction.State != state
                && !UpdatePlanValidator.CanTransition(transaction.State, state))
            {
                throw new InvalidOperationException($"Invalid update state transition {transaction.State} -> {state}.");
            }
            transaction = transaction with { State = state, UpdatedAt = DateTimeOffset.UtcNow };
            await journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<UpdateTransactionV1> ResumeAfterRebootAsync(
        UpdateTransactionV1 transaction,
        CancellationToken cancellationToken)
    {
        if (transaction.State != UpdateTransactionState.PendingReboot)
        {
            throw new InvalidOperationException("Only a pending-reboot update can resume after boot.");
        }
        return await VerifyOrRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateTransactionV1> VerifyOrRollbackAsync(
        UpdateTransactionV1 transaction,
        CancellationToken cancellationToken)
    {
        transaction = await TransitionAsync(transaction, UpdateTransactionState.Verifying, cancellationToken).ConfigureAwait(false);
        if (await executor.VerifyInstalledVersionAsync(transaction.Plan.Candidate, cancellationToken).ConfigureAwait(false))
        {
            await executor.ClearRebootSentinelAsync(transaction.Id, cancellationToken).ConfigureAwait(false);
            transaction = transaction with { Error = null };
            return await TransitionAsync(transaction, UpdateTransactionState.Completed, cancellationToken).ConfigureAwait(false);
        }
        transaction = transaction with { Error = "Installed version did not match the expected target after update." };
        return await RollbackAfterFailureAsync(transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateTransactionV1> RollbackAfterFailureAsync(
        UpdateTransactionV1 transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await executor.RollbackAsync(transaction.Plan, cancellationToken).ConfigureAwait(false);
            UpdateTransactionV1 rolledBack = transaction with
            {
                State = UpdateTransactionState.RolledBack,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await journal.SaveAsync(rolledBack, cancellationToken).ConfigureAwait(false);
            await executor.ClearRebootSentinelAsync(transaction.Id, cancellationToken).ConfigureAwait(false);
            return rolledBack;
        }
        catch (Exception rollback) when (rollback is not OperationCanceledException)
        {
            UpdateTransactionV1 recovery = transaction with
            {
                State = UpdateTransactionState.RecoveryRequired,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = $"{transaction.Error} Rollback failed: {rollback.Message}".Trim()
            };
            await journal.SaveAsync(recovery, cancellationToken).ConfigureAwait(false);
            return recovery;
        }
    }

    private async Task MarkFailedAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken)
    {
        UpdateTransactionV1 failed = transaction with
        {
            State = UpdateTransactionState.Failed,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await journal.SaveAsync(failed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateTransactionV1> TransitionAsync(
        UpdateTransactionV1 transaction,
        UpdateTransactionState next,
        CancellationToken cancellationToken)
    {
        if (!UpdatePlanValidator.CanTransition(transaction.State, next))
        {
            throw new InvalidOperationException($"Invalid update state transition {transaction.State} -> {next}.");
        }
        UpdateTransactionV1 updated = transaction with { State = next, UpdatedAt = DateTimeOffset.UtcNow };
        await journal.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
