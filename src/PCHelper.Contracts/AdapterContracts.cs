namespace PCHelper.Contracts;

public interface IHardwareAdapter : IAsyncDisposable
{
    AdapterManifest Manifest { get; }

    Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken);

    Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken);

    Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken);

    Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken);

    Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken);

    Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken);

    Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Opt-in read-back contract for adapters that can prove a rollback or a
/// firmware/vendor-default reset. A reset is never considered successful from
/// the write call alone.
/// </summary>
public interface IHardwareStateVerifier
{
    Task<HardwareStateVerification> VerifyDefaultStateAsync(
        string capabilityId,
        CancellationToken cancellationToken);

    Task<HardwareStateVerification> VerifyRollbackStateAsync(
        PreparedAction action,
        CancellationToken cancellationToken);
}

/// <summary>
/// Opt-in policy for adapters whose Probe operation performs slow, mostly
/// static topology discovery. Sensor reads remain uncached and run every
/// telemetry tick. A zero duration keeps the default probe-every-tick behavior.
/// </summary>
public interface IAdapterTopologyCachePolicy
{
    TimeSpan TopologyCacheDuration { get; }
}

public interface IOwnershipAwareAdapter
{
    Task<OwnershipLeaseV1> AcquireOwnershipAsync(
        IReadOnlyList<string> resourceFamilies,
        CancellationToken cancellationToken);

    Task ReleaseOwnershipAsync(OwnershipLeaseV1 lease, CancellationToken cancellationToken);
}

public interface IUpdateAdapter
{
    Task<IReadOnlyList<UpdateCandidateV1>> DiscoverUpdatesAsync(
        HardwareDevice device,
        CancellationToken cancellationToken);

    Task<UpdatePlanV1> ValidateUpdateAsync(UpdatePlanV1 plan, CancellationToken cancellationToken);

    Task<UpdateTransactionV1> ApplyUpdateAsync(UpdatePlanV1 plan, CancellationToken cancellationToken);

    Task<UpdateTransactionV1> VerifyUpdateAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken);

    Task<UpdateTransactionV1> RollbackUpdateAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken);
}

public interface ITraceableAdapter
{
    IAsyncEnumerable<AdapterTraceEvent> ReadTraceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional, privacy-minimised diagnostics for an isolated adapter host. This
/// is not a control surface and must never include a session token, user name,
/// device path, or raw native exception text.
/// </summary>
public interface IAdapterDiagnosticsProvider
{
    Task<AdapterHostDiagnosticsV1?> GetDiagnosticsAsync(CancellationToken cancellationToken);
}

public interface IProfileTransactionJournal
{
    Task SaveAsync(ProfileTransaction transaction, CancellationToken cancellationToken);

    Task<ProfileTransaction?> GetPendingAsync(CancellationToken cancellationToken);

    Task ClearPendingAsync(string transactionId, CancellationToken cancellationToken);

    Task<ProfileTransaction?> GetLatestCommittedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ProfileTransaction?>(null);
}

public interface IProfileStore
{
    Task<IReadOnlyList<ProfileV1>> GetProfilesAsync(CancellationToken cancellationToken);

    Task<ProfileV1?> GetProfileAsync(string id, CancellationToken cancellationToken);

    Task SaveProfileAsync(ProfileV1 profile, CancellationToken cancellationToken);
}

public interface ISensorHistoryStore
{
    Task AppendAsync(IReadOnlyList<SensorSample> samples, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorSample>> QueryAsync(
        string sensorId,
        DateTimeOffset lowerBoundary,
        DateTimeOffset upperBoundary,
        CancellationToken cancellationToken);

    Task EnforceRetentionAsync(CancellationToken cancellationToken);
}

public interface IHardwareOperationStore
{
    Task SaveOperationAsync(HardwareOperationStatus operation, CancellationToken cancellationToken);

    Task<HardwareOperationStatus?> GetLatestOperationAsync(CancellationToken cancellationToken);

    Task<HardwareOperationStatus?> GetOperationAsync(string operationId, CancellationToken cancellationToken);

    Task<HardwareOperationStatus?> GetPendingOperationAsync(CancellationToken cancellationToken);

    Task ClearPendingOperationAsync(string operationId, CancellationToken cancellationToken);
}

public interface IAutomationRuleStore
{
    Task<IReadOnlyList<AutomationRuleV1>> GetAutomationRulesAsync(CancellationToken cancellationToken);

    Task SaveAutomationRuleAsync(AutomationRuleV1 rule, CancellationToken cancellationToken);

    Task DeleteAutomationRuleAsync(string ruleId, CancellationToken cancellationToken);
}

public interface ISuiteStateStore
{
    Task<IReadOnlyList<T>> GetSuiteEntitiesAsync<T>(
        SuiteEntityKind kind,
        CancellationToken cancellationToken);

    Task<T?> GetSuiteEntityAsync<T>(
        SuiteEntityKind kind,
        string id,
        CancellationToken cancellationToken);

    Task SaveSuiteEntityAsync<T>(
        SuiteEntityKind kind,
        string id,
        T entity,
        CancellationToken cancellationToken);

    Task DeleteSuiteEntityAsync(
        SuiteEntityKind kind,
        string id,
        CancellationToken cancellationToken);
}
