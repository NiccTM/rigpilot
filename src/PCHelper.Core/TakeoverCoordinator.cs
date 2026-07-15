using PCHelper.Contracts;

namespace PCHelper.Core;

public interface ITakeoverProcessController
{
    Task<TakeoverProcessIdentity?> GetCurrentIdentityAsync(string executablePath, CancellationToken cancellationToken);

    Task<bool> RequestGracefulStopAsync(TakeoverProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken);

    Task ForceStopAsync(TakeoverProcessIdentity identity, CancellationToken cancellationToken);

    Task<bool> IsRunningAsync(TakeoverProcessIdentity identity, CancellationToken cancellationToken);
}

public interface ITakeoverStartupController
{
    Task<IReadOnlyList<StartupEntryBackupV1>> BackupAsync(
        TakeoverProcessIdentity identity,
        CancellationToken cancellationToken);

    Task DisableAsync(IReadOnlyList<StartupEntryBackupV1> entries, CancellationToken cancellationToken);

    Task RestoreAsync(IReadOnlyList<StartupEntryBackupV1> entries, CancellationToken cancellationToken);
}

public interface ITakeoverHardwareController
{
    Task ResetAndVerifyAsync(string capabilityId, CancellationToken cancellationToken);

    Task<OwnershipLeaseV1> AcquireAsync(
        IReadOnlyList<string> resourceFamilies,
        CancellationToken cancellationToken);

    Task ReleaseAsync(OwnershipLeaseV1 lease, CancellationToken cancellationToken);
}

public interface ITakeoverJournal
{
    Task SaveAsync(TakeoverTransactionV1 transaction, CancellationToken cancellationToken);
}

public sealed class TakeoverCoordinator(
    ITakeoverProcessController processes,
    ITakeoverStartupController startup,
    ITakeoverHardwareController hardware,
    ITakeoverJournal journal)
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(5);

    public async Task<(TakeoverTransactionV1 Transaction, OwnershipLeaseV1? Lease)> ExecuteAsync(
        TakeoverPlanV1 plan,
        IReadOnlyList<OwnershipConsentV1> consents,
        CancellationToken cancellationToken)
    {
        ValidatePlan(plan);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TakeoverTransactionV1 transaction = new(
            TakeoverTransactionV1.CurrentSchemaVersion,
            $"takeover.{Guid.NewGuid():N}",
            plan,
            TakeoverTransactionState.Planned,
            [],
            [],
            [],
            now,
            now,
            null);
        await SaveAsync(TakeoverTransactionState.Validating, cancellationToken).ConfigureAwait(false);
        List<(TakeoverProcessIdentity Identity, OwnershipConsentV1 Consent)> authorised = [];
        foreach (TakeoverProcessIdentity expected in plan.Processes)
        {
            TakeoverProcessIdentity current = await processes.GetCurrentIdentityAsync(expected.ExecutablePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Takeover target '{expected.DisplayName}' is no longer running.");
            OwnershipConsentV1 consent = consents.FirstOrDefault(item =>
                Path.GetFullPath(item.ExecutablePath).Equals(Path.GetFullPath(current.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                ?? throw new UnauthorizedAccessException($"No stored consent matches '{current.ExecutablePath}'.");
            TakeoverAuthorizationResult authorization = TakeoverConsentValidator.Validate(
                current,
                consent,
                // Force termination is checked only if graceful shutdown
                // fails below. A user may explicitly allow a graceful-only
                // takeover without granting the stronger process-kill right.
                requireForceTermination: false,
                requireStartupDisable: consent.DisableStartup);
            if (!authorization.Authorized)
            {
                throw new UnauthorizedAccessException(string.Join(" ", authorization.Errors));
            }
            TakeoverAuthorizationResult planIdentity = TakeoverConsentValidator.Validate(
                current,
                TakeoverConsentValidator.Create(expected, true, consent.DisableStartup, consent.GrantedAt),
                requireForceTermination: true,
                requireStartupDisable: consent.DisableStartup);
            if (!planIdentity.Authorized)
            {
                throw new InvalidOperationException($"Takeover target changed after preview: {string.Join(" ", planIdentity.Errors)}");
            }
            authorised.Add((current, consent));
        }

        OwnershipLeaseV1? lease = null;
        List<StartupEntryBackupV1> backups = [];
        List<string> stopped = [];
        List<string> reset = [];
        try
        {
            await SaveAsync(TakeoverTransactionState.BackingUpStartup, cancellationToken).ConfigureAwait(false);
            foreach ((TakeoverProcessIdentity identity, OwnershipConsentV1 consent) in authorised)
            {
                IReadOnlyList<StartupEntryBackupV1> entries = await startup.BackupAsync(identity, cancellationToken).ConfigureAwait(false);
                backups.AddRange(entries);
                transaction = transaction with { StartupBackups = backups.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
                await journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);
                if (consent.DisableStartup)
                {
                    await startup.DisableAsync(entries, cancellationToken).ConfigureAwait(false);
                }
            }

            await SaveAsync(TakeoverTransactionState.StoppingProcesses, cancellationToken).ConfigureAwait(false);
            foreach ((TakeoverProcessIdentity identity, OwnershipConsentV1 consent) in authorised)
            {
                bool stoppedGracefully = await processes.RequestGracefulStopAsync(identity, GracefulStopTimeout, cancellationToken).ConfigureAwait(false);
                if (!stoppedGracefully && await processes.IsRunningAsync(identity, cancellationToken).ConfigureAwait(false))
                {
                    if (!consent.AllowForceTermination)
                    {
                        throw new UnauthorizedAccessException($"Consent does not permit force-closing '{identity.DisplayName}'.");
                    }
                    await processes.ForceStopAsync(identity, cancellationToken).ConfigureAwait(false);
                }
                if (await processes.IsRunningAsync(identity, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"'{identity.DisplayName}' is still running after the takeover timeout.");
                }
                stopped.Add(identity.ExecutablePath);
                transaction = transaction with { StoppedProcessPaths = stopped.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
                await journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);
            }

            await SaveAsync(TakeoverTransactionState.ResettingHardware, cancellationToken).ConfigureAwait(false);
            foreach (string capabilityId in plan.ControlsToReset.Distinct(StringComparer.Ordinal))
            {
                await hardware.ResetAndVerifyAsync(capabilityId, cancellationToken).ConfigureAwait(false);
                reset.Add(capabilityId);
                transaction = transaction with { ResetControls = reset.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
                await journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);
            }

            await SaveAsync(TakeoverTransactionState.AcquiringOwnership, cancellationToken).ConfigureAwait(false);
            string[] resources = plan.Processes.SelectMany(process => process.ResourceFamilies)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            lease = await hardware.AcquireAsync(resources, cancellationToken).ConfigureAwait(false);
            await SaveAsync(TakeoverTransactionState.Completed, cancellationToken).ConfigureAwait(false);
            return (transaction, lease);
        }
        catch (OperationCanceledException exception)
        {
            await RollbackAsync(exception, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await RollbackAsync(exception, CancellationToken.None).ConfigureAwait(false);
            return (transaction, null);
        }

        async Task RollbackAsync(Exception exception, CancellationToken safetyToken)
        {
            transaction = transaction with { Error = exception.Message };
            await SaveAsync(TakeoverTransactionState.RollingBack, safetyToken).ConfigureAwait(false);
            List<string> rollbackErrors = [];
            if (lease is not null)
            {
                try { await hardware.ReleaseAsync(lease, safetyToken).ConfigureAwait(false); }
                catch (Exception rollback) { rollbackErrors.Add($"lease: {rollback.Message}"); }
            }
            try { await startup.RestoreAsync(backups, safetyToken).ConfigureAwait(false); }
            catch (Exception rollback) { rollbackErrors.Add($"startup: {rollback.Message}"); }
            transaction = transaction with
            {
                Error = rollbackErrors.Count == 0 ? exception.Message : $"{exception.Message} Rollback failed: {string.Join("; ", rollbackErrors)}"
            };
            await SaveAsync(
                rollbackErrors.Count == 0 ? TakeoverTransactionState.RolledBack : TakeoverTransactionState.RecoveryRequired,
                safetyToken).ConfigureAwait(false);
        }

        async Task SaveAsync(TakeoverTransactionState state, CancellationToken journalToken)
        {
            transaction = transaction with { State = state, UpdatedAt = DateTimeOffset.UtcNow };
            await journal.SaveAsync(transaction, journalToken).ConfigureAwait(false);
        }
    }

    public async Task<TakeoverTransactionV1> GiveControlBackAsync(
        TakeoverTransactionV1 transaction,
        OwnershipLeaseV1 lease,
        CancellationToken cancellationToken)
    {
        foreach (string capabilityId in transaction.ResetControls.Reverse())
        {
            await hardware.ResetAndVerifyAsync(capabilityId, cancellationToken).ConfigureAwait(false);
        }
        await hardware.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
        await startup.RestoreAsync(transaction.StartupBackups, cancellationToken).ConfigureAwait(false);
        TakeoverTransactionV1 released = transaction with
        {
            State = TakeoverTransactionState.Released,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null
        };
        await journal.SaveAsync(released, cancellationToken).ConfigureAwait(false);
        return released;
    }

    private static void ValidatePlan(TakeoverPlanV1 plan)
    {
        if (plan.SchemaVersion != TakeoverPlanV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(plan.Id)
            || plan.Processes.Count == 0
            || plan.Processes.Select(process => process.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Processes.Count)
        {
            throw new InvalidDataException("Takeover plan is malformed or contains duplicate process identities.");
        }
    }
}
