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
    private readonly SemaphoreSlim _coolingGraphGate = new(1, 1);
    private readonly SemaphoreSlim _hardwareMutationGate = new(1, 1);
    private readonly object _operationSync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, IpcResponse> _idempotentResponses = new(StringComparer.Ordinal);
    private readonly OwnershipLeaseManager _ownershipLeases = new();
    private readonly HealthRuleEngine _healthRuleEngine = new();
    private readonly ReleaseTrustPolicy _releaseTrust = ReleaseTrustPolicy.FromAssembly(typeof(PCHelperRuntime).Assembly);
    private readonly string _serviceInstanceId = Guid.NewGuid().ToString("N");
    private readonly WindowsSystemHealthSignalProbe _healthSignalProbe = new();
    private SqliteStateStore? _store;
    private AdapterCoordinator? _coordinator;
    private ProfileTransactionEngine? _engine;
    private AdapterPackManager? _adapterPackManager;
    private NvmlGpuFanCoolerTransport? _gpuFanTransport;
    private IHardwareAdapter? _gpuFanAdapter;
    private volatile bool _gpuFanArmed;
    private string _gpuFanDeviceId = "nvidia:gpu-0";
    private NvmlGpuPowerLimitTransport? _gpuPowerTransport;
    private IHardwareAdapter? _gpuPowerAdapter;
    private volatile bool _gpuPowerArmed;
    private NvapiGpuClockOffsetTransport? _gpuClockTransport;
    private IHardwareAdapter? _gpuClockCoreAdapter;
    private IHardwareAdapter? _gpuClockMemoryAdapter;
    private volatile bool _gpuClockArmed;
    private CpuTuneBootSentinel? _cpuTuneSentinel;
    private string _cpuTuneRecoveryMessage = "CPU tune journal has not been inspected.";
    private WindowsTakeoverExecutionGate? _takeoverGate;
    private WindowsDriverUpdateExecutor? _updateExecutor;
    private UpdateTransactionCoordinator? _updateCoordinator;
    private string? _dataDirectory;
    private long _suiteRevision;
    private HardwareSnapshot _snapshot = EmptySnapshot();
    private bool _rollbackBlocked;
    private int _disposeState;
    private HardwareOperationStatus? _operationStatus;
    private CancellationTokenSource? _operationCancellation;
    private Task? _activeOperationTask;
    private ActiveCoolingGraphRuntime? _activeCoolingGraph;
    private CoolingRuntimeStatusV1 _coolingRuntimeStatus = CoolingRuntimeStatusV1.Inactive(
        "No service-owned cooling graph is active; firmware/default control is in effect.");
    private SafetyRecoveryStateV1 _safetyRecoveryState = new(
        SafetyRecoveryStateV1.CurrentSchemaVersion,
        SafetyRecoveryStateV1.DefaultId,
        SafeModeEnabled: false,
        AutomationSuspended: false,
        DateTimeOffset.MinValue,
        "Safe mode has not been configured.");
    private DateTimeOffset _lastHealthSignalScan = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _store = await CreateStoreAsync(cancellationToken).ConfigureAwait(false);
        _takeoverGate = new WindowsTakeoverExecutionGate(typeof(PCHelperRuntime).Assembly.Location);
        _updateExecutor = new WindowsDriverUpdateExecutor(
            Path.Combine(_dataDirectory!, "Updates"),
            typeof(PCHelperRuntime).Assembly.Location);
        _updateCoordinator = new UpdateTransactionCoordinator(
            _updateExecutor,
            new SuiteUpdateTransactionJournal(_store));
        _safetyRecoveryState = await _store.GetSuiteEntityAsync<SafetyRecoveryStateV1>(
            SuiteEntityKind.SafetyRecoveryState,
            SafetyRecoveryStateV1.DefaultId,
            cancellationToken).ConfigureAwait(false)
            ?? _safetyRecoveryState with { UpdatedAt = DateTimeOffset.UtcNow };
        foreach (OwnershipLeaseV1 lease in await _store.GetSuiteEntitiesAsync<OwnershipLeaseV1>(SuiteEntityKind.OwnershipLease, cancellationToken).ConfigureAwait(false))
        {
            _ownershipLeases.Restore(lease, DateTimeOffset.UtcNow);
        }
        foreach (ProfileV1 profile in BuiltInProfiles.Create())
        {
            await _store.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        List<IHardwareAdapter> adapters =
        [
            new TraceableHardwareAdapter(new SystemInventoryAdapter()),
            new TraceableHardwareAdapter(new WindowsPowerAdapter()),
            new TraceableHardwareAdapter(new NvmlTelemetryAdapter()),
            new TraceableHardwareAdapter(new IntelGraphicsControlAdapter()),
            new TraceableHardwareAdapter(new AmdGraphicsControlAdapter()),
            new TraceableHardwareAdapter(new VendorControlEligibilityAdapter()),
            new TraceableHardwareAdapter(new WindowsPeripheralInventoryAdapter()),
            new TraceableHardwareAdapter(new AdapterHostProxy()),
            // Operator-declared file-backed sensor inputs (read-only; exposes no
            // capability). Definitions live in file-sensors.json beside the service
            // state and are re-read when the file changes.
            new TraceableHardwareAdapter(new FileSensorAdapter(Path.Combine(_dataDirectory!, "file-sensors.json")))
        ];

        // Experimental GPU-fan control. The transport is created write-capable (fan
        // setters are marshalled) but starts DISARMED: the capability registers read-only
        // and no write can occur until an operator explicitly arms it with an Experimental
        // acknowledgement for the exact device (SetGpuFanControlArmed). Deploying this code
        // therefore never arms a live fan write even though the service runs elevated. The
        // adapter is registered only when NVML reports a usable fan transport with valid
        // bounds. An environment opt-in remains as a developer override inside the transport.
        _gpuFanArmed = false;
        if (NvmlGpuFanCoolerTransport.TryCreate(enableWrites: true, out NvmlGpuFanCoolerTransport fanTransport, out _))
        {
            GpuFanBounds? bounds = fanTransport.CanWrite
                ? await fanTransport.ReadBoundsAsync("0", cancellationToken).ConfigureAwait(false)
                : null;
            if (bounds is { IsValid: true })
            {
                _gpuFanTransport = fanTransport;
                _gpuFanDeviceId = "nvidia:gpu-0";
                TraceableHardwareAdapter gpuFanAdapter = new(
                    new NvidiaGpuFanAdapter(fanTransport, _gpuFanDeviceId, "0", () => _gpuFanArmed));
                _gpuFanAdapter = gpuFanAdapter;
                adapters.Add(gpuFanAdapter);
            }
            else
            {
                fanTransport.Dispose();
            }
        }

        // Experimental GPU power-limit control: identical disarmed-by-default pattern.
        // The capability registers ReadOnly and no write can occur until an operator
        // explicitly arms it with an Experimental acknowledgement for the exact device
        // (SetGpuPowerLimitArmed). Registered only when NVML reports valid constraints.
        _gpuPowerArmed = false;
        if (NvmlGpuPowerLimitTransport.TryCreate(enableWrites: true, out NvmlGpuPowerLimitTransport powerTransport, out _))
        {
            GpuPowerLimitBounds? powerBounds = powerTransport.CanWrite
                ? await powerTransport.ReadBoundsAsync("0", cancellationToken).ConfigureAwait(false)
                : null;
            if (powerBounds is { IsValid: true })
            {
                _gpuPowerTransport = powerTransport;
                TraceableHardwareAdapter gpuPowerAdapter = new(
                    new NvidiaGpuPowerLimitAdapter(powerTransport, _gpuFanDeviceId, "0", () => _gpuPowerArmed));
                _gpuPowerAdapter = gpuPowerAdapter;
                adapters.Add(gpuPowerAdapter);
            }
            else
            {
                powerTransport.Dispose();
            }
        }

        // Experimental GPU clock offsets (core + memory): identical disarmed-by-default
        // pattern over NVAPI performance states 2.0. Both capabilities register ReadOnly
        // and share one arm keystone (SetGpuClockOffsetArmed); no voltage parameter is
        // ever constructed or submitted. Registered only when the driver reports an
        // editable delta range for at least the core domain.
        _gpuClockArmed = false;
        if (NvapiGpuClockOffsetTransport.TryCreate(0, enableWrites: true, out NvapiGpuClockOffsetTransport clockTransport, out _))
        {
            GpuClockOffsetBounds? coreBounds = clockTransport.CanWrite
                ? await clockTransport.ReadBoundsAsync(GpuClockOffsetDomain.Core, cancellationToken).ConfigureAwait(false)
                : null;
            if (coreBounds is { IsValid: true })
            {
                _gpuClockTransport = clockTransport;
                TraceableHardwareAdapter coreAdapter = new(
                    new NvidiaGpuClockOffsetAdapter(clockTransport, GpuClockOffsetDomain.Core, _gpuFanDeviceId, "0", () => _gpuClockArmed));
                _gpuClockCoreAdapter = coreAdapter;
                adapters.Add(coreAdapter);

                GpuClockOffsetBounds? memoryBounds = await clockTransport
                    .ReadBoundsAsync(GpuClockOffsetDomain.Memory, cancellationToken)
                    .ConfigureAwait(false);
                if (memoryBounds is { IsValid: true })
                {
                    TraceableHardwareAdapter memoryAdapter = new(
                        new NvidiaGpuClockOffsetAdapter(clockTransport, GpuClockOffsetDomain.Memory, _gpuFanDeviceId, "0", () => _gpuClockArmed));
                    _gpuClockMemoryAdapter = memoryAdapter;
                    adapters.Add(memoryAdapter);
                }
            }
            else
            {
                clockTransport.Dispose();
            }
        }

        // CPU tuning boot-recovery sentinel (docs/qualification/cpu-tuning-and-intel-arc.md
        // step 3). No SMU tuning transport exists in production, so recovery can only
        // observe: a surviving journal entry is reported and retained as evidence.
        _cpuTuneSentinel = new CpuTuneBootSentinel(Path.Combine(_dataDirectory!, "cpu-tune-journal.json"));
        _cpuTuneRecoveryMessage = await _cpuTuneSentinel.RecoverAsync(transport: null, cancellationToken).ConfigureAwait(false);

        _coordinator = new AdapterCoordinator(adapters);
        _engine = new ProfileTransactionEngine(
            adapters,
            _store,
            mutationLock: _hardwareMutationGate,
            suiteStore: _store,
            serviceInstanceId: _serviceInstanceId);
        _adapterPackManager = CreateAdapterPackManager();
        await RecoverPendingTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(persistSensors: true, cancellationToken).ConfigureAwait(false);
        foreach (ProfileV2 profile in CapabilityProfileFactory.Create(GetSnapshot()))
        {
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.ProfileV2, profile.Id, profile, cancellationToken).ConfigureAwait(false);
        }
        await RecoverPendingOperationAsync(cancellationToken).ConfigureAwait(false);
        await RecoverPendingUpdateTransactionsAsync(cancellationToken).ConfigureAwait(false);
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
        if (!_releaseTrust.WritesAllowed)
        {
            snapshot = snapshot with
            {
                Warnings = snapshot.Warnings
                    .Append(new DiagnosticWarning(
                        "PUBLIC_PREVIEW_READ_ONLY",
                        "Information",
                        ReleaseTrustPolicy.WriteLockReason,
                        "Install a signed RigPilot beta after verifying its signature and publisher."))
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
        await EvaluateHealthRulesAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await TickCoolingGraphAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the given capabilities in the cached snapshot in place, matched by
    /// capability id, leaving every other capability untouched. Used to apply a targeted
    /// single-adapter re-probe synchronously (for example immediately after arming GPU fan
    /// control) so a following request observes the new state without a full capture.
    /// </summary>
    private async Task PatchSnapshotCapabilitiesAsync(
        IReadOnlyList<CapabilityDescriptor> refreshed,
        CancellationToken cancellationToken)
    {
        if (refreshed.Count == 0)
        {
            return;
        }

        HashSet<string> refreshedIds = refreshed.Select(capability => capability.Id).ToHashSet(StringComparer.Ordinal);
        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CapabilityDescriptor[] merged = _snapshot.Capabilities
                .Where(capability => !refreshedIds.Contains(capability.Id))
                .Concat(refreshed)
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .ToArray();
            _snapshot = _snapshot with { Capabilities = merged };
        }
        finally
        {
            _snapshotGate.Release();
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
                "Hardware and profile changes require membership of the installed RigPilot operator group or an elevated session. Group membership added by the installer takes effect after you sign out and back in.");
        }

        if (_releaseTrust.GetMutationRejection(request.Command) is string releaseRejection)
        {
            return Failure(request, "PUBLIC_PREVIEW_READ_ONLY", releaseRejection);
        }

        if (IsMutatingCommand(request.Command) && _rollbackBlocked)
        {
            return Failure(
                request,
                "RECOVERY_REQUIRED",
                "Hardware writes are locked because the service could not prove a default state during recovery. Restart after correcting the adapter or driver fault; read-only IPC remains available.");
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
                IpcCommand.Handshake => ServiceHandshake.Create(request, Version, CurrentRevision),
                IpcCommand.GetInventory => Success(request, GetSnapshot()),
                IpcCommand.SubscribeSensors => Success(request, GetSnapshot().Sensors),
                IpcCommand.GetProfiles => Success(request, await _store!.GetProfilesAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.GetCapabilitiesV2 => Success(request, GetCapabilitiesV2()),
                IpcCommand.GetProfilesV2 => Success(
                    request,
                    await _store!.GetSuiteEntitiesAsync<ProfileV2>(SuiteEntityKind.ProfileV2, cancellationToken).ConfigureAwait(false)),
                IpcCommand.PreviewProfileV2 => await PreviewProfileV2Async(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveProfileV2 => await SaveProfileV2Async(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetCoolingGraphs => Success(
                    request,
                    await _store!.GetSuiteEntitiesAsync<CoolingGraphV1>(SuiteEntityKind.CoolingGraph, cancellationToken).ConfigureAwait(false)),
                IpcCommand.SaveCoolingGraph => await SaveCoolingGraphAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetCoolingOutputAssignments => Success(
                    request,
                    await _store!.GetSuiteEntitiesAsync<CoolingOutputAssignmentV1>(SuiteEntityKind.CoolingOutputAssignment, cancellationToken).ConfigureAwait(false)),
                IpcCommand.SaveCoolingOutputAssignment => await SaveCoolingOutputAssignmentAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetFanCommissioningSessions => Success(
                    request,
                    await _store!.GetSuiteEntitiesAsync<FanCommissioningSessionV1>(SuiteEntityKind.FanCommissioningSession, cancellationToken).ConfigureAwait(false)),
                IpcCommand.GetFanCalibrations => Success(
                    request,
                    await _store!.GetSuiteEntitiesAsync<FanCalibrationV2>(SuiteEntityKind.FanCalibration, cancellationToken).ConfigureAwait(false)),
                IpcCommand.BeginFanCommissioning => await BeginFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.PreflightFanCommissioning => await PreflightFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.PulseFanCommissioning => await PulseFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ObserveFanCommissioning => await ObserveFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ConfirmFanCommissioning => await ConfirmFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.CompleteFanCommissioning => await CompleteFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.CancelFanCommissioning => await CancelFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.RecoverFanCommissioning => await RecoverFanCommissioningAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.PreviewAfterburnerImport => PreviewAfterburnerImport(request),
                IpcCommand.PreviewFanControlImport => PreviewFanControlImport(request),
                IpcCommand.GetAdapterPacks => Success(
                    request,
                    await _store!.GetSuiteEntitiesAsync<AdapterPackInspection>(SuiteEntityKind.AdapterPackInspection, cancellationToken).ConfigureAwait(false)),
                IpcCommand.InspectAdapterPack => await InspectAdapterPackAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.InstallAdapterPack => await InstallAdapterPackAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.RemoveAdapterPack => await RemoveAdapterPackAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.PreviewTakeover => await PreviewTakeoverAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GrantOwnershipConsent => await SaveOwnershipConsentAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ExecuteTakeover => await ExecuteTakeoverAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ReleaseOwnership => await ReleaseOwnershipAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetOwnership => await GetOwnershipAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetAutomationRules => Success(
                    request,
                    await _store!.GetAutomationRulesAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.SaveAutomationRule => await SaveAutomationRuleAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.DeleteAutomationRule => await DeleteAutomationRuleAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ValidateProfile => ValidateProfile(request),
                IpcCommand.ApplyProfile => await ApplyProfileAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ApplyProfileV2 => await ApplyProfileV2Async(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ExportReport => ExportReport(request),
                IpcCommand.GetServiceStatus => Success(request, GetStatus()),
                IpcCommand.GetAdapterTrace => Success(request, await GetAdapterTraceAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.GetHealthRules => Success(request, await _store!.GetSuiteEntitiesAsync<HealthRuleV1>(SuiteEntityKind.HealthRule, cancellationToken).ConfigureAwait(false)),
                IpcCommand.SaveHealthRule => await SaveHealthRuleAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.DeleteHealthRule => await DeleteHealthRuleAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetHealthAlerts => Success(request, await GetHealthAlertsAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.AcknowledgeHealthAlert => await AcknowledgeHealthAlertAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetSafetyRecoveryStatus => Success(request, await GetSafetyRecoveryStatusAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.SetSafeMode => await SetSafeModeAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetHardwareEvidence => Success(request, await BuildHardwareEvidenceAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.GetCoolingQualificationReports => Success(request, await GetCoolingQualificationReportsAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.GetDeviceQualificationPlans => Success(request, DeviceQualificationPlanner.Build(GetSnapshot())),
                IpcCommand.ResetHardware => await ResetHardwareAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StartCalibration => await StartCalibrationAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StartTune => await StartTuneAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StartAutoOc => await StartAutoOcAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StartAutoOcV3 => await StartAutoOcV3Async(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.AbortOperation => AbortOperation(request),
                IpcCommand.GetOperationStatus => Success(request, GetOperationStatus()),
                IpcCommand.GetOperationById => await GetOperationByIdAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ValidateUpdate => await ValidateUpdateAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ApplyUpdate => await ApplyUpdateAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetUpdateStatus => await GetUpdateStatusAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.DiscoverControllers => await DiscoverControllersAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.DiscoverHidInventory => await DiscoverHidInventoryAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ReadKrakenTelemetry => await ReadKrakenTelemetryAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetStorageHealth => GetStorageHealth(request),
                IpcCommand.SetKrakenLighting => await SetKrakenLightingAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetKrakenPumpDuty => await SetKrakenPumpDutyAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetAuraLighting => await SetAuraLightingAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetDimmRgb => await SetDimmRgbAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetRazerRgb => await SetRazerRgbAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StopConflictingProcesses => StopConflictingProcesses(request),
                IpcCommand.ReadRyzenSmuFeasibility => await ReadRyzenSmuFeasibilityAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetGpuFanControlArmed => await SetGpuFanControlArmedSerializedAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetGpuPowerLimitArmed => await SetGpuPowerLimitArmedSerializedAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetGpuClockOffsetArmed => await SetGpuClockOffsetArmedSerializedAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetHardwareControlArmed => await SetHardwareControlArmedAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SetCpuTuningArmed => SetCpuTuningArmed(request),
                _ when IsUserAgentCommand(request.Command) => Failure(
                    request,
                    "WRONG_EXECUTION_CONTEXT",
                    $"Command {request.Command} belongs to the signed-in user agent and cannot execute as LocalSystem."),
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
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        CancellationTokenSource? operationCancellation;
        Task? operationTask;
        lock (_operationSync)
        {
            operationCancellation = _operationCancellation;
            operationTask = _activeOperationTask;
        }

        operationCancellation?.Cancel();
        if (operationTask is not null)
        {
            try
            {
                await operationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await _hardwareMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _coolingGraphGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    ActiveCoolingGraphRuntime? active = _activeCoolingGraph;
                    _activeCoolingGraph = null;
                    if (active is not null && _coordinator is not null)
                    {
                        await ResetCoolingGraphOutputsAsync(active.Graph, GetSnapshot(), CancellationToken.None).ConfigureAwait(false);
                    }
                    PublishInactiveCoolingStatus("Service shutdown restored firmware/default cooling control.");
                }
                finally
                {
                    _coolingGraphGate.Release();
                }
            }
            finally
            {
                _hardwareMutationGate.Release();
            }
        }
        catch (Exception exception)
        {
            ServiceLog.CoolingGraphDeactivated(logger, exception);
        }

        try
        {
            await CompleteCleanShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _rollbackBlocked = true;
            ServiceLog.ShutdownRecoveryFailed(logger, exception);
        }

        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
        }

        if (_gpuFanTransport is not null)
        {
            _gpuFanTransport.Dispose();
        }

        if (_gpuPowerTransport is not null)
        {
            _gpuPowerTransport.Dispose();
        }

        if (_gpuClockTransport is not null)
        {
            _gpuClockTransport.Dispose();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }

        _engine?.Dispose();

        operationCancellation?.Dispose();
        _shutdown.Dispose();
        _snapshotGate.Dispose();
        _coolingGraphGate.Dispose();
        _hardwareMutationGate.Dispose();
    }

    private static string Version => RuntimeVersion.Get(Assembly.GetExecutingAssembly());

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
        bool writesAvailable = !_rollbackBlocked && _releaseTrust.WritesAllowed;
        CoolingRuntimeStatusV1 cooling = Volatile.Read(ref _coolingRuntimeStatus);
        bool coolingEmergency = cooling.State is CoolingRuntimeState.EmergencyMaximum or CoolingRuntimeState.RecoveryRequired;
        return new ServiceStatus(
            Version,
            _startedAt,
            CurrentRevision,
            _engine!.ActiveProfileId,
            writesAvailable,
            EmergencyMode: _rollbackBlocked || coolingEmergency,
            _rollbackBlocked
                ? "RecoveryRequired: hardware writes are locked because default-state read-back did not complete."
                : !_releaseTrust.WritesAllowed
                    ? ReleaseTrustPolicy.WriteLockReason
                    : coolingEmergency
                        ? cooling.Reason
                        : "Service is healthy; Verified controls and explicitly confirmed Experimental controls can be written.",
            RecoveryRequired: _rollbackBlocked,
            HardwareControlArmed: IsHardwareControlFullyArmed(),
            Cooling: cooling,
            ReleaseWritesLocked: !_releaseTrust.WritesAllowed,
            WriteLockReason: _releaseTrust.WritesAllowed ? null : ReleaseTrustPolicy.WriteLockReason);
    }

    private bool IsHardwareControlFullyArmed()
    {
        bool anyAvailable = _gpuFanTransport is not null
            || _gpuPowerTransport is not null
            || _gpuClockTransport is not null;
        return anyAvailable
            && (_gpuFanTransport is null || _gpuFanArmed)
            && (_gpuPowerTransport is null || _gpuPowerArmed)
            && (_gpuClockTransport is null || _gpuClockArmed);
    }

    private long CurrentRevision => checked((_engine?.Revision ?? 0) + Interlocked.Read(ref _suiteRevision));

    private CapabilityDescriptorV2[] GetCapabilitiesV2() => GetSnapshot().Capabilities
        .Select(ToV2)
        .ToArray();

    internal static CapabilityDescriptorV2 ToV2(CapabilityDescriptor capability)
    {
        bool voltage = capability.Name.Contains("voltage", StringComparison.OrdinalIgnoreCase)
            || capability.Id.Contains("voltage", StringComparison.OrdinalIgnoreCase);
        HazardClass hazard = voltage
            ? HazardClass.Voltage
            : capability.Domain == ControlDomain.Cooling
                ? HazardClass.Cooling
                : capability.Domain is ControlDomain.Cpu or ControlDomain.Gpu
                    ? HazardClass.Performance
                    : HazardClass.None;
        BootApplyPolicy boot = voltage
            ? BootApplyPolicy.ManualOnly
            : capability.State == CapabilityAccessState.Verified && capability.Risk is RiskLevel.Safe or RiskLevel.Guarded
                ? BootApplyPolicy.Allowed
                : BootApplyPolicy.Never;
        OwnershipState ownership = string.IsNullOrWhiteSpace(capability.ConflictOwner)
            ? OwnershipState.Available
            : OwnershipState.OwnedByAnotherApplication;
        bool supportsReadBack = capability.Evidence >= EvidenceLevel.ReadBackVerified
            || capability.AdapterId is LibreHardwareMonitorAdapter.AdapterId
                or NvidiaGpuFanAdapter.AdapterId
                or NvidiaGpuPowerLimitAdapter.AdapterId
                or NvidiaGpuClockOffsetAdapter.CoreAdapterId
                or NvidiaGpuClockOffsetAdapter.MemoryAdapterId;
        return new CapabilityDescriptorV2(
            CapabilityDescriptorV2.CurrentSchemaVersion,
            capability,
            hazard,
            capability.Range is null ? "Adapter-declared discrete values" : "Adapter-reported numeric bounds",
            supportsReadBack,
            capability.CanResetToDefault
                ? capability.Evidence >= EvidenceLevel.SingleSystem
                    ? ResetGuarantee.FirmwareDefaultVerified
                    : ResetGuarantee.ReadBackVerified
                : ResetGuarantee.None,
            ownership,
            boot,
            null,
            [],
            null,
            null,
            null);
    }

    private IpcResponse ValidateProfile(IpcRequest request)
    {
        ApplyProfileRequest payload = IpcJson.FromElement<ApplyProfileRequest>(request.Payload)
            ?? throw new InvalidDataException("ValidateProfile requires an ApplyProfileRequest payload.");
        IReadOnlyDictionary<string, CapabilityDescriptor> capabilities = GetSnapshot().Capabilities.ToDictionary(
            capability => capability.Id,
            StringComparer.Ordinal);
        bool confirmed = payload.ConfirmExperimental && payload.ConfirmDevices;
        ProfileValidationResult validation = ProfileValidator.Validate(payload.Profile, capabilities, confirmed);
        return Success(request, validation);
    }

    private async Task<IpcResponse> SaveProfileV2Async(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        ProfileV2 profile = IpcJson.FromElement<ProfileV2>(request.Payload)
            ?? throw new InvalidDataException("SaveProfileV2 requires a ProfileV2 payload.");
        IReadOnlyDictionary<string, CapabilityDescriptorV2> capabilities = GetCapabilitiesV2()
            .ToDictionary(item => item.Capability.Id, StringComparer.Ordinal);
        ProfileValidationResult validation = ProfileV2Validator.Validate(
            profile,
            capabilities,
            ProfileActivationSource.Manual,
            confirmManualVoltage: true);
        if (!validation.Valid)
        {
            return Failure(request, "PROFILE_REJECTED", string.Join(" ", validation.Errors));
        }
        string? protectionError = await ValidateProtectedCoolingActionsAsync(profile.HardwareActions, cancellationToken).ConfigureAwait(false);
        if (protectionError is not null)
        {
            return Failure(request, "PROTECTED_COOLING_OUTPUT", protectionError);
        }
        if (profile.CoolingGraphId is string coolingGraphId)
        {
            CoolingGraphV1? graph = await _store!.GetSuiteEntityAsync<CoolingGraphV1>(
                SuiteEntityKind.CoolingGraph,
                coolingGraphId,
                cancellationToken).ConfigureAwait(false);
            if (graph is null)
            {
                return Failure(request, "COOLING_GRAPH_MISSING", $"Cooling graph '{coolingGraphId}' is unavailable.");
            }
            string? graphRoleError = await ValidateCoolingGraphOutputRolesAsync(graph, cancellationToken).ConfigureAwait(false);
            if (graphRoleError is not null)
            {
                return Failure(request, "PROTECTED_COOLING_OUTPUT", graphRoleError);
            }
        }
        await _store!.SaveSuiteEntityAsync(SuiteEntityKind.ProfileV2, profile.Id, profile, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, profile);
    }

    private async Task<IpcResponse> SaveCoolingGraphAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        CoolingGraphV1 graph = IpcJson.FromElement<CoolingGraphV1>(request.Payload)
            ?? throw new InvalidDataException("SaveCoolingGraph requires a CoolingGraphV1 payload.");
        IReadOnlyList<string> errors = CoolingGraphValidator.Validate(graph);
        if (errors.Count > 0)
        {
            return Failure(request, "INVALID_COOLING_GRAPH", string.Join(" ", errors));
        }
        HashSet<string> coolingCapabilities = GetSnapshot().Capabilities
            .Where(capability => capability.Domain == ControlDomain.Cooling)
            .Select(capability => capability.Id)
            .ToHashSet(StringComparer.Ordinal);
        string[] unknown = graph.Outputs
            .Select(output => output.CapabilityId)
            .Where(capabilityId => !coolingCapabilities.Contains(capabilityId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            return Failure(request, "UNKNOWN_COOLING_OUTPUT", $"Cooling outputs are unavailable: {string.Join(", ", unknown)}.");
        }
        string? roleError = await ValidateCoolingGraphOutputRolesAsync(graph, cancellationToken).ConfigureAwait(false);
        if (roleError is not null)
        {
            return Failure(request, "PROTECTED_COOLING_OUTPUT", roleError);
        }
        await _store!.SaveSuiteEntityAsync(SuiteEntityKind.CoolingGraph, graph.Id, graph, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, graph);
    }

    private async Task<IpcResponse> SaveCoolingOutputAssignmentAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        CoolingOutputAssignmentUpdateRequest payload = IpcJson.FromElement<CoolingOutputAssignmentUpdateRequest>(request.Payload)
            ?? throw new InvalidDataException("SaveCoolingOutputAssignment requires a typed output-role assignment.");
        CoolingOutputAssignmentV1 submitted = payload.Assignment
            ?? throw new InvalidDataException("Cooling-output assignment cannot be empty.");
        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, submitted.CapabilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected cooling control was not discovered.");
        if (capability.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety)
            || capability.ValueKind != ControlValueKind.Numeric)
        {
            return Failure(request, "INVALID_COOLING_OUTPUT", "Only detected numeric cooling outputs can receive a physical-output role.");
        }
        if (!Enum.IsDefined(submitted.Role))
        {
            return Failure(request, "INVALID_COOLING_ROLE", "The requested cooling-output role is invalid.");
        }

        CoolingOutputAssignmentV1? existing = await _store!.GetSuiteEntityAsync<CoolingOutputAssignmentV1>(
            SuiteEntityKind.CoolingOutputAssignment,
            capability.Id,
            cancellationToken).ConfigureAwait(false);
        if (CoolingOutputAssignmentPolicy.RequiresExplicitProtectionRemoval(existing, submitted.Role)
            && !payload.ConfirmRemoveSafetyProtection)
        {
            return Failure(
                request,
                "SAFETY_PROTECTION_CONFIRMATION_REQUIRED",
                "This output is recorded as a CPU fan or pump. Explicitly acknowledge removal of that safety protection before changing it to a case fan or unknown.");
        }

        string? rpmSensorId = string.IsNullOrWhiteSpace(submitted.RpmSensorId) ? null : submitted.RpmSensorId.Trim();
        if (rpmSensorId is not null)
        {
            SensorSample? rpm = snapshot.Sensors.FirstOrDefault(item => string.Equals(item.SensorId, rpmSensorId, StringComparison.Ordinal));
            if (rpm is null
                || !string.Equals(rpm.Unit, "RPM", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rpm.AdapterId, capability.AdapterId, StringComparison.Ordinal)
                || !string.Equals(rpm.DeviceId, capability.DeviceId, StringComparison.Ordinal))
            {
                return Failure(request, "RPM_SENSOR_MISMATCH", "A persisted output role may only reference an RPM sensor from the same exact controller.");
            }
        }

        string headerName = string.IsNullOrWhiteSpace(submitted.HeaderName)
            ? capability.Name
            : submitted.HeaderName.Trim();
        CoolingOutputAssignmentV1 canonical = new(
            CoolingOutputAssignmentV1.CurrentSchemaVersion,
            capability.Id,
            capability.Id,
            capability.AdapterId,
            capability.DeviceId,
            rpmSensorId,
            headerName,
            submitted.Role,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(submitted.Notes) ? null : submitted.Notes.Trim());
        IReadOnlyList<string> errors = CoolingOutputAssignmentPolicy.Validate(canonical);
        if (errors.Count > 0)
        {
            return Failure(request, "INVALID_COOLING_OUTPUT", string.Join(" ", errors));
        }

        if (canonical.Role == CoolingOutputRole.Unknown)
        {
            if (existing is not null)
            {
                await _store.DeleteSuiteEntityAsync(SuiteEntityKind.CoolingOutputAssignment, existing.Id, cancellationToken).ConfigureAwait(false);
                IncrementSuiteRevision();
            }
            return Success(request, new CoolingOutputAssignmentSaveResultV1(canonical, Removed: existing is not null));
        }

        await _store.SaveSuiteEntityAsync(SuiteEntityKind.CoolingOutputAssignment, canonical.Id, canonical, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, new CoolingOutputAssignmentSaveResultV1(canonical, Removed: false));
    }

    private async Task<bool> IsProtectedCoolingOutputAsync(
        CapabilityDescriptor capability,
        CancellationToken cancellationToken)
    {
        if (capability.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety))
        {
            return false;
        }

        CoolingOutputAssignmentV1? assignment = await GetCoolingOutputAssignmentAsync(capability, cancellationToken).ConfigureAwait(false);
        return CoolingOutputAssignmentPolicy.IsProtected(assignment, capability);
    }

    private Task<CoolingOutputAssignmentV1?> GetCoolingOutputAssignmentAsync(
        CapabilityDescriptor capability,
        CancellationToken cancellationToken) =>
        _store!.GetSuiteEntityAsync<CoolingOutputAssignmentV1>(
            SuiteEntityKind.CoolingOutputAssignment,
            capability.Id,
            cancellationToken);

    private async Task<string?> ValidateProtectedCoolingActionsAsync(
        IReadOnlyList<ProfileAction> actions,
        CancellationToken cancellationToken)
    {
        if (actions.Count == 0)
        {
            return null;
        }

        IReadOnlyList<CoolingOutputAssignmentV1> assignments = await _store!.GetSuiteEntitiesAsync<CoolingOutputAssignmentV1>(
            SuiteEntityKind.CoolingOutputAssignment,
            cancellationToken).ConfigureAwait(false);
        if (assignments.Count == 0)
        {
            return null;
        }

        Dictionary<string, CapabilityDescriptor> capabilities = GetSnapshot().Capabilities
            .ToDictionary(capability => capability.Id, StringComparer.Ordinal);
        foreach (ProfileAction action in actions)
        {
            if (!capabilities.TryGetValue(action.CapabilityId, out CapabilityDescriptor? capability)
                || !assignments.Any(assignment => CoolingOutputAssignmentPolicy.IsProtected(assignment, capability)))
            {
                continue;
            }

            CoolingOutputAssignmentV1? assignment = assignments.FirstOrDefault(candidate =>
                CoolingOutputAssignmentPolicy.IsProtected(candidate, capability));
            if (assignment is not null && CoolingOutputAssignmentPolicy.IsPump(assignment.Role))
            {
                return $"'{capability.Name}' is persistently classified as a pump and remains read-only until an exact pump-specific qualification path exists.";
            }

            if (action.Value.Kind != ControlValueKind.Numeric
                || action.Value.Numeric is not double value
                || !double.IsFinite(value)
                || value <= 0)
            {
                return $"'{capability.Name}' is persistently classified as a CPU fan or pump; profiles may only command a finite, positive numeric duty to it.";
            }
        }

        return null;
    }

    private async Task<string?> ValidateCoolingGraphOutputRolesAsync(
        CoolingGraphV1 graph,
        CancellationToken cancellationToken)
    {
        if (graph.Outputs.Count == 0)
        {
            return null;
        }

        IReadOnlyList<CoolingOutputAssignmentV1> assignments = await _store!.GetSuiteEntitiesAsync<CoolingOutputAssignmentV1>(
            SuiteEntityKind.CoolingOutputAssignment,
            cancellationToken).ConfigureAwait(false);
        if (assignments.Count == 0)
        {
            return null;
        }

        Dictionary<string, CapabilityDescriptor> capabilities = GetSnapshot().Capabilities
            .ToDictionary(capability => capability.Id, StringComparer.Ordinal);
        foreach (CoolingGraphOutputV1 output in graph.Outputs)
        {
            if (!capabilities.TryGetValue(output.CapabilityId, out CapabilityDescriptor? capability))
            {
                return $"Cooling output '{output.CapabilityId}' is unavailable.";
            }

            CoolingOutputAssignmentV1? assignment = assignments.FirstOrDefault(candidate =>
                CoolingOutputAssignmentPolicy.Targets(candidate, capability));
            if (assignment is null)
            {
                continue;
            }
            if (CoolingOutputAssignmentPolicy.IsPump(assignment.Role))
            {
                return $"Pump output '{assignment.HeaderName}' remains read-only until it has exact device-specific qualification.";
            }
            if (CoolingOutputAssignmentPolicy.IsSafetyCritical(assignment.Role) && output.Minimum <= 0)
            {
                return $"CPU-fan output '{assignment.HeaderName}' cannot have a zero-RPM cooling-graph floor.";
            }
        }

        return null;
    }

    private async Task<string?> ValidateCoolingGraphQualificationAsync(
        CoolingGraphV1 graph,
        CancellationToken cancellationToken)
    {
        if (graph.Outputs.Count == 0)
        {
            return null;
        }

        IReadOnlyList<FanCommissioningSessionV1> sessions = await _store!
            .GetSuiteEntitiesAsync<FanCommissioningSessionV1>(SuiteEntityKind.FanCommissioningSession, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<FanCalibrationV2> calibrations = await _store
            .GetSuiteEntitiesAsync<FanCalibrationV2>(SuiteEntityKind.FanCalibration, cancellationToken)
            .ConfigureAwait(false);
        HardwareSnapshot snapshot = GetSnapshot();
        foreach (CoolingGraphOutputV1 output in graph.Outputs)
        {
            CapabilityDescriptor? capability = snapshot.Capabilities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, output.CapabilityId, StringComparison.Ordinal));
            if (capability is null
                || capability.Range is not NumericRange range
                || !capability.CanResetToDefault
                || output.Minimum < range.Minimum - 1e-6
                || output.Maximum > range.Maximum + 1e-6)
            {
                return $"Cooling output '{output.CapabilityId}' no longer has compatible bounds and a firmware/default reset path.";
            }
            FanCommissioningSessionV1? session = sessions
                .Where(candidate => candidate.State == FanCommissioningState.Completed
                    && candidate.PhysicalHeaderObserved
                    && string.Equals(candidate.CapabilityId, output.CapabilityId, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .FirstOrDefault();
            if (session is null)
            {
                // Automatic-mode route (owner amendment 2026-07-18): an
                // uncalibrated output is acceptable only when its floor is at or
                // above the configured 20% floor (and controller minimum) AND its ceiling is
                // the full controller maximum (emergency headroom). Bounds/reset
                // checks above and pump/CPU-fan role blocks still apply, and the
                // graph runtime's stale-sensor maximum-cooling behaviour is
                // unchanged. Anything below the configured floor still requires a
                // physically observed commissioning session and calibration.
                if (AdaptiveCoolingProfileFactory.CanActivateWithoutCalibration(capability, output))
                {
                    continue;
                }

                return $"Cooling output '{output.CapabilityId}' has no physically observed commissioning session; without one, automatic mode requires a minimum duty of at least {AdaptiveCoolingProfileFactory.UncalibratedFloorDutyPercent:0}% (or the controller minimum, if higher) and the full controller maximum.";
            }

            FanCalibrationV2? calibration = calibrations
                .Where(candidate => string.Equals(candidate.CapabilityId, output.CapabilityId, StringComparison.Ordinal)
                    && string.Equals(candidate.CommissioningSessionId, session.Id, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.VerifiedAt)
                .FirstOrDefault();
            if (calibration is null)
            {
                return $"Cooling output '{output.CapabilityId}' has no calibration linked to its completed commissioning session.";
            }

            string? safetyError = FanCalibrationPolicy.ValidateOutput(output, calibration);
            if (safetyError is not null)
            {
                return safetyError;
            }
        }

        return null;
    }

    private IpcResponse PreviewAfterburnerImport(IpcRequest request)
    {
        AfterburnerImportRequest payload = IpcJson.FromElement<AfterburnerImportRequest>(request.Payload)
            ?? throw new InvalidDataException("PreviewAfterburnerImport requires a profile path and section.");
        return Success(request, MsiAfterburnerProfileImporter.Preview(payload.ProfilePath, payload.Section, GetCapabilitiesV2()));
    }

    private IpcResponse PreviewFanControlImport(IpcRequest request)
    {
        FanControlImportRequest payload = IpcJson.FromElement<FanControlImportRequest>(request.Payload)
            ?? throw new InvalidDataException("PreviewFanControlImport requires a configuration path and explicit mappings.");
        return Success(request, FanControlConfigurationImporter.Preview(
            payload.ConfigurationPath,
            payload.SensorMappings,
            payload.ControlMappings));
    }

    private async Task<IpcResponse> InspectAdapterPackAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        InspectAdapterPackRequest payload = IpcJson.FromElement<InspectAdapterPackRequest>(request.Payload)
            ?? throw new InvalidDataException("InspectAdapterPack requires a package path.");
        AdapterPackInspection inspection = await _adapterPackManager!.InspectAsync(payload.PackagePath, cancellationToken).ConfigureAwait(false);
        return Success(request, inspection);
    }

    private async Task<IpcResponse> InstallAdapterPackAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        InstallAdapterPackRequest payload = IpcJson.FromElement<InstallAdapterPackRequest>(request.Payload)
            ?? throw new InvalidDataException("InstallAdapterPack requires a package path.");
        AdapterPackInspection inspection = await _adapterPackManager!.InspectAsync(payload.PackagePath, cancellationToken).ConfigureAwait(false);
        if (!inspection.Valid || inspection.Manifest is null)
        {
            return Failure(request, "ADAPTER_PACK_REJECTED", string.Join(" ", inspection.Errors));
        }
        if (inspection.DevelopmentTrust && !payload.ConfirmDevelopmentTrust)
        {
            return Failure(request, "DEVELOPMENT_TRUST_NOT_CONFIRMED", "Development-hash trust requires an explicit confirmation.");
        }
        string installedPath = await _adapterPackManager.InstallAsync(payload.PackagePath, cancellationToken).ConfigureAwait(false);
        await _store!.SaveSuiteEntityAsync(
            SuiteEntityKind.AdapterPackInspection,
            $"{inspection.Manifest.Id}@{inspection.Manifest.Version}",
            inspection,
            cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, new { Inspection = inspection, InstalledPath = installedPath });
    }

    private async Task<IpcResponse> RemoveAdapterPackAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        RemoveAdapterPackRequest payload = IpcJson.FromElement<RemoveAdapterPackRequest>(request.Payload)
            ?? throw new InvalidDataException("RemoveAdapterPack requires a pack identity and version.");
        if (string.IsNullOrWhiteSpace(payload.PackId) || string.IsNullOrWhiteSpace(payload.Version))
        {
            return Failure(request, "INVALID_ADAPTER_PACK_IDENTITY", "RemoveAdapterPack requires a non-empty pack ID and version.");
        }

        bool removed;
        try
        {
            removed = _adapterPackManager!.Remove(payload.PackId, payload.Version);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return Failure(request, "INVALID_ADAPTER_PACK_IDENTITY", exception.Message);
        }

        string entityId = $"{payload.PackId}@{payload.Version}";
        await _store!.DeleteSuiteEntityAsync(SuiteEntityKind.AdapterPackInspection, entityId, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return Failure(request, "ADAPTER_PACK_NOT_INSTALLED", $"Adapter pack {entityId} is not installed.");
        }

        IncrementSuiteRevision();
        return Success(request, new { Removed = true, PackId = payload.PackId, payload.Version });
    }

    private async Task<IpcResponse> SaveOwnershipConsentAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        GrantOwnershipConsentRequest payload = IpcJson.FromElement<GrantOwnershipConsentRequest>(request.Payload)
            ?? throw new InvalidDataException("GrantOwnershipConsent requires an exact process consent.");
        OwnershipConsentV1 consent = payload.Consent;
        if (consent.SchemaVersion != OwnershipConsentV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(consent.Id)
            || !Path.IsPathFullyQualified(consent.ExecutablePath)
            || string.IsNullOrWhiteSpace(consent.ProcessName)
            || consent.Sha256.Length != 64
            || !consent.Sha256.All(Uri.IsHexDigit))
        {
            return Failure(request, "INVALID_OWNERSHIP_CONSENT", "Ownership consent identity is incomplete or malformed.");
        }
        TakeoverProcessIdentity? actual = await WindowsTakeoverProcessController.TryReadIdentityAsync(
            consent.ExecutablePath,
            consent.ProcessName,
            consent.ProcessName,
            [],
            cancellationToken).ConfigureAwait(false);
        if (actual is null)
        {
            return Failure(request, "OWNERSHIP_IDENTITY_UNAVAILABLE", "The target executable could not be read for exact identity validation.");
        }
        if (string.IsNullOrWhiteSpace(actual.SignerThumbprint))
        {
            return Failure(request, "UNSIGNED_TAKEOVER_TARGET", "Automatic takeover refuses unsigned competitor binaries.");
        }
        TakeoverAuthorizationResult validation = TakeoverConsentValidator.Validate(
            actual,
            consent,
            requireForceTermination: false,
            requireStartupDisable: false);
        if (!validation.Authorized)
        {
            return Failure(request, "OWNERSHIP_IDENTITY_CHANGED", string.Join(" ", validation.Errors));
        }
        OwnershipConsentV1 canonical = TakeoverConsentValidator.Create(
            actual,
            consent.AllowForceTermination,
            consent.DisableStartup,
            DateTimeOffset.UtcNow);
        await _store!.SaveSuiteEntityAsync(
            SuiteEntityKind.OwnershipConsent,
            canonical.Id,
            canonical,
            cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, canonical);
    }

    private async Task<IpcResponse> PreviewTakeoverAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        WindowsTakeoverProcessController processes = new();
        TakeoverPlanV1 plan = await WindowsTakeoverPlanBuilder.CreateAsync(GetSnapshot(), processes, cancellationToken).ConfigureAwait(false);
        TakeoverExecutionStatusV1 executor = GetTakeoverGate().GetStatus();
        if (!executor.CanExecute)
        {
            plan = plan with { Warnings = plan.Warnings.Append(executor.Message).ToArray() };
        }
        await _store!.SaveSuiteEntityAsync(SuiteEntityKind.TakeoverPlan, plan.Id, plan, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, new TakeoverPreviewResultV1(plan, executor));
    }

    private async Task<IpcResponse> ExecuteTakeoverAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (HasActiveOperation())
        {
            return Failure(request, "OPERATION_ACTIVE", "Abort and finish the active hardware operation before changing controller ownership.");
        }
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "Ownership takeover is blocked until the pending hardware rollback is recovered.");
        }
        EnsureExpectedRevision(request);
        ExecuteTakeoverRequest payload = IpcJson.FromElement<ExecuteTakeoverRequest>(request.Payload)
            ?? throw new InvalidDataException("ExecuteTakeover requires a plan ID and explicit exact-process confirmation.");
        if (!payload.ConfirmExactProcesses)
        {
            return Failure(request, "TAKEOVER_NOT_CONFIRMED", "Automatic takeover requires confirmation that only the exact previewed binaries may be stopped.");
        }
        TakeoverExecutionStatusV1 executor = GetTakeoverGate().GetStatus();
        if (!executor.CanExecute)
        {
            return Failure(request, "TAKEOVER_EXECUTOR_BLOCKED", executor.Message);
        }
        TakeoverPlanV1? plan = await _store!.GetSuiteEntityAsync<TakeoverPlanV1>(SuiteEntityKind.TakeoverPlan, payload.PlanId, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return Failure(request, "TAKEOVER_PLAN_MISSING", "The requested takeover preview is unavailable. Create a new preview before executing.");
        }
        if (plan.Processes.Count == 0 || plan.ControlsToReset.Count == 0)
        {
            return Failure(request, "TAKEOVER_PLAN_INCOMPLETE", "A takeover preview must contain exact processes and resettable overlapping controls.");
        }
        if (plan.Processes.Any(process => string.IsNullOrWhiteSpace(process.SignerThumbprint)))
        {
            return Failure(request, "UNSIGNED_TAKEOVER_TARGET", "Automatic takeover refuses a target without a readable signer thumbprint.");
        }

        IReadOnlyList<OwnershipConsentV1> consents = await _store.GetSuiteEntitiesAsync<OwnershipConsentV1>(SuiteEntityKind.OwnershipConsent, cancellationToken).ConfigureAwait(false);
        TakeoverCoordinator coordinator = new(
            new WindowsTakeoverProcessController(),
            new WindowsRegistryStartupController(),
            new RuntimeTakeoverHardwareController(ResetCapabilityForTakeoverAsync, _ownershipLeases),
            new SuiteTakeoverJournal(_store));
        (TakeoverTransactionV1 transaction, OwnershipLeaseV1? lease) = await coordinator.ExecuteAsync(plan, consents, cancellationToken).ConfigureAwait(false);
        if (transaction.State != TakeoverTransactionState.Completed || lease is null)
        {
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.TakeoverTransaction, transaction.Id, transaction, CancellationToken.None).ConfigureAwait(false);
            IncrementSuiteRevision();
            return FailureWithPayload(
                request,
                "TAKEOVER_FAILED",
                transaction.Error ?? "The takeover did not complete and recovery was attempted.",
                transaction);
        }

        transaction = transaction with { LeaseId = lease.Id, UpdatedAt = DateTimeOffset.UtcNow };
        await _store.SaveSuiteEntityAsync(SuiteEntityKind.OwnershipLease, lease.Id, lease, cancellationToken).ConfigureAwait(false);
        await _store.SaveSuiteEntityAsync(SuiteEntityKind.TakeoverTransaction, transaction.Id, transaction, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        await RefreshAsync(persistSensors: false, cancellationToken).ConfigureAwait(false);
        return Success(request, new TakeoverExecutionResultV1(transaction, lease));
    }

    private async Task<IpcResponse> ReleaseOwnershipAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (HasActiveOperation())
        {
            return Failure(request, "OPERATION_ACTIVE", "Abort and finish the active hardware operation before giving control back.");
        }
        EnsureExpectedRevision(request);
        ReleaseOwnershipRequest payload = IpcJson.FromElement<ReleaseOwnershipRequest>(request.Payload)
            ?? throw new InvalidDataException("ReleaseOwnership requires a takeover transaction ID.");
        TakeoverTransactionV1? transaction = await _store!.GetSuiteEntityAsync<TakeoverTransactionV1>(SuiteEntityKind.TakeoverTransaction, payload.TransactionId, cancellationToken).ConfigureAwait(false);
        if (transaction is null || string.IsNullOrWhiteSpace(transaction.LeaseId))
        {
            return Failure(request, "OWNERSHIP_LEASE_MISSING", "The takeover transaction has no active ownership lease to release.");
        }
        OwnershipLeaseV1? lease = await _store.GetSuiteEntityAsync<OwnershipLeaseV1>(SuiteEntityKind.OwnershipLease, transaction.LeaseId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return Failure(request, "OWNERSHIP_LEASE_MISSING", "The persisted ownership lease is unavailable; recovery requires manual inspection.");
        }

        TakeoverCoordinator coordinator = new(
            new WindowsTakeoverProcessController(),
            new WindowsRegistryStartupController(),
            new RuntimeTakeoverHardwareController(ResetCapabilityForTakeoverAsync, _ownershipLeases),
            new SuiteTakeoverJournal(_store));
        try
        {
            TakeoverTransactionV1 released = await coordinator.GiveControlBackAsync(transaction, lease, cancellationToken).ConfigureAwait(false);
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.TakeoverTransaction, released.Id, released, cancellationToken).ConfigureAwait(false);
            await _store.DeleteSuiteEntityAsync(SuiteEntityKind.OwnershipLease, lease.Id, cancellationToken).ConfigureAwait(false);
            IncrementSuiteRevision();
            await RefreshAsync(persistSensors: false, cancellationToken).ConfigureAwait(false);
            return Success(request, released);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TakeoverTransactionV1 recovery = transaction with
            {
                State = TakeoverTransactionState.RecoveryRequired,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = $"Give-control-back failed: {exception.Message}"
            };
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.TakeoverTransaction, recovery.Id, recovery, CancellationToken.None).ConfigureAwait(false);
            IncrementSuiteRevision();
            return FailureWithPayload(request, "OWNERSHIP_RECOVERY_REQUIRED", recovery.Error, recovery);
        }
    }

    private async Task<IpcResponse> GetOwnershipAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<OwnershipConsentV1> consents = await _store!.GetSuiteEntitiesAsync<OwnershipConsentV1>(
            SuiteEntityKind.OwnershipConsent,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OwnershipLeaseV1> leases = await _store.GetSuiteEntitiesAsync<OwnershipLeaseV1>(
            SuiteEntityKind.OwnershipLease,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TakeoverTransactionV1> transactions = await _store.GetSuiteEntitiesAsync<TakeoverTransactionV1>(
            SuiteEntityKind.TakeoverTransaction,
            cancellationToken).ConfigureAwait(false);
        return Success(request, new OwnershipOverview(
            consents,
            leases,
            transactions.OrderByDescending(transaction => transaction.UpdatedAt).ToArray(),
            GetTakeoverGate().GetStatus()));
    }

    private async Task<IpcResponse> ApplyProfileAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (HasActiveOperation())
        {
            return Failure(request, "OPERATION_ACTIVE", "A calibration or tuning operation is active. Abort it before applying a profile.");
        }

        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "New profile writes are blocked until pending rollback recovery succeeds.");
        }

        EnsureExpectedRevision(request);
        ApplyProfileRequest payload = IpcJson.FromElement<ApplyProfileRequest>(request.Payload)
            ?? throw new InvalidDataException("ApplyProfile requires an ApplyProfileRequest payload.");
        string? protectionError = await ValidateProtectedCoolingActionsAsync(payload.Profile.Actions, cancellationToken).ConfigureAwait(false);
        if (protectionError is not null)
        {
            return Failure(request, "PROTECTED_COOLING_OUTPUT", protectionError);
        }
        IReadOnlyDictionary<string, CapabilityDescriptor> capabilities = GetSnapshot().Capabilities.ToDictionary(
            capability => capability.Id,
            StringComparer.Ordinal);
        (ProfileTransaction transaction, ProfileValidationResult validation) = await _engine!.ApplyAsync(
            payload.Profile,
            capabilities,
            _engine.Revision,
            payload.ConfirmExperimental && payload.ConfirmDevices,
            cancellationToken).ConfigureAwait(false);
        if (transaction.State == ProfileTransactionState.RecoveryRequired)
        {
            _rollbackBlocked = true;
            return FailureWithPayload(
                request,
                "RECOVERY_REQUIRED",
                transaction.Error ?? "The profile outcome is unknown and hardware recovery is required.",
                transaction);
        }
        if (!validation.Valid || transaction.State != ProfileTransactionState.Committed)
        {
            return Failure(request, "PROFILE_REJECTED", transaction.Error ?? string.Join(" ", validation.Errors));
        }

        await _store!.SaveProfileAsync(payload.Profile, cancellationToken).ConfigureAwait(false);
        return Success(request, new ApplyProfileResult(transaction, _engine.ActiveProfileId));
    }

    private async Task<IpcResponse> ApplyProfileV2Async(IpcRequest request, CancellationToken cancellationToken)
    {
        if (HasActiveOperation())
        {
            return Failure(request, "OPERATION_ACTIVE", "A calibration or tuning operation is active. Abort it before applying a profile.");
        }
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "New profile writes are blocked until pending rollback recovery succeeds.");
        }

        EnsureExpectedRevision(request);
        ApplyProfileV2Request payload = IpcJson.FromElement<ApplyProfileV2Request>(request.Payload)
            ?? throw new InvalidDataException("ApplyProfileV2 requires an ApplyProfileV2Request payload.");
        Dictionary<string, CapabilityDescriptorV2> capabilitiesV2 = GetCapabilitiesV2()
            .ToDictionary(item => item.Capability.Id, StringComparer.Ordinal);
        ProfileValidationResult validation = ProfileV2Validator.Validate(
            payload.Profile,
            capabilitiesV2,
            payload.Source,
            payload.ConfirmManualVoltage);
        if (!validation.Valid)
        {
            return Failure(request, "PROFILE_REJECTED", string.Join(" ", validation.Errors));
        }
        string? protectionError = await ValidateProtectedCoolingActionsAsync(payload.Profile.HardwareActions, cancellationToken).ConfigureAwait(false);
        if (protectionError is not null)
        {
            return Failure(request, "PROTECTED_COOLING_OUTPUT", protectionError);
        }
        if (payload.Profile.IsExperimental && !payload.ConfirmExperimental)
        {
            return Failure(request, "EXPERIMENTAL_NOT_CONFIRMED", "Experimental profiles require an explicit confirmation.");
        }
        HashSet<string> deviceIds = payload.Profile.HardwareActions
            .Select(action => capabilitiesV2[action.CapabilityId].Capability.DeviceId)
            .ToHashSet(StringComparer.Ordinal);
        ActiveCoolingGraphRuntime? requestedCoolingGraph = null;
        if (payload.Profile.CoolingGraphId is string coolingId)
        {
            CoolingGraphV1? graph = await _store!.GetSuiteEntityAsync<CoolingGraphV1>(
                SuiteEntityKind.CoolingGraph,
                coolingId,
                cancellationToken).ConfigureAwait(false);
            if (graph is null)
            {
                return Failure(request, "COOLING_GRAPH_MISSING", $"Cooling graph '{coolingId}' is unavailable.");
            }
            string? graphRoleError = await ValidateCoolingGraphOutputRolesAsync(graph, cancellationToken).ConfigureAwait(false);
            if (graphRoleError is not null)
            {
                return Failure(request, "PROTECTED_COOLING_OUTPUT", graphRoleError);
            }
            string? graphQualificationError = await ValidateCoolingGraphQualificationAsync(graph, cancellationToken).ConfigureAwait(false);
            if (graphQualificationError is not null)
            {
                return Failure(request, "COOLING_GRAPH_UNQUALIFIED", graphQualificationError);
            }
            if (graph.Outputs.Any(output => capabilitiesV2[output.CapabilityId].Capability.State == CapabilityAccessState.Experimental)
                && !payload.Profile.IsExperimental)
            {
                return Failure(request, "EXPERIMENTAL_COOLING_GRAPH", "A profile that activates Experimental cooling outputs must itself be marked Experimental.");
            }
            foreach (CoolingGraphOutputV1 output in graph.Outputs)
            {
                deviceIds.Add(capabilitiesV2[output.CapabilityId].Capability.DeviceId);
            }
            requestedCoolingGraph = await CreateActiveCoolingGraphAsync(
                payload.Profile.Id,
                graph,
                payload.Profile.SafetyLimits,
                cancellationToken).ConfigureAwait(false);
        }
        string[] unconfirmed = deviceIds
            .Where(deviceId => !payload.ConfirmedDeviceIds.Contains(deviceId, StringComparer.Ordinal))
            .ToArray();
        if (payload.Profile.IsExperimental && unconfirmed.Length > 0)
        {
            return Failure(request, "DEVICE_NOT_CONFIRMED", $"Experimental controls require exact-device confirmation: {string.Join(", ", unconfirmed)}.");
        }

        ProfileV1 hardwareProfile = ProfileMigration.Downgrade(payload.Profile);
        IReadOnlyDictionary<string, CapabilityDescriptor> capabilities = capabilitiesV2
            .ToDictionary(pair => pair.Key, pair => pair.Value.Capability, StringComparer.Ordinal);
        Func<CancellationToken, Task>? activateCooling = requestedCoolingGraph is null
            ? null
            : token => ReplaceActiveCoolingGraphTransactionAsync(requestedCoolingGraph, token);
        IReadOnlyList<HardwareControlLeaseItemV1> coolingControls = requestedCoolingGraph is null
            ? []
            : requestedCoolingGraph.Graph.Outputs.Select(output => new HardwareControlLeaseItemV1(
                capabilitiesV2[output.CapabilityId].Capability.AdapterId,
                output.CapabilityId)).ToArray();
        (ProfileTransaction transaction, ProfileValidationResult legacyValidation) = await _engine!.ApplyAsync(
            hardwareProfile,
            capabilities,
            _engine.Revision,
            payload.ConfirmExperimental && unconfirmed.Length == 0,
            cancellationToken,
            activateCooling,
            coolingControls).ConfigureAwait(false);
        if (transaction.State == ProfileTransactionState.RecoveryRequired)
        {
            _rollbackBlocked = true;
            return FailureWithPayload(
                request,
                "RECOVERY_REQUIRED",
                transaction.Error ?? "The profile outcome is unknown and hardware recovery is required.",
                transaction);
        }
        if (!legacyValidation.Valid || transaction.State != ProfileTransactionState.Committed)
        {
            return Failure(request, "PROFILE_REJECTED", transaction.Error ?? string.Join(" ", legacyValidation.Errors));
        }
        await _store!.SaveSuiteEntityAsync(
            SuiteEntityKind.ProfileV2,
            payload.Profile.Id,
            payload.Profile,
            cancellationToken).ConfigureAwait(false);
        return Success(request, new ApplyProfileResult(transaction, _engine.ActiveProfileId));
    }

    private async Task<IpcResponse> PreviewProfileV2Async(IpcRequest request, CancellationToken cancellationToken)
    {
        PreviewProfileV2Request payload = IpcJson.FromElement<PreviewProfileV2Request>(request.Payload)
            ?? throw new InvalidDataException("PreviewProfileV2 requires a PreviewProfileV2Request payload.");
        Dictionary<string, CapabilityDescriptorV2> capabilities = GetCapabilitiesV2()
            .ToDictionary(item => item.Capability.Id, StringComparer.Ordinal);
        List<ProfileDryRunActionV1> linkedActions = [];
        List<string> linkedConflicts = [];
        List<string> linkedRequiredCapabilities = [];

        if (payload.Profile.CoolingGraphId is string coolingGraphId)
        {
            CoolingGraphV1? graph = await _store!.GetSuiteEntityAsync<CoolingGraphV1>(
                SuiteEntityKind.CoolingGraph,
                coolingGraphId,
                cancellationToken).ConfigureAwait(false);
            if (graph is null)
            {
                const string message = "The linked cooling graph is unavailable; no profile mutation should start.";
                linkedActions.Add(new(
                    "linked-cooling",
                    "Cooling",
                    coolingGraphId,
                    ProfileDryRunActionState.Blocked,
                    true,
                    null,
                    message));
                linkedConflicts.Add(message);
            }
            else
            {
                string[] missingOutputs = graph.Outputs
                    .Select(item => item.CapabilityId)
                    .Where(item => !capabilities.ContainsKey(item))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                string? graphError = missingOutputs.Length > 0
                    ? $"Cooling outputs are unavailable: {string.Join(", ", missingOutputs)}."
                    : await ValidateCoolingGraphOutputRolesAsync(graph, cancellationToken).ConfigureAwait(false)
                        ?? await ValidateCoolingGraphQualificationAsync(graph, cancellationToken).ConfigureAwait(false);
                if (graphError is null)
                {
                    string[] unconfirmedCoolingDevices = payload.Profile.IsExperimental
                        ? graph.Outputs
                            .Select(item => capabilities[item.CapabilityId].Capability.DeviceId)
                            .Distinct(StringComparer.Ordinal)
                            .Where(item => !payload.ConfirmedDeviceIds.Contains(item, StringComparer.Ordinal))
                            .ToArray()
                        : [];
                    if (unconfirmedCoolingDevices.Length > 0)
                    {
                        string message = $"Exact-device confirmation is missing for linked cooling outputs: {string.Join(", ", unconfirmedCoolingDevices)}.";
                        linkedActions.Add(new(
                            "linked-cooling",
                            "Cooling",
                            graph.Name,
                            ProfileDryRunActionState.RequiresConfirmation,
                            true,
                            null,
                            message));
                        linkedConflicts.Add(message);
                    }
                    else
                    {
                        linkedRequiredCapabilities.AddRange(graph.Outputs.Select(item => item.CapabilityId));
                        linkedActions.Add(new(
                            "linked-cooling",
                            "Cooling",
                            graph.Name,
                            ProfileDryRunActionState.Ready,
                            true,
                            null,
                            "Ready; cooling replacement participates in the service transaction and its prior graph is restored on failure."));
                    }
                }
                else
                {
                    linkedActions.Add(new(
                        "linked-cooling",
                        "Cooling",
                        graph.Name,
                        ProfileDryRunActionState.Blocked,
                        true,
                        null,
                        graphError));
                    linkedConflicts.Add(graphError);
                }
            }
        }

        AddCompanionPreview(
            payload.Profile.LightingSceneId,
            payload.KnownLightingSceneIds,
            "linked-lighting",
            "Lighting",
            "lighting scene",
            linkedActions,
            linkedConflicts);
        AddCompanionPreview(
            payload.Profile.OsdLayoutId,
            payload.KnownOsdLayoutIds,
            "linked-osd",
            "OSD",
            "OSD layout",
            linkedActions,
            linkedConflicts);

        ProfileDryRunResultV1 result = ProfileDryRunPlanner.Build(
            payload,
            capabilities,
            linkedActions,
            linkedConflicts);
        if (linkedRequiredCapabilities.Count > 0)
        {
            result = result with
            {
                RequiredCapabilities = result.RequiredCapabilities
                    .Concat(linkedRequiredCapabilities)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray()
            };
        }
        return Success(request, result);
    }

    private static void AddCompanionPreview(
        string? referenceId,
        IReadOnlyList<string> knownIds,
        string actionId,
        string domain,
        string label,
        List<ProfileDryRunActionV1> actions,
        List<string> conflicts)
    {
        if (string.IsNullOrWhiteSpace(referenceId))
        {
            return;
        }

        if (knownIds.Contains(referenceId, StringComparer.Ordinal))
        {
            actions.Add(new(
                actionId,
                domain,
                referenceId,
                ProfileDryRunActionState.IndependentCompanion,
                true,
                null,
                $"The linked {label} is available, but runs in the user session after the verified service commit and reports independently."));
            return;
        }

        string message = $"The linked {label} '{referenceId}' is unavailable in this user session.";
        actions.Add(new(
            actionId,
            domain,
            referenceId,
            ProfileDryRunActionState.Blocked,
            true,
            null,
            message));
        conflicts.Add(message);
    }

    private async Task<IpcResponse> SaveAutomationRuleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        AutomationRuleV1 rule = IpcJson.FromElement<AutomationRuleV1>(request.Payload)
            ?? throw new InvalidDataException("SaveAutomationRule requires an AutomationRuleV1 payload.");
        IReadOnlyList<ProfileV1> profiles = await _store!.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProfileV2> suiteProfiles = await _store.GetSuiteEntitiesAsync<ProfileV2>(
            SuiteEntityKind.ProfileV2,
            cancellationToken).ConfigureAwait(false);
        HashSet<string> availableProfileIds = profiles.Select(profile => profile.Id)
            .Concat(suiteProfiles.Select(profile => profile.Id))
            .ToHashSet(StringComparer.Ordinal);
        string? error = AutomationRuleMatcher.Validate(
            rule,
            availableProfileIds);
        if (error is not null)
        {
            return Failure(request, "INVALID_AUTOMATION_RULE", error);
        }

        await _store.SaveAutomationRuleAsync(rule, cancellationToken).ConfigureAwait(false);
        return Success(request, rule);
    }

    private async Task<IpcResponse> DeleteAutomationRuleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        DeleteAutomationRuleRequest payload = IpcJson.FromElement<DeleteAutomationRuleRequest>(request.Payload)
            ?? throw new InvalidDataException("DeleteAutomationRule requires a rule ID.");
        if (string.IsNullOrWhiteSpace(payload.RuleId))
        {
            return Failure(request, "INVALID_AUTOMATION_RULE", "Rule ID is required.");
        }

        await _store!.DeleteAutomationRuleAsync(payload.RuleId, cancellationToken).ConfigureAwait(false);
        return Success(request, payload.RuleId);
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
        if (HasActiveOperation())
        {
            return Failure(request, "OPERATION_ACTIVE", "A calibration or tuning operation is active. Abort it before resetting another control.");
        }

        string? capabilityId = IpcJson.FromElement<string>(request.Payload);
        CapabilityDescriptor capability = GetSnapshot().Capabilities.FirstOrDefault(item => item.Id == capabilityId)
            ?? throw new InvalidOperationException("The requested capability was not discovered.");
        if (!capability.CanResetToDefault || capability.State != CapabilityAccessState.Verified)
        {
            return Failure(request, "RESET_NOT_AVAILABLE", "Only Verified capabilities with explicit reset semantics can be reset.");
        }

        HardwareRecoveryResult recovery = await _engine!.RestoreDefaultsAsync(
            [new HardwareControlLeaseItemV1(capability.AdapterId, capability.Id)],
            cancellationToken).ConfigureAwait(false);
        if (!recovery.AllDefaultsVerified)
        {
            _rollbackBlocked = true;
            string message = recovery.Errors.Count == 0
                ? "The adapter did not return a successful default-state read-back."
                : string.Join(" ", recovery.Errors);
            return Failure(
                request,
                "RECOVERY_REQUIRED",
                $"Reset outcome is not verified; writes are locked until recovery succeeds. {message}");
        }

        await RefreshAsync(persistSensors: false, cancellationToken).ConfigureAwait(false);
        return Success(request, capability.Id);
    }

    private async Task ResetCapabilityForTakeoverAsync(string capabilityId, CancellationToken cancellationToken)
    {
        CapabilityDescriptor capability = GetSnapshot().Capabilities.FirstOrDefault(item => item.Id == capabilityId)
            ?? throw new InvalidOperationException($"The takeover control '{capabilityId}' is no longer available.");
        if (!capability.CanResetToDefault || capability.Evidence < EvidenceLevel.ReadBackVerified)
        {
            throw new InvalidOperationException($"The takeover control '{capability.Name}' has no verified firmware/default reset path.");
        }
        if (capability.State is CapabilityAccessState.ReadOnly or CapabilityAccessState.Unsupported or CapabilityAccessState.Faulted)
        {
            throw new InvalidOperationException($"The takeover control '{capability.Name}' is no longer writable.");
        }

        IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
        await adapter.ResetToDefaultAsync(capability.Id, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(persistSensors: false, cancellationToken).ConfigureAwait(false);
        AdapterHealth? health = GetSnapshot().AdapterHealth.FirstOrDefault(item => item.AdapterId == capability.AdapterId);
        if (health is null || !health.Healthy)
        {
            throw new InvalidOperationException($"The adapter could not verify a healthy default state for '{capability.Name}'.");
        }
    }

    private async Task<IpcResponse> StartCalibrationAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "Hardware operations are blocked until pending recovery succeeds.");
        }

        EnsureExpectedRevision(request);
        StartCalibrationRequest payload = IpcJson.FromElement<StartCalibrationRequest>(request.Payload)
            ?? throw new InvalidDataException("StartCalibration requires a StartCalibrationRequest payload.");
        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(
            item => string.Equals(item.Id, payload.CapabilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The requested cooling capability was not discovered.");
        CoolingOutputAssignmentV1? outputAssignment = await GetCoolingOutputAssignmentAsync(capability, cancellationToken).ConfigureAwait(false);
        if (outputAssignment is not null && CoolingOutputAssignmentPolicy.IsPump(outputAssignment.Role))
        {
            return Failure(
                request,
                "PUMP_CALIBRATION_BLOCKED",
                "Pump calibration is blocked until RigPilot has an exact device-specific nonzero-floor qualification path.");
        }
        if (payload.AllowFanStop && CoolingOutputAssignmentPolicy.IsProtected(outputAssignment, capability))
        {
            return Failure(
                request,
                "FAN_STOP_FORBIDDEN",
                "Zero-RPM calibration is forbidden because this output is persistently classified as a CPU fan or pump.");
        }
        HardwareOperationEligibility eligibility = HardwareOperationEligibilityEvaluator.ForCalibration(
            capability,
            payload.ConfirmExperimental,
            payload.ConfirmDevice);
        if (!eligibility.Eligible)
        {
            return Failure(request, "OPERATION_NOT_ELIGIBLE", eligibility.Reason);
        }

        SensorSample rpm = snapshot.Sensors.FirstOrDefault(
            item => string.Equals(item.SensorId, payload.RpmSensorId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected RPM sensor was not discovered.");
        if (!string.Equals(rpm.Unit, "RPM", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rpm.AdapterId, capability.AdapterId, StringComparison.Ordinal)
            || !string.Equals(rpm.DeviceId, capability.DeviceId, StringComparison.Ordinal))
        {
            return Failure(
                request,
                "RPM_SENSOR_MISMATCH",
                "Calibration requires an RPM sensor from the same adapter and exact device as the selected control.");
        }

        FanCommissioningSessionV1[] candidates = (await _store!.GetSuiteEntitiesAsync<FanCommissioningSessionV1>(
                SuiteEntityKind.FanCommissioningSession,
                cancellationToken).ConfigureAwait(false))
            .Where(session => string.Equals(session.CapabilityId, capability.Id, StringComparison.Ordinal)
                && string.Equals(session.RpmSensorId, rpm.SensorId, StringComparison.Ordinal)
                && session.State == FanCommissioningState.ReadyForCalibration
                && session.HeaderConfirmed
                && session.PhysicalHeaderObserved)
            .ToArray();
        FanCommissioningSessionV1? commissioningSession = string.IsNullOrWhiteSpace(payload.CommissioningSessionId)
            ? candidates.Length == 1 ? candidates[0] : null
            : candidates.FirstOrDefault(session => string.Equals(
                session.Id,
                payload.CommissioningSessionId,
                StringComparison.Ordinal));
        if (commissioningSession is null)
        {
            return Failure(
                request,
                "COMMISSIONING_REQUIRED",
                candidates.Length > 1
                    ? "Select the exact ready commissioning session before calibration; multiple matching sessions exist."
                    : "Visually observe and confirm the exact physical header and paired RPM sensor before calibration.");
        }

        FanCalibrationTemperatureLimit[] temperatureLimits = (payload.TemperatureLimits ?? [])
            .DistinctBy(limit => limit.SensorId, StringComparer.Ordinal)
            .ToArray();
        if (temperatureLimits.Length > 8
            || temperatureLimits.Any(limit => !double.IsFinite(limit.MaximumCelsius)
                || limit.MaximumCelsius is < 40 or > 110))
        {
            return Failure(request, "INVALID_TEMPERATURE_LIMITS", "Calibration accepts at most eight temperature limits between 40 and 110 °C.");
        }

        foreach (FanCalibrationTemperatureLimit limit in temperatureLimits)
        {
            SensorSample? sensor = snapshot.Sensors.FirstOrDefault(item =>
                string.Equals(item.SensorId, limit.SensorId, StringComparison.Ordinal));
            if (sensor is null
                || !string.Equals(sensor.Unit, "°C", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sensor.AdapterId, capability.AdapterId, StringComparison.Ordinal)
                || !string.Equals(sensor.DeviceId, capability.DeviceId, StringComparison.Ordinal))
            {
                return Failure(
                    request,
                    "TEMPERATURE_SENSOR_MISMATCH",
                    "Every calibration temperature limit must reference a temperature sensor from the same adapter and exact device.");
            }
        }

        TimeSpan settling = payload.SettlingTime ?? TimeSpan.FromSeconds(3);
        if (settling < TimeSpan.FromSeconds(1) || settling > TimeSpan.FromSeconds(10))
        {
            return Failure(request, "INVALID_SETTLING_TIME", "Production calibration settling time must be between 1 and 10 seconds.");
        }

        TimeSpan sampleInterval = payload.SampleInterval ?? TimeSpan.FromMilliseconds(500);
        if (sampleInterval < TimeSpan.FromMilliseconds(250) || sampleInterval > TimeSpan.FromSeconds(2))
        {
            return Failure(request, "INVALID_SAMPLE_INTERVAL", "RPM sample interval must be between 250 milliseconds and 2 seconds.");
        }

        if (payload.StableSampleCount is < 3 or > 5
            || payload.MaximumSampleCount < payload.StableSampleCount
            || payload.MaximumSampleCount > 15)
        {
            return Failure(request, "INVALID_STABILITY_WINDOW", "Production calibration requires 3-5 stable readings inside a maximum window of 15 samples.");
        }

        if (!double.IsFinite(payload.StabilityTolerancePercent)
            || payload.StabilityTolerancePercent is < 2 or > 15)
        {
            return Failure(request, "INVALID_STABILITY_TOLERANCE", "RPM stability tolerance must be between 2% and 15%.");
        }

        if (payload.RestartVerificationCycles is < 2 or > 3)
        {
            return Failure(request, "INVALID_RESTART_CYCLES", "Production calibration requires 2 or 3 restart verification cycles.");
        }

        payload = payload with
        {
            SettlingTime = settling,
            SampleInterval = sampleInterval,
            TemperatureLimits = temperatureLimits,
            CommissioningSessionId = commissioningSession.Id
        };
        HardwareOperationStatus status = CreateOperationStatus(
            HardwareOperationKind.Calibration,
            capability,
            "Calibration is queued; no write has occurred yet.");
        if (!TryReserveOperation(status, out CancellationTokenSource? operationCancellation))
        {
            return Failure(request, "OPERATION_ACTIVE", "Another calibration or tuning operation is already active.");
        }

        try
        {
            await _store!.SaveOperationAsync(status, cancellationToken).ConfigureAwait(false);
            Task task = RunCalibrationOperationAsync(status.Id, payload, capability, operationCancellation!);
            RegisterOperationTask(status.Id, task);
            return Success(request, status);
        }
        catch
        {
            ReleaseOperationReservation(status.Id);
            throw;
        }
    }

    private async Task<IpcResponse> BeginFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        BeginFanCommissioningRequest payload = IpcJson.FromElement<BeginFanCommissioningRequest>(request.Payload)
            ?? throw new InvalidDataException("BeginFanCommissioning requires a BeginFanCommissioningRequest payload.");
        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, payload.CapabilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected cooling control was not discovered.");
        if (capability.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety)
            || capability.State is not (CapabilityAccessState.Verified or CapabilityAccessState.Experimental)
            || string.IsNullOrWhiteSpace(payload.HeaderName)
            || payload.HeaderName.Trim().Length > 80)
        {
            return Failure(request, "INVALID_COMMISSIONING", "Commissioning requires a detected cooling control and a header name up to 80 characters.");
        }

        SensorSample rpm = snapshot.Sensors.FirstOrDefault(item =>
            string.Equals(item.SensorId, payload.RpmSensorId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected RPM sensor was not discovered.");
        if (!string.Equals(rpm.Unit, "RPM", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rpm.AdapterId, capability.AdapterId, StringComparison.Ordinal)
            || !string.Equals(rpm.DeviceId, capability.DeviceId, StringComparison.Ordinal))
        {
            return Failure(request, "RPM_SENSOR_MISMATCH", "Commissioning requires an RPM sensor from the same adapter and exact device.");
        }

        bool protectedOutput = await IsProtectedCoolingOutputAsync(capability, cancellationToken).ConfigureAwait(false)
            || payload.IsCpuOrPump
            || capability.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
            || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase);
        if (!protectedOutput && !FanCommissioningWorkflow.IsDeclaredChassisHeader(payload.HeaderName))
        {
            return Failure(
                request,
                "CHASSIS_HEADER_REQUIRED",
                "Before a generic controller can be pulsed, declare the exact physical chassis header (for example CHA_FAN1). Generic labels such as Fan #1 are not sufficient.");
        }
        if (protectedOutput && payload.AllowFanStop)
        {
            return Failure(request, "FAN_STOP_FORBIDDEN", "CPU fans and pumps cannot be commissioned with fan stop enabled.");
        }

        FanCommissioningSessionV1 session = new(
            FanCommissioningSessionV1.CurrentSchemaVersion,
            $"commission.{Guid.NewGuid():N}",
            capability.Id,
            rpm.SensorId,
            payload.HeaderName.Trim(),
            FanCommissioningState.AwaitingIdentification,
            protectedOutput,
            payload.AllowFanStop,
            HeaderConfirmed: false,
            CalibrationId: null,
            StartedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(payload.Notes) ? null : payload.Notes.Trim(),
            Error: null);
        SuiteValidationResult validation = FanCommissioningWorkflow.Validate(session);
        if (!validation.IsValid)
        {
            return Failure(request, "INVALID_COMMISSIONING", string.Join(" ", validation.Errors));
        }
        await _store!.SaveSuiteEntityAsync(SuiteEntityKind.FanCommissioningSession, session.Id, session, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, session);
    }

    /// <summary>
    /// Diagnoses whether a selected controller can enter the adapter Prepare
    /// phase. This route is intentionally not an operation: it never reserves
    /// a hardware operation and it never calls Apply, Verify, Rollback, or
    /// Reset. It exists so a context/driver failure can be investigated before
    /// a user is offered another physical identification pulse.
    /// </summary>
    private async Task<IpcResponse> PreflightFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        PreflightFanCommissioningRequest payload = IpcJson.FromElement<PreflightFanCommissioningRequest>(request.Payload)
            ?? throw new InvalidDataException("PreflightFanCommissioning requires a PreflightFanCommissioningRequest payload.");
        FanCommissioningSessionV1? session = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            payload.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Failure(request, "COMMISSIONING_NOT_FOUND", "The requested fan commissioning session does not exist.");
        }
        if (session.State != FanCommissioningState.AwaitingIdentification)
        {
            return Failure(request, "COMMISSIONING_STATE_INVALID", "Only an awaiting-identification session can run a software-control preflight.");
        }
        if (!FanCommissioningWorkflow.CanIssueIdentificationPulse(session, out string? preflightSafetyReason))
        {
            return Failure(request, "PULSE_TARGET_PROTECTED", preflightSafetyReason!);
        }

        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item => item.Id == session.CapabilityId)
            ?? throw new InvalidOperationException("The commissioned cooling control is no longer available.");
        if (await IsProtectedCoolingOutputAsync(capability, cancellationToken).ConfigureAwait(false))
        {
            return Failure(
                request,
                "PULSE_TARGET_PROTECTED",
                "The selected output is persistently classified as a CPU fan or pump, so even the no-write identification preflight is blocked.");
        }
        HardwareOperationEligibility eligibility = HardwareOperationEligibilityEvaluator.ForCalibration(
            capability,
            payload.ConfirmExperimental,
            payload.ConfirmDevice);
        if (!eligibility.Eligible)
        {
            return Failure(request, "OPERATION_NOT_ELIGIBLE", eligibility.Reason);
        }
        if (snapshot.Sensors.All(item => item.SensorId != session.RpmSensorId))
        {
            return Failure(request, "RPM_SENSOR_MISSING", "The paired RPM sensor is no longer available for this exact controller.");
        }

        IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
        try
        {
            _ = await FanCommissioningWorkflow.PrepareIdentificationPulseAsync(
                capability,
                adapter,
                cancellationToken).ConfigureAwait(false);
            AdapterHostDiagnosticsV1? diagnostics = await TryGetAdapterDiagnosticsAsync(adapter).ConfigureAwait(false);
            FanCommissioningSessionV1 updated = session with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = null,
                Notes = AppendCommissioningNote(
                    session.Notes,
                    "No-write software-control preflight passed. No pulse, apply, verify, rollback, or reset was issued.")
            };
            await _store.SaveSuiteEntityAsync(
                SuiteEntityKind.FanCommissioningSession,
                updated.Id,
                updated,
                CancellationToken.None).ConfigureAwait(false);
            IncrementSuiteRevision();
            return Success(request, new FanCommissioningPreflightResultV1(
                FanCommissioningPreflightResultV1.CurrentSchemaVersion,
                updated,
                Prepared: true,
                ApplyIssued: false,
                RollbackIssued: false,
                ResetIssued: false,
                DateTimeOffset.UtcNow,
                "PREPARE_SUCCEEDED_NO_WRITE",
                "Adapter Prepare completed. No pulse, apply, verify, rollback, or reset was issued; physical header mapping and calibration remain blocked.",
                diagnostics));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            AdapterHostDiagnosticsV1? diagnostics = await TryGetAdapterDiagnosticsAsync(adapter).ConfigureAwait(false);
            string detail = DescribeNoWritePreflightFailure(diagnostics);
            FanCommissioningSessionV1 blocked = FanCommissioningWorkflow.FailNoWritePreflight(
                session,
                detail,
                DateTimeOffset.UtcNow) with
            {
                Notes = AppendCommissioningNote(
                    session.Notes,
                    "No commissioning pulse was issued because the no-write software-control preflight failed before any physical operation. This session is closed until the execution-context fault is corrected.")
            };
            await _store.SaveSuiteEntityAsync(
                SuiteEntityKind.FanCommissioningSession,
                blocked.Id,
                blocked,
                CancellationToken.None).ConfigureAwait(false);
            IncrementSuiteRevision();
            FanCommissioningPreflightResultV1 result = new(
                FanCommissioningPreflightResultV1.CurrentSchemaVersion,
                blocked,
                Prepared: false,
                ApplyIssued: false,
                RollbackIssued: false,
                ResetIssued: false,
                DateTimeOffset.UtcNow,
                "PREPARE_FAILED_NO_WRITE",
                $"{detail} No pulse, apply, verify, rollback, or reset was issued.",
                diagnostics);
            return FailureWithPayload(
                request,
                "CONTROL_PREPARE_FAILED",
                "The selected controller failed its no-write software-control preflight; no pulse, apply, verify, rollback, or reset was issued.",
                result);
        }
    }

    private static async Task<AdapterHostDiagnosticsV1?> TryGetAdapterDiagnosticsAsync(IHardwareAdapter adapter)
    {
        if (adapter is not IAdapterDiagnosticsProvider diagnosticsProvider)
        {
            return null;
        }

        try
        {
            return await diagnosticsProvider.GetDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DescribeNoWritePreflightFailure(AdapterHostDiagnosticsV1? diagnostics)
    {
        AdapterHostFailureV1? failure = diagnostics?.LastFailure;
        if (failure is null)
        {
            return "The adapter Prepare call failed, but isolated-host diagnostics were unavailable.";
        }

        string errorCode = $"HResult 0x{unchecked((uint)failure.HResult):X8}";
        if (failure.Win32Error is int win32Error)
        {
            errorCode += $", Win32 {win32Error}";
        }
        return $"The adapter Prepare call failed at {failure.Stage} ({failure.ExceptionType}; {errorCode}).";
    }

    private static string AppendCommissioningNote(string? existing, string note) =>
        string.Join(" ", new[] { existing, note }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private async Task<IpcResponse> PulseFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "Hardware operations are blocked until pending recovery succeeds.");
        }

        EnsureExpectedRevision(request);
        PulseFanCommissioningRequest payload = IpcJson.FromElement<PulseFanCommissioningRequest>(request.Payload)
            ?? throw new InvalidDataException("PulseFanCommissioning requires a PulseFanCommissioningRequest payload.");
        if (payload.Duration < TimeSpan.FromSeconds(2) || payload.Duration > TimeSpan.FromSeconds(5))
        {
            return Failure(request, "INVALID_PULSE_DURATION", "Header-identification pulses must last from 2 to 5 seconds.");
        }

        FanCommissioningSessionV1? session = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            payload.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Failure(request, "COMMISSIONING_NOT_FOUND", "The requested fan commissioning session does not exist.");
        }
        if (session.State != FanCommissioningState.AwaitingIdentification)
        {
            return Failure(request, "COMMISSIONING_STATE_INVALID", "Only an awaiting-identification session can issue an identification pulse.");
        }
        if (!FanCommissioningWorkflow.CanIssueIdentificationPulse(session, out string? pulseSafetyReason))
        {
            return Failure(request, "PULSE_TARGET_PROTECTED", pulseSafetyReason!);
        }

        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item => item.Id == session.CapabilityId)
            ?? throw new InvalidOperationException("The commissioned cooling control is no longer available.");
        if (await IsProtectedCoolingOutputAsync(capability, cancellationToken).ConfigureAwait(false))
        {
            return Failure(
                request,
                "PULSE_TARGET_PROTECTED",
                "The selected output is persistently classified as a CPU fan or pump, so an identification pulse is blocked.");
        }
        HardwareOperationEligibility eligibility = HardwareOperationEligibilityEvaluator.ForCalibration(
            capability,
            payload.ConfirmExperimental,
            payload.ConfirmDevice);
        if (!eligibility.Eligible)
        {
            return Failure(request, "OPERATION_NOT_ELIGIBLE", eligibility.Reason);
        }
        if (snapshot.Sensors.All(item => item.SensorId != session.RpmSensorId))
        {
            return Failure(request, "RPM_SENSOR_MISSING", "The paired RPM sensor is no longer available for this exact controller.");
        }

        // Prepare reads the bounded range and current controller state without
        // applying a value. Check it before reserving a physical operation so
        // an inaccessible low-level backend cannot reach reset/apply paths.
        IHardwareAdapter preflightAdapter = FindAdapter(capability.AdapterId);
        try
        {
            _ = await FanCommissioningWorkflow.PrepareIdentificationPulseAsync(
                capability,
                preflightAdapter,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterHostDiagnosticsV1? diagnostics = await TryGetAdapterDiagnosticsAsync(preflightAdapter).ConfigureAwait(false);
            string detail = DescribeNoWritePreflightFailure(diagnostics);
            FanCommissioningSessionV1 blocked = FanCommissioningWorkflow.FailNoWritePreflight(
                session,
                detail,
                DateTimeOffset.UtcNow) with
            {
                Notes = string.Join(" ", new[]
                {
                    session.Notes,
                    "No commissioning pulse was issued because the controller failed the read-only software-control preflight. This session is closed until the execution-context fault is corrected."
                }.Where(value => !string.IsNullOrWhiteSpace(value)))
            };
            await _store!.SaveSuiteEntityAsync(
                SuiteEntityKind.FanCommissioningSession,
                blocked.Id,
                blocked,
                CancellationToken.None).ConfigureAwait(false);
            IncrementSuiteRevision();
            return FailureWithPayload(
                request,
                "CONTROL_PREPARE_FAILED",
                "The selected controller failed its read-only software-control preflight; no pulse, apply, or reset was issued.",
                blocked);
        }

        HardwareOperationStatus status = CreateOperationStatus(
            HardwareOperationKind.CommissioningPulse,
            capability,
            "Header-identification pulse is queued; no write has occurred yet.");
        if (!TryReserveOperation(status, out CancellationTokenSource? operationCancellation))
        {
            return Failure(request, "OPERATION_ACTIVE", "Another calibration, tuning, or identification operation is already active.");
        }

        try
        {
            await _store.SaveOperationAsync(status, cancellationToken).ConfigureAwait(false);
            Task task = RunCommissioningPulseOperationAsync(
                status.Id,
                session,
                capability,
                payload.Duration,
                operationCancellation!);
            RegisterOperationTask(status.Id, task);
            return Success(request, status);
        }
        catch
        {
            ReleaseOperationReservation(status.Id);
            throw;
        }
    }

    private async Task<IpcResponse> ObserveFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        FanCommissioningSessionRequest payload = IpcJson.FromElement<FanCommissioningSessionRequest>(request.Payload)
            ?? throw new InvalidDataException("ObserveFanCommissioning requires a FanCommissioningSessionRequest payload.");
        FanCommissioningSessionV1? session = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            payload.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Failure(request, "COMMISSIONING_NOT_FOUND", "The requested fan commissioning session does not exist.");
        }

        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor? capability = snapshot.Capabilities.FirstOrDefault(item => item.Id == session.CapabilityId);
        SensorSample? rpm = snapshot.Sensors.FirstOrDefault(item => item.SensorId == session.RpmSensorId);
        if (capability is null)
        {
            HardwareOperationStatus? missingOperation = GetOperationStatus();
            if (missingOperation?.CapabilityId != session.CapabilityId)
            {
                missingOperation = null;
            }

            return Success(request, new FanCommissioningObservationV1(
                session,
                rpm,
                [],
                missingOperation,
                "The controller is absent from the current snapshot, so this observation contains no device thermal context. The persisted operation result remains authoritative."));
        }
        SensorSample[] thermal = snapshot.Sensors.Where(item =>
                item.AdapterId == capability.AdapterId
                && item.DeviceId == capability.DeviceId
                && string.Equals(item.Unit, "°C", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        HardwareOperationStatus? operation = GetOperationStatus();
        if (operation?.CapabilityId != session.CapabilityId)
        {
            operation = null;
        }
        string guidance = session.State switch
        {
            FanCommissioningState.AwaitingIdentification => "Run the existing bounded calibration only after visually confirming this header changes the intended fan.",
            FanCommissioningState.ReadyForCalibration => "Header confirmed. Use the calibration controls below; the service restores the prior policy after completion or cancellation.",
            FanCommissioningState.Completed => "Commissioned calibration evidence is stored. Keep the control Experimental until broader hardware qualification passes.",
            FanCommissioningState.Failed => "The no-write controller preflight failed before any fan command. This session is closed; create a new one only after the adapter execution-context fault is corrected.",
            FanCommissioningState.RecoveryRequired => "A prior hardware recovery requires attention before further writes.",
            _ => "Review the attached operation state before continuing."
        };
        return Success(request, new FanCommissioningObservationV1(session, rpm, thermal, operation, guidance));
    }

    private async Task<IpcResponse> ConfirmFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        ConfirmFanCommissioningRequest payload = IpcJson.FromElement<ConfirmFanCommissioningRequest>(request.Payload)
            ?? throw new InvalidDataException("ConfirmFanCommissioning requires a ConfirmFanCommissioningRequest payload.");
        FanCommissioningSessionV1? session = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            payload.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Failure(request, "COMMISSIONING_NOT_FOUND", "The requested fan commissioning session does not exist.");
        }
        if (session.State != FanCommissioningState.AwaitingIdentification
            || string.IsNullOrWhiteSpace(payload.HeaderName)
            || payload.HeaderName.Trim().Length > 80)
        {
            return Failure(request, "COMMISSIONING_STATE_INVALID", "Only an awaiting-identification session can be confirmed with a valid header name.");
        }

        FanCommissioningSessionV1 updated = FanCommissioningWorkflow.Confirm(
            session,
            payload.HeaderConfirmed,
            payload.PhysicalHeaderObserved,
            payload.HeaderName,
            payload.Notes,
            DateTimeOffset.UtcNow);
        await _store.SaveSuiteEntityAsync(SuiteEntityKind.FanCommissioningSession, updated.Id, updated, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, updated);
    }

    private async Task<IpcResponse> CompleteFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        FanCommissioningSessionRequest payload = IpcJson.FromElement<FanCommissioningSessionRequest>(request.Payload)
            ?? throw new InvalidDataException("CompleteFanCommissioning requires a FanCommissioningSessionRequest payload.");
        FanCommissioningSessionV1? session = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            payload.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Failure(request, "COMMISSIONING_NOT_FOUND", "The requested fan commissioning session does not exist.");
        }
        if (!session.HeaderConfirmed
            || !session.PhysicalHeaderObserved
            || session.State != FanCommissioningState.ReadyForCalibration)
        {
            return Failure(request, "COMMISSIONING_STATE_INVALID", "Visually observe and confirm the physical header before completing commissioning.");
        }

        HardwareOperationStatus? operation = GetOperationStatus();
        if (operation?.CapabilityId == session.CapabilityId && operation.State == HardwareOperationState.RecoveryRequired)
        {
            FanCommissioningSessionV1 recovery = session with
            {
                State = FanCommissioningState.RecoveryRequired,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = operation.Error ?? "Calibration recovery is required."
            };
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.FanCommissioningSession, recovery.Id, recovery, cancellationToken).ConfigureAwait(false);
            IncrementSuiteRevision();
            return FailureWithPayload(request, "RECOVERY_REQUIRED", recovery.Error!, recovery);
        }
        if (operation?.CapabilityId != session.CapabilityId
            || operation.State != HardwareOperationState.Completed
            || operation.CalibrationResult is null)
        {
            return Failure(request, "CALIBRATION_NOT_COMPLETE", "Complete the bounded calibration for this confirmed header before finalising commissioning.");
        }
        if (!FanCalibrationPolicy.SupportsNonZeroCurve(operation.CalibrationResult))
        {
            return Failure(
                request,
                "NONZERO_FLOOR_NOT_VERIFIED",
                "The calibration did not prove a stable non-zero operating floor. Keep firmware/default control; no curve or commissioned profile can be enabled.");
        }

        FanCalibrationV2 calibration = ToCalibrationV2(operation.CalibrationResult, session.Id);
        await _store.SaveSuiteEntityAsync(SuiteEntityKind.FanCalibration, calibration.CapabilityId, calibration, cancellationToken).ConfigureAwait(false);
        FanCommissioningSessionV1 completed = FanCommissioningWorkflow.Complete(
            session,
            calibration.CapabilityId,
            DateTimeOffset.UtcNow);
        await _store.SaveSuiteEntityAsync(SuiteEntityKind.FanCommissioningSession, completed.Id, completed, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, completed);
    }

    private async Task<IpcResponse> CancelFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        FanCommissioningSessionRequest payload = IpcJson.FromElement<FanCommissioningSessionRequest>(request.Payload)
            ?? throw new InvalidDataException("CancelFanCommissioning requires a FanCommissioningSessionRequest payload.");
        FanCommissioningSessionV1? session = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            payload.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Failure(request, "COMMISSIONING_NOT_FOUND", "The requested fan commissioning session does not exist.");
        }
        HardwareOperationStatus? operation = GetOperationStatus();
        if (operation?.CapabilityId == session.CapabilityId && IsActive(operation.State))
        {
            return Failure(request, "CALIBRATION_ACTIVE", "Abort the active calibration first; cancelling commissioning never interrupts a hardware operation silently.");
        }

        FanCommissioningSessionV1 cancelled = FanCommissioningWorkflow.Cancel(session, DateTimeOffset.UtcNow);
        await _store.SaveSuiteEntityAsync(SuiteEntityKind.FanCommissioningSession, cancelled.Id, cancelled, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, cancelled);
    }

    private async Task<IpcResponse> RecoverFanCommissioningAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        FanCommissioningSessionRequest payload = IpcJson.FromElement<FanCommissioningSessionRequest>(request.Payload)
            ?? throw new InvalidDataException("RecoverFanCommissioning requires a FanCommissioningSessionRequest payload.");
        FanCommissioningSessionV1? session = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            payload.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Failure(request, "COMMISSIONING_NOT_FOUND", "The requested fan commissioning session does not exist.");
        }
        if (session.State != FanCommissioningState.RecoveryRequired)
        {
            return Failure(request, "COMMISSIONING_STATE_INVALID", "Only a session requiring recovery can request a firmware/default reset.");
        }
        if (HasActiveOperation())
        {
            return Failure(request, "OPERATION_ACTIVE", "Abort and finish the active hardware operation before recovery.");
        }

        CapabilityDescriptor capability = GetSnapshot().Capabilities.FirstOrDefault(item => item.Id == session.CapabilityId)
            ?? throw new InvalidOperationException("The commissioned cooling control is no longer available.");
        if (!capability.CanResetToDefault)
        {
            return Failure(request, "RESET_NOT_AVAILABLE", "The controller has no declared firmware/default reset endpoint.");
        }
        try
        {
            IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
            await adapter.ResetToDefaultAsync(capability.Id, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(persistSensors: false, cancellationToken).ConfigureAwait(false);
            FanCommissioningSessionV1 recovered = session with
            {
                State = FanCommissioningState.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = null,
                Notes = string.Join(" ", new[] { session.Notes, "Firmware/default recovery completed." }.Where(value => !string.IsNullOrWhiteSpace(value)))
            };
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.FanCommissioningSession, recovered.Id, recovered, cancellationToken).ConfigureAwait(false);
            IncrementSuiteRevision();
            return Success(request, recovered);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FanCommissioningSessionV1 failed = session with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = exception.Message
            };
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.FanCommissioningSession, failed.Id, failed, CancellationToken.None).ConfigureAwait(false);
            IncrementSuiteRevision();
            return FailureWithPayload(request, "RECOVERY_FAILED", exception.Message, failed);
        }
    }

    private async Task<IpcResponse> StartTuneAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "Hardware operations are blocked until pending recovery succeeds.");
        }

        EnsureExpectedRevision(request);
        StartTuneRequest payload = IpcJson.FromElement<StartTuneRequest>(request.Payload)
            ?? throw new InvalidDataException("StartTune requires a StartTuneRequest payload.");
        if (payload.Plan.ScreeningDuration < TimeSpan.FromMinutes(10)
            || payload.Plan.ScreeningDuration > TimeSpan.FromHours(1))
        {
            return Failure(request, "INVALID_SCREENING_TIME", "Final screening must run for at least 10 minutes and at most 1 hour.");
        }

        TimeSpan candidateTime = payload.CandidateScreeningTime ?? TimeSpan.FromSeconds(30);
        if (candidateTime < TimeSpan.FromSeconds(5) || candidateTime > TimeSpan.FromMinutes(5))
        {
            return Failure(request, "INVALID_CANDIDATE_TIME", "Each candidate screening must run for 5 seconds to 5 minutes.");
        }

        if (!double.IsFinite(payload.Plan.TemperatureCeilingCelsius)
            || payload.Plan.TemperatureCeilingCelsius is < 40 or > 100)
        {
            return Failure(request, "INVALID_TEMPERATURE_CEILING", "Temperature ceiling must be between 40 °C and 100 °C.");
        }

        if (payload.Plan.PowerCeilingWatts is double powerCeiling
            && (!double.IsFinite(powerCeiling) || powerCeiling <= 0))
        {
            return Failure(request, "INVALID_POWER_CEILING", "Power ceiling must be a positive finite value.");
        }

        if (payload.Plan.Bounds.Count != 1
            || !payload.Plan.Bounds.ContainsKey(payload.CapabilityId))
        {
            return Failure(request, "MULTI_DOMAIN_TUNE_FORBIDDEN", "A tuning operation must target exactly one bounded capability.");
        }

        if (payload.RefinementCandidates is < 0 or > 20)
        {
            return Failure(request, "INVALID_REFINEMENT", "Refinement candidate count must be between 0 and 20.");
        }

        if (!double.IsFinite(payload.SafetyMargin) || payload.SafetyMargin < 0)
        {
            return Failure(request, "INVALID_SAFETY_MARGIN", "The safety margin must be a non-negative finite value.");
        }

        if (!double.IsFinite(payload.ThermalHeadroomCelsius) || payload.ThermalHeadroomCelsius is < 0 or > 40)
        {
            return Failure(request, "INVALID_THERMAL_HEADROOM", "The thermal headroom must be between 0 and 40 °C.");
        }

        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(
            item => string.Equals(item.Id, payload.CapabilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The requested tuning capability was not discovered.");
        if (await IsProtectedCoolingOutputAsync(capability, cancellationToken).ConfigureAwait(false))
        {
            return Failure(
                request,
                "PROTECTED_COOLING_OUTPUT",
                "Automatic tuning is unavailable for an output persistently classified as a CPU fan or pump.");
        }
        HardwareOperationEligibility eligibility = HardwareOperationEligibilityEvaluator.ForTuning(
            capability,
            payload.Plan,
            payload.ConfirmExperimental,
            payload.ConfirmDevice);
        if (!eligibility.Eligible)
        {
            return Failure(request, "OPERATION_NOT_ELIGIBLE", eligibility.Reason);
        }

        payload = payload with { CandidateScreeningTime = candidateTime };
        HardwareOperationStatus status = CreateOperationStatus(
            HardwareOperationKind.Tuning,
            capability,
            "Tuning is queued; no candidate has been applied yet.");
        if (!TryReserveOperation(status, out CancellationTokenSource? operationCancellation))
        {
            return Failure(request, "OPERATION_ACTIVE", "Another calibration or tuning operation is already active.");
        }

        try
        {
            await _store!.SaveOperationAsync(status, cancellationToken).ConfigureAwait(false);
            Task task = RunTuneOperationAsync(status.Id, payload, capability, operationCancellation!);
            RegisterOperationTask(status.Id, task);
            return Success(request, status);
        }
        catch
        {
            ReleaseOperationReservation(status.Id);
            throw;
        }
    }

    private async Task<IpcResponse> StartAutoOcAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "Auto OC is blocked until pending hardware recovery succeeds.");
        }

        EnsureExpectedRevision(request);
        StartAutoOcV2Request payload = IpcJson.FromElement<StartAutoOcV2Request>(request.Payload)
            ?? throw new InvalidDataException("StartAutoOc requires a StartAutoOcV2Request payload.");
        if (payload.SchemaVersion != StartAutoOcV2Request.CurrentSchemaVersion
            || !payload.ConfirmExperimental
            || !payload.ConfirmDevice
            || !string.Equals(payload.DeviceId, payload.WorkloadHost.TargetDeviceId, StringComparison.Ordinal))
        {
            return Failure(request, "AUTO_OC_NOT_CONFIRMED", "Auto OC requires Experimental and exact-device confirmation for the workload and both clock controls.");
        }

        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor? core = snapshot.Capabilities.FirstOrDefault(capability =>
            string.Equals(capability.Id, payload.CoreCapabilityId, StringComparison.Ordinal));
        CapabilityDescriptor? memory = snapshot.Capabilities.FirstOrDefault(capability =>
            string.Equals(capability.Id, payload.MemoryCapabilityId, StringComparison.Ordinal));
        if (core is null
            || memory is null
            || !core.Id.StartsWith("gpuclock.core:", StringComparison.Ordinal)
            || !memory.Id.StartsWith("gpuclock.memory:", StringComparison.Ordinal)
            || !string.Equals(core.DeviceId, payload.DeviceId, StringComparison.Ordinal)
            || !string.Equals(memory.DeviceId, payload.DeviceId, StringComparison.Ordinal)
            || core.Range is null
            || memory.Range is null)
        {
            return Failure(request, "AUTO_OC_TARGET_INVALID", "Auto OC requires the exact bounded core and memory clock controls on one GPU.");
        }

        StartTuneRequest coreRequest = CreateAutoOcTuneRequest(core, safetyMargin: 15);
        StartTuneRequest memoryRequest = CreateAutoOcTuneRequest(memory, safetyMargin: 100);
        HardwareOperationEligibility coreEligibility = HardwareOperationEligibilityEvaluator.ForTuning(
            core, coreRequest.Plan, payload.ConfirmExperimental, payload.ConfirmDevice);
        HardwareOperationEligibility memoryEligibility = HardwareOperationEligibilityEvaluator.ForTuning(
            memory, memoryRequest.Plan, payload.ConfirmExperimental, payload.ConfirmDevice);
        if (!coreEligibility.Eligible || !memoryEligibility.Eligible)
        {
            return Failure(
                request,
                "AUTO_OC_NOT_ELIGIBLE",
                !coreEligibility.Eligible ? coreEligibility.Reason : memoryEligibility.Reason);
        }

        TuneSensorBindingV2 binding;
        WorkloadHostController workload;
        try
        {
            binding = GpuTuneSensorBindingResolver.Resolve(snapshot, payload.DeviceId);
            workload = new WorkloadHostController(payload.WorkloadHost);
            WorkloadHostStatusV1 ready = await workload.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Ready || ready.Running || ready.Mode != AutoOcWorkloadMode.Stopped)
            {
                return Failure(request, "WORKLOAD_HOST_NOT_READY", ready.Error ?? "The workload host did not start in a clean stopped state.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(request, "WORKLOAD_HOST_NOT_READY", exception.Message);
        }

        HardwareOperationStatus status = CreateOperationStatus(
            HardwareOperationKind.AutoOc,
            core,
            "Full Auto OC is queued; the workload is stopped and no offset has been applied.");
        if (!TryReserveOperation(status, out CancellationTokenSource? operationCancellation))
        {
            return Failure(request, "OPERATION_ACTIVE", "Another calibration or tuning operation is already active.");
        }

        try
        {
            await _store!.SaveOperationAsync(status, cancellationToken).ConfigureAwait(false);
            Task task = RunAutoOcOperationAsync(
                status.Id,
                payload.DeviceId,
                coreRequest,
                core,
                memoryRequest,
                memory,
                binding,
                workload,
                operationCancellation!);
            RegisterOperationTask(status.Id, task);
            return Success(request, status);
        }
        catch
        {
            ReleaseOperationReservation(status.Id);
            try { await workload.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private async Task<IpcResponse> StartAutoOcV3Async(IpcRequest request, CancellationToken cancellationToken)
    {
        if (_rollbackBlocked)
        {
            return Failure(request, "ROLLBACK_BLOCKED", "Auto OC V3 is blocked until pending hardware recovery succeeds.");
        }

        EnsureExpectedRevision(request);
        StartAutoOcV3Request payload = IpcJson.FromElement<StartAutoOcV3Request>(request.Payload)
            ?? throw new InvalidDataException("StartAutoOcV3 requires a StartAutoOcV3Request payload.");
        string? constraintError = AutoOcV3Policy.Validate(payload.Constraints);
        if (payload.SchemaVersion != StartAutoOcV3Request.CurrentSchemaVersion
            || !payload.ConfirmExperimental
            || !payload.ConfirmDevice
            || !string.Equals(payload.DeviceId, payload.WorkloadHost.TargetDeviceId, StringComparison.Ordinal))
        {
            return Failure(request, "AUTO_OC_V3_NOT_CONFIRMED", "Auto OC V3 requires feature negotiation plus Experimental and exact-device confirmation.");
        }
        if (constraintError is not null)
        {
            return Failure(request, "AUTO_OC_V3_CONSTRAINT_INVALID", constraintError);
        }

        HardwareSnapshot snapshot = GetSnapshot();
        CapabilityDescriptor? core = snapshot.Capabilities.FirstOrDefault(capability =>
            string.Equals(capability.Id, payload.CoreCapabilityId, StringComparison.Ordinal));
        CapabilityDescriptor? memory = snapshot.Capabilities.FirstOrDefault(capability =>
            string.Equals(capability.Id, payload.MemoryCapabilityId, StringComparison.Ordinal));
        CapabilityDescriptor? power = string.IsNullOrWhiteSpace(payload.PowerLimitCapabilityId)
            ? null
            : snapshot.Capabilities.FirstOrDefault(capability =>
                string.Equals(capability.Id, payload.PowerLimitCapabilityId, StringComparison.Ordinal));
        if (core is null
            || memory is null
            || !core.Id.StartsWith("gpuclock.core:", StringComparison.Ordinal)
            || !memory.Id.StartsWith("gpuclock.memory:", StringComparison.Ordinal)
            || !string.Equals(core.DeviceId, payload.DeviceId, StringComparison.Ordinal)
            || !string.Equals(memory.DeviceId, payload.DeviceId, StringComparison.Ordinal)
            || core.Range is null
            || memory.Range is null
            || (payload.PowerLimitCapabilityId is not null
                && (power is null
                    || !power.Id.StartsWith("gpupower.limit:", StringComparison.Ordinal)
                    || !string.Equals(power.DeviceId, payload.DeviceId, StringComparison.Ordinal)
                    || power.Range?.Default is null)))
        {
            return Failure(request, "AUTO_OC_V3_TARGET_INVALID", "Auto OC V3 requires exact bounded core, memory, and optional power-limit controls on one GPU.");
        }

        StartTuneRequest coreRequest = CreateAutoOcTuneRequest(core, safetyMargin: 15, payload.Constraints);
        StartTuneRequest memoryRequest = CreateAutoOcTuneRequest(memory, safetyMargin: 100, payload.Constraints);
        StartTuneRequest? powerRequest = power is null ? null : CreateAutoOcPowerRequest(power, payload.Constraints);
        AutoOcTuneStage[] stages = power is null
            ? [new(coreRequest, core, FindAdapter(core.AdapterId)), new(memoryRequest, memory, FindAdapter(memory.AdapterId))]
            : [new(coreRequest, core, FindAdapter(core.AdapterId)), new(memoryRequest, memory, FindAdapter(memory.AdapterId)), new(powerRequest!, power, FindAdapter(power.AdapterId))];
        foreach (AutoOcTuneStage stage in stages)
        {
            HardwareOperationEligibility eligibility = HardwareOperationEligibilityEvaluator.ForTuning(
                stage.Capability,
                stage.Request.Plan,
                payload.ConfirmExperimental,
                payload.ConfirmDevice);
            if (!eligibility.Eligible)
            {
                return Failure(request, "AUTO_OC_V3_NOT_ELIGIBLE", eligibility.Reason);
            }
        }

        TuneSensorBindingV2 binding;
        HardwareFingerprintV1 fingerprint;
        WorkloadHostController workload;
        try
        {
            binding = GpuTuneSensorBindingResolver.Resolve(snapshot, payload.DeviceId);
            if (!HardwareFingerprintBuilder.TryCreate(
                    snapshot,
                    payload.DeviceId,
                    binding.BoundDeviceIds,
                    out HardwareFingerprintV1? capturedFingerprint,
                    out string fingerprintReason))
            {
                return Failure(request, "AUTO_OC_V3_FINGERPRINT_INCOMPLETE", fingerprintReason);
            }
            fingerprint = capturedFingerprint!;
            workload = new WorkloadHostController(payload.WorkloadHost);
            WorkloadHostStatusV1 ready = await workload.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!ready.Ready || ready.Running || ready.Mode != AutoOcWorkloadMode.Stopped)
            {
                return Failure(request, "WORKLOAD_HOST_NOT_READY", ready.Error ?? "The workload host did not start in a clean stopped state.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(request, "WORKLOAD_HOST_NOT_READY", exception.Message);
        }

        HardwareOperationStatus status = CreateOperationStatus(
            HardwareOperationKind.AutoOc,
            core,
            $"Auto OC V3 ({payload.Constraints.Objective}) is queued; no candidate has been applied.");
        if (!TryReserveOperation(status, out CancellationTokenSource? operationCancellation))
        {
            return Failure(request, "OPERATION_ACTIVE", "Another calibration or tuning operation is already active.");
        }

        try
        {
            await _store!.SaveOperationAsync(status, cancellationToken).ConfigureAwait(false);
            Task task = RunAutoOcV3OperationAsync(
                status.Id,
                payload.DeviceId,
                payload.Constraints,
                fingerprint,
                new AutoOcTuneStage(coreRequest, core, FindAdapter(core.AdapterId)),
                new AutoOcTuneStage(memoryRequest, memory, FindAdapter(memory.AdapterId)),
                power is null ? null : new AutoOcTuneStage(powerRequest!, power, FindAdapter(power.AdapterId)),
                binding,
                workload,
                operationCancellation!);
            RegisterOperationTask(status.Id, task);
            return Success(request, status);
        }
        catch
        {
            ReleaseOperationReservation(status.Id);
            try { await workload.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static StartTuneRequest CreateAutoOcTuneRequest(CapabilityDescriptor capability, double safetyMargin)
    {
        NumericRange range = capability.Range
            ?? throw new InvalidOperationException("Auto OC target has no numeric bounds.");
        TunePlan plan = new(
            Guid.NewGuid().ToString("N"),
            capability.DeviceId,
            TuningObjective.Performance,
            new Dictionary<string, TuneBounds>(StringComparer.Ordinal)
            {
                [capability.Id] = new TuneBounds(Math.Max(0, range.Minimum), range.Maximum, range.Step)
            },
            TimeSpan.FromMinutes(10),
            TemperatureCeilingCelsius: 83,
            PowerCeilingWatts: null,
            Provisional: true,
            SoakStartedAt: null,
            ActiveUseRequired: TimeSpan.FromHours(10),
            ColdBootsRequired: 3);
        return new StartTuneRequest(
            plan,
            capability.Id,
            TuneDirection.Maximize,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            CandidateScreeningTime: TimeSpan.FromSeconds(30),
            MaximumCandidates: 12,
            RefinementCandidates: 5,
            SafetyMargin: safetyMargin,
            ThermalHeadroomCelsius: 4);
    }

    private static StartTuneRequest CreateAutoOcTuneRequest(
        CapabilityDescriptor capability,
        double safetyMargin,
        AutoOcObjectiveConstraintsV3 constraints)
    {
        StartTuneRequest request = CreateAutoOcTuneRequest(capability, safetyMargin);
        return request with
        {
            Plan = request.Plan with
            {
                Objective = constraints.Objective,
                TemperatureCeilingCelsius = constraints.TemperatureCeilingCelsius,
                PowerCeilingWatts = constraints.PowerCeilingWatts
            },
            CandidateScreeningTime = constraints.CandidateScreeningDuration ?? TimeSpan.FromSeconds(30)
        };
    }

    private static StartTuneRequest CreateAutoOcPowerRequest(
        CapabilityDescriptor capability,
        AutoOcObjectiveConstraintsV3 constraints)
    {
        NumericRange range = capability.Range
            ?? throw new InvalidOperationException("Auto OC power target has no numeric bounds.");
        double stock = range.Default
            ?? throw new InvalidOperationException("Auto OC V3 refuses a power-limit control without a controller-reported stock value.");
        bool maximize = constraints.Objective == TuningObjective.Performance;
        TunePlan plan = new(
            Guid.NewGuid().ToString("N"),
            capability.DeviceId,
            constraints.Objective,
            new Dictionary<string, TuneBounds>(StringComparer.Ordinal)
            {
                [capability.Id] = maximize
                    ? new TuneBounds(stock, range.Maximum, range.Step)
                    : new TuneBounds(range.Minimum, stock, range.Step)
            },
            constraints.CandidateScreeningDuration ?? TimeSpan.FromSeconds(30),
            constraints.TemperatureCeilingCelsius,
            constraints.PowerCeilingWatts,
            Provisional: true,
            SoakStartedAt: null,
            ActiveUseRequired: TimeSpan.FromHours(10),
            ColdBootsRequired: 3);
        return new StartTuneRequest(
            plan,
            capability.Id,
            maximize ? TuneDirection.Maximize : TuneDirection.Minimize,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            CandidateScreeningTime: constraints.CandidateScreeningDuration ?? TimeSpan.FromSeconds(30),
            MaximumCandidates: 12,
            RefinementCandidates: 3,
            SafetyMargin: 0,
            ThermalHeadroomCelsius: 4);
    }

    private IpcResponse AbortOperation(IpcRequest request)
    {
        EnsureExpectedRevision(request);
        HardwareOperationStatus? status;
        lock (_operationSync)
        {
            status = _operationStatus;
            if (status is null || !IsActive(status.State) || _operationCancellation is null)
            {
                return Failure(request, "NO_ACTIVE_OPERATION", "No calibration or tuning operation is active.");
            }

            _operationCancellation.Cancel();
            _operationStatus = status = status with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "Cancellation requested; the service is restoring the prior control state."
            };
        }

        return Success(request, status);
    }

    private async Task RunCalibrationOperationAsync(
        string operationId,
        StartCalibrationRequest request,
        CapabilityDescriptor capability,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            await TransitionOperationAsync(
                operationId,
                HardwareOperationState.Running,
                "Calibration started at 100% duty.").ConfigureAwait(false);
            IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
            FanCalibrationEngine engine = new();
            FanCalibrationResult result = await engine.RunAsync(
                request,
                capability,
                adapter,
                (progress, message) => ReportOperationProgress(operationId, HardwareOperationState.Running, progress, message),
                operationCancellation.Token).ConfigureAwait(false);
            await _store!.SaveSuiteEntityAsync(
                SuiteEntityKind.FanCalibration,
                result.CapabilityId,
                ToCalibrationV2(result, request.CommissioningSessionId),
                CancellationToken.None).ConfigureAwait(false);
            await CompleteOperationAsync(
                operationId,
                "Calibration completed and the prior control policy was restored.",
                calibrationResult: result,
                tuneResult: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            await AbortOperationAsync(operationId).ConfigureAwait(false);
        }
        catch (HardwareOperationRecoveryException exception)
        {
            _rollbackBlocked = true;
            await FailOperationAsync(operationId, exception, recoveryRequired: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailOperationAsync(operationId, exception, recoveryRequired: false).ConfigureAwait(false);
        }
        finally
        {
            await FinishOperationTaskAsync(operationId, operationCancellation).ConfigureAwait(false);
        }
    }

    private async Task RunCommissioningPulseOperationAsync(
        string operationId,
        FanCommissioningSessionV1 session,
        CapabilityDescriptor capability,
        TimeSpan duration,
        CancellationTokenSource operationCancellation)
    {
        IHardwareAdapter? adapter = null;
        Exception? failure = null;
        bool writeAttempted = false;
        bool defaultReset = false;
        double duty = FanCommissioningWorkflow.GetIdentificationPulseDuty(capability.Range!);
        try
        {
            await TransitionOperationAsync(
                operationId,
                HardwareOperationState.Running,
                $"Applying a {duty:0}% identification pulse. Watch only the intended physical fan.").ConfigureAwait(false);
            adapter = FindAdapter(capability.AdapterId);
            ProfileAction action = new(
                $"commission-pulse:{Guid.NewGuid():N}",
                capability.AdapterId,
                capability.Id,
                ControlValue.FromNumeric(duty),
                Required: true,
                Order: 0);
            PreparedAction prepared = await adapter.PrepareAsync(action, operationCancellation.Token).ConfigureAwait(false);
            writeAttempted = true;
            await adapter.ApplyAsync(prepared, operationCancellation.Token).ConfigureAwait(false);
            ActionVerification verification = await adapter.VerifyAsync(prepared, operationCancellation.Token).ConfigureAwait(false);
            if (!verification.Success)
            {
                throw new InvalidOperationException($"The identification pulse could not be read back: {verification.Message}");
            }

            ReportOperationProgress(
                operationId,
                HardwareOperationState.Running,
                60,
                $"Pulse verified at {duty:0}%. Maintain visual observation for {duration.TotalSeconds:0} seconds.");
            await Task.Delay(duration, operationCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (adapter is not null)
        {
            try
            {
                await adapter.ResetToDefaultAsync(capability.Id, CancellationToken.None).ConfigureAwait(false);
                defaultReset = true;
            }
            catch (Exception resetError)
            {
                string originalFailure = failure?.Message ?? "The pulse did not reach an apply result.";
                failure = new HardwareOperationRecoveryException(
                    "The header pulse could not return this controller to its firmware/default policy. "
                    + $"Reset endpoint: {resetError.Message} Original operation result: {originalFailure}",
                    resetError);
            }
        }

        try
        {
            if (failure is null && defaultReset)
            {
                await RecordCommissioningPulseOutcomeAsync(
                    session.Id,
                    $"Identification pulse verified at {duty:0}% for {duration.TotalSeconds:0} seconds; firmware/default control was restored.",
                    error: null,
                    recoveryRequired: false).ConfigureAwait(false);
                await CompleteOperationAsync(
                    operationId,
                    "Identification pulse completed and firmware/default control was restored. Confirm the physical header before calibration.",
                    calibrationResult: null,
                    tuneResult: null).ConfigureAwait(false);
            }
            else if (failure is OperationCanceledException && operationCancellation.IsCancellationRequested && defaultReset)
            {
                await RecordCommissioningPulseOutcomeAsync(
                    session.Id,
                    "Identification pulse cancelled; firmware/default control was restored.",
                    error: null,
                    recoveryRequired: false).ConfigureAwait(false);
                await AbortOperationAsync(operationId).ConfigureAwait(false);
            }
            else if (writeAttempted && !defaultReset)
            {
                _rollbackBlocked = true;
                await RecordCommissioningPulseOutcomeAsync(
                    session.Id,
                    "Identification pulse recovery failed; controller recovery is required.",
                    failure?.Message ?? "Firmware/default reset did not complete.",
                    recoveryRequired: true).ConfigureAwait(false);
                await FailOperationAsync(
                    operationId,
                    failure ?? new InvalidOperationException("Firmware/default reset did not complete."),
                    recoveryRequired: true,
                    message: "Commissioning pulse recovery failed; further writes are blocked until the controller is recovered.").ConfigureAwait(false);
            }
            else
            {
                await RecordCommissioningPulseOutcomeAsync(
                    session.Id,
                    defaultReset
                        ? "Identification pulse failed before calibration; firmware/default control was restored."
                        : "Identification pulse failed before calibration and the firmware/default reset endpoint did not complete.",
                    failure?.Message,
                    recoveryRequired: false).ConfigureAwait(false);
                await FailOperationAsync(
                    operationId,
                    failure ?? new InvalidOperationException("The identification pulse did not complete."),
                    recoveryRequired: false,
                    message: defaultReset
                        ? "Commissioning pulse failed; firmware/default control was restored."
                        : "Commissioning setup failed before a software-control apply, and the firmware/default reset endpoint did not complete.").ConfigureAwait(false);
            }
        }
        finally
        {
            await FinishOperationTaskAsync(operationId, operationCancellation).ConfigureAwait(false);
        }
    }

    private async Task RecordCommissioningPulseOutcomeAsync(
        string sessionId,
        string note,
        string? error,
        bool recoveryRequired)
    {
        FanCommissioningSessionV1? current = await _store!.GetSuiteEntityAsync<FanCommissioningSessionV1>(
            SuiteEntityKind.FanCommissioningSession,
            sessionId,
            CancellationToken.None).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        FanCommissioningSessionV1 updated = current with
        {
            State = recoveryRequired ? FanCommissioningState.RecoveryRequired : current.State,
            UpdatedAt = DateTimeOffset.UtcNow,
            Notes = string.Join(" ", new[] { current.Notes, note }.Where(value => !string.IsNullOrWhiteSpace(value))),
            Error = error
        };
        await _store.SaveSuiteEntityAsync(
            SuiteEntityKind.FanCommissioningSession,
            updated.Id,
            updated,
            CancellationToken.None).ConfigureAwait(false);
        IncrementSuiteRevision();
    }

    private static FanCalibrationV2 ToCalibrationV2(
        FanCalibrationResult result,
        string? commissioningSessionId = null) => new(
        FanCalibrationV2.CurrentSchemaVersion,
        result.CapabilityId,
        result.RpmSensorId,
        result.Measurements,
        result.MaximumRpm,
        result.StallDutyPercent,
        result.RestartDutyPercent,
        result.MinimumDutyPercent,
        Math.Min(100, result.RestartDutyPercent ?? result.MinimumDutyPercent),
        [],
        DateTimeOffset.UtcNow,
        commissioningSessionId,
        result.EffectiveFloorDutyPercent,
        result.EffectiveFloorRpm,
        result.FirstResponsiveDutyPercent,
        result.NonStopFloorObserved,
        FanCalibrationPolicy.SupportsVerifiedFanStop(result));

    private async Task RunTuneOperationAsync(
        string operationId,
        StartTuneRequest request,
        CapabilityDescriptor capability,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            await TransitionOperationAsync(
                operationId,
                HardwareOperationState.Running,
                "Bounded candidate search started.").ConfigureAwait(false);
            IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
            RuntimeTuneScreeningMonitor monitor = new(GetSnapshot, capability);
            TuneResult result = await HardwareTuneEngine.RunAsync(
                request,
                capability,
                adapter,
                monitor,
                (progress, message) => ReportOperationProgress(
                    operationId,
                    message.Contains("screening", StringComparison.OrdinalIgnoreCase)
                        ? HardwareOperationState.Screening
                        : HardwareOperationState.Running,
                    progress,
                    message),
                operationCancellation.Token).ConfigureAwait(false);
            if (result.GeneratedProfile is ProfileV1 generated)
            {
                await _store!.SaveProfileAsync(generated, CancellationToken.None).ConfigureAwait(false);
            }

            await CompleteOperationAsync(
                operationId,
                result.StatusLabel,
                calibrationResult: null,
                tuneResult: result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            await AbortOperationAsync(operationId).ConfigureAwait(false);
        }
        catch (HardwareOperationRecoveryException exception)
        {
            _rollbackBlocked = true;
            await FailOperationAsync(operationId, exception, recoveryRequired: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailOperationAsync(operationId, exception, recoveryRequired: false).ConfigureAwait(false);
        }
        finally
        {
            await FinishOperationTaskAsync(operationId, operationCancellation).ConfigureAwait(false);
        }
    }

    private async Task RunAutoOcOperationAsync(
        string operationId,
        string deviceId,
        StartTuneRequest coreRequest,
        CapabilityDescriptor coreCapability,
        StartTuneRequest memoryRequest,
        CapabilityDescriptor memoryCapability,
        TuneSensorBindingV2 binding,
        WorkloadHostController workload,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            await TransitionOperationAsync(
                operationId,
                HardwareOperationState.Running,
                "Exact-GPU workload host authenticated; capturing the prior core and memory state.").ConfigureAwait(false);
            IHardwareAdapter coreAdapter = FindAdapter(coreCapability.AdapterId);
            IHardwareAdapter memoryAdapter = FindAdapter(memoryCapability.AdapterId);
            AutoOcResultV2 result = await FullAutoOcEngine.RunAsync(
                deviceId,
                coreRequest,
                coreCapability,
                coreAdapter,
                memoryRequest,
                memoryCapability,
                memoryAdapter,
                TimeSpan.FromMinutes(15),
                mode => new RuntimeTuneScreeningMonitor(
                    GetSnapshot,
                    mode == AutoOcWorkloadMode.Memory ? memoryCapability : coreCapability,
                    sensorBinding: binding,
                    workload: workload,
                    requiredWorkloadMode: mode,
                    requiredAverageLoadPercent: 70),
                workload,
                (progress, message) => ReportOperationProgress(
                    operationId,
                    message.Contains("screen", StringComparison.OrdinalIgnoreCase)
                        ? HardwareOperationState.Screening
                        : HardwareOperationState.Running,
                    progress,
                    message),
                operationCancellation.Token).ConfigureAwait(false);
            if (result.GeneratedProfile is ProfileV2 generated
                && result.AllRequestedFamiliesVerified
                && result.PriorStateRestored
                && result.HardwareStateKnown)
            {
                await _store!.SaveSuiteEntityAsync(
                    SuiteEntityKind.ProfileV2,
                    generated.Id,
                    generated,
                    CancellationToken.None).ConfigureAwait(false);
                IncrementSuiteRevision();
            }

            await CompleteOperationAsync(
                operationId,
                result.Message,
                calibrationResult: null,
                tuneResult: null,
                autoOcResult: result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            await AbortOperationAsync(operationId).ConfigureAwait(false);
        }
        catch (HardwareOperationRecoveryException exception)
        {
            _rollbackBlocked = true;
            await FailOperationAsync(operationId, exception, recoveryRequired: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailOperationAsync(operationId, exception, recoveryRequired: false).ConfigureAwait(false);
        }
        finally
        {
            await FinishOperationTaskAsync(operationId, operationCancellation).ConfigureAwait(false);
        }
    }

    private async Task RunAutoOcV3OperationAsync(
        string operationId,
        string deviceId,
        AutoOcObjectiveConstraintsV3 constraints,
        HardwareFingerprintV1 fingerprint,
        AutoOcTuneStage core,
        AutoOcTuneStage memory,
        AutoOcTuneStage? power,
        TuneSensorBindingV2 binding,
        WorkloadHostController workload,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            await TransitionOperationAsync(
                operationId,
                HardwareOperationState.Running,
                "Exact-GPU workload host authenticated; capturing three stock-state baseline measurements.").ConfigureAwait(false);
            AutoOcResultV3 result = await FullAutoOcV3Engine.RunAsync(
                deviceId,
                constraints,
                fingerprint,
                core,
                memory,
                power,
                mode => new RuntimeTuneScreeningMonitor(
                    GetSnapshot,
                    mode == AutoOcWorkloadMode.Memory ? memory.Capability : core.Capability,
                    sensorBinding: binding,
                    workload: workload,
                    requiredWorkloadMode: mode,
                    requiredAverageLoadPercent: 70),
                workload,
                (progress, message) => ReportOperationProgress(
                    operationId,
                    message.Contains("screen", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("Baseline", StringComparison.OrdinalIgnoreCase)
                            ? HardwareOperationState.Screening
                            : HardwareOperationState.Running,
                    progress,
                    message),
                operationCancellation.Token).ConfigureAwait(false);
            if (result.GeneratedProfile is ProfileV2 generated
                && result.ValidationState == AutoOcValidationState.Provisional
                && result.AllRequestedFamiliesVerified
                && result.RestorationProof is { PriorStateRestored: true, HardwareStateKnown: true })
            {
                await _store!.SaveSuiteEntityAsync(
                    SuiteEntityKind.ProfileV2,
                    generated.Id,
                    generated,
                    CancellationToken.None).ConfigureAwait(false);
                IncrementSuiteRevision();
            }

            await CompleteOperationAsync(
                operationId,
                result.Message,
                calibrationResult: null,
                tuneResult: null,
                autoOcResultV3: result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            await AbortOperationAsync(operationId).ConfigureAwait(false);
        }
        catch (HardwareOperationRecoveryException exception)
        {
            _rollbackBlocked = true;
            await FailOperationAsync(operationId, exception, recoveryRequired: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailOperationAsync(operationId, exception, recoveryRequired: false).ConfigureAwait(false);
        }
        finally
        {
            await FinishOperationTaskAsync(operationId, operationCancellation).ConfigureAwait(false);
        }
    }

    private HardwareOperationStatus? GetOperationStatus()
    {
        lock (_operationSync)
        {
            return _operationStatus;
        }
    }

    private async Task<IpcResponse> GetOperationByIdAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        OperationLookupRequest payload = IpcJson.FromElement<OperationLookupRequest>(request.Payload)
            ?? throw new InvalidDataException("GetOperationById requires an operation identifier.");
        string operationId = payload.OperationId?.Trim() ?? string.Empty;
        if (operationId.Length is 0 or > 128)
        {
            return Failure(request, "INVALID_OPERATION_ID", "Operation identifiers must contain 1 through 128 characters.");
        }

        HardwareOperationStatus? active = GetOperationStatus();
        if (active is not null && string.Equals(active.Id, operationId, StringComparison.Ordinal))
        {
            return Success(request, active);
        }

        HardwareOperationStatus? stored = await _store!.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        return stored is null
            ? Failure(request, "OPERATION_NOT_FOUND", $"No operation with identifier '{operationId}' exists in local state.")
            : Success(request, stored);
    }

    private bool TryReserveOperation(
        HardwareOperationStatus status,
        out CancellationTokenSource? operationCancellation)
    {
        lock (_operationSync)
        {
            if (_operationStatus is not null && IsActive(_operationStatus.State))
            {
                operationCancellation = null;
                return false;
            }

            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _operationStatus = status;
            _operationCancellation = operationCancellation;
            _activeOperationTask = null;
            return true;
        }
    }

    private void RegisterOperationTask(string operationId, Task task)
    {
        lock (_operationSync)
        {
            if (string.Equals(_operationStatus?.Id, operationId, StringComparison.Ordinal))
            {
                _activeOperationTask = task;
            }
        }
    }

    private void ReleaseOperationReservation(string operationId)
    {
        lock (_operationSync)
        {
            if (!string.Equals(_operationStatus?.Id, operationId, StringComparison.Ordinal))
            {
                return;
            }

            _operationCancellation?.Dispose();
            _operationCancellation = null;
            _activeOperationTask = null;
            _operationStatus = null;
        }
    }

    private void ReportOperationProgress(
        string operationId,
        HardwareOperationState state,
        double progress,
        string message)
    {
        lock (_operationSync)
        {
            HardwareOperationStatus? status = _operationStatus;
            if (status is null || !string.Equals(status.Id, operationId, StringComparison.Ordinal))
            {
                return;
            }

            _operationStatus = status with
            {
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                ProgressPercent = Math.Clamp(progress, 0, 100),
                Message = message
            };
        }
    }

    private async Task TransitionOperationAsync(
        string operationId,
        HardwareOperationState state,
        string message)
    {
        HardwareOperationStatus status = UpdateOperation(
            operationId,
            current => current with
            {
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = message
            });
        await _store!.SaveOperationAsync(status, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CompleteOperationAsync(
        string operationId,
        string message,
        FanCalibrationResult? calibrationResult,
        TuneResult? tuneResult,
        AutoOcResultV2? autoOcResult = null,
        AutoOcResultV3? autoOcResultV3 = null)
    {
        HardwareOperationStatus status = UpdateOperation(
            operationId,
            current => current with
            {
                State = HardwareOperationState.Completed,
                UpdatedAt = DateTimeOffset.UtcNow,
                ProgressPercent = 100,
                Message = message,
                CalibrationResult = calibrationResult,
                TuneResult = tuneResult,
                AutoOcResult = autoOcResult,
                AutoOcResultV3 = autoOcResultV3,
                Error = null
            });
        await _store!.SaveOperationAsync(status, CancellationToken.None).ConfigureAwait(false);
        await _store.ClearPendingOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AbortOperationAsync(string operationId)
    {
        HardwareOperationStatus status = UpdateOperation(
            operationId,
            current => current with
            {
                State = HardwareOperationState.Aborted,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "Operation aborted; the prior control state was restored.",
                Error = null
            });
        await _store!.SaveOperationAsync(status, CancellationToken.None).ConfigureAwait(false);
        await _store.ClearPendingOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FailOperationAsync(
        string operationId,
        Exception exception,
        bool recoveryRequired,
        string? message = null)
    {
        HardwareOperationStatus status = UpdateOperation(
            operationId,
            current => current with
            {
                State = recoveryRequired
                    ? HardwareOperationState.RecoveryRequired
                    : HardwareOperationState.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = message ?? (recoveryRequired
                    ? "Hardware recovery is required; further writes are blocked."
                    : "Operation failed; the prior control state was restored."),
                Error = exception.Message
            });
        await _store!.SaveOperationAsync(status, CancellationToken.None).ConfigureAwait(false);
        if (!recoveryRequired)
        {
            await _store.ClearPendingOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FinishOperationTaskAsync(
        string operationId,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            await RefreshAsync(persistSensors: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ServiceLog.RefreshFailed(logger, exception);
        }

        lock (_operationSync)
        {
            if (string.Equals(_operationStatus?.Id, operationId, StringComparison.Ordinal))
            {
                _operationCancellation = null;
                _activeOperationTask = null;
            }
        }

        operationCancellation.Dispose();
    }

    private HardwareOperationStatus UpdateOperation(
        string operationId,
        Func<HardwareOperationStatus, HardwareOperationStatus> update)
    {
        lock (_operationSync)
        {
            if (_operationStatus is null
                || !string.Equals(_operationStatus.Id, operationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The hardware operation is no longer current.");
            }

            _operationStatus = update(_operationStatus);
            return _operationStatus;
        }
    }

    private bool HasActiveOperation()
    {
        lock (_operationSync)
        {
            return _operationStatus is not null && IsActive(_operationStatus.State);
        }
    }

    private async Task<IpcResponse> ValidateUpdateAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ValidateUpdateRequest payload = IpcJson.FromElement<ValidateUpdateRequest>(request.Payload)
            ?? throw new InvalidDataException("ValidateUpdate requires a versioned update plan.");
        WindowsDriverUpdateExecutor executor = GetUpdateExecutor();
        UpdateValidationContext context = await executor.InspectStagedPackageAsync(payload.Plan, cancellationToken).ConfigureAwait(false);
        SuiteValidationResult validation = UpdatePlanValidator.Validate(payload.Plan, context);
        UpdateValidationResultV1 result = new(
            UpdateValidationResultV1.CurrentSchemaVersion,
            payload.Plan,
            validation.IsValid,
            validation.Errors,
            validation.Warnings,
            executor.ProductionExecutionReady,
            executor.ExecutionMessage);
        return Success(request, result);
    }

    private async Task<IpcResponse> ApplyUpdateAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (HasActiveOperation())
        {
            return Failure(request, "OPERATION_ACTIVE", "Finish or abort the active hardware operation before installing a driver.");
        }
        EnsureExpectedRevision(request);
        ApplyUpdateRequest payload = IpcJson.FromElement<ApplyUpdateRequest>(request.Payload)
            ?? throw new InvalidDataException("ApplyUpdate requires a versioned confirmed update plan.");
        WindowsDriverUpdateExecutor executor = GetUpdateExecutor();
        if (payload.Plan.Candidate.Kind != UpdateKind.Driver)
        {
            return Failure(
                request,
                "FIRMWARE_EXECUTOR_UNAVAILABLE",
                "RigPilot does not run generic firmware or BIOS writers. Use only an exact-model vendor-signed updater, ESRT capsule, or documented UEFI workflow.");
        }
        if (!executor.ProductionExecutionReady)
        {
            return Failure(request, "SERVICE_SIGNING_REQUIRED", executor.ExecutionMessage);
        }

        UpdateTransactionV1 transaction = await (_updateCoordinator
            ?? throw new InvalidOperationException("The update coordinator is not initialised."))
            .ApplyAsync(payload.Plan, cancellationToken)
            .ConfigureAwait(false);
        IncrementSuiteRevision();
        return transaction.State is UpdateTransactionState.Completed or UpdateTransactionState.PendingReboot
            ? Success(request, transaction)
            : FailureWithPayload(
                request,
                transaction.State == UpdateTransactionState.RecoveryRequired ? "UPDATE_RECOVERY_REQUIRED" : "UPDATE_FAILED",
                transaction.Error ?? $"Update ended in state {transaction.State}.",
                transaction);
    }

    private async Task<IpcResponse> GetUpdateStatusAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        WindowsDriverUpdateExecutor executor = GetUpdateExecutor();
        IReadOnlyList<UpdateTransactionV1> transactions = await _store!
            .GetSuiteEntitiesAsync<UpdateTransactionV1>(SuiteEntityKind.UpdateTransaction, cancellationToken)
            .ConfigureAwait(false);
        return Success(request, new UpdateStatusV1(
            UpdateStatusV1.CurrentSchemaVersion,
            executor.ProductionExecutionReady,
            executor.ExecutionMessage,
            transactions
                .OrderByDescending(transaction => transaction.UpdatedAt)
            .ToArray()));
    }

    private async Task<IpcResponse> SetHardwareControlArmedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetHardwareControlArmedRequest payload = IpcJson.FromElement<SetHardwareControlArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetHardwareControlArmed requires an arming request.");
        HardwareControlTransactionResult result = await ApplyHardwareControlTransactionAsync(
            payload,
            requestedFamily: null,
            cancellationToken).ConfigureAwait(false);
        return result.AllRequestedFamiliesVerified
            ? Success(request, result)
            : FailureWithPayload(
                request,
                result.RecoveryRequired ? "RECOVERY_REQUIRED" : "HARDWARE_CONTROL_NOT_VERIFIED",
                result.Message,
                result);
    }

    private async Task<IpcResponse> SetGpuFanControlArmedSerializedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetGpuFanControlArmedRequest payload = IpcJson.FromElement<SetGpuFanControlArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetGpuFanControlArmed requires an arming request.");
        HardwareControlTransactionResult result = await ApplyHardwareControlTransactionAsync(
            new SetHardwareControlArmedRequest(payload.Armed, payload.ConfirmExperimental, payload.ConfirmedDeviceIds),
            HardwareControlFamilyNames.GpuFan,
            cancellationToken).ConfigureAwait(false);
        HardwareControlFamilyResult? family = result.Families.Count > 0 ? result.Families[0] : null;
        GpuFanControlStatus status = new(
            family?.Available == true,
            result.AllRequestedFamiliesVerified && result.Armed,
            _gpuFanDeviceId,
            family?.Message ?? result.Message);
        return result.AllRequestedFamiliesVerified
            ? Success(request, status)
            : FailureWithPayload(request, result.RecoveryRequired ? "RECOVERY_REQUIRED" : "GPU_FAN_NOT_VERIFIED", result.Message, status);
    }

    private async Task<IpcResponse> SetGpuPowerLimitArmedSerializedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetGpuPowerLimitArmedRequest payload = IpcJson.FromElement<SetGpuPowerLimitArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetGpuPowerLimitArmed requires an arming request.");
        HardwareControlTransactionResult result = await ApplyHardwareControlTransactionAsync(
            new SetHardwareControlArmedRequest(payload.Armed, payload.ConfirmExperimental, payload.ConfirmedDeviceIds),
            HardwareControlFamilyNames.GpuPower,
            cancellationToken).ConfigureAwait(false);
        HardwareControlFamilyResult? family = result.Families.Count > 0 ? result.Families[0] : null;
        GpuPowerLimitStatus status = new(
            family?.Available == true,
            result.AllRequestedFamiliesVerified && result.Armed,
            _gpuFanDeviceId,
            family?.Message ?? result.Message);
        return result.AllRequestedFamiliesVerified
            ? Success(request, status)
            : FailureWithPayload(request, result.RecoveryRequired ? "RECOVERY_REQUIRED" : "GPU_POWER_NOT_VERIFIED", result.Message, status);
    }

    private async Task<IpcResponse> SetGpuClockOffsetArmedSerializedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetGpuClockOffsetArmedRequest payload = IpcJson.FromElement<SetGpuClockOffsetArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetGpuClockOffsetArmed requires an arming request.");
        HardwareControlTransactionResult result = await ApplyHardwareControlTransactionAsync(
            new SetHardwareControlArmedRequest(payload.Armed, payload.ConfirmExperimental, payload.ConfirmedDeviceIds),
            HardwareControlFamilyNames.GpuClock,
            cancellationToken).ConfigureAwait(false);
        HardwareControlFamilyResult? family = result.Families.Count > 0 ? result.Families[0] : null;
        GpuClockOffsetStatus status = new(
            family?.Available == true,
            result.AllRequestedFamiliesVerified && result.Armed,
            _gpuFanDeviceId,
            family?.Message ?? result.Message);
        return result.AllRequestedFamiliesVerified
            ? Success(request, status)
            : FailureWithPayload(request, result.RecoveryRequired ? "RECOVERY_REQUIRED" : "GPU_CLOCK_NOT_VERIFIED", result.Message, status);
    }

    private async Task<HardwareControlTransactionResult> ApplyHardwareControlTransactionAsync(
        SetHardwareControlArmedRequest request,
        string? requestedFamily,
        CancellationToken cancellationToken)
    {
        HardwareControlFamilyDefinition[] available = BuildHardwareControlFamilies()
            .Where(family => requestedFamily is null || string.Equals(family.Name, requestedFamily, StringComparison.Ordinal))
            .ToArray();
        if (available.Length == 0)
        {
            return new HardwareControlTransactionResult(
                Armed: false,
                AllRequestedFamiliesVerified: false,
                RecoveryRequired: false,
                [],
                requestedFamily is null
                    ? "No implemented GPU hardware-control family is available on this system."
                    : $"{requestedFamily} is unavailable on this system.");
        }

        if (request.Armed)
        {
            if (!request.ConfirmExperimental)
            {
                return new HardwareControlTransactionResult(false, false, false, [], "Arming hardware control requires explicit Experimental confirmation.");
            }
            if (!request.ConfirmedDeviceIds.Contains(_gpuFanDeviceId, StringComparer.Ordinal))
            {
                return new HardwareControlTransactionResult(false, false, false, [], $"Arming hardware control requires exact-device confirmation for '{_gpuFanDeviceId}'.");
            }
        }

        List<HardwareControlFamilyResult> results = [];
        await _hardwareMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (HardwareControlFamilyDefinition family in available)
            {
                family.SetTransportGate(true);
            }

            foreach (HardwareControlFamilyDefinition family in available)
            {
                results.Add(await ResetAndVerifyHardwareFamilyAsync(family, cancellationToken).ConfigureAwait(false));
            }

            bool allVerified = results.All(result => result.ReadBackVerified);
            foreach (HardwareControlFamilyDefinition family in available)
            {
                family.CommitLogicalState(allVerified && request.Armed);
                family.SetTransportGate(allVerified && request.Armed);
            }

            if (!allVerified)
            {
                _rollbackBlocked = true;
                await SaveHardwareRecoveryRequiredLeaseAsync(available, results, CancellationToken.None).ConfigureAwait(false);
            }

            IncrementSuiteRevision();
            try
            {
                await RefreshHardwareControlCapabilitiesAsync(available, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ServiceLog.HardwareControlSnapshotRefreshFailed(logger, exception);
            }

            return new HardwareControlTransactionResult(
                Armed: allVerified && request.Armed,
                AllRequestedFamiliesVerified: allVerified,
                RecoveryRequired: !allVerified,
                results,
                allVerified
                    ? request.Armed
                        ? $"Hardware control armed only after default-state read-back verified {available.Length} requested family/families."
                        : $"Hardware control disarmed after vendor/default state was restored and read back for {available.Length} requested family/families."
                    : "RecoveryRequired: at least one requested hardware family could not be restored and read back at its default state.");
        }
        catch (Exception operationException)
        {
            // A caller cancellation or an unexpected service-side failure can
            // happen after one or more transport gates have opened. Make the
            // exceptional exit a contained rollback: attempt default restore
            // with a non-cancellable token, then always close every gate.
            List<HardwareControlFamilyResult> recoveryResults = [];
            try
            {
                foreach (HardwareControlFamilyDefinition family in available)
                {
                    family.SetTransportGate(true);
                    recoveryResults.Add(await ResetAndVerifyHardwareFamilyAsync(
                        family,
                        CancellationToken.None).ConfigureAwait(false));
                }
            }
            finally
            {
                foreach (HardwareControlFamilyDefinition family in available)
                {
                    family.CommitLogicalState(false);
                    family.SetTransportGate(false);
                }
            }

            bool defaultsVerified = recoveryResults.Count == available.Length
                && recoveryResults.All(result => result.ReadBackVerified);
            bool recoveryRequired = operationException is HardwareStateUnknownException
                || !defaultsVerified
                || _rollbackBlocked;
            if (recoveryRequired)
            {
                _rollbackBlocked = true;
                await SaveHardwareRecoveryRequiredLeaseAsync(
                    available,
                    recoveryResults,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (operationException is OperationCanceledException)
            {
                throw;
            }

            return new HardwareControlTransactionResult(
                Armed: false,
                AllRequestedFamiliesVerified: false,
                RecoveryRequired: recoveryRequired,
                recoveryResults,
                defaultsVerified
                    ? $"Hardware-control change failed ({operationException.Message}); vendor/default state was restored and read back, so control remains disarmed."
                    : $"RecoveryRequired: hardware-control change failed ({operationException.Message}) and the default state could not be proved.");
        }
        finally
        {
            _hardwareMutationGate.Release();
        }
    }

    private async Task RefreshHardwareControlCapabilitiesAsync(
        IEnumerable<HardwareControlFamilyDefinition> families,
        CancellationToken cancellationToken)
    {
        foreach (IHardwareAdapter adapter in families
            .SelectMany(family => family.Controls)
            .Select(control => control.Adapter)
            .Distinct())
        {
            IReadOnlyList<CapabilityDescriptor> refreshed = await AdapterCoordinator
                .CaptureAdapterCapabilitiesAsync(adapter, cancellationToken)
                .ConfigureAwait(false);
            await PatchSnapshotCapabilitiesAsync(refreshed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HardwareControlFamilyResult> ResetAndVerifyHardwareFamilyAsync(
        HardwareControlFamilyDefinition family,
        CancellationToken cancellationToken)
    {
        List<string> messages = [];
        bool retried = false;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            bool verified = true;
            bool unknownMutationOutcome = false;
            messages.Clear();
            foreach ((IHardwareAdapter adapter, string capabilityId) in family.Controls)
            {
                try
                {
                    await adapter.ResetToDefaultAsync(capabilityId, cancellationToken).ConfigureAwait(false);
                    if (adapter is not IHardwareStateVerifier verifier)
                    {
                        verified = false;
                        messages.Add($"{capabilityId}: default-state verifier is unavailable.");
                        continue;
                    }

                    HardwareStateVerification verification = await verifier
                        .VerifyDefaultStateAsync(capabilityId, cancellationToken)
                        .ConfigureAwait(false);
                    verified &= verification.Success;
                    messages.Add($"{capabilityId}: {verification.Message}");
                }
                catch (HardwareStateUnknownException exception)
                {
                    // Never convert a timed-out mutation into Verified by
                    // retrying it. Continue through the remaining controls so
                    // their defaults are still attempted, then fail closed.
                    verified = false;
                    unknownMutationOutcome = true;
                    messages.Add($"{capabilityId}: {exception.Message}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    verified = false;
                    messages.Add($"{capabilityId}: {exception.Message}");
                }
            }

            if (unknownMutationOutcome)
            {
                return new HardwareControlFamilyResult(
                    family.Name,
                    Available: true,
                    RequestedStateApplied: false,
                    ReadBackVerified: false,
                    RolledBack: false,
                    $"Hardware state is unknown after a timed-out mutation. {string.Join(" ", messages)}");
            }

            if (verified)
            {
                return new HardwareControlFamilyResult(
                    family.Name,
                    Available: true,
                    RequestedStateApplied: true,
                    ReadBackVerified: true,
                    RolledBack: retried,
                    string.Join(" ", messages));
            }

            retried = true;
        }

        return new HardwareControlFamilyResult(
            family.Name,
            Available: true,
            RequestedStateApplied: false,
            ReadBackVerified: false,
            RolledBack: false,
            string.Join(" ", messages));
    }

    private HardwareControlFamilyDefinition[] BuildHardwareControlFamilies()
    {
        List<HardwareControlFamilyDefinition> families = [];
        if (_gpuFanTransport is not null && _gpuFanAdapter is not null)
        {
            families.Add(new HardwareControlFamilyDefinition(
                HardwareControlFamilyNames.GpuFan,
                [(_gpuFanAdapter, $"{NvidiaGpuFanAdapter.CapabilityPrefix}0")],
                armed => _gpuFanTransport.SetArmed(armed),
                armed => _gpuFanArmed = armed));
        }
        if (_gpuPowerTransport is not null && _gpuPowerAdapter is not null)
        {
            families.Add(new HardwareControlFamilyDefinition(
                HardwareControlFamilyNames.GpuPower,
                [(_gpuPowerAdapter, $"{NvidiaGpuPowerLimitAdapter.CapabilityPrefix}0")],
                armed => _gpuPowerTransport.SetArmed(armed),
                armed => _gpuPowerArmed = armed));
        }
        if (_gpuClockTransport is not null && _gpuClockCoreAdapter is not null)
        {
            List<(IHardwareAdapter Adapter, string CapabilityId)> controls =
            [
                (_gpuClockCoreAdapter, $"{NvidiaGpuClockOffsetAdapter.CorePrefix}0")
            ];
            if (_gpuClockMemoryAdapter is not null)
            {
                controls.Add((_gpuClockMemoryAdapter, $"{NvidiaGpuClockOffsetAdapter.MemoryPrefix}0"));
            }
            families.Add(new HardwareControlFamilyDefinition(
                HardwareControlFamilyNames.GpuClock,
                controls,
                armed => _gpuClockTransport.SetArmed(armed),
                armed => _gpuClockArmed = armed));
        }
        return families.ToArray();
    }

    private async Task SaveHardwareRecoveryRequiredLeaseAsync(
        IEnumerable<HardwareControlFamilyDefinition> families,
        IEnumerable<HardwareControlFamilyResult> results,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HardwareControlLeaseV1? existing = await _store!.GetSuiteEntityAsync<HardwareControlLeaseV1>(
            SuiteEntityKind.HardwareControlLease,
            HardwareControlLeaseV1.DefaultId,
            cancellationToken).ConfigureAwait(false);
        HardwareControlLeaseItemV1[] controls = (existing?.Controls ?? [])
            .Concat(families.SelectMany(family => family.Controls).Select(control =>
                new HardwareControlLeaseItemV1(control.Adapter.Manifest.Id, control.CapabilityId)))
            .DistinctBy(item => (item.AdapterId, item.CapabilityId))
            .ToArray();
        HardwareControlLeaseV1 lease = new(
            HardwareControlLeaseV1.CurrentSchemaVersion,
            HardwareControlLeaseV1.DefaultId,
            _serviceInstanceId,
            existing?.ActiveProfileId,
            existing?.LastTransactionId,
            controls,
            existing?.AcquiredAt ?? now,
            now,
            CleanShutdown: false,
            DefaultsVerified: false,
            HardwareControlLeaseState.RecoveryRequired,
            string.Join(" ", results.Where(result => !result.ReadBackVerified).Select(result => result.Message)));
        await _store.SaveSuiteEntityAsync(
            SuiteEntityKind.HardwareControlLease,
            lease.Id,
            lease,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record HardwareControlFamilyDefinition(
        string Name,
        IReadOnlyList<(IHardwareAdapter Adapter, string CapabilityId)> Controls,
        Action<bool> SetTransportGate,
        Action<bool> CommitLogicalState);

    private static class HardwareControlFamilyNames
    {
        public const string GpuFan = "GPU fan";
        public const string GpuPower = "GPU power limit";
        public const string GpuClock = "GPU clock offset";
    }

    private async Task<IpcResponse> SetGpuFanControlArmedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetGpuFanControlArmedRequest payload = IpcJson.FromElement<SetGpuFanControlArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetGpuFanControlArmed requires an arming request.");

        if (_gpuFanTransport is null)
        {
            return FailureWithPayload(
                request,
                "GPU_FAN_UNAVAILABLE",
                "No GPU fan control transport is available on this system.",
                new GpuFanControlStatus(false, false, _gpuFanDeviceId, "GPU fan control is unavailable."));
        }

        if (payload.Armed)
        {
            // Arming an Experimental physical write requires an explicit acknowledgement
            // plus exact-device confirmation, mirroring Manual-Only profile actions.
            if (!payload.ConfirmExperimental)
            {
                return Failure(request, "EXPERIMENTAL_NOT_CONFIRMED",
                    "Arming GPU fan control requires explicit Experimental confirmation.");
            }

            if (!payload.ConfirmedDeviceIds.Contains(_gpuFanDeviceId, StringComparer.Ordinal))
            {
                return Failure(request, "DEVICE_NOT_CONFIRMED",
                    $"Arming GPU fan control requires exact-device confirmation for '{_gpuFanDeviceId}'.");
            }
        }

        if (!payload.Armed)
        {
            // Return the fan to the driver automatic curve while still armed, then disarm,
            // so disarming never strands the fan in a manual state.
            foreach (string fanChannel in (string[])["0", "1"])
            {
                try
                {
                    await _gpuFanTransport.RestoreAutomaticAsync(fanChannel, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception restoreException) when (restoreException is GpuFanSafetyException or InvalidOperationException)
                {
                    // Nothing to restore (never written) or a transient driver error.
                }
            }
        }

        _gpuFanArmed = payload.Armed;
        _gpuFanTransport.SetArmed(payload.Armed);
        IncrementSuiteRevision();

        // Synchronously refresh just the GPU-fan adapter's capabilities into the cached
        // snapshot so that an ApplyProfileV2 issued immediately after this call sees the new
        // Experimental/ReadOnly state instead of the stale one. This targeted re-probe is
        // fast (a single adapter) and applies the same conflict resolution as a full
        // capture. The full re-probe of every adapter stays backgrounded below because it
        // can outrun the client timeout.
        if (_gpuFanAdapter is not null && _coordinator is not null)
        {
            try
            {
                IReadOnlyList<CapabilityDescriptor> refreshed = await AdapterCoordinator
                    .CaptureAdapterCapabilitiesAsync(_gpuFanAdapter, cancellationToken)
                    .ConfigureAwait(false);
                await PatchSnapshotCapabilitiesAsync(refreshed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception patchException) when (patchException is not OperationCanceledException)
            {
                // If the targeted re-probe fails, the backgrounded full refresh still
                // reconciles the snapshot; the armed flag itself is already applied.
            }
        }

        // Reconcile the rest of the snapshot (other adapters, health, cooling graph) without
        // blocking the response on a full adapter re-probe.
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(persistSensors: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception refreshException) when (refreshException is not OperationCanceledException)
            {
                // A background snapshot refresh failure is non-fatal; the next poll recovers.
            }
        }, CancellationToken.None);
        return Success(request, new GpuFanControlStatus(
            true,
            payload.Armed,
            _gpuFanDeviceId,
            payload.Armed
                ? "GPU fan control is armed. Manual-only writes are permitted for confirmed profiles until disarmed."
                : "GPU fan control is disarmed and read-only."));
    }

    private async Task<IpcResponse> SetGpuPowerLimitArmedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetGpuPowerLimitArmedRequest payload = IpcJson.FromElement<SetGpuPowerLimitArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetGpuPowerLimitArmed requires an arming request.");

        if (_gpuPowerTransport is null)
        {
            return FailureWithPayload(
                request,
                "GPU_POWER_UNAVAILABLE",
                "No GPU power-limit transport is available on this system.",
                new GpuPowerLimitStatus(false, false, _gpuFanDeviceId, "GPU power-limit control is unavailable."));
        }

        if (payload.Armed)
        {
            // Arming an Experimental physical write requires an explicit acknowledgement
            // plus exact-device confirmation, mirroring the GPU-fan arm flow.
            if (!payload.ConfirmExperimental)
            {
                return Failure(request, "EXPERIMENTAL_NOT_CONFIRMED",
                    "Arming GPU power-limit control requires explicit Experimental confirmation.");
            }

            if (!payload.ConfirmedDeviceIds.Contains(_gpuFanDeviceId, StringComparer.Ordinal))
            {
                return Failure(request, "DEVICE_NOT_CONFIRMED",
                    $"Arming GPU power-limit control requires exact-device confirmation for '{_gpuFanDeviceId}'.");
            }
        }

        if (!payload.Armed)
        {
            // Return the limit to the vendor default while still armed, then disarm,
            // so disarming never strands a non-default power limit.
            try
            {
                GpuPowerLimitBounds? bounds = await _gpuPowerTransport.ReadBoundsAsync("0", cancellationToken).ConfigureAwait(false);
                if (bounds is { IsValid: true } valid)
                {
                    await _gpuPowerTransport.SetPowerLimitAsync("0", valid.DefaultMilliwatts, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception restoreException) when (restoreException is GpuPowerSafetyException or InvalidOperationException)
            {
                // Nothing to restore (never written) or a transient driver error.
            }
        }

        _gpuPowerArmed = payload.Armed;
        _gpuPowerTransport.SetArmed(payload.Armed);
        IncrementSuiteRevision();

        // Synchronously refresh just this adapter's capabilities into the cached snapshot
        // so an ApplyProfileV2 issued immediately after this call sees the new state.
        if (_gpuPowerAdapter is not null && _coordinator is not null)
        {
            try
            {
                IReadOnlyList<CapabilityDescriptor> refreshed = await AdapterCoordinator
                    .CaptureAdapterCapabilitiesAsync(_gpuPowerAdapter, cancellationToken)
                    .ConfigureAwait(false);
                await PatchSnapshotCapabilitiesAsync(refreshed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception patchException) when (patchException is not OperationCanceledException)
            {
                // If the targeted re-probe fails, the backgrounded full refresh still
                // reconciles the snapshot; the armed flag itself is already applied.
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(persistSensors: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception refreshException) when (refreshException is not OperationCanceledException)
            {
                // A background snapshot refresh failure is non-fatal; the next poll recovers.
            }
        }, CancellationToken.None);
        return Success(request, new GpuPowerLimitStatus(
            true,
            payload.Armed,
            _gpuFanDeviceId,
            payload.Armed
                ? "GPU power-limit control is armed. Manual-only writes are permitted for confirmed profiles until disarmed."
                : "GPU power-limit control is disarmed and read-only; the vendor default limit was restored."));
    }

    private async Task<IpcResponse> SetGpuClockOffsetArmedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetGpuClockOffsetArmedRequest payload = IpcJson.FromElement<SetGpuClockOffsetArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetGpuClockOffsetArmed requires an arming request.");

        if (_gpuClockTransport is null)
        {
            return FailureWithPayload(
                request,
                "GPU_CLOCK_UNAVAILABLE",
                "No GPU clock-offset transport is available on this system.",
                new GpuClockOffsetStatus(false, false, _gpuFanDeviceId, "GPU clock-offset control is unavailable."));
        }

        if (payload.Armed)
        {
            // Arming an Experimental physical write requires an explicit acknowledgement
            // plus exact-device confirmation, mirroring the GPU fan and power-limit flows.
            if (!payload.ConfirmExperimental)
            {
                return Failure(request, "EXPERIMENTAL_NOT_CONFIRMED",
                    "Arming GPU clock-offset control requires explicit Experimental confirmation.");
            }

            if (!payload.ConfirmedDeviceIds.Contains(_gpuFanDeviceId, StringComparer.Ordinal))
            {
                return Failure(request, "DEVICE_NOT_CONFIRMED",
                    $"Arming GPU clock-offset control requires exact-device confirmation for '{_gpuFanDeviceId}'.");
            }
        }

        if (!payload.Armed)
        {
            // Return both domains to stock clocks (0 kHz offset) while still armed,
            // then disarm, so disarming never strands a non-default offset.
            foreach (GpuClockOffsetDomain domain in (GpuClockOffsetDomain[])[GpuClockOffsetDomain.Core, GpuClockOffsetDomain.Memory])
            {
                try
                {
                    GpuClockOffsetBounds? bounds = await _gpuClockTransport.ReadBoundsAsync(domain, cancellationToken).ConfigureAwait(false);
                    if (bounds is { IsValid: true })
                    {
                        await _gpuClockTransport.SetOffsetAsync(domain, GpuClockOffsetBounds.DefaultKiloHertz, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception restoreException) when (restoreException is GpuClockSafetyException or InvalidOperationException)
                {
                    // Nothing to restore (never written) or a transient driver error.
                }
            }
        }

        _gpuClockArmed = payload.Armed;
        _gpuClockTransport.SetArmed(payload.Armed);
        IncrementSuiteRevision();

        // Synchronously refresh both clock adapters' capabilities into the cached snapshot
        // so an ApplyProfileV2 issued immediately after this call sees the new state.
        if (_coordinator is not null)
        {
            foreach (IHardwareAdapter? adapter in (IHardwareAdapter?[])[_gpuClockCoreAdapter, _gpuClockMemoryAdapter])
            {
                if (adapter is null)
                {
                    continue;
                }
                try
                {
                    IReadOnlyList<CapabilityDescriptor> refreshed = await AdapterCoordinator
                        .CaptureAdapterCapabilitiesAsync(adapter, cancellationToken)
                        .ConfigureAwait(false);
                    await PatchSnapshotCapabilitiesAsync(refreshed, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception patchException) when (patchException is not OperationCanceledException)
                {
                    // If the targeted re-probe fails, the backgrounded full refresh still
                    // reconciles the snapshot; the armed flag itself is already applied.
                }
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(persistSensors: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception refreshException) when (refreshException is not OperationCanceledException)
            {
                // A background snapshot refresh failure is non-fatal; the next poll recovers.
            }
        }, CancellationToken.None);
        return Success(request, new GpuClockOffsetStatus(
            true,
            payload.Armed,
            _gpuFanDeviceId,
            payload.Armed
                ? "GPU clock-offset control is armed. Manual-only writes are permitted for confirmed profiles until disarmed."
                : "GPU clock-offset control is disarmed and read-only; both domains were returned to stock clocks."));
    }

    private IpcResponse SetCpuTuningArmed(IpcRequest request)
    {
        EnsureExpectedRevision(request);
        SetCpuTuningArmedRequest payload = IpcJson.FromElement<SetCpuTuningArmedRequest>(request.Payload)
            ?? throw new InvalidDataException("SetCpuTuningArmed requires an arming request.");

        // CPU PBO tuning is behind the full qualification gate
        // (docs/qualification/cpu-tuning-and-intel-arc.md): no qualification record
        // exists for any system and no SMU tuning transport is implemented, so arming
        // is refused regardless of confirmations. Disarming is trivially satisfied.
        if (payload.Armed)
        {
            return FailureWithPayload(
                request,
                "CPU_TUNING_QUALIFICATION_REQUIRED",
                "CPU PBO tuning cannot be armed: the CPU-tuning qualification gate has not been passed on this system and no SMU tuning transport exists. This is a policy gate, not a missing confirmation.",
                new CpuTuningStatus(
                    false,
                    false,
                    false,
                    string.Empty,
                    $"CPU tuning is blocked by the qualification gate. Boot-recovery sentinel: {_cpuTuneRecoveryMessage}"));
        }

        return Success(request, new CpuTuningStatus(
            false,
            false,
            false,
            string.Empty,
            $"CPU PBO tuning is disarmed (it can never be armed on an unqualified system). Boot-recovery sentinel: {_cpuTuneRecoveryMessage}"));
    }

    private async Task<IpcResponse> DiscoverControllersAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // USB/AIO controller enumeration runs behind a process boundary so a native
        // HidSharp fault cannot terminate the service. The result is read-only
        // inventory evidence; it never produces a writable capability.
        ContainedControllerDiscovery discovery = new(
            static () => new AdapterHostControllerDiscoveryProcess());
        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> DiscoverHidInventoryAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // Read-only HID peripheral enumeration runs behind the same process boundary so a
        // native HidSharp fault cannot terminate the service. The result is inventory
        // evidence only; it never produces a writable capability.
        ContainedHidInventory inventory = new(
            static () => new AdapterHostControllerDiscoveryProcess("--discover-hid"));
        HidInventoryResultV1 result = await inventory.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> ReadRyzenSmuFeasibilityAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // Read-only SMU/PBO qualification evidence behind the crash-isolated process
        // boundary. The child references no tuning or register-write module function;
        // this evidence never un-gates a CPU write (CPU/SMU tuning stays Blocked).
        ContainedRyzenSmuFeasibility feasibility = new(
            static () => new AdapterHostControllerDiscoveryProcess("--read-ryzen-smu"));
        RyzenSmuFeasibilityV1 result = await feasibility.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private IpcResponse GetStorageHealth(IpcRequest request)
    {
        // Read-only Windows Storage provider snapshot (identity, health status,
        // reliability counters). RigPilot has no storage write path; a provider
        // failure is an explanatory report, not an error.
        return Success(request, StorageHealthProbe.Read());
    }

    private async Task<IpcResponse> ReadKrakenTelemetryAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // Read-only Kraken X3 liquid-cooler telemetry runs behind the same crash-isolated
        // process boundary as the HID inventory. The child never writes a HID report —
        // the firmware streams status unsolicited — so no pump capability is implied.
        ContainedKrakenTelemetry telemetry = new(
            static () => new AdapterHostControllerDiscoveryProcess("--read-kraken"));
        KrakenTelemetryV1 result = await telemetry.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private IpcResponse StopConflictingProcesses(IpcRequest request)
    {
        // "Close blockers": terminates the running processes of detected
        // conflicting controllers so they release the device handles that block
        // RigPilot's gated writes. This takes over NO hardware control and is
        // deliberately distinct from the identity-verified takeover executor:
        // it only terminates processes on ConflictDetector's curated allowlist,
        // never an arbitrary name, and only with explicit confirmation. The
        // LocalSystem service can terminate the elevated controllers.
        StopConflictingProcessesRequestV1 payload = IpcJson.FromElement<StopConflictingProcessesRequestV1>(request.Payload)
            ?? throw new InvalidDataException("StopConflictingProcesses requires a StopConflictingProcessesRequestV1 payload.");
        ConflictProcessTerminator terminator = new(new WindowsProcessControl());
        StopConflictingProcessesResultV1 result = terminator.Terminate(payload);
        return Success(request, result);
    }

    private async Task<IpcResponse> SetKrakenLightingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // RigPilot's own native Kraken lighting write. Experimental and
        // double-confirmed (explicit Experimental flag + exact device id);
        // lighting only — pump/cooling registers are never touched — and the
        // write runs in the crash-contained Adapter Host child. There is no
        // firmware read-back for lighting, so the result never claims
        // verification; an exclusively-held device is a designed refusal.
        KrakenLightingRequestV1 payload = IpcJson.FromElement<KrakenLightingRequestV1>(request.Payload)
            ?? throw new InvalidDataException("SetKrakenLighting requires a KrakenLightingRequestV1 payload.");
        if (payload.Validate() is string refusal)
        {
            return Failure(request, "KRAKEN_LIGHTING_NOT_CONFIRMED", refusal);
        }

        string argument = payload.TurnOff ? "off" : payload.Colour.Trim().TrimStart('#');
        ContainedKrakenLighting lighting = new(
            () => new AdapterHostControllerDiscoveryProcess("--set-kraken-rgb", argument));
        KrakenLightingResultV1 result = await lighting.WriteAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> SetAuraLightingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // RigPilot's in-house AURA addressable lighting write. Experimental and
        // double-confirmed; lighting registers only (no EEPROM/save), runs in
        // the crash-contained Adapter Host child, no firmware read-back.
        AuraLightingRequestV1 payload = IpcJson.FromElement<AuraLightingRequestV1>(request.Payload)
            ?? throw new InvalidDataException("SetAuraLighting requires an AuraLightingRequestV1 payload.");
        if (payload.Validate() is string refusal)
        {
            return Failure(request, "AURA_LIGHTING_NOT_CONFIRMED", refusal);
        }

        string argument = payload.TurnOff ? "off" : payload.Colour.Trim().TrimStart('#');
        if (payload.HeaderIndex is int auraHeader)
        {
            // Single-header target: a passive ARGB device on one header (e.g.
            // the Cooler Master GPU sag bracket) without repainting the other.
            argument = $"{argument}@{auraHeader.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        ContainedAuraLighting aura = new(
            () => new AdapterHostControllerDiscoveryProcess("--set-aura-rgb", argument));
        AuraLightingResultV1 result = await aura.WriteAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> SetDimmRgbAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // RigPilot's in-house DIMM RGB write over the system SMBus (signed
        // PawnIO transport). Experimental and double-confirmed; quadruple-gated
        // in the writer (transport, default-deny address policy, identity
        // check, per-kit first-light audit) and runs in the crash-contained
        // Adapter Host child. No firmware read-back.
        DimmRgbRequestV1 payload = IpcJson.FromElement<DimmRgbRequestV1>(request.Payload)
            ?? throw new InvalidDataException("SetDimmRgb requires a DimmRgbRequestV1 payload.");
        if (payload.Validate() is string refusal)
        {
            return Failure(request, "DIMM_RGB_NOT_CONFIRMED", refusal);
        }

        string argument = payload.TurnOff ? "off" : payload.Colour.Trim().TrimStart('#');
        ContainedDimmRgb dimm = new(
            () => new AdapterHostControllerDiscoveryProcess("--set-smbus-rgb", argument));
        DimmRgbResultV1 result = await dimm.WriteAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> SetRazerRgbAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // RigPilot's in-house native Razer USB lighting write (no Synapse
        // dependency). Experimental and double-confirmed; extended-matrix
        // static effect only — no firmware/profile/EEPROM command class — runs
        // in the crash-contained Adapter Host child, and the firmware's status
        // reply is read back before WriteIssued may be reported.
        RazerRgbRequestV1 payload = IpcJson.FromElement<RazerRgbRequestV1>(request.Payload)
            ?? throw new InvalidDataException("SetRazerRgb requires a RazerRgbRequestV1 payload.");
        if (payload.Validate() is string refusal)
        {
            return Failure(request, "RAZER_RGB_NOT_CONFIRMED", refusal);
        }

        string argument = payload.TurnOff ? "off" : payload.Colour.Trim().TrimStart('#');
        ContainedRazerRgb razer = new(
            () => new AdapterHostControllerDiscoveryProcess("--set-razer-usb-rgb", argument));
        RazerRgbResultV1 result = await razer.WriteAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> SetKrakenPumpDutyAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        // RigPilot's own native Kraken pump-duty write. Experimental and
        // double-confirmed (explicit Experimental flag + exact device id).
        // Pump speed is safety-critical, so the duty is hard-clamped to
        // [60, 100]% at the request, the writer, and the report-builder layers
        // — never below the floor, never stopped — the write runs in the
        // crash-contained Adapter Host child, and the firmware status stream
        // is read back before the result may claim verification.
        KrakenPumpRequestV1 payload = IpcJson.FromElement<KrakenPumpRequestV1>(request.Payload)
            ?? throw new InvalidDataException("SetKrakenPumpDuty requires a KrakenPumpRequestV1 payload.");
        if (payload.Validate() is string refusal)
        {
            return Failure(request, "KRAKEN_PUMP_NOT_CONFIRMED", refusal);
        }

        ContainedKrakenPump pump = new(
            () => new AdapterHostControllerDiscoveryProcess(
                "--set-kraken-pump",
                payload.DutyPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        KrakenPumpResultV1 result = await pump.WriteAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> SaveHealthRuleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        HealthRuleV1 rule = IpcJson.FromElement<HealthRuleV1>(request.Payload)
            ?? throw new InvalidDataException("SaveHealthRule requires a HealthRuleV1 payload.");
        SuiteValidationResult validation = HealthRuleEngine.Validate(rule);
        if (!validation.IsValid)
        {
            return Failure(request, "INVALID_HEALTH_RULE", string.Join(" ", validation.Errors));
        }
        HealthRuleV1 saved = rule with
        {
            Name = rule.Name.Trim(),
            SensorId = string.IsNullOrWhiteSpace(rule.SensorId) ? null : rule.SensorId.Trim(),
            EmergencyProfileId = string.IsNullOrWhiteSpace(rule.EmergencyProfileId) ? null : rule.EmergencyProfileId.Trim()
        };
        await _store!.SaveSuiteEntityAsync(SuiteEntityKind.HealthRule, saved.Id, saved, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, saved);
    }

    private async Task<IpcResponse> DeleteHealthRuleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        DeleteHealthRuleRequestV1 payload = IpcJson.FromElement<DeleteHealthRuleRequestV1>(request.Payload)
            ?? throw new InvalidDataException("DeleteHealthRule requires a DeleteHealthRuleRequestV1 payload.");
        if (payload.SchemaVersion != DeleteHealthRuleRequestV1.CurrentSchemaVersion || string.IsNullOrWhiteSpace(payload.RuleId))
        {
            return Failure(request, "INVALID_HEALTH_RULE", "A valid health-rule ID is required.");
        }
        await _store!.DeleteSuiteEntityAsync(SuiteEntityKind.HealthRule, payload.RuleId, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, payload);
    }

    private async Task<IReadOnlyList<HealthAlertEventV1>> GetHealthAlertsAsync(CancellationToken cancellationToken) =>
        (await _store!.GetSuiteEntitiesAsync<HealthAlertEventV1>(SuiteEntityKind.HealthAlertEvent, cancellationToken).ConfigureAwait(false))
            .OrderByDescending(alert => alert.UpdatedAt)
            .Take(256)
            .ToArray();

    private async Task<IpcResponse> AcknowledgeHealthAlertAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        AcknowledgeHealthAlertRequestV1 payload = IpcJson.FromElement<AcknowledgeHealthAlertRequestV1>(request.Payload)
            ?? throw new InvalidDataException("AcknowledgeHealthAlert requires an AcknowledgeHealthAlertRequestV1 payload.");
        if (payload.SchemaVersion != AcknowledgeHealthAlertRequestV1.CurrentSchemaVersion || string.IsNullOrWhiteSpace(payload.AlertId))
        {
            return Failure(request, "INVALID_HEALTH_ALERT", "A valid alert ID is required.");
        }
        HealthAlertEventV1? alert = await _store!.GetSuiteEntityAsync<HealthAlertEventV1>(
            SuiteEntityKind.HealthAlertEvent,
            payload.AlertId,
            cancellationToken).ConfigureAwait(false);
        if (alert is null)
        {
            return Failure(request, "HEALTH_ALERT_NOT_FOUND", "The requested health alert does not exist.");
        }
        HealthAlertEventV1 acknowledged = alert.State == HealthAlertState.Cleared
            ? alert
            : alert with { State = HealthAlertState.Acknowledged, UpdatedAt = DateTimeOffset.UtcNow };
        await _store.SaveSuiteEntityAsync(SuiteEntityKind.HealthAlertEvent, acknowledged.Id, acknowledged, cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, acknowledged);
    }

    private async Task<SafetyRecoveryStatusV1> GetSafetyRecoveryStatusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FanCommissioningSessionV1> sessions = await _store!
            .GetSuiteEntitiesAsync<FanCommissioningSessionV1>(SuiteEntityKind.FanCommissioningSession, cancellationToken)
            .ConfigureAwait(false);
        return SafetyRecoveryPlanner.Build(_safetyRecoveryState, _rollbackBlocked, GetOperationStatus(), sessions);
    }

    private async Task<IpcResponse> SetSafeModeAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EnsureExpectedRevision(request);
        SetSafeModeRequestV1 payload = IpcJson.FromElement<SetSafeModeRequestV1>(request.Payload)
            ?? throw new InvalidDataException("SetSafeMode requires a SetSafeModeRequestV1 payload.");
        if (payload.SchemaVersion != SetSafeModeRequestV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(payload.Reason)
            || payload.Reason.Trim().Length > 256)
        {
            return Failure(request, "INVALID_SAFE_MODE", "A safe-mode reason up to 256 characters is required.");
        }
        _safetyRecoveryState = _safetyRecoveryState with
        {
            SafeModeEnabled = payload.Enabled,
            AutomationSuspended = payload.Enabled,
            UpdatedAt = DateTimeOffset.UtcNow,
            Reason = payload.Reason.Trim()
        };
        await _store!.SaveSuiteEntityAsync(
            SuiteEntityKind.SafetyRecoveryState,
            _safetyRecoveryState.Id,
            _safetyRecoveryState,
            cancellationToken).ConfigureAwait(false);
        IncrementSuiteRevision();
        return Success(request, await GetSafetyRecoveryStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<ActiveCoolingGraphRuntime> CreateActiveCoolingGraphAsync(
        string profileId,
        CoolingGraphV1 graph,
        SafetyLimits limits,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FanCommissioningSessionV1> sessions = await _store!
            .GetSuiteEntitiesAsync<FanCommissioningSessionV1>(SuiteEntityKind.FanCommissioningSession, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<FanCalibrationV2> calibrations = await _store
            .GetSuiteEntitiesAsync<FanCalibrationV2>(SuiteEntityKind.FanCalibration, cancellationToken)
            .ConfigureAwait(false);
        HardwareSnapshot snapshot = GetSnapshot();
        Dictionary<string, FanCalibrationV2> selected = new(StringComparer.Ordinal);
        foreach (CoolingGraphOutputV1 output in graph.Outputs)
        {
            CapabilityDescriptor? capability = snapshot.Capabilities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, output.CapabilityId, StringComparison.Ordinal));
            FanCommissioningSessionV1? session = sessions
                .Where(candidate => candidate.State == FanCommissioningState.Completed
                    && candidate.PhysicalHeaderObserved
                    && string.Equals(candidate.CapabilityId, output.CapabilityId, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .FirstOrDefault();
            FanCalibrationV2? calibration = session is null
                ? null
                : calibrations
                    .Where(candidate => string.Equals(candidate.CapabilityId, output.CapabilityId, StringComparison.Ordinal)
                        && string.Equals(candidate.CommissioningSessionId, session.Id, StringComparison.Ordinal))
                    .OrderByDescending(candidate => candidate.VerifiedAt)
                    .FirstOrDefault();
            if (calibration is null)
            {
                if (session is null
                    && capability is not null
                    && AdaptiveCoolingProfileFactory.CanActivateWithoutCalibration(capability, output))
                {
                    // Duty-percent automatic mode does not need an RPM map. The
                    // graph engine already supports a missing calibration and
                    // keeps the configured floor/full-maximum envelope.
                    continue;
                }

                throw new InvalidOperationException($"Cooling output '{output.CapabilityId}' lost its exact commissioned calibration before activation.");
            }

            string? safetyError = FanCalibrationPolicy.ValidateOutput(output, calibration);
            if (safetyError is not null)
            {
                throw new InvalidOperationException(safetyError);
            }
            selected.Add(output.CapabilityId, calibration);
        }

        return new ActiveCoolingGraphRuntime(profileId, graph, selected, limits);
    }

    /// <summary>
    /// Replaces the active graph as a verified pre-commit step of a hardware
    /// profile transaction. A null profile graph never calls this method, so a
    /// hardware-only profile preserves the independent base cooling policy.
    /// </summary>
    private async Task ReplaceActiveCoolingGraphTransactionAsync(
        ActiveCoolingGraphRuntime requested,
        CancellationToken cancellationToken)
    {
        await _coolingGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ActiveCoolingGraphRuntime? previous = _activeCoolingGraph;
        HardwareSnapshot snapshot = GetSnapshot();
        try
        {
            CoolingGraphRuntimeTick firstTick = requested.Runtime.Evaluate(
                requested.Graph,
                snapshot.Sensors,
                requested.Calibrations,
                requested.SafetyLimits.StalePollLimit,
                snapshot.CapturedAt);
            CoolingSafetyDecision firstDecision = requested.Supervisor.Evaluate(
                requested.Graph,
                firstTick,
                snapshot.Sensors,
                requested.SafetyLimits,
                snapshot.CapturedAt);
            if (firstDecision.State == CoolingRuntimeState.EmergencyMaximum)
            {
                throw new InvalidOperationException(
                    $"Cooling policy activation was refused before commit: {firstDecision.Reason}");
            }

            await ApplyCoolingGraphOutputsAsync(requested, snapshot, firstDecision.Evaluation, cancellationToken).ConfigureAwait(false);

            if (previous is not null)
            {
                HashSet<string> retainedOutputs = requested.Graph.Outputs
                    .Select(output => output.CapabilityId)
                    .ToHashSet(StringComparer.Ordinal);
                await ResetCoolingGraphOutputsAsync(
                    previous.Graph,
                    snapshot,
                    cancellationToken,
                    retainedOutputs).ConfigureAwait(false);
            }

            _activeCoolingGraph = requested;
            PublishCoolingStatus(requested, firstDecision, firstTick);
        }
        catch (Exception exception)
        {
            List<string> recoveryErrors = [];
            try
            {
                await ResetCoolingGraphOutputsAsync(requested.Graph, snapshot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception resetException)
            {
                recoveryErrors.Add($"new cooling policy reset failed: {resetException.Message}");
            }

            if (previous is not null)
            {
                try
                {
                    CoolingGraphRuntimeTick restoreTick = previous.Runtime.Evaluate(
                        previous.Graph,
                        snapshot.Sensors,
                        previous.Calibrations,
                        previous.SafetyLimits.StalePollLimit,
                        snapshot.CapturedAt);
                    CoolingSafetyDecision restoreDecision = previous.Supervisor.Evaluate(
                        previous.Graph,
                        restoreTick,
                        snapshot.Sensors,
                        previous.SafetyLimits,
                        snapshot.CapturedAt);
                    await ApplyCoolingGraphOutputsAsync(previous, snapshot, restoreDecision.Evaluation, CancellationToken.None).ConfigureAwait(false);
                    _activeCoolingGraph = previous;
                    PublishCoolingStatus(previous, restoreDecision, restoreTick);
                }
                catch (Exception restoreException)
                {
                    recoveryErrors.Add($"previous cooling policy restore failed: {restoreException.Message}");
                    _activeCoolingGraph = null;
                }
            }
            else
            {
                _activeCoolingGraph = null;
                PublishInactiveCoolingStatus("Cooling policy activation failed; no previous service-owned graph was active.");
            }

            if (recoveryErrors.Count > 0)
            {
                throw new HardwareStateUnknownException(
                    "cooling-policy",
                    "ReplaceActiveCoolingGraph",
                    $"{exception.Message} Recovery errors: {string.Join("; ", recoveryErrors)}",
                    exception);
            }

            throw;
        }
        finally
        {
            _coolingGraphGate.Release();
        }
    }

    private async Task TickCoolingGraphAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_rollbackBlocked)
        {
            CoolingRuntimeStatusV1 current = Volatile.Read(ref _coolingRuntimeStatus);
            if (current.State != CoolingRuntimeState.Inactive)
            {
                Volatile.Write(ref _coolingRuntimeStatus, current with
                {
                    State = CoolingRuntimeState.RecoveryRequired,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Reason = "Cooling updates are blocked because hardware default-state recovery is required."
                });
            }
            return;
        }

        if (!await _hardwareMutationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        bool coolingGateHeld = false;
        try
        {
            coolingGateHeld = await _coolingGraphGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!coolingGateHeld)
            {
                return;
            }

            ActiveCoolingGraphRuntime? active = _activeCoolingGraph;
            if (active is null)
            {
                return;
            }

            CoolingGraphRuntimeTick tick = active.Runtime.Evaluate(
                active.Graph,
                snapshot.Sensors,
                active.Calibrations,
                active.SafetyLimits.StalePollLimit,
                snapshot.CapturedAt);
            CoolingSafetyDecision decision = active.Supervisor.Evaluate(
                active.Graph,
                tick,
                snapshot.Sensors,
                active.SafetyLimits,
                snapshot.CapturedAt);
            string? excludedCapabilityId = GetActiveCoolingOperationCapabilityId(snapshot);
            if (decision.State == CoolingRuntimeState.EmergencyMaximum && excludedCapabilityId is not null)
            {
                RequestActiveOperationCancellation(excludedCapabilityId);
            }

            try
            {
                await ApplyCoolingGraphOutputsAsync(
                    active,
                    snapshot,
                    decision.Evaluation,
                    cancellationToken,
                    excludedCapabilityId).ConfigureAwait(false);
                PublishCoolingStatus(active, decision, tick, excludedCapabilityId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                CoolingSafetyDecision forced = active.Supervisor.ForceEmergency(
                    active.Graph,
                    snapshot.CapturedAt,
                    $"A cooling output update failed: {exception.Message}");
                try
                {
                    await ApplyCoolingGraphOutputsAsync(
                        active,
                        snapshot,
                        forced.Evaluation,
                        CancellationToken.None,
                        excludedCapabilityId).ConfigureAwait(false);
                    PublishCoolingStatus(active, forced, tick, excludedCapabilityId);
                }
                catch (Exception maximumException)
                {
                    await RecoverCoolingGraphFailureAsync(
                        active,
                        snapshot,
                        $"{forced.Reason} Maximum-cooling fallback also failed: {maximumException.Message}").ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (coolingGateHeld)
            {
                _coolingGraphGate.Release();
            }
            _hardwareMutationGate.Release();
        }
    }

    private async Task ApplyCoolingGraphOutputsAsync(
        ActiveCoolingGraphRuntime active,
        HardwareSnapshot snapshot,
        CoolingGraphEvaluation evaluation,
        CancellationToken cancellationToken,
        string? excludedCapabilityId = null)
    {
        foreach (CoolingGraphOutputV1 output in active.Graph.Outputs)
        {
            if (string.Equals(output.CapabilityId, excludedCapabilityId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!evaluation.OutputValues.TryGetValue(output.CapabilityId, out double target))
            {
                throw new InvalidOperationException($"Cooling graph '{active.Graph.Id}' did not produce '{output.CapabilityId}'.");
            }

            CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item => item.Id == output.CapabilityId)
                ?? throw new InvalidOperationException($"Cooling output '{output.CapabilityId}' is no longer available.");
            NumericRange range = capability.Range
                ?? throw new InvalidOperationException($"Cooling output '{capability.Name}' no longer exposes numeric bounds.");
            if (evaluation.Emergency)
            {
                // Emergency duty is fixed by the hardware capability, not by a
                // profile's output cap; profiles cannot weaken maximum cooling.
                target = range.Maximum;
            }
            if (capability.State is not (CapabilityAccessState.Verified or CapabilityAccessState.Experimental)
                || capability.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety)
                || !capability.CanResetToDefault
                || target < range.Minimum - 1e-6
                || target > range.Maximum + 1e-6)
            {
                throw new InvalidOperationException($"Cooling output '{capability.Name}' no longer meets its active safety contract.");
            }

            if (!evaluation.Emergency
                && active.LastApplied.TryGetValue(capability.Id, out double applied)
                && active.LastAppliedAt.TryGetValue(capability.Id, out DateTimeOffset appliedAt))
            {
                double seconds = Math.Max(0, (evaluation.Timestamp - appliedAt).TotalSeconds);
                double maximumDelta = target >= applied
                    ? output.StepUpPerSecond * seconds
                    : output.StepDownPerSecond * seconds;
                target = Math.Clamp(target, applied - maximumDelta, applied + maximumDelta);
            }

            double threshold = Math.Max(0.25, range.Step / 2d);
            bool changed = !active.LastApplied.TryGetValue(capability.Id, out double previous)
                || Math.Abs(previous - target) >= threshold;
            if (evaluation.Emergency || changed)
            {
                await ApplyCoolingDutyAsync(active, capability, target, cancellationToken).ConfigureAwait(false);
                active.LastAppliedAt[capability.Id] = evaluation.Timestamp;
            }
        }
    }

    private async Task ApplyCoolingDutyAsync(
        ActiveCoolingGraphRuntime active,
        CapabilityDescriptor capability,
        double dutyPercent,
        CancellationToken cancellationToken)
    {
        ProfileAction action = new(
            $"cooling-loop:{active.Graph.Id}:{capability.Id}",
            capability.AdapterId,
            capability.Id,
            ControlValue.FromNumeric(dutyPercent),
            Required: true,
            Order: 0);
        IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
        PreparedAction prepared = await adapter.PrepareAsync(action, cancellationToken).ConfigureAwait(false);
        await adapter.ApplyAsync(prepared, cancellationToken).ConfigureAwait(false);
        ActionVerification verification = await adapter.VerifyAsync(prepared, cancellationToken).ConfigureAwait(false);
        if (!verification.Success)
        {
            throw new InvalidOperationException($"Cooling output '{capability.Name}' did not read back after a graph update: {verification.Message}");
        }

        active.LastApplied[capability.Id] = dutyPercent;
    }

    private async Task RecoverCoolingGraphFailureAsync(
        ActiveCoolingGraphRuntime active,
        HardwareSnapshot snapshot,
        string reason)
    {
        List<string> errors = [];
        try
        {
            await ResetCoolingGraphOutputsAsync(active.Graph, snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add($"firmware/default recovery failed ({exception.Message})");
        }

        _activeCoolingGraph = null;
        if (errors.Count > 0)
        {
            _rollbackBlocked = true;
            Volatile.Write(ref _coolingRuntimeStatus, new CoolingRuntimeStatusV1(
                CoolingRuntimeStatusV1.CurrentSchemaVersion,
                active.ProfileId,
                active.Graph.Id,
                CoolingRuntimeState.RecoveryRequired,
                DateTimeOffset.UtcNow,
                active.Supervisor.EmergencySince,
                new Dictionary<string, double>(active.LastApplied, StringComparer.Ordinal),
                [],
                new Dictionary<string, int>(),
                $"RecoveryRequired: {reason} {string.Join("; ", errors)}"));
        }
        else
        {
            PublishInactiveCoolingStatus($"The active cooling writer failed; firmware/default control was restored and read back. {reason}");
        }
        ServiceLog.CoolingGraphEmergency(logger, active.Graph.Id, reason, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private string? GetActiveCoolingOperationCapabilityId(HardwareSnapshot snapshot)
    {
        HardwareOperationStatus? operation;
        lock (_operationSync)
        {
            operation = _operationStatus is not null && IsActive(_operationStatus.State)
                ? _operationStatus
                : null;
        }

        return SelectCoolingOperationExclusion(operation, snapshot);
    }

    internal static string? SelectCoolingOperationExclusion(
        HardwareOperationStatus? operation,
        HardwareSnapshot snapshot)
    {
        if (operation is null || !IsActive(operation.State))
        {
            return null;
        }

        CapabilityDescriptor? capability = snapshot.Capabilities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, operation.CapabilityId, StringComparison.Ordinal));
        return capability?.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
            ? capability.Id
            : null;
    }

    private void RequestActiveOperationCancellation(string capabilityId)
    {
        CancellationTokenSource? cancellation = null;
        lock (_operationSync)
        {
            if (_operationStatus is not null
                && IsActive(_operationStatus.State)
                && string.Equals(_operationStatus.CapabilityId, capabilityId, StringComparison.Ordinal))
            {
                cancellation = _operationCancellation;
            }
        }

        cancellation?.Cancel();
    }

    private void PublishCoolingStatus(
        ActiveCoolingGraphRuntime active,
        CoolingSafetyDecision decision,
        CoolingGraphRuntimeTick tick,
        string? excludedCapabilityId = null)
    {
        string reason = excludedCapabilityId is null
            ? decision.Reason
            : $"{decision.Reason} Output '{excludedCapabilityId}' is temporarily owned by an active cooling safety operation.";
        Volatile.Write(ref _coolingRuntimeStatus, new CoolingRuntimeStatusV1(
            CoolingRuntimeStatusV1.CurrentSchemaVersion,
            active.ProfileId,
            active.Graph.Id,
            decision.State,
            decision.Evaluation.Timestamp,
            decision.State == CoolingRuntimeState.EmergencyMaximum ? decision.EmergencySince : null,
            new Dictionary<string, double>(active.LastApplied, StringComparer.Ordinal),
            tick.HeldSensorIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            new Dictionary<string, int>(tick.StalePollCounts, StringComparer.Ordinal),
            reason));
    }

    private void PublishInactiveCoolingStatus(string reason) =>
        Volatile.Write(ref _coolingRuntimeStatus, CoolingRuntimeStatusV1.Inactive(reason));

    private async Task ResetCoolingGraphOutputsAsync(
        CoolingGraphV1 graph,
        HardwareSnapshot snapshot,
        CancellationToken cancellationToken,
        HashSet<string>? excludedCapabilityIds = null)
    {
        List<string> errors = [];
        foreach (CoolingGraphOutputV1 output in graph.Outputs)
        {
            if (excludedCapabilityIds?.Contains(output.CapabilityId) == true)
            {
                continue;
            }

            try
            {
                CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item => item.Id == output.CapabilityId)
                    ?? throw new InvalidOperationException("Cooling output disappeared.");
                if (!capability.CanResetToDefault)
                {
                    throw new InvalidOperationException("Cooling output has no firmware/default reset path.");
                }
                IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
                await adapter.ResetToDefaultAsync(capability.Id, cancellationToken).ConfigureAwait(false);
                if (adapter is not IHardwareStateVerifier verifier)
                {
                    throw new InvalidOperationException("Cooling output adapter does not support default-state read-back verification.");
                }

                HardwareStateVerification verification = await verifier
                    .VerifyDefaultStateAsync(capability.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (!verification.Success)
                {
                    throw new InvalidOperationException($"Default-state read-back failed: {verification.Message}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{output.CapabilityId}: {exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors));
        }
    }

    private async Task<IReadOnlyList<CoolingQualificationReportV1>> GetCoolingQualificationReportsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FanCommissioningSessionV1> sessions = await _store!
            .GetSuiteEntitiesAsync<FanCommissioningSessionV1>(SuiteEntityKind.FanCommissioningSession, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<FanCalibrationV2> calibrations = await _store
            .GetSuiteEntitiesAsync<FanCalibrationV2>(SuiteEntityKind.FanCalibration, cancellationToken)
            .ConfigureAwait(false);
        return DeviceQualificationPlanner.BuildCoolingReports(sessions, calibrations);
    }

    private async Task<HardwareEvidenceReportV1> BuildHardwareEvidenceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HealthAlertEventV1> alerts = await GetHealthAlertsAsync(cancellationToken).ConfigureAwait(false);
        SafetyRecoveryStatusV1 recovery = await GetSafetyRecoveryStatusAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CoolingQualificationReportV1> cooling = await GetCoolingQualificationReportsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AdapterTraceEvent> trace = await GetAdapterTraceAsync(cancellationToken).ConfigureAwait(false);
        return HardwareEvidenceBuilder.Build(
            GetSnapshot(),
            alerts,
            trace,
            recovery,
            DeviceQualificationPlanner.Build(GetSnapshot()),
            cooling,
            DateTimeOffset.UtcNow);
    }

    private async Task EvaluateHealthRulesAsync(HardwareSnapshot snapshot, CancellationToken cancellationToken)
    {
        IReadOnlyList<HealthRuleV1> rules = await _store!
            .GetSuiteEntitiesAsync<HealthRuleV1>(SuiteEntityKind.HealthRule, cancellationToken)
            .ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<HealthSystemSignal> signals = _healthSignalProbe.ReadSince(_lastHealthSignalScan, now);
        _lastHealthSignalScan = now;
        IReadOnlyList<HealthAlertEventV1> existing = await GetHealthAlertsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HealthAlertEventV1> changes = _healthRuleEngine.Evaluate(rules, snapshot.Sensors, signals, existing, now);
        foreach (HealthAlertEventV1 change in changes)
        {
            HealthAlertEventV1 persisted = change;
            if (change.State == HealthAlertState.Active && change.RequestedAction == HealthRuleActionKind.EnterSafeMode)
            {
                _safetyRecoveryState = _safetyRecoveryState with
                {
                    SafeModeEnabled = true,
                    AutomationSuspended = true,
                    UpdatedAt = now,
                    Reason = $"Health rule '{change.RuleName}' entered safe mode."
                };
                await _store.SaveSuiteEntityAsync(
                    SuiteEntityKind.SafetyRecoveryState,
                    _safetyRecoveryState.Id,
                    _safetyRecoveryState,
                    cancellationToken).ConfigureAwait(false);
                persisted = change with { ActionExecuted = true, ActionResult = "Safe mode was enabled; no hardware write was issued." };
            }
            else if (change.State == HealthAlertState.Active && change.RequestedAction == HealthRuleActionKind.RequestEmergencyProfile)
            {
                persisted = change with { ActionResult = _safetyRecoveryState.SafeModeEnabled
                    ? "Emergency profile request is suppressed while safe mode is active."
                    : "Emergency profile request is queued for explicit operator confirmation; no automatic hardware write was issued." };
            }
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.HealthAlertEvent, persisted.Id, persisted, cancellationToken).ConfigureAwait(false);
            IncrementSuiteRevision();
        }
        await PruneHealthAlertsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneHealthAlertsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HealthAlertEventV1> all = await _store!
            .GetSuiteEntitiesAsync<HealthAlertEventV1>(SuiteEntityKind.HealthAlertEvent, cancellationToken)
            .ConfigureAwait(false);
        foreach (HealthAlertEventV1 obsolete in all
            .OrderByDescending(alert => alert.UpdatedAt)
            .Skip(512))
        {
            await _store.DeleteSuiteEntityAsync(SuiteEntityKind.HealthAlertEvent, obsolete.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<AdapterTraceEvent>> GetAdapterTraceAsync(CancellationToken cancellationToken)
    {
        List<AdapterTraceEvent> trace = [];
        foreach (ITraceableAdapter adapter in _coordinator!.Adapters.OfType<ITraceableAdapter>())
        {
            await foreach (AdapterTraceEvent item in adapter.ReadTraceAsync(cancellationToken).ConfigureAwait(false))
            {
                trace.Add(item);
            }
        }
        return trace
            .OrderByDescending(item => item.Timestamp)
            .Take(512)
            .OrderBy(item => item.Timestamp)
            .ToArray();
    }

    private IHardwareAdapter FindAdapter(string adapterId) =>
        _coordinator!.Adapters.FirstOrDefault(item => string.Equals(item.Manifest.Id, adapterId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Adapter '{adapterId}' is unavailable.");

    private WindowsTakeoverExecutionGate GetTakeoverGate() =>
        _takeoverGate ?? throw new InvalidOperationException("The takeover execution policy is not initialised.");

    private WindowsDriverUpdateExecutor GetUpdateExecutor() =>
        _updateExecutor ?? throw new InvalidOperationException("The driver update executor is not initialised.");

    private void EnsureExpectedRevision(IpcRequest request)
    {
        if (request.ExpectedStateRevision is long expected && expected != CurrentRevision)
        {
            throw new StateRevisionException(expected, CurrentRevision);
        }
    }

    private void IncrementSuiteRevision() => Interlocked.Increment(ref _suiteRevision);

    private static HardwareOperationStatus CreateOperationStatus(
        HardwareOperationKind kind,
        CapabilityDescriptor capability,
        string message)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new HardwareOperationStatus(
            Guid.NewGuid().ToString("N"),
            kind,
            HardwareOperationState.Pending,
            capability.Id,
            capability.Name,
            now,
            now,
            0,
            message,
            null,
            null,
            null);
    }

    private static bool IsActive(HardwareOperationState state) => state is
        HardwareOperationState.Pending or
        HardwareOperationState.Running or
        HardwareOperationState.Screening;

    private async Task RecoverPendingOperationAsync(CancellationToken cancellationToken)
    {
        HardwareOperationStatus? latest = await _store!.GetLatestOperationAsync(cancellationToken).ConfigureAwait(false);
        HardwareOperationStatus? pending = await _store.GetPendingOperationAsync(cancellationToken).ConfigureAwait(false);
        lock (_operationSync)
        {
            _operationStatus = latest;
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            CapabilityDescriptor capability = GetSnapshot().Capabilities.FirstOrDefault(
                item => string.Equals(item.Id, pending.CapabilityId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The pending operation's capability is no longer available.");
            IHardwareAdapter adapter = FindAdapter(capability.AdapterId);
            await adapter.ResetToDefaultAsync(capability.Id, cancellationToken).ConfigureAwait(false);
            HardwareOperationStatus recovered = pending with
            {
                State = HardwareOperationState.Aborted,
                UpdatedAt = DateTimeOffset.UtcNow,
                ProgressPercent = 100,
                Message = "Boot sentinel prevented candidate reapplication; firmware/default control was restored.",
                Error = null
            };
            lock (_operationSync)
            {
                _operationStatus = recovered;
            }

            await _store.SaveOperationAsync(recovered, cancellationToken).ConfigureAwait(false);
            await _store.ClearPendingOperationAsync(recovered.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _rollbackBlocked = true;
            HardwareOperationStatus blocked = pending with
            {
                State = HardwareOperationState.RecoveryRequired,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "A pending hardware operation could not be returned to firmware/default control.",
                Error = exception.Message
            };
            lock (_operationSync)
            {
                _operationStatus = blocked;
            }

            await _store.SaveOperationAsync(blocked, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecoverPendingUpdateTransactionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UpdateTransactionV1> pending = await _store!
            .GetSuiteEntitiesAsync<UpdateTransactionV1>(SuiteEntityKind.UpdateTransaction, cancellationToken)
            .ConfigureAwait(false);
        foreach (UpdateTransactionV1 transaction in pending
            .Where(item => item.State == UpdateTransactionState.PendingReboot)
            .OrderBy(item => item.UpdatedAt))
        {
            try
            {
                await (_updateCoordinator
                    ?? throw new InvalidOperationException("The update coordinator is not initialised."))
                    .ResumeAfterRebootAsync(transaction, cancellationToken)
                    .ConfigureAwait(false);
                IncrementSuiteRevision();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                UpdateTransactionV1 recovery = transaction with
                {
                    State = UpdateTransactionState.RecoveryRequired,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = $"Post-reboot driver verification could not complete: {exception.Message}"
                };
                await _store.SaveSuiteEntityAsync(
                    SuiteEntityKind.UpdateTransaction,
                    recovery.Id,
                    recovery,
                    CancellationToken.None).ConfigureAwait(false);
                IncrementSuiteRevision();
            }
        }
    }

    private async Task RecoverPendingTransactionAsync(CancellationToken cancellationToken)
    {
        ProfileTransaction? pending = await _store!.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        HardwareControlLeaseV1? previousLease = await _store.GetSuiteEntityAsync<HardwareControlLeaseV1>(
            SuiteEntityKind.HardwareControlLease,
            HardwareControlLeaseV1.DefaultId,
            cancellationToken).ConfigureAwait(false);
        // Migration safety: older builds had no lease marker, so the latest
        // committed profile is treated as potentially active exactly once.
        ProfileTransaction? legacyCommitted = previousLease is null
            ? await _store.GetLatestCommittedAsync(cancellationToken).ConfigureAwait(false)
            : null;
        HardwareStartupRecoveryPlan startupPlan = HardwareControlRecoveryPlanner.BuildStartupPlan(
            previousLease,
            pending,
            legacyCommitted);
        IReadOnlyList<HardwareControlLeaseItemV1> controls = startupPlan.Controls;
        bool uncleanStartup = startupPlan.RequiresRecovery;
        HardwareRecoveryResult recovery = new(true, [], []);

        if (uncleanStartup)
        {
            if (pending is not null)
            {
                ServiceLog.RecoveringTransaction(logger, pending.Id);
            }

            SetGpuTransportRecoveryGate(armed: true);
            try
            {
                recovery = await _engine!.RestoreDefaultsAsync(controls, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SetGpuTransportRecoveryGate(armed: false);
                _gpuFanArmed = false;
                _gpuPowerArmed = false;
                _gpuClockArmed = false;
            }

            _rollbackBlocked = !recovery.AllDefaultsVerified;
            if (pending is not null)
            {
                ProfileTransaction recovered = pending with
                {
                    State = recovery.AllDefaultsVerified
                        ? ProfileTransactionState.RolledBack
                        : ProfileTransactionState.RecoveryRequired,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = recovery.AllDefaultsVerified
                        ? "Unclean-start recovery restored and read back every leased capability at its default state."
                        : $"RecoveryRequired: {string.Join("; ", recovery.Errors)}"
                };
                await _store.SaveAsync(recovered, cancellationToken).ConfigureAwait(false);
                if (recovery.AllDefaultsVerified)
                {
                    await _store.ClearPendingAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        HardwareControlLeaseV1 runningMarker = HardwareControlRecoveryPlanner.CreateRunningMarker(
            _serviceInstanceId,
            previousLease,
            startupPlan,
            recovery,
            DateTimeOffset.UtcNow);
        await _store.SaveSuiteEntityAsync(
            SuiteEntityKind.HardwareControlLease,
            runningMarker.Id,
            runningMarker,
            cancellationToken).ConfigureAwait(false);
    }

    private void SetGpuTransportRecoveryGate(bool armed)
    {
        _gpuFanTransport?.SetArmed(armed);
        _gpuPowerTransport?.SetArmed(armed);
        _gpuClockTransport?.SetArmed(armed);
    }

    private async Task CompleteCleanShutdownAsync()
    {
        if (_store is null || _engine is null)
        {
            return;
        }

        HardwareControlLeaseV1? lease = await _store.GetSuiteEntityAsync<HardwareControlLeaseV1>(
            SuiteEntityKind.HardwareControlLease,
            HardwareControlLeaseV1.DefaultId,
            CancellationToken.None).ConfigureAwait(false);
        HardwareControlLeaseItemV1[] controls = lease?.Controls.ToArray() ?? [];
        SetGpuTransportRecoveryGate(armed: true);
        HardwareRecoveryResult recovery;
        try
        {
            recovery = await _engine.RestoreDefaultsAsync(controls, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            SetGpuTransportRecoveryGate(armed: false);
            _gpuFanArmed = false;
            _gpuPowerArmed = false;
            _gpuClockArmed = false;
        }

        HardwareControlLeaseV1 marker = HardwareControlRecoveryPlanner.CreateShutdownMarker(
            _serviceInstanceId,
            lease,
            recovery,
            DateTimeOffset.UtcNow);
        await _store.SaveSuiteEntityAsync(
            SuiteEntityKind.HardwareControlLease,
            marker.Id,
            marker,
            CancellationToken.None).ConfigureAwait(false);
        _rollbackBlocked = !recovery.AllDefaultsVerified;
    }

    private IpcResponse Success<T>(IpcRequest request, T payload) => new(
        ProtocolConstants.Version,
        request.RequestId,
        true,
        CurrentRevision,
        null,
        null,
        IpcJson.ToElement(payload));

    private static bool IsMutatingCommand(IpcCommand command) => IpcCommandPolicy.IsMutation(command);

    private static bool IsUserAgentCommand(IpcCommand command) => command is
        IpcCommand.GetWorkflows or IpcCommand.SaveWorkflow or IpcCommand.DeleteWorkflow or
        IpcCommand.GetLightingScenes or IpcCommand.SaveLightingScene or
        IpcCommand.GetEffectGraphs or IpcCommand.SaveEffectGraph or IpcCommand.RenderEffectFrame or
        IpcCommand.GetGames or IpcCommand.SaveGame or IpcCommand.ScanGames or
        IpcCommand.GetMacros or IpcCommand.SaveMacro or IpcCommand.ExecuteMacro or
        IpcCommand.GetMacroRecordingSessions or IpcCommand.GetMacroRecordingStatus or
        IpcCommand.BeginMacroRecording or IpcCommand.StopMacroRecording or
        IpcCommand.CancelMacroRecording or IpcCommand.RecoverMacroRecording or
        IpcCommand.GetScripts or IpcCommand.SaveScript or IpcCommand.ExecuteScript or
        IpcCommand.GetOsdLayouts or IpcCommand.SaveOsdLayout or
        IpcCommand.GetOsdPresentationSettings or IpcCommand.SaveOsdPresentationSettings or
        IpcCommand.GetMonitoringPreferences or IpcCommand.SaveMonitoringPreferences or
        IpcCommand.GetOverlayStatus or IpcCommand.GetWgcRecordingPreflight or
        IpcCommand.GetCapturePresets or IpcCommand.SaveCapturePreset or
        IpcCommand.GetCaptureTargets or IpcCommand.CaptureDesktopSnapshot or
        IpcCommand.StartVideoRecording or IpcCommand.StopVideoRecording or
        IpcCommand.GetVideoRecordingStatus or
        IpcCommand.GetRtssOsdBridgeStatus or IpcCommand.GetRtssFrameStats or
        IpcCommand.PublishRtssOsdText or IpcCommand.ReleaseRtssOsd or
        IpcCommand.StartFrametimeBenchmark or IpcCommand.StopFrametimeBenchmark or
        IpcCommand.GetFrametimeBenchmarkStatus or
        IpcCommand.StartPresentMonBenchmark or IpcCommand.StopPresentMonBenchmark or
        IpcCommand.GetPresentMonBenchmarkStatus or
        IpcCommand.GetMonitorBrightnesses or IpcCommand.SetMonitorBrightness or
        IpcCommand.RunInteractiveFanPreflight or
        IpcCommand.DiscoverUpdates;

    private IpcResponse Failure(IpcRequest request, string code, string error) => new(
        ProtocolConstants.Version,
        request.RequestId,
        false,
        CurrentRevision,
        code,
        error,
        null);

    private IpcResponse FailureWithPayload<T>(IpcRequest request, string code, string error, T payload) => new(
        ProtocolConstants.Version,
        request.RequestId,
        false,
        CurrentRevision,
        code,
        error,
        IpcJson.ToElement(payload));

    private async Task<SqliteStateStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable("PCHELPER_DATA_DIR");
        string dataDirectory = configured ?? DataPaths.GetDefaultDataDirectory();
        SqliteStateStore store;
        try
        {
            store = new SqliteStateStore(Path.Combine(dataDirectory, "state.db"));
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _dataDirectory = dataDirectory;
        }
        catch (UnauthorizedAccessException) when (configured is null && Environment.UserInteractive)
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCHelper",
                "Development");
            store = new SqliteStateStore(Path.Combine(fallback, "state.db"));
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _dataDirectory = fallback;
            ServiceLog.UsingFallbackDirectory(logger, fallback);
        }

        return store;
    }

    private AdapterPackManager CreateAdapterPackManager()
    {
        string dataDirectory = _dataDirectory ?? throw new InvalidOperationException("The data directory is not initialised.");
        HashSet<string> developmentHashes = new(StringComparer.OrdinalIgnoreCase);
#if DEBUG
        string? allowlisted = Environment.GetEnvironmentVariable("PCHELPER_DEV_PACK_HASHES");
        if (!string.IsNullOrWhiteSpace(allowlisted))
        {
            foreach (string hash in allowlisted.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
                {
                    developmentHashes.Add(hash);
                }
            }
        }
#endif
        return new AdapterPackManager(
            Path.Combine(dataDirectory, "AdapterPacks"),
            new Dictionary<string, byte[]>(),
            developmentHashes);
    }

    private void EnsureInitialised()
    {
        if (_store is null || _coordinator is null || _engine is null || _updateExecutor is null || _updateCoordinator is null)
        {
            throw new InvalidOperationException("RigPilot runtime has not been initialised.");
        }
    }

    private sealed class ActiveCoolingGraphRuntime(
        string profileId,
        CoolingGraphV1 graph,
        IReadOnlyDictionary<string, FanCalibrationV2> calibrations,
        SafetyLimits safetyLimits)
    {
        public string ProfileId { get; } = profileId;
        public CoolingGraphV1 Graph { get; } = graph;
        public IReadOnlyDictionary<string, FanCalibrationV2> Calibrations { get; } = calibrations;
        public SafetyLimits SafetyLimits { get; } = safetyLimits;
        public CoolingGraphRuntime Runtime { get; } = new();
        public CoolingSafetySupervisor Supervisor { get; } = new();
        public Dictionary<string, double> LastApplied { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> LastAppliedAt { get; } = new(StringComparer.Ordinal);
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
