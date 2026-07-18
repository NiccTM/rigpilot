using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed class ProfileTransactionEngine : IDisposable
{
    private readonly Dictionary<string, IHardwareAdapter> _adapters;
    private readonly IProfileTransactionJournal _journal;
    private readonly SemaphoreSlim _mutationLock;
    private readonly bool _ownsMutationLock;
    private readonly ISuiteStateStore? _suiteStore;
    private readonly string _serviceInstanceId;
    private long _revision;

    public ProfileTransactionEngine(
        IEnumerable<IHardwareAdapter> adapters,
        IProfileTransactionJournal journal,
        long initialRevision = 0,
        SemaphoreSlim? mutationLock = null,
        ISuiteStateStore? suiteStore = null,
        string? serviceInstanceId = null)
    {
        _adapters = adapters.ToDictionary(adapter => adapter.Manifest.Id, StringComparer.Ordinal);
        _journal = journal;
        _mutationLock = mutationLock ?? new SemaphoreSlim(1, 1);
        _ownsMutationLock = mutationLock is null;
        _suiteStore = suiteStore;
        _serviceInstanceId = serviceInstanceId ?? Guid.NewGuid().ToString("N");
        _revision = initialRevision;
    }

    public long Revision => Interlocked.Read(ref _revision);

    public string? ActiveProfileId { get; private set; }

    public async Task<(ProfileTransaction Transaction, ProfileValidationResult Validation)> ApplyAsync(
        ProfileV1 profile,
        IReadOnlyDictionary<string, CapabilityDescriptor> capabilities,
        long? expectedRevision,
        bool confirmExperimental,
        CancellationToken cancellationToken)
    {
        ProfileValidationResult validation = ProfileValidator.Validate(profile, capabilities, confirmExperimental);
        if (!validation.Valid)
        {
            return (CreateRejectedTransaction(profile, validation.Errors), validation);
        }

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedRevision is long expected && expected != Revision)
            {
                throw new StateRevisionException(expected, Revision);
            }

            HashSet<string> skippedActionIds = validation.SkippedOptionalActions
                .Select(ParseActionId)
                .Where(id => id is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            IReadOnlyList<ProfileAction> actions = profile.Actions
                .Where(action => !skippedActionIds.Contains(action.Id))
                .OrderBy(action => SafetyOrder(capabilities[action.CapabilityId].Domain))
                .ThenBy(action => action.Order)
                .ThenBy(action => action.Id, StringComparer.Ordinal)
                .ToArray();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            ProfileTransaction transaction = new(
                Guid.NewGuid().ToString("N"),
                Revision,
                profile.Id,
                ProfileTransactionState.Pending,
                now,
                now,
                [],
                [],
                null);
            await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);

            List<PreparedAction> prepared = [];
            List<PreparedAction> applied = [];
            List<ActionVerification> verifications = [];

            try
            {
                foreach (ProfileAction action in actions)
                {
                    IHardwareAdapter adapter = GetAdapter(action.AdapterId);
                    prepared.Add(await adapter.PrepareAsync(action, cancellationToken).ConfigureAwait(false));
                }

                transaction = Update(transaction, ProfileTransactionState.Prepared, prepared, verifications, null);
                await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);

                transaction = Update(transaction, ProfileTransactionState.Applying, prepared, verifications, null);
                await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);

                foreach (PreparedAction action in prepared)
                {
                    IHardwareAdapter adapter = GetAdapter(action.Action.AdapterId);
                    // Treat an attempted apply as changed: native calls can fail after a partial write.
                    applied.Add(action);
                    await adapter.ApplyAsync(action, cancellationToken).ConfigureAwait(false);
                }

                transaction = Update(transaction, ProfileTransactionState.Verifying, prepared, verifications, null);
                await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);

                foreach (PreparedAction action in prepared)
                {
                    IHardwareAdapter adapter = GetAdapter(action.Action.AdapterId);
                    ActionVerification verification = await adapter.VerifyAsync(action, cancellationToken).ConfigureAwait(false);
                    verifications.Add(verification);
                    if (!verification.Success)
                    {
                        throw new ProfileVerificationException(verification.Message);
                    }
                }

                await RecordActiveControlsUnsafeAsync(
                    profile.Id,
                    transaction.Id,
                    prepared.Select(item => new HardwareControlLeaseItemV1(
                        item.Action.AdapterId,
                        item.Action.CapabilityId)),
                    cancellationToken).ConfigureAwait(false);

                long committedRevision = Interlocked.Increment(ref _revision);
                transaction = transaction with
                {
                    Revision = committedRevision,
                    State = ProfileTransactionState.Committed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    PreparedActions = prepared.ToArray(),
                    Verifications = verifications.ToArray()
                };
                await _journal.SaveAsync(transaction, cancellationToken).ConfigureAwait(false);
                await _journal.ClearPendingAsync(transaction.Id, cancellationToken).ConfigureAwait(false);
                ActiveProfileId = profile.Id;
                return (transaction, validation);
            }
            catch (Exception exception)
            {
                transaction = Update(transaction, ProfileTransactionState.RollingBack, prepared, verifications, exception.Message);
                await _journal.SaveAsync(transaction, CancellationToken.None).ConfigureAwait(false);

                List<string> rollbackErrors = [];
                foreach (PreparedAction action in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        IHardwareAdapter adapter = GetAdapter(action.Action.AdapterId);
                        await adapter.RollbackAsync(action, CancellationToken.None).ConfigureAwait(false);
                        if (adapter is not IHardwareStateVerifier stateVerifier)
                        {
                            rollbackErrors.Add($"{action.Action.Id}: adapter does not implement rollback read-back verification.");
                            continue;
                        }

                        HardwareStateVerification rollbackVerification = await stateVerifier
                            .VerifyRollbackStateAsync(action, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!rollbackVerification.Success)
                        {
                            rollbackErrors.Add($"{action.Action.Id}: {rollbackVerification.Message}");
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackErrors.Add($"{action.Action.Id}: {rollbackException.Message}");
                    }
                }

                bool unknownMutationOutcome = exception is HardwareStateUnknownException;
                bool recoveryRequired = unknownMutationOutcome || rollbackErrors.Count > 0;
                string error = rollbackErrors.Count == 0
                    ? exception.Message
                    : $"{exception.Message} Rollback errors: {string.Join("; ", rollbackErrors)}";
                transaction = Update(
                    transaction,
                    recoveryRequired ? ProfileTransactionState.RecoveryRequired : ProfileTransactionState.RolledBack,
                    prepared,
                    verifications,
                    error);
                await _journal.SaveAsync(transaction, CancellationToken.None).ConfigureAwait(false);
                if (recoveryRequired)
                {
                    await MarkRecoveryRequiredLeaseUnsafeAsync(transaction, error).ConfigureAwait(false);
                }
                else
                {
                    await _journal.ClearPendingAsync(transaction.Id, CancellationToken.None).ConfigureAwait(false);
                }

                return (transaction, validation);
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Resets every leased capability and independently reads it back. Missing
    /// adapters and missing verifier implementations fail closed.
    /// </summary>
    public async Task<HardwareRecoveryResult> RestoreDefaultsAsync(
        IEnumerable<HardwareControlLeaseItemV1> controls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controls);
        HardwareControlLeaseItemV1[] requested = controls
            .Where(item => !string.IsNullOrWhiteSpace(item.AdapterId) && !string.IsNullOrWhiteSpace(item.CapabilityId))
            .DistinctBy(item => (item.AdapterId, item.CapabilityId))
            .ToArray();
        List<HardwareStateVerification> verifications = [];
        List<string> errors = [];

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (HardwareControlLeaseItemV1 control in requested.Reverse())
            {
                if (!_adapters.TryGetValue(control.AdapterId, out IHardwareAdapter? adapter))
                {
                    string message = $"Adapter '{control.AdapterId}' is unavailable for default recovery.";
                    errors.Add($"{control.CapabilityId}: {message}");
                    verifications.Add(new HardwareStateVerification(control.AdapterId, control.CapabilityId, false, null, message));
                    continue;
                }

                try
                {
                    await adapter.ResetToDefaultAsync(control.CapabilityId, cancellationToken).ConfigureAwait(false);
                    if (adapter is not IHardwareStateVerifier verifier)
                    {
                        string message = "The adapter does not implement default-state read-back verification.";
                        errors.Add($"{control.CapabilityId}: {message}");
                        verifications.Add(new HardwareStateVerification(control.AdapterId, control.CapabilityId, false, null, message));
                        continue;
                    }

                    HardwareStateVerification verification = await verifier
                        .VerifyDefaultStateAsync(control.CapabilityId, cancellationToken)
                        .ConfigureAwait(false);
                    verifications.Add(verification);
                    if (!verification.Success)
                    {
                        errors.Add($"{control.CapabilityId}: {verification.Message}");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{control.CapabilityId}: {exception.Message}");
                    verifications.Add(new HardwareStateVerification(
                        control.AdapterId,
                        control.CapabilityId,
                        false,
                        null,
                        exception.Message));
                }
            }

            if (errors.Count == 0)
            {
                ActiveProfileId = null;
            }

            return new HardwareRecoveryResult(errors.Count == 0, verifications, errors);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task RecordActiveControlsAsync(
        string? profileId,
        string? transactionId,
        IEnumerable<HardwareControlLeaseItemV1> controls,
        CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecordActiveControlsUnsafeAsync(profileId, transactionId, controls, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task RecordActiveControlsUnsafeAsync(
        string? profileId,
        string? transactionId,
        IEnumerable<HardwareControlLeaseItemV1> controls,
        CancellationToken cancellationToken)
    {
        if (_suiteStore is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        HardwareControlLeaseV1? existing = await _suiteStore.GetSuiteEntityAsync<HardwareControlLeaseV1>(
            SuiteEntityKind.HardwareControlLease,
            HardwareControlLeaseV1.DefaultId,
            cancellationToken).ConfigureAwait(false);
        HardwareControlLeaseItemV1[] merged = (existing?.Controls ?? [])
            .Concat(controls)
            .DistinctBy(item => (item.AdapterId, item.CapabilityId))
            .OrderBy(item => item.AdapterId, StringComparer.Ordinal)
            .ThenBy(item => item.CapabilityId, StringComparer.Ordinal)
            .ToArray();
        HardwareControlLeaseV1 lease = new(
            HardwareControlLeaseV1.CurrentSchemaVersion,
            HardwareControlLeaseV1.DefaultId,
            _serviceInstanceId,
            profileId ?? existing?.ActiveProfileId,
            transactionId ?? existing?.LastTransactionId,
            merged,
            existing?.AcquiredAt ?? now,
            now,
            CleanShutdown: false,
            DefaultsVerified: false,
            HardwareControlLeaseState.Active,
            merged.Length == 0
                ? "Service is running with no leased hardware controls."
                : $"Service owns {merged.Length} hardware control(s); shutdown defaults are not yet verified.");
        await _suiteStore.SaveSuiteEntityAsync(
            SuiteEntityKind.HardwareControlLease,
            lease.Id,
            lease,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkRecoveryRequiredLeaseUnsafeAsync(ProfileTransaction transaction, string message)
    {
        if (_suiteStore is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        HardwareControlLeaseV1? existing = await _suiteStore.GetSuiteEntityAsync<HardwareControlLeaseV1>(
            SuiteEntityKind.HardwareControlLease,
            HardwareControlLeaseV1.DefaultId,
            CancellationToken.None).ConfigureAwait(false);
        HardwareControlLeaseItemV1[] controls = (existing?.Controls ?? [])
            .Concat(transaction.PreparedActions.Select(item => new HardwareControlLeaseItemV1(
                item.Action.AdapterId,
                item.Action.CapabilityId)))
            .DistinctBy(item => (item.AdapterId, item.CapabilityId))
            .ToArray();
        HardwareControlLeaseV1 lease = new(
            HardwareControlLeaseV1.CurrentSchemaVersion,
            HardwareControlLeaseV1.DefaultId,
            _serviceInstanceId,
            transaction.ProfileId,
            transaction.Id,
            controls,
            existing?.AcquiredAt ?? now,
            now,
            CleanShutdown: false,
            DefaultsVerified: false,
            HardwareControlLeaseState.RecoveryRequired,
            message);
        await _suiteStore.SaveSuiteEntityAsync(
            SuiteEntityKind.HardwareControlLease,
            lease.Id,
            lease,
            CancellationToken.None).ConfigureAwait(false);
    }

    private IHardwareAdapter GetAdapter(string id) =>
        _adapters.TryGetValue(id, out IHardwareAdapter? adapter)
            ? adapter
            : throw new InvalidOperationException($"Adapter '{id}' is not loaded.");

    private static ProfileTransaction Update(
        ProfileTransaction transaction,
        ProfileTransactionState state,
        IReadOnlyList<PreparedAction> prepared,
        IReadOnlyList<ActionVerification> verifications,
        string? error) => transaction with
        {
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            PreparedActions = prepared.ToArray(),
            Verifications = verifications.ToArray(),
            Error = error
        };

    private static ProfileTransaction CreateRejectedTransaction(ProfileV1 profile, IReadOnlyList<string> errors)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProfileTransaction(
            Guid.NewGuid().ToString("N"),
            0,
            profile.Id,
            ProfileTransactionState.Failed,
            now,
            now,
            [],
            [],
            string.Join(" ", errors));
    }

    private static string? ParseActionId(string message)
    {
        const string marker = "Action '";
        int start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = message.IndexOf('\'', start);
        return end > start ? message[start..end] : null;
    }

    private static int SafetyOrder(ControlDomain domain) => domain switch
    {
        ControlDomain.CoolingSafety => 0,
        ControlDomain.Power => 1,
        ControlDomain.Cpu => 2,
        ControlDomain.Gpu => 3,
        ControlDomain.Cooling => 4,
        ControlDomain.Lighting => 5,
        _ => 6
    };

    public void Dispose()
    {
        if (_ownsMutationLock)
        {
            _mutationLock.Dispose();
        }
    }
}

public sealed class StateRevisionException(long expected, long actual)
    : InvalidOperationException($"State revision mismatch. Expected {expected}, actual {actual}.")
{
    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}

public sealed class ProfileVerificationException(string message) : InvalidOperationException(message);
