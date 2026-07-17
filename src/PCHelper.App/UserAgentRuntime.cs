using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Security.Principal;
using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public sealed class UserAgentRuntime : IAsyncDisposable
{
    private readonly string _currentUser = WindowsIdentity.GetCurrent().Name;
    private readonly SqliteStateStore _store;
    private readonly SemaphoreSlim _macroGate = new(1, 1);
    private readonly SemaphoreSlim _effectGate = new(1, 1);
    private readonly IMacroRecorder _macroRecorder;
    private readonly IDesktopSnapshotBackend _desktopSnapshots;
    private readonly IDesktopVideoRecorder _videoRecorder;
    private readonly IRtssOsdBridge _rtssOsd;
    private readonly IFrametimeBenchmarkRecorder _frametimeBenchmark;
    private readonly IFrametimeBenchmarkRecorder _presentMonBenchmark;
    private readonly IMonitorBrightnessBackend _monitorBrightness;
    private readonly IInteractiveFanPreflightLauncher _interactiveFanPreflight;
    private readonly HashSet<string> _gameBarPackageSids;
    private readonly object _macroRecordingSync = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CaptureSnapshotResultV1> _snapshotIdempotency = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _monitorBrightnessGate = new(1, 1);
    private readonly ConcurrentDictionary<string, MonitorBrightnessApplyResultV1> _monitorBrightnessIdempotency = new(StringComparer.Ordinal);
    private long _revision;
    private MacroRecordingSessionV1? _macroRecording;
    private bool _macroRecordingOwnsGate;

    public UserAgentRuntime(
        string? stateRoot = null,
        IMacroRecorder? macroRecorder = null,
        IEnumerable<System.Security.Principal.SecurityIdentifier>? gameBarPackageSids = null,
        IDesktopSnapshotBackend? desktopSnapshots = null,
        IInteractiveFanPreflightLauncher? interactiveFanPreflight = null,
        IMonitorBrightnessBackend? monitorBrightness = null,
        IDesktopVideoRecorder? videoRecorder = null,
        IRtssOsdBridge? rtssOsdBridge = null,
        IFrametimeBenchmarkRecorder? frametimeBenchmark = null,
        IFrametimeBenchmarkRecorder? presentMonBenchmark = null)
    {
        string resolvedStateRoot = stateRoot is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCHelper",
                "UserAgent")
            : Path.GetFullPath(stateRoot);
        _store = new SqliteStateStore(Path.Combine(resolvedStateRoot, "state.db"));
        _macroRecorder = macroRecorder ?? new WindowsMacroInputRecorder();
        _desktopSnapshots = desktopSnapshots ?? new WindowsDesktopSnapshotBackend();
        _videoRecorder = videoRecorder ?? new WindowsDesktopVideoRecorder(() => _desktopSnapshots!.DiscoverTargets());
        _rtssOsd = rtssOsdBridge ?? new RtssSharedMemoryBridge();
        _frametimeBenchmark = frametimeBenchmark ?? new FrametimeBenchmarkRecorder(() => _rtssOsd!.ReadFrameStats());
        _presentMonBenchmark = presentMonBenchmark ?? new PresentMonBenchmarkRecorder(new PresentMonSessionFactory());
        _monitorBrightness = monitorBrightness ?? new WindowsMonitorBrightnessBackend();
        _interactiveFanPreflight = interactiveFanPreflight ?? new ElevatedInteractiveFanPreflightLauncher();
        _gameBarPackageSids = new HashSet<string>(
            gameBarPackageSids?.Select(sid => sid.Value) ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedMacroRecordingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcResponse> HandleRequestAsync(
        IpcRequest request,
        IpcClientContext client,
        CancellationToken cancellationToken)
    {
        if (IsTrustedGameBarClient(client))
        {
            return HandleGameBarRequest(request);
        }
        if (!string.Equals(client.UserName, _currentUser, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(request, "NOT_CURRENT_USER", "User-agent commands are available only to the signed-in user that owns this tray agent.");
        }
        if (request.ExpectedStateRevision is long expected && expected != Interlocked.Read(ref _revision))
        {
            return Failure(request, "STATE_REVISION_MISMATCH", $"Expected revision {expected}; current revision is {_revision}.");
        }

        try
        {
            return request.Command switch
            {
                IpcCommand.Handshake => Success(request, new HandshakeResponseV2(
                    ProtocolConstants.Version,
                    ProtocolConstants.LegacyReadOnlyVersion,
                    typeof(UserAgentRuntime).Assembly.GetName().Version?.ToString() ?? "0.5.0-alpha",
                    _revision,
                    ["workflows", "effects", "effect-host", "games", "macros", "macro-recording", "scripts", "osd", "osd-presentation", "monitoring-preferences", "monitoring-comparison", "overlay-status", "capture", "desktop-snapshot", "monitor-brightness", "wgc-recording-preflight", "interactive-fan-preflight", "rtss-osd", "frametime-benchmark", "presentmon-benchmark"])),
                IpcCommand.GetWorkflows => await GetAsync<AutomationWorkflowV1>(request, SuiteEntityKind.AutomationWorkflow, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveWorkflow => await SaveWorkflowAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.DeleteWorkflow => await DeleteAsync(request, SuiteEntityKind.AutomationWorkflow, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetLightingScenes => await GetAsync<LightingSceneV1>(request, SuiteEntityKind.LightingScene, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveLightingScene => await SaveLightingSceneAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetEffectGraphs => await GetAsync<EffectGraphV1>(request, SuiteEntityKind.EffectGraph, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveEffectGraph => await SaveEffectGraphAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.RenderEffectFrame => await RenderEffectFrameAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetGames => await GetAsync<GameEntryV1>(request, SuiteEntityKind.GameEntry, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveGame => await SaveGameAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ScanGames => await ScanGamesAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetMacros => await GetAsync<MacroV1>(request, SuiteEntityKind.Macro, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveMacro => await SaveMacroAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ExecuteMacro => await ExecuteMacroAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetMacroRecordingSessions => await GetAsync<MacroRecordingSessionV1>(request, SuiteEntityKind.MacroRecordingSession, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetMacroRecordingStatus => GetMacroRecordingStatus(request),
                IpcCommand.BeginMacroRecording => await BeginMacroRecordingAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StopMacroRecording => await StopMacroRecordingAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.CancelMacroRecording => await CancelMacroRecordingAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.RecoverMacroRecording => await RecoverMacroRecordingAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetScripts => await GetAsync<ScriptActionV1>(request, SuiteEntityKind.ScriptAction, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveScript => await SaveScriptAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.ExecuteScript => await ExecuteScriptAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetOsdLayouts => await GetAsync<OsdLayoutV1>(request, SuiteEntityKind.OsdLayout, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveOsdLayout => await SaveOsdLayoutAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetOsdPresentationSettings => await GetOsdPresentationSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveOsdPresentationSettings => await SaveOsdPresentationSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetMonitoringPreferences => await GetMonitoringPreferencesAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveMonitoringPreferences => await SaveMonitoringPreferencesAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetMonitoringComparisonLayout => await GetMonitoringComparisonLayoutAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveMonitoringComparisonLayout => await SaveMonitoringComparisonLayoutAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetOverlayStatus => Success(request, OverlayBridgeProbe.Probe()),
                IpcCommand.GetWgcRecordingPreflight => Success(request, OverlayBridgeProbe.ProbeRecordingPreflight()),
                IpcCommand.GetCapturePresets => await GetAsync<CapturePresetV1>(request, SuiteEntityKind.CapturePreset, cancellationToken).ConfigureAwait(false),
                IpcCommand.SaveCapturePreset => await SaveCapturePresetAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.GetCaptureTargets => Success(request, _desktopSnapshots.DiscoverTargets()),
                IpcCommand.CaptureDesktopSnapshot => await CaptureDesktopSnapshotAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StartVideoRecording => await StartVideoRecordingAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.StopVideoRecording => Success(request, await _videoRecorder.StopAsync(cancellationToken).ConfigureAwait(false)),
                IpcCommand.GetVideoRecordingStatus => Success(request, _videoRecorder.Status),
                IpcCommand.GetRtssOsdBridgeStatus => Success(request, _rtssOsd.Status),
                IpcCommand.GetRtssFrameStats => Success(request, _rtssOsd.ReadFrameStats()),
                IpcCommand.PublishRtssOsdText => PublishRtssOsdText(request),
                IpcCommand.ReleaseRtssOsd => ReleaseRtssOsd(request),
                IpcCommand.StartFrametimeBenchmark => StartBenchmark(request, _frametimeBenchmark),
                IpcCommand.StopFrametimeBenchmark => Success(request, _frametimeBenchmark.StopBenchmark()),
                IpcCommand.GetFrametimeBenchmarkStatus => Success(request, _frametimeBenchmark.Status),
                IpcCommand.StartPresentMonBenchmark => StartBenchmark(request, _presentMonBenchmark),
                IpcCommand.StopPresentMonBenchmark => Success(request, _presentMonBenchmark.StopBenchmark()),
                IpcCommand.GetPresentMonBenchmarkStatus => Success(request, _presentMonBenchmark.Status),
                IpcCommand.GetMonitorBrightnesses => Success(request, _monitorBrightness.Discover()),
                IpcCommand.SetMonitorBrightness => await SetMonitorBrightnessAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.RunInteractiveFanPreflight => await RunInteractiveFanPreflightAsync(request, cancellationToken).ConfigureAwait(false),
                IpcCommand.DiscoverUpdates => Success(request, UpdateDiscoveryResultV1.NoSourceConfigured()),
                _ => Failure(request, "WRONG_EXECUTION_CONTEXT", $"Command {request.Command} is not owned by the user agent.")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure(request, "USER_AGENT_COMMAND_FAILED", exception.Message);
        }
    }

    private bool IsTrustedGameBarClient(IpcClientContext client) =>
        client.IsAppContainer
        && !string.IsNullOrWhiteSpace(client.UserSid)
        && _gameBarPackageSids.Contains(client.UserSid);

    private IpcResponse HandleGameBarRequest(IpcRequest request) => request.Command switch
    {
        IpcCommand.Handshake => Success(request, new HandshakeResponseV2(
            ProtocolConstants.Version,
            ProtocolConstants.LegacyReadOnlyVersion,
            typeof(UserAgentRuntime).Assembly.GetName().Version?.ToString() ?? "0.5.0-alpha",
            Interlocked.Read(ref _revision),
            ["gamebar-widget", "overlay-status"])),
        IpcCommand.GetOverlayStatus => Success(request, OverlayBridgeProbe.Probe()),
        _ => Failure(
            request,
            "GAMEBAR_READ_ONLY",
            "The Game Bar package may request only its read-only user-agent overlay status. Hardware, profile, macro, script, capture, and ownership commands are unavailable."),
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _macroRecorder.CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The recorder is user-session-only. Shutdown must continue even if
            // Windows input polling is already unavailable.
        }
        finally
        {
            lock (_macroRecordingSync)
            {
                _macroRecording = null;
                if (_macroRecordingOwnsGate)
                {
                    _macroRecordingOwnsGate = false;
                    _macroGate.Release();
                }
            }
        }
        await _macroRecorder.DisposeAsync().ConfigureAwait(false);
        _frametimeBenchmark.Dispose();
        _presentMonBenchmark.Dispose();
        _rtssOsd.Dispose();
        _macroGate.Dispose();
        _effectGate.Dispose();
        _snapshotGate.Dispose();
        _monitorBrightnessGate.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<IpcResponse> GetAsync<T>(IpcRequest request, SuiteEntityKind kind, CancellationToken cancellationToken) =>
        Success(request, await _store.GetSuiteEntitiesAsync<T>(kind, cancellationToken).ConfigureAwait(false));

    private async Task<IpcResponse> SaveWorkflowAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        AutomationWorkflowV1 workflow = Payload<AutomationWorkflowV1>(request);
        if (workflow.SchemaVersion != AutomationWorkflowV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(workflow.Id)
            || string.IsNullOrWhiteSpace(workflow.Name)
            || workflow.Triggers.Count == 0
            || workflow.Actions.Count == 0
            || workflow.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != workflow.Actions.Count)
        {
            return Failure(request, "INVALID_WORKFLOW", "Workflow identity, triggers, actions, and unique action IDs are required.");
        }
        return await SaveAsync(request, SuiteEntityKind.AutomationWorkflow, workflow.Id, workflow, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IpcResponse> SaveLightingSceneAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        LightingSceneV1 scene = Payload<LightingSceneV1>(request);
        if (scene.SchemaVersion != LightingSceneV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(scene.Id)
            || scene.BrightnessPercent is < 0 or > 100
            || scene.Zones.Select(zone => zone.Id).Distinct(StringComparer.Ordinal).Count() != scene.Zones.Count
            || scene.Zones.Any(zone => zone.LedIndices.Count == 0 || zone.LedIndices.Any(index => index < 0)))
        {
            return Failure(request, "INVALID_LIGHTING_SCENE", "Lighting scene brightness and physical LED zones are invalid.");
        }
        return await SaveAsync(request, SuiteEntityKind.LightingScene, scene.Id, scene, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IpcResponse> SaveEffectGraphAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EffectGraphV1 graph = Payload<EffectGraphV1>(request);
        SuiteValidationResult validation = EffectGraphValidator.Validate(graph);
        return validation.IsValid
            ? await SaveAsync(request, SuiteEntityKind.EffectGraph, graph.Id, graph, cancellationToken).ConfigureAwait(false)
            : Failure(request, "INVALID_EFFECT_GRAPH", string.Join(" ", validation.Errors));
    }

    private async Task<IpcResponse> RenderEffectFrameAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        EffectRenderRequestV1 payload = Payload<EffectRenderRequestV1>(request);
        if (!await _effectGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Failure(request, "EFFECT_ACTIVE", "Another effect frame is already rendering.");
        }
        try
        {
            EffectRenderResultV1 result = await LaunchEffectHostAsync(payload, cancellationToken).ConfigureAwait(false);
            return result.Completed
                ? Success(request, result)
                : FailureWithPayload(request, result.TimedOut ? "EFFECT_TIMEOUT" : "EFFECT_FAILED", result.Error ?? "Effect rendering failed.", result);
        }
        finally
        {
            _effectGate.Release();
        }
    }

    private async Task<IpcResponse> RunInteractiveFanPreflightAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        InteractiveFanPreflightRequestV1 payload = Payload<InteractiveFanPreflightRequestV1>(request);
        if (payload.SchemaVersion != InteractiveFanPreflightRequestV1.CurrentSchemaVersion
            || !ElevatedInteractiveFanPreflightLauncher.IsSupportedCapabilityId(payload.CapabilityId))
        {
            return Failure(
                request,
                "INVALID_INTERACTIVE_PREFLIGHT",
                "The elevated diagnostic accepts only a bounded LibreHardwareMonitor LPC cooling capability.");
        }

        InteractiveFanPreflightResultV1 result = await _interactiveFanPreflight
            .RunAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        return Success(request, result);
    }

    private async Task<IpcResponse> SaveGameAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        GameEntryV1 game = Payload<GameEntryV1>(request);
        if (game.SchemaVersion != GameEntryV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(game.Id)
            || string.IsNullOrWhiteSpace(game.Name)
            || !Path.IsPathFullyQualified(game.ExecutablePath)
            || game.WorkflowIds is null
            || game.MacroIds is not null && game.MacroIds.Any(string.IsNullOrWhiteSpace))
        {
            return Failure(request, "INVALID_GAME", "Game identity and an absolute executable path are required.");
        }
        string? referenceError = await ValidateGameReferencesAsync(game, cancellationToken).ConfigureAwait(false);
        if (referenceError is not null)
        {
            return Failure(request, "INVALID_GAME_REFERENCE", referenceError);
        }
        return await SaveAsync(request, SuiteEntityKind.GameEntry, game.Id, game, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ValidateGameReferencesAsync(GameEntryV1 game, CancellationToken cancellationToken)
    {
        if (game.WorkflowIds.Distinct(StringComparer.Ordinal).Count() != game.WorkflowIds.Count
            || (game.MacroIds ?? []).Distinct(StringComparer.Ordinal).Count() != (game.MacroIds ?? []).Count)
        {
            return "Game bundle references must not contain duplicates.";
        }

        (string? Id, SuiteEntityKind Kind, string Label)[] singleReferences =
        [
            (game.LightingSceneId, SuiteEntityKind.LightingScene, "lighting scene"),
            (game.OsdLayoutId, SuiteEntityKind.OsdLayout, "OSD layout"),
            (game.CapturePresetId, SuiteEntityKind.CapturePreset, "capture preset")
        ];
        foreach ((string? id, SuiteEntityKind kind, string label) in singleReferences)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            if (!await SuiteEntityExistsAsync(kind, id, cancellationToken).ConfigureAwait(false))
            {
                return $"The selected {label} '{id}' does not exist in this user session.";
            }
        }
        foreach (string workflowId in game.WorkflowIds)
        {
            if (!await SuiteEntityExistsAsync(SuiteEntityKind.AutomationWorkflow, workflowId, cancellationToken).ConfigureAwait(false))
            {
                return $"Workflow '{workflowId}' does not exist in this user session.";
            }
        }
        foreach (string macroId in game.MacroIds ?? [])
        {
            if (!await SuiteEntityExistsAsync(SuiteEntityKind.Macro, macroId, cancellationToken).ConfigureAwait(false))
            {
                return $"Macro '{macroId}' does not exist in this user session.";
            }
        }
        return null;
    }

    private async Task<bool> SuiteEntityExistsAsync(
        SuiteEntityKind kind,
        string id,
        CancellationToken cancellationToken) => kind switch
    {
        SuiteEntityKind.LightingScene => await _store.GetSuiteEntityAsync<LightingSceneV1>(kind, id, cancellationToken).ConfigureAwait(false) is not null,
        SuiteEntityKind.OsdLayout => await _store.GetSuiteEntityAsync<OsdLayoutV1>(kind, id, cancellationToken).ConfigureAwait(false) is not null,
        SuiteEntityKind.CapturePreset => await _store.GetSuiteEntityAsync<CapturePresetV1>(kind, id, cancellationToken).ConfigureAwait(false) is not null,
        SuiteEntityKind.AutomationWorkflow => await _store.GetSuiteEntityAsync<AutomationWorkflowV1>(kind, id, cancellationToken).ConfigureAwait(false) is not null,
        SuiteEntityKind.Macro => await _store.GetSuiteEntityAsync<MacroV1>(kind, id, cancellationToken).ConfigureAwait(false) is not null,
        _ => false
    };

    private async Task<IpcResponse> ScanGamesAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<GameScanRoot> roots = Payload<IReadOnlyList<GameScanRoot>>(request);
        GameScanResult result = LocalGameScanner.Scan(roots);
        foreach (GameEntryV1 game in result.Games)
        {
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.GameEntry, game.Id, game, cancellationToken).ConfigureAwait(false);
        }
        if (result.Games.Count > 0)
        {
            Interlocked.Increment(ref _revision);
        }
        return Success(request, result);
    }

    private async Task<IpcResponse> SaveMacroAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        MacroV1 macro = Payload<MacroV1>(request);
        SuiteValidationResult validation = MacroValidator.Validate(macro);
        return validation.IsValid
            ? await SaveAsync(request, SuiteEntityKind.Macro, macro.Id, macro, cancellationToken).ConfigureAwait(false)
            : Failure(request, "INVALID_MACRO", string.Join(" ", validation.Errors));
    }

    private async Task<IpcResponse> ExecuteMacroAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ExecuteMacroRequest payload = Payload<ExecuteMacroRequest>(request);
        if (!payload.ConfirmedVisibleSession)
        {
            return Failure(request, "VISIBLE_SESSION_NOT_CONFIRMED", "Macro playback requires a visible signed-in session confirmation for every run.");
        }
        MacroV1? macro = await _store.GetSuiteEntityAsync<MacroV1>(
            SuiteEntityKind.Macro,
            payload.MacroId,
            cancellationToken).ConfigureAwait(false);
        if (macro is null)
        {
            return Failure(request, "MACRO_NOT_FOUND", "The selected macro does not exist.");
        }
        if (!await _macroGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Failure(request, "MACRO_ACTIVE", "Another macro is already playing.");
        }
        try
        {
            MacroPlaybackEngine engine = new(new WindowsMacroInputSink(), new SystemMacroDelay());
            MacroExecutionResultV1 result = await engine.ExecuteAsync(macro, cancellationToken).ConfigureAwait(false);
            return result.Completed
                ? Success(request, result)
                : FailureWithPayload(request, "MACRO_FAILED", result.Error ?? "Macro playback did not complete.", result);
        }
        finally
        {
            _macroGate.Release();
        }
    }

    private IpcResponse GetMacroRecordingStatus(IpcRequest request)
    {
        lock (_macroRecordingSync)
        {
            return Success(request, new MacroRecordingStatusV1(
                _macroRecording,
                _macroRecorder.IsRecording,
                _macroRecording is null
                    ? "No input is being recorded. Start a visible recording session to capture keyboard and mouse input."
                    : "Recording is active. Stop saves a macro; cancel discards all captured input."));
        }
    }

    private async Task<IpcResponse> BeginMacroRecordingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        BeginMacroRecordingRequest payload = Payload<BeginMacroRecordingRequest>(request);
        string name = payload.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)
            || payload.MaximumDuration < TimeSpan.FromSeconds(1)
            || payload.MaximumDuration > TimeSpan.FromMinutes(10))
        {
            return Failure(request, "INVALID_RECORDING", "Recording name and a duration from 1 second to 10 minutes are required.");
        }

        lock (_macroRecordingSync)
        {
            if (_macroRecording is not null)
            {
                return Failure(request, "RECORDING_ACTIVE", "A macro recording is already active.");
            }
        }
        if (!await _macroGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Failure(request, "MACRO_ACTIVE", "Macro playback or another recording is active.");
        }

        MacroRecordingSessionV1 session = new(
            MacroRecordingSessionV1.CurrentSchemaVersion,
            $"recording.{Guid.NewGuid():N}",
            name,
            MacroRecordingState.Recording,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            payload.MaximumDuration,
            0,
            null,
            null);
        SuiteValidationResult validation = MacroRecordingValidator.Validate(session);
        if (!validation.IsValid)
        {
            _macroGate.Release();
            return Failure(request, "INVALID_RECORDING", string.Join(" ", validation.Errors));
        }

        bool recordingOwnsGate = false;
        try
        {
            await _macroRecorder.StartAsync(payload.MaximumDuration, CancellationToken.None).ConfigureAwait(false);
            lock (_macroRecordingSync)
            {
                _macroRecording = session;
                _macroRecordingOwnsGate = true;
                recordingOwnsGate = true;
            }
            await _store.SaveSuiteEntityAsync(
                SuiteEntityKind.MacroRecordingSession,
                session.Id,
                session,
                cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _revision);
            return Success(request, session);
        }
        catch
        {
            try { await _macroRecorder.CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            if (recordingOwnsGate)
            {
                ReleaseMacroRecordingGate();
            }
            else
            {
                _macroGate.Release();
            }
            throw;
        }
    }

    private async Task<IpcResponse> StopMacroRecordingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        StopMacroRecordingRequest payload = Payload<StopMacroRecordingRequest>(request);
        MacroRecordingSessionV1 session = GetActiveRecording(payload.SessionId, request, out IpcResponse? failure);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            IReadOnlyList<MacroStepV1> steps = await _macroRecorder.StopAsync(cancellationToken).ConfigureAwait(false);
            MacroV1 macro = new(
                MacroV1.CurrentSchemaVersion,
                $"macro.{Guid.NewGuid():N}",
                session.Name,
                steps);
            SuiteValidationResult validation = MacroValidator.Validate(macro);
            if (!validation.IsValid)
            {
                MacroRecordingSessionV1 failedSession = session with
                {
                    State = MacroRecordingState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    StepCount = steps.Count,
                    Error = string.Join(" ", validation.Errors)
                };
                await SaveRecordingAsync(failedSession, cancellationToken).ConfigureAwait(false);
                return FailureWithPayload(request, "INVALID_RECORDED_MACRO", failedSession.Error!, failedSession);
            }

            MacroRecordingSessionV1 completed = session with
            {
                State = MacroRecordingState.Completed,
                UpdatedAt = DateTimeOffset.UtcNow,
                StepCount = steps.Count,
                MacroId = macro.Id,
                Error = null
            };
            await _store.SaveSuiteEntityAsync(SuiteEntityKind.Macro, macro.Id, macro, cancellationToken).ConfigureAwait(false);
            await SaveRecordingAsync(completed, cancellationToken).ConfigureAwait(false);
            return Success(request, new MacroRecordingResultV1(completed, macro));
        }
        catch (OperationCanceledException)
        {
            try { await _macroRecorder.CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            MacroRecordingSessionV1 cancelled = session with
            {
                State = MacroRecordingState.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = "Recording was cancelled before it could be saved."
            };
            await SaveRecordingAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
            return FailureWithPayload(request, "RECORDING_CANCELLED", cancelled.Error, new MacroRecordingResultV1(cancelled, null));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MacroRecordingSessionV1 failed = session with
            {
                State = MacroRecordingState.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = exception.Message
            };
            await SaveRecordingAsync(failed, CancellationToken.None).ConfigureAwait(false);
            return FailureWithPayload(request, "RECORDING_FAILED", exception.Message, failed);
        }
    }

    private async Task<IpcResponse> CancelMacroRecordingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        CancelMacroRecordingRequest payload = Payload<CancelMacroRecordingRequest>(request);
        MacroRecordingSessionV1 session = GetActiveRecording(payload.SessionId, request, out IpcResponse? failure);
        if (failure is not null)
        {
            return failure;
        }

        await _macroRecorder.CancelAsync(CancellationToken.None).ConfigureAwait(false);
        MacroRecordingSessionV1 cancelled = session with
        {
            State = MacroRecordingState.Cancelled,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null
        };
        await SaveRecordingAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
        return Success(request, cancelled);
    }

    private MacroRecordingSessionV1 GetActiveRecording(
        string sessionId,
        IpcRequest request,
        out IpcResponse? failure)
    {
        lock (_macroRecordingSync)
        {
            if (_macroRecording is null || !string.Equals(_macroRecording.Id, sessionId, StringComparison.Ordinal))
            {
                failure = Failure(request, "RECORDING_NOT_FOUND", "The requested active recording does not exist.");
                return null!;
            }
            failure = null;
            return _macroRecording;
        }
    }

    private async Task SaveRecordingAsync(MacroRecordingSessionV1 session, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveSuiteEntityAsync(
                SuiteEntityKind.MacroRecordingSession,
                session.Id,
                session,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseMacroRecordingGate();
            Interlocked.Increment(ref _revision);
        }
    }

    private async Task<IpcResponse> RecoverMacroRecordingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<MacroRecordingSessionV1> recovered = await RecoverInterruptedMacroRecordingsAsync(cancellationToken).ConfigureAwait(false);
        return Success(request, recovered);
    }

    private async Task<IReadOnlyList<MacroRecordingSessionV1>> RecoverInterruptedMacroRecordingsAsync(CancellationToken cancellationToken)
    {
        try { await _macroRecorder.CancelAsync(CancellationToken.None).ConfigureAwait(false); } catch { }

        IReadOnlyList<MacroRecordingSessionV1> active = await _store.GetSuiteEntitiesAsync<MacroRecordingSessionV1>(
            SuiteEntityKind.MacroRecordingSession,
            cancellationToken).ConfigureAwait(false);
        MacroRecordingSessionV1[] recovered = active
            .Where(session => session.State == MacroRecordingState.Recording)
            .Select(session => session with
            {
                State = MacroRecordingState.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = "RigPilot recovered this recording after the user agent stopped. Captured input was discarded."
            })
            .ToArray();
        foreach (MacroRecordingSessionV1 session in recovered)
        {
            await _store.SaveSuiteEntityAsync(
                SuiteEntityKind.MacroRecordingSession,
                session.Id,
                session,
                cancellationToken).ConfigureAwait(false);
        }
        if (recovered.Length > 0)
        {
            Interlocked.Add(ref _revision, recovered.Length);
        }
        return recovered;
    }

    private void ReleaseMacroRecordingGate()
    {
        lock (_macroRecordingSync)
        {
            _macroRecording = null;
            if (_macroRecordingOwnsGate)
            {
                _macroRecordingOwnsGate = false;
                _macroGate.Release();
            }
        }
    }

    private async Task<IpcResponse> SaveScriptAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ScriptActionV1 script = Payload<ScriptActionV1>(request);
        SuiteValidationResult validation = await ScriptActionValidator.ValidateFileAsync(script, cancellationToken).ConfigureAwait(false);
        return validation.IsValid
            ? await SaveAsync(request, SuiteEntityKind.ScriptAction, script.Id, script, cancellationToken).ConfigureAwait(false)
            : Failure(request, "INVALID_SCRIPT", string.Join(" ", validation.Errors));
    }

    private async Task<IpcResponse> ExecuteScriptAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ExecuteScriptRequest payload = Payload<ExecuteScriptRequest>(request);
        ScriptActionV1? script = await _store.GetSuiteEntityAsync<ScriptActionV1>(
            SuiteEntityKind.ScriptAction,
            payload.ScriptId,
            cancellationToken).ConfigureAwait(false);
        if (script is null)
        {
            return Failure(request, "SCRIPT_NOT_FOUND", "The trusted script does not exist.");
        }
        SuiteValidationResult validation = await ScriptActionValidator.ValidateFileAsync(script, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Failure(request, "SCRIPT_TRUST_INVALID", string.Join(" ", validation.Errors));
        }
        ScriptExecutionResultV1 result = await LaunchAutomationHostAsync(script, cancellationToken).ConfigureAwait(false);
        return result.Completed && result.ExitCode == 0
            ? Success(request, result)
            : FailureWithPayload(request, "SCRIPT_FAILED", result.Error ?? result.StandardError, result);
    }

    private async Task<IpcResponse> SaveCapturePresetAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        CapturePresetV1 preset = Payload<CapturePresetV1>(request);
        if (preset.SchemaVersion != CapturePresetV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(preset.Id)
            || string.IsNullOrWhiteSpace(preset.Name)
            || preset.FramesPerSecond is < 1 or > 240
            || preset.VideoBitrateKbps is < 500 or > 200_000
            || string.IsNullOrWhiteSpace(preset.VideoCodec)
            || preset.Container is not ("mp4" or "mkv"))
        {
            return Failure(request, "INVALID_CAPTURE_PRESET", "Capture FPS, bitrate, or container is outside supported bounds.");
        }
        return await SaveAsync(request, SuiteEntityKind.CapturePreset, preset.Id, preset, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IpcResponse> CaptureDesktopSnapshotAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Failure(request, "IDEMPOTENCY_KEY_REQUIRED", "A desktop snapshot request requires an idempotency key.");
        }

        CaptureSnapshotRequestV1 payload = Payload<CaptureSnapshotRequestV1>(request);
        if (payload.SchemaVersion != CaptureSnapshotRequestV1.CurrentSchemaVersion
            || payload.Target is null
            || string.IsNullOrWhiteSpace(payload.Target.StableId)
            || string.IsNullOrWhiteSpace(payload.Target.DisplayName))
        {
            return Failure(request, "INVALID_CAPTURE_REQUEST", "The capture target or schema version is invalid.");
        }
        if (!payload.ConfirmedVisibleCapture)
        {
            return Failure(request, "CAPTURE_CONFIRMATION_REQUIRED", "Desktop capture requires explicit visible-session confirmation.");
        }
        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshotIdempotency.TryGetValue(request.IdempotencyKey, out CaptureSnapshotResultV1? prior))
            {
                return Success(request, prior);
            }

            CaptureSnapshotResultV1 result = await _desktopSnapshots.CaptureAsync(payload, cancellationToken).ConfigureAwait(false);
            if (_snapshotIdempotency.Count >= 64)
            {
                _snapshotIdempotency.Clear();
            }
            _snapshotIdempotency[request.IdempotencyKey] = result;
            Interlocked.Increment(ref _revision);
            return Success(request, result);
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private async Task<IpcResponse> StartVideoRecordingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        VideoRecordingStartRequestV1 payload = Payload<VideoRecordingStartRequestV1>(request);
        if (payload.SchemaVersion != VideoRecordingStartRequestV1.CurrentSchemaVersion
            || payload.Target is null
            || string.IsNullOrWhiteSpace(payload.Target.StableId))
        {
            return Failure(request, "INVALID_CAPTURE_REQUEST", "The recording target or schema version is invalid.");
        }
        if (!payload.ConfirmedVisibleCapture)
        {
            return Failure(request, "CAPTURE_CONFIRMATION_REQUIRED", "Video recording requires explicit visible-session confirmation.");
        }
        if (string.IsNullOrWhiteSpace(payload.IdempotencyKey))
        {
            return Failure(request, "IDEMPOTENCY_KEY_REQUIRED", "A video recording request requires an idempotency key.");
        }

        try
        {
            VideoRecordingStatusV1 status = await _videoRecorder.StartAsync(payload, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _revision);
            return Success(request, status);
        }
        catch (InvalidDataException exception)
        {
            return Failure(request, "INVALID_CAPTURE_REQUEST", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request, "RECORDING_IN_PROGRESS", exception.Message);
        }
    }

    private IpcResponse StartBenchmark(IpcRequest request, IFrametimeBenchmarkRecorder recorder)
    {
        FrametimeBenchmarkStartRequestV1 payload = Payload<FrametimeBenchmarkStartRequestV1>(request);
        try
        {
            FrametimeBenchmarkStatusV1 status = recorder.Start(payload);
            Interlocked.Increment(ref _revision);
            return Success(request, status);
        }
        catch (InvalidDataException exception)
        {
            return Failure(request, "INVALID_BENCHMARK_REQUEST", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request, "BENCHMARK_IN_PROGRESS", exception.Message);
        }
    }

    private IpcResponse PublishRtssOsdText(IpcRequest request)
    {
        RtssOsdPublishRequestV1 payload = Payload<RtssOsdPublishRequestV1>(request);
        if (payload.SchemaVersion != RtssOsdPublishRequestV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(payload.Text))
        {
            return Failure(request, "INVALID_RTSS_OSD_REQUEST", "An RTSS OSD publish request needs the current schema version and a non-empty text line.");
        }
        if (!payload.ConfirmedThirdPartyOsdWrite)
        {
            return Failure(request, "RTSS_OSD_CONFIRMATION_REQUIRED", "Publishing to the RTSS on-screen display requires explicit confirmation that RigPilot may claim an RTSS OSD slot.");
        }

        RtssOsdBridgeStatusV1 status = _rtssOsd.Publish(payload.Text);
        if (!status.Publishing)
        {
            return FailureWithPayload(request, "RTSS_OSD_UNAVAILABLE", status.Message, status);
        }
        Interlocked.Increment(ref _revision);
        return Success(request, status);
    }

    private IpcResponse ReleaseRtssOsd(IpcRequest request)
    {
        RtssOsdBridgeStatusV1 status = _rtssOsd.Release();
        Interlocked.Increment(ref _revision);
        return Success(request, status);
    }

    private async Task<IpcResponse> SetMonitorBrightnessAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Failure(request, "IDEMPOTENCY_KEY_REQUIRED", "A monitor brightness request requires an idempotency key.");
        }

        SetMonitorBrightnessRequestV1 payload = Payload<SetMonitorBrightnessRequestV1>(request);
        if (payload.SchemaVersion != SetMonitorBrightnessRequestV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(payload.MonitorId)
            || payload.BrightnessPercent is < 0 or > 100)
        {
            return Failure(request, "INVALID_MONITOR_BRIGHTNESS_REQUEST", "The selected monitor or brightness percentage is outside supported bounds.");
        }
        if (!payload.ConfirmDevice)
        {
            return Failure(request, "MONITOR_BRIGHTNESS_CONFIRMATION_REQUIRED", "Confirm the exact selected monitor before changing its brightness.");
        }

        await _monitorBrightnessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_monitorBrightnessIdempotency.TryGetValue(request.IdempotencyKey, out MonitorBrightnessApplyResultV1? prior))
            {
                return Success(request, prior);
            }

            MonitorBrightnessDeviceV1? selected = _monitorBrightness.Discover()
                .FirstOrDefault(device => string.Equals(device.Id, payload.MonitorId, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                return Failure(request, "MONITOR_NOT_FOUND", "The selected monitor is no longer available in this Windows session.");
            }
            if (selected.State is not (CapabilityAccessState.Experimental or CapabilityAccessState.Verified))
            {
                return Failure(request, "MONITOR_BRIGHTNESS_UNAVAILABLE", selected.Reason);
            }

            MonitorBrightnessApplyResultV1 result = await _monitorBrightness
                .SetBrightnessAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(result.MonitorId, payload.MonitorId, StringComparison.OrdinalIgnoreCase))
            {
                return FailureWithPayload(request, "MONITOR_BRIGHTNESS_ID_MISMATCH", "The brightness backend returned a result for a different monitor.", result);
            }
            if (!result.Applied || !result.ReadBackVerified)
            {
                return FailureWithPayload(request, "MONITOR_BRIGHTNESS_VERIFY_FAILED", result.Message, result);
            }

            if (_monitorBrightnessIdempotency.Count >= 64)
            {
                _monitorBrightnessIdempotency.Clear();
            }
            _monitorBrightnessIdempotency[request.IdempotencyKey] = result;
            Interlocked.Increment(ref _revision);
            return Success(request, result);
        }
        finally
        {
            _monitorBrightnessGate.Release();
        }
    }

    private async Task<IpcResponse> SaveOsdLayoutAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        OsdLayoutV1 layout = Payload<OsdLayoutV1>(request);
        SuiteValidationResult validation = OsdFrameRenderer.Validate(layout);
        return validation.IsValid
            ? await SaveAsync(request, SuiteEntityKind.OsdLayout, layout.Id, layout, cancellationToken).ConfigureAwait(false)
            : Failure(request, "INVALID_OSD_LAYOUT", string.Join(" ", validation.Errors));
    }

    private async Task<IpcResponse> GetOsdPresentationSettingsAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        OsdPresentationSettingsV1? settings = await _store.GetSuiteEntityAsync<OsdPresentationSettingsV1>(
            SuiteEntityKind.OsdPresentationSettings,
            OsdPresentationSettingsV1.DefaultId,
            cancellationToken).ConfigureAwait(false);
        return Success(request, settings ?? DefaultOsdPresentationSettings());
    }

    private async Task<IpcResponse> SaveOsdPresentationSettingsAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        OsdPresentationSettingsV1 settings = Payload<OsdPresentationSettingsV1>(request);
        if (settings.SchemaVersion != OsdPresentationSettingsV1.CurrentSchemaVersion
            || !string.Equals(settings.Id, OsdPresentationSettingsV1.DefaultId, StringComparison.Ordinal)
            || settings.OpacityOverride is double opacity && (!double.IsFinite(opacity) || opacity is < 0.2 or > 1)
            || settings.ScaleOverride is double scale && (!double.IsFinite(scale) || scale is < 0.6 or > 2.5)
            || string.IsNullOrWhiteSpace(settings.Hotkey)
            || settings.Hotkey.Trim().Length > 32)
        {
            return Failure(request, "INVALID_OSD_PRESENTATION", "OSD placement, opacity (0.2-1), scale (0.6-2.5), and a short hotkey are required.");
        }
        OsdPresentationSettingsV1 saved = settings with
        {
            MonitorStableId = string.IsNullOrWhiteSpace(settings.MonitorStableId) ? null : settings.MonitorStableId.Trim(),
            Hotkey = settings.Hotkey.Trim()
        };
        return await SaveAsync(request, SuiteEntityKind.OsdPresentationSettings, saved.Id, saved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IpcResponse> GetMonitoringPreferencesAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        MonitoringPreferencesV1? preferences = await _store.GetSuiteEntityAsync<MonitoringPreferencesV1>(
            SuiteEntityKind.MonitoringPreferences,
            MonitoringPreferencesV1.DefaultId,
            cancellationToken).ConfigureAwait(false);
        return Success(request, preferences ?? DefaultMonitoringPreferences());
    }

    private async Task<IpcResponse> SaveMonitoringPreferencesAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        MonitoringPreferencesV1 preferences = Payload<MonitoringPreferencesV1>(request);
        bool aliasesValid = preferences.Aliases.Count <= 64
            && preferences.Aliases.All(alias => !string.IsNullOrWhiteSpace(alias.SensorId)
                && !string.IsNullOrWhiteSpace(alias.Alias)
                && alias.Alias.Trim().Length <= 80);
        bool pinsValid = preferences.PinnedSensorIds.Count <= 24
            && preferences.PinnedSensorIds.All(id => !string.IsNullOrWhiteSpace(id));
        if (preferences.SchemaVersion != MonitoringPreferencesV1.CurrentSchemaVersion
            || !string.Equals(preferences.Id, MonitoringPreferencesV1.DefaultId, StringComparison.Ordinal)
            || !aliasesValid
            || !pinsValid)
        {
            return Failure(request, "INVALID_MONITORING_PREFERENCES", "Use at most 64 sensor aliases and 24 pinned exact sensor IDs.");
        }
        MonitoringPreferencesV1 saved = preferences with
        {
            Aliases = preferences.Aliases
                .GroupBy(alias => alias.SensorId.Trim(), StringComparer.Ordinal)
                .Select(group => new SensorAliasV1(group.Key, group.Last().Alias.Trim()))
                .ToArray(),
            PinnedSensorIds = preferences.PinnedSensorIds
                .Distinct(StringComparer.Ordinal)
                .Take(24)
                .ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return await SaveAsync(request, SuiteEntityKind.MonitoringPreferences, saved.Id, saved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IpcResponse> GetMonitoringComparisonLayoutAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        MonitoringComparisonLayoutV1? layout = await _store.GetSuiteEntityAsync<MonitoringComparisonLayoutV1>(
            SuiteEntityKind.MonitoringComparisonLayout,
            MonitoringComparisonLayoutV1.DefaultId,
            cancellationToken).ConfigureAwait(false);
        return Success(request, layout ?? DefaultMonitoringComparisonLayout());
    }

    private async Task<IpcResponse> SaveMonitoringComparisonLayoutAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        MonitoringComparisonLayoutV1 layout = Payload<MonitoringComparisonLayoutV1>(request);
        string[] normalizedIds = layout.SensorIds?
            .Select(id => id?.Trim() ?? string.Empty)
            .ToArray() ?? [];
        bool validIds = layout.SensorIds is not null
            && normalizedIds.Length <= 4
            && normalizedIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 512)
            && normalizedIds.Distinct(StringComparer.Ordinal).Count() == normalizedIds.Length;
        if (layout.SchemaVersion != MonitoringComparisonLayoutV1.CurrentSchemaVersion
            || !string.Equals(layout.Id, MonitoringComparisonLayoutV1.DefaultId, StringComparison.Ordinal)
            || !validIds)
        {
            return Failure(request, "INVALID_MONITORING_COMPARISON", "Choose zero to four distinct, bounded sensor IDs for the normalized comparison workspace.");
        }
        MonitoringComparisonLayoutV1 saved = layout with
        {
            SensorIds = normalizedIds,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return await SaveAsync(request, SuiteEntityKind.MonitoringComparisonLayout, saved.Id, saved, cancellationToken).ConfigureAwait(false);
    }

    private static OsdPresentationSettingsV1 DefaultOsdPresentationSettings() => new(
        OsdPresentationSettingsV1.CurrentSchemaVersion,
        OsdPresentationSettingsV1.DefaultId,
        MonitorStableId: null,
        OsdScreenAnchor.TopLeft,
        OpacityOverride: null,
        ScaleOverride: null,
        Hotkey: "Ctrl+Alt+O",
        Enabled: true);

    private static MonitoringPreferencesV1 DefaultMonitoringPreferences() => new(
        MonitoringPreferencesV1.CurrentSchemaVersion,
        MonitoringPreferencesV1.DefaultId,
        [],
        [],
        DateTimeOffset.UtcNow);

    private static MonitoringComparisonLayoutV1 DefaultMonitoringComparisonLayout() => new(
        MonitoringComparisonLayoutV1.CurrentSchemaVersion,
        MonitoringComparisonLayoutV1.DefaultId,
        [],
        NormalizeEachSeries: true,
        DateTimeOffset.UtcNow);

    private async Task<IpcResponse> SaveSimpleAsync<T>(
        IpcRequest request,
        SuiteEntityKind kind,
        Func<T, string> idSelector,
        CancellationToken cancellationToken)
    {
        T entity = Payload<T>(request);
        string id = idSelector(entity);
        if (string.IsNullOrWhiteSpace(id))
        {
            return Failure(request, "INVALID_ENTITY", "Entity ID is required.");
        }
        return await SaveAsync(request, kind, id, entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IpcResponse> SaveAsync<T>(
        IpcRequest request,
        SuiteEntityKind kind,
        string id,
        T entity,
        CancellationToken cancellationToken)
    {
        await _store.SaveSuiteEntityAsync(kind, id, entity, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _revision);
        return Success(request, entity);
    }

    private async Task<IpcResponse> DeleteAsync(IpcRequest request, SuiteEntityKind kind, CancellationToken cancellationToken)
    {
        DeleteEntityRequest payload = Payload<DeleteEntityRequest>(request);
        await _store.DeleteSuiteEntityAsync(kind, payload.Id, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _revision);
        return Success(request, payload.Id);
    }

    private static T Payload<T>(IpcRequest request) => IpcJson.FromElement<T>(request.Payload)
        ?? throw new InvalidDataException($"{request.Command} requires a {typeof(T).Name} payload.");

    private static async Task<ScriptExecutionResultV1> LaunchAutomationHostAsync(
        ScriptActionV1 action,
        CancellationToken cancellationToken)
    {
        string requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCHelper",
            "UserAgent",
            "Requests");
        Directory.CreateDirectory(requestDirectory);
        string requestPath = Path.Combine(requestDirectory, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(new ScriptExecutionRequestV1(ScriptExecutionRequestV1.CurrentSchemaVersion, action), JsonDefaults.Options),
            cancellationToken).ConfigureAwait(false);
        try
        {
            string[] executableCandidates =
            [
                Path.Combine(AppContext.BaseDirectory, "PCHelper.AutomationHost.exe"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "automation-host", "PCHelper.AutomationHost.exe"))
            ];
            string executable = executableCandidates.FirstOrDefault(File.Exists) ?? executableCandidates[0];
            string hostDll = Path.ChangeExtension(executable, ".dll");
            ProcessStartInfo startInfo = new()
            {
                FileName = File.Exists(executable) ? executable : Environment.ProcessPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!File.Exists(executable))
            {
                if (!File.Exists(hostDll))
                {
                    throw new FileNotFoundException("PCHelper.AutomationHost is not installed beside the user agent.", hostDll);
                }
                startInfo.FileName = "dotnet";
                startInfo.ArgumentList.Add(hostDll);
            }
            startInfo.ArgumentList.Add("--request");
            startInfo.ArgumentList.Add(requestPath);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Automation Host did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource hostTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hostTimeout.CancelAfter(action.Timeout + TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(hostTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            string json = await output.ConfigureAwait(false);
            string diagnostic = await error.ConfigureAwait(false);
            ScriptExecutionResultV1? result;
            try { result = JsonSerializer.Deserialize<ScriptExecutionResultV1>(json, JsonDefaults.Options); }
            catch (JsonException) { result = null; }
            return result ?? throw new InvalidDataException($"Automation Host returned no valid result. {diagnostic}".Trim());
        }
        finally
        {
            File.Delete(requestPath);
        }
    }

    private static async Task<EffectRenderResultV1> LaunchEffectHostAsync(
        EffectRenderRequestV1 effectRequest,
        CancellationToken cancellationToken)
    {
        string requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCHelper",
            "UserAgent",
            "Requests");
        Directory.CreateDirectory(requestDirectory);
        string requestPath = Path.Combine(requestDirectory, $"effect-{Guid.NewGuid():N}.json");
        string responsePath = requestPath + ".result";
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(effectRequest, JsonDefaults.Options),
            cancellationToken).ConfigureAwait(false);
        try
        {
            string[] executableCandidates =
            [
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "effect-host", "PCHelper.EffectHost.exe")),
                Path.Combine(AppContext.BaseDirectory, "PCHelper.EffectHost.exe")
            ];
            string executable = executableCandidates.FirstOrDefault(candidate =>
                File.Exists(candidate) && File.Exists(Path.ChangeExtension(candidate, ".dll"))) ?? executableCandidates[0];
            string hostDll = Path.ChangeExtension(executable, ".dll");
            ProcessStartInfo startInfo = new()
            {
                FileName = File.Exists(executable) ? executable : Environment.ProcessPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!File.Exists(executable))
            {
                if (!File.Exists(hostDll))
                {
                    throw new FileNotFoundException("PCHelper.EffectHost is not installed beside the user agent.", hostDll);
                }
                startInfo.FileName = "dotnet";
                startInfo.ArgumentList.Add(hostDll);
            }
            startInfo.ArgumentList.Add("--request");
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add("--response");
            startInfo.ArgumentList.Add(responsePath);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Effect Host did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(effectRequest.WatchdogMilliseconds) + TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            string json = File.Exists(responsePath)
                ? await File.ReadAllTextAsync(responsePath, cancellationToken).ConfigureAwait(false)
                : await output.ConfigureAwait(false);
            string diagnostic = await error.ConfigureAwait(false);
            EffectRenderResultV1? result;
            try { result = JsonSerializer.Deserialize<EffectRenderResultV1>(json, JsonDefaults.Options); }
            catch (JsonException) { result = null; }
            return result ?? throw new InvalidDataException($"Effect Host returned no valid result. {diagnostic}".Trim());
        }
        finally
        {
            File.Delete(requestPath);
            File.Delete(responsePath);
        }
    }

    private IpcResponse Success<T>(IpcRequest request, T payload) => new(
        ProtocolConstants.Version,
        request.RequestId,
        true,
        Interlocked.Read(ref _revision),
        null,
        null,
        IpcJson.ToElement(payload));

    private IpcResponse Failure(IpcRequest request, string code, string error) => new(
        ProtocolConstants.Version,
        request.RequestId,
        false,
        Interlocked.Read(ref _revision),
        code,
        error,
        null);

    private IpcResponse FailureWithPayload<T>(IpcRequest request, string code, string error, T payload) => new(
        ProtocolConstants.Version,
        request.RequestId,
        false,
        Interlocked.Read(ref _revision),
        code,
        error,
        IpcJson.ToElement(payload));
}
