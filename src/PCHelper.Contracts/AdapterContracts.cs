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

public interface IProfileTransactionJournal
{
    Task SaveAsync(ProfileTransaction transaction, CancellationToken cancellationToken);

    Task<ProfileTransaction?> GetPendingAsync(CancellationToken cancellationToken);

    Task ClearPendingAsync(string transactionId, CancellationToken cancellationToken);
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
