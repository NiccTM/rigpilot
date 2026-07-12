using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed class ProfileTransactionEngine : IDisposable
{
    private readonly Dictionary<string, IHardwareAdapter> _adapters;
    private readonly IProfileTransactionJournal _journal;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private long _revision;

    public ProfileTransactionEngine(
        IEnumerable<IHardwareAdapter> adapters,
        IProfileTransactionJournal journal,
        long initialRevision = 0)
    {
        _adapters = adapters.ToDictionary(adapter => adapter.Manifest.Id, StringComparer.Ordinal);
        _journal = journal;
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

                long committedRevision = Interlocked.Increment(ref _revision);
                ActiveProfileId = profile.Id;
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
                        await GetAdapter(action.Action.AdapterId).RollbackAsync(action, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackErrors.Add($"{action.Action.Id}: {rollbackException.Message}");
                    }
                }

                string error = rollbackErrors.Count == 0
                    ? exception.Message
                    : $"{exception.Message} Rollback errors: {string.Join("; ", rollbackErrors)}";
                transaction = Update(
                    transaction,
                    rollbackErrors.Count == 0 ? ProfileTransactionState.RolledBack : ProfileTransactionState.RollingBack,
                    prepared,
                    verifications,
                    error);
                await _journal.SaveAsync(transaction, CancellationToken.None).ConfigureAwait(false);
                if (rollbackErrors.Count == 0)
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
        _mutationLock.Dispose();
    }
}

public sealed class StateRevisionException(long expected, long actual)
    : InvalidOperationException($"State revision mismatch. Expected {expected}, actual {actual}.")
{
    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}

public sealed class ProfileVerificationException(string message) : InvalidOperationException(message);
