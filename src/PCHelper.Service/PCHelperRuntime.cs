using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.Service;

public sealed class PCHelperRuntime(ILogger<PCHelperRuntime> logger) : IAsyncDisposable
{
    private static readonly HashSet<string> PersistedHistoryUnits =
        ["°C", "RPM", "%", "W", "MHz"];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly ConcurrentDictionary<string, IpcResponse> _idempotentResponses = new(StringComparer.Ordinal);
    private SqliteStateStore? _store;
    private AdapterCoordinator? _coordinator;
    private ProfileTransactionEngine? _engine;
    private HardwareSnapshot _snapshot = EmptySnapshot();
    private bool _rollbackBlocked;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _store = await CreateStoreAsync(cancellationToken).ConfigureAwait(false);
        foreach (ProfileV1 profile in BuiltInProfiles.Create())
        {
            await _store.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        IHardwareAdapter[] adapters =
        [
            new SystemInventoryAdapter(),
            new WindowsPowerAdapter(),
            new LibreHardwareMonitorAdapter()
        ];
        _coordinator = new AdapterCoordinator(adapters);
        _engine = new ProfileTransactionEngine(adapters, _store);
        await RecoverPendingTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(persistSensors: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(bool persistSensors, CancellationToken cancellationToken)
    {
        EnsureInitialised();
        HardwareSnapshot snapshot = await _coordinator!.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (_rollbackBlocked)
        {
            snapshot = snapshot with
            {
                Warnings = snapshot.Warnings
                    .Append(new DiagnosticWarning(
                        "ROLLBACK_BLOCKED",
                        "Critical",
                        "A previous hardware transaction could not be fully rolled back. New profile writes are blocked.",
                        "Use Reset verified controls, inspect logs, and reboot before attempting another profile."))
                    .ToArray()
            };
        }
        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshot = snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }

        if (persistSensors)
        {
            SensorSample[] history = snapshot.Sensors
                .Where(sample => sample.Value is double value
                    && double.IsFinite(value)
                    && PersistedHistoryUnits.Contains(sample.Unit))
                .ToArray();
            await _store!.AppendAsync(history, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task EnforceRetentionAsync(CancellationToken cancellationToken)
    {
        EnsureInitialised();
        return _store!.EnforceRetentionAsync(cancellationToken);
    }

    public Task<IpcResponse> HandleRequestAsync(IpcRequest request, CancellationToken cancellationToken) =>
        HandleRequestAsync(request, new IpcClientContext(IsOperator: true, UserName: null), cancellationToken);

    public async Task<IpcResponse> HandleRequestAsync(IpcRequest request, IpcClientContext client, CancellationToken cancellationToken)
    {
        EnsureInitialised();
        if (IsMutatingCommand(request.Command) && !client.IsOperator)
        {
            return Failure(
                request,
                "NOT_AUTHORIZED",
                "Hardware and profile changes require membership of the PC Helper Operators group or an elevated session. Group membership added by the installer takes effect after you sign out and back in.");
        }

        if (request.IdempotencyKey is string key && _idempotentResponses.TryGetValue(key, out IpcResponse? cached))
        {
            return cached with { RequestId = request.RequestId };
        }

        IpcResponse response;
        try
        {
            response = request.Command switch
            {
                IpcCommand.Handshake => Success(request, new HandshakeResponse(
                    ProtocolConstants.Version,
                    Version,
                    _engine!.Revision)),
                IpcCommand.GetInventory => Success(request, GetSnapshot()),
                IpcCommand.SubscribeSensors => Success(request, GetSnapshot().Sensors),
                IpcCommand.GetProfiles => Success(request, await _store!.GetProfilesAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.ValidateProfile => ValidateProfile(request),
                IpcCommand.ApplyProfile => await ApplyProfileAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ExportReport => ExportReport(request),
                IpcCommand.GetServiceStatus => Success(request, GetStatus()),
                IpcCommand.ResetHardware => await ResetHardwareAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StartCalibration or IpcCommand.StartTune => Failure(
                    request,
                    "NOT_QUALIFIED",
                    "This build does not expose unqualified hardware writes. The exact controller must pass read-back and reset qualification first."),
                IpcCommand.AbortOperation => Failure(request, "NO_ACTIVE_OPERATION", "No calibration or tuning operation is active."),
                _ => Failure(request, "NOT_IMPLEMENTED", $"Command {request.Command} is not implemented.")
            };
        }
        catch (StateRevisionException exception)
        {
            response = Failure(request, "STATE_REVISION_MISMATCH", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ServiceLog.CommandFailed(logger, request.Command, exception);
            response = Failure(request, "COMMAND_FAILED", exception.Message);
        }

        if (request.IdempotencyKey is string idempotencyKey && response.Success)
        {
            if (_idempotentResponses.Count > 256)
            {
                _idempotentResponses.Clear();
            }

            _idempotentResponses[idempotencyKey] = response;
        }

        return response;
    }

    public async ValueTask DisposeAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
        }

        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }

        _engine?.Dispose();

        _snapshotGate.Dispose();
    }

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.2.0";

    private HardwareSnapshot GetSnapshot()
    {
        _snapshotGate.Wait();
        try
        {
            return _snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private ServiceStatus GetStatus()
    {
        bool writesAvailable = !_rollbackBlocked
            && _snapshot.Capabilities.Any(capability => capability.State == CapabilityAccessState.Verified);
        return new ServiceStatus(
            Version,
            _startedAt,
            _engine!.Revision,
            _engine.ActiveProfileId,
            writesAvailable,
            EmergencyMode: _rollbackBlocked,
            _rollbackBlocked
                ? "Hardware writes are blocked because rollback recovery is incomplete."
                : writesAvailable
                    ? "Service is healthy; only Verified controls can be written."
                    : "Service is healthy in read-only mode.");
    }

    private IpcResponse ValidateProfile(IpcRequest request)
    {
        ApplyProfileRequest payload = IpcJson.FromElement<ApplyProfileRequest>(request.Payload)
            ?? throw new InvalidDataException("ValidateProfile requires an ApplyProfileRequest payload.");
        IReadOnlyDictionary<string, CapabilityDescriptor> capabilities = GetSnapshot().Capabilities.ToDictionary(
            capability => capability.Id,
            StringComparer.Ordinal);
        ProfileValidationResult validation = ProfileValidator.Validate(payload.Profile, capabilities, payload.ConfirmExperimental);
        return Success(request, validation);
    }

    private async Task<IpcResponse> ApplyProfileAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "New profile writes are blocked until pending rollback recovery succeeds.");
        }

        ApplyProfileRequest payload = IpcJson.FromElement<ApplyProfileRequest>(request.Payload)
            ?? throw new InvalidDataException("ApplyProfile requires an ApplyProfileRequest payload.");
        IReadOnlyDictionary<string, CapabilityDescriptor> capabilities = GetSnapshot().Capabilities.ToDictionary(
            capability => capability.Id,
            StringComparer.Ordinal);
        (ProfileTransaction transaction, ProfileValidationResult validation) = await _engine!.ApplyAsync(
            payload.Profile,
            capabilities,
            request.ExpectedStateRevision,
            payload.ConfirmExperimental,
            cancellationToken).ConfigureAwait(false);
        if (!validation.Valid || transaction.State != ProfileTransactionState.Committed)
        {
            return Failure(request, "PROFILE_REJECTED", transaction.Error ?? string.Join(" ", validation.Errors));
        }

        await _store!.SaveProfileAsync(payload.Profile, cancellationToken).ConfigureAwait(false);
        return Success(request, new ApplyProfileResult(transaction, _engine.ActiveProfileId));
    }

    private IpcResponse ExportReport(IpcRequest request)
    {
        CompatibilityReportV1 report = CompatibilityReportBuilder.Build(
            GetSnapshot(),
            Version,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["framework"] = Environment.Version.ToString(),
                ["osVersion"] = Environment.OSVersion.VersionString,
                ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            },
            [],
            userApproved: false);
        return Success(request, report);
    }

    private async Task<IpcResponse> ResetHardwareAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        string? capabilityId = IpcJson.FromElement<string>(request.Payload);
        CapabilityDescriptor capability = GetSnapshot().Capabilities.FirstOrDefault(item => item.Id == capabilityId)
            ?? throw new InvalidOperationException("The requested capability was not discovered.");
        if (!capability.CanResetToDefault || capability.State != CapabilityAccessState.Verified)
        {
            return Failure(request, "RESET_NOT_AVAILABLE", "Only Verified capabilities with explicit reset semantics can be reset.");
        }

        IHardwareAdapter adapter = _coordinator!.Adapters.First(item => item.Manifest.Id == capability.AdapterId);
        await adapter.ResetToDefaultAsync(capability.Id, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(persistSensors: false, cancellationToken).ConfigureAwait(false);
        return Success(request, capability.Id);
    }

    private async Task RecoverPendingTransactionAsync(CancellationToken cancellationToken)
    {
        ProfileTransaction? pending = await _store!.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return;
        }

        ServiceLog.RecoveringTransaction(logger, pending.Id);
        List<string> errors = [];
        foreach (PreparedAction prepared in pending.PreparedActions.Reverse())
        {
            IHardwareAdapter? adapter = _coordinator!.Adapters.FirstOrDefault(item => item.Manifest.Id == prepared.Action.AdapterId);
            if (adapter is null)
            {
                errors.Add($"Adapter {prepared.Action.AdapterId} is unavailable.");
                continue;
            }

            try
            {
                await adapter.RollbackAsync(prepared, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add($"{prepared.Action.Id}: {exception.Message}");
            }
        }

        ProfileTransaction recovered = pending with
        {
            State = errors.Count == 0 ? ProfileTransactionState.RolledBack : ProfileTransactionState.RollingBack,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = errors.Count == 0 ? "Recovered after an unclean stop." : string.Join("; ", errors)
        };
        await _store.SaveAsync(recovered, cancellationToken).ConfigureAwait(false);
        _rollbackBlocked = errors.Count > 0;
        if (!_rollbackBlocked)
        {
            await _store.ClearPendingAsync(pending.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private IpcResponse Success<T>(IpcRequest request, T payload) => new(
        ProtocolConstants.Version,
        request.RequestId,
        true,
        _engine?.Revision ?? 0,
        null,
        null,
        IpcJson.ToElement(payload));

    private static bool IsMutatingCommand(IpcCommand command) => command is
        IpcCommand.ApplyProfile or
        IpcCommand.ResetHardware or
        IpcCommand.StartCalibration or
        IpcCommand.StartTune or
        IpcCommand.AbortOperation;

    private IpcResponse Failure(IpcRequest request, string code, string error) => new(
        ProtocolConstants.Version,
        request.RequestId,
        false,
        _engine?.Revision ?? 0,
        code,
        error,
        null);

    private async Task<SqliteStateStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable("PCHELPER_DATA_DIR");
        string dataDirectory = configured ?? DataPaths.GetDefaultDataDirectory();
        SqliteStateStore store;
        try
        {
            store = new SqliteStateStore(Path.Combine(dataDirectory, "state.db"));
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) when (configured is null && Environment.UserInteractive)
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCHelper",
                "Development");
            store = new SqliteStateStore(Path.Combine(fallback, "state.db"));
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            ServiceLog.UsingFallbackDirectory(logger, fallback);
        }

        return store;
    }

    private void EnsureInitialised()
    {
        if (_store is null || _coordinator is null || _engine is null)
        {
            throw new InvalidOperationException("PC Helper runtime has not been initialised.");
        }
    }

    private static HardwareSnapshot EmptySnapshot() => new(
        DateTimeOffset.MinValue,
        [],
        [],
        [],
        [],
        [],
        []);
}
