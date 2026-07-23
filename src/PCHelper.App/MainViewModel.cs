using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfPointCollection = System.Windows.Media.PointCollection;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public enum TimelineScope
{
    All,
    Health,
    Profile,
    Conflict,
    Adapter
}

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan LocalProbeInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ServiceControlPlaneRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServiceDiagnosticsRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly NamedPipeRequestClient _client = new(ProtocolConstants.ServicePipeName, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    private readonly NamedPipeRequestClient _userAgentClient = new(ProtocolConstants.UserAgentPipeName, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    private readonly System.Threading.Timer _refreshTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AutomationRuleStateMachine _automationMachine = new();
    private readonly List<DeviceDisplay> _allDevices = [];
    private readonly Dictionary<string, ProfileV2> _suiteProfilesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutoOcProfileValidationV1> _autoOcValidationsByProfileId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoolingGraphV1> _coolingGraphsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FanCalibrationV2> _fanCalibrationsByCapability = new(StringComparer.Ordinal);
    private readonly List<OpenRgbController> _openRgbControllers = [];
    private readonly SemaphoreSlim _rgbMutationGate = new(1, 1);
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _applyProfileCommand;
    private readonly AsyncCommand _previewProfileCommand;
    private readonly AsyncCommand _resetVerifiedCommand;
    private readonly AsyncCommand _closeBlockersCommand;
    private bool _closeBlockersAcknowledged;
    private readonly AsyncCommand _startCalibrationCommand;
    private readonly AsyncCommand _startTuneCommand;
    private readonly AsyncCommand _abortOperationCommand;
    private readonly AsyncCommand _probeOpenRgbCommand;
    private readonly AsyncCommand _applyOpenRgbCommand;
    private readonly AsyncCommand _turnOffOpenRgbCommand;
    private readonly AsyncCommand _probeDynamicLightingCommand;
    private readonly AsyncCommand _addAutomationRuleCommand;
    private readonly AsyncCommand _deleteAutomationRuleCommand;
    private readonly AsyncCommand _scanGamesCommand;
    private readonly AsyncCommand _beginFanCommissioningCommand;
    private readonly AsyncCommand _pulseFanCommissioningCommand;
    private readonly AsyncCommand _observeFanCommissioningCommand;
    private readonly AsyncCommand _runInteractiveFanPreflightCommand;
    private readonly AsyncCommand _confirmFanCommissioningCommand;
    private readonly AsyncCommand _completeFanCommissioningCommand;
    private readonly AsyncCommand _createAdaptiveCoolingProfileCommand;
    private readonly AsyncCommand _saveCustomCoolingCurveCommand;
    private readonly AsyncCommand _cancelFanCommissioningCommand;
    private readonly AsyncCommand _recoverFanCommissioningCommand;
    private readonly AsyncCommand _saveCoolingOutputAssignmentCommand;
    private readonly AsyncCommand _addLightingZoneCommand;
    private readonly AsyncCommand _saveLightingLayoutCommand;
    private readonly AsyncCommand _applyDynamicLightingSceneCommand;
    private readonly AsyncCommand _startMacroRecordingCommand;
    private readonly AsyncCommand _stopMacroRecordingCommand;
    private readonly AsyncCommand _cancelMacroRecordingCommand;
    private readonly AsyncCommand _testMacroCommand;
    private readonly AsyncCommand _addMacroKeyPressCommand;
    private readonly AsyncCommand _removeMacroKeyPressCommand;
    private readonly AsyncCommand _saveMacroEditCommand;
    private readonly AsyncCommand _saveGameBundleCommand;
    private readonly AsyncCommand _applyGameBundleCommand;
    private readonly AsyncCommand _showDesktopOsdCommand;
    private readonly AsyncCommand _hideDesktopOsdCommand;
    private readonly AsyncCommand _captureDesktopSnapshotCommand;
    private readonly AsyncCommand _startVideoRecordingCommand;
    private readonly AsyncCommand _publishRtssOsdCommand;
    private readonly AsyncCommand _releaseRtssOsdCommand;
    private readonly AsyncCommand _readKrakenTelemetryCommand;
    private readonly AsyncCommand _readRtssFrameStatsCommand;
    private readonly AsyncCommand _startFrametimeBenchmarkCommand;
    private readonly AsyncCommand _stopFrametimeBenchmarkCommand;
    private bool _isFrametimeBenchmarkRunning;
    private string _frametimeBenchmarkStatus = "No benchmark has run. Start a game monitored by RTSS, then start a benchmark; sampling is passive and injection-free.";
    private readonly AsyncCommand _startPresentMonBenchmarkCommand;
    private readonly AsyncCommand _stopPresentMonBenchmarkCommand;
    private bool _isPresentMonBenchmarkRunning;
    private string _presentMonBenchmarkStatus = "No per-frame benchmark has run. Requires the separately-installed Intel PresentMon console; capture is passive ETW reading, injection-free.";
    private string _rtssFrameStatsStatus = "Frame statistics have not been read. RTSS measures FPS and frame times for running games; RigPilot reads them from shared memory without injecting.";
    private string _krakenTelemetryStatus = "Liquid-cooler telemetry has not been read. The pass is read-only: the Kraken streams status by itself and RigPilot never writes to it.";
    private readonly AsyncCommand _stopVideoRecordingCommand;
    private readonly AsyncCommand _refreshMonitorBrightnessCommand;
    private readonly AsyncCommand _scanHidInventoryCommand;
    private readonly AsyncCommand _setMonitorBrightnessCommand;
    private readonly AsyncCommand _saveOsdPresentationCommand;
    private readonly AsyncCommand _saveMonitoringPreferencesCommand;
    private readonly AsyncCommand _addMonitoringComparisonSensorCommand;
    private readonly AsyncCommand _removeMonitoringComparisonSensorCommand;
    private readonly AsyncCommand _saveMonitoringComparisonLayoutCommand;
    private readonly AsyncCommand _saveHealthRuleCommand;
    private readonly AsyncCommand _addRecommendedHealthRulesCommand;
    private readonly AsyncCommand _deleteHealthRuleCommand;
    private readonly AsyncCommand _acknowledgeHealthAlertCommand;
    private readonly AsyncCommand _enableSafeModeCommand;
    private readonly AsyncCommand _disableSafeModeCommand;
    private readonly AsyncCommand _previewTakeoverCommand;
    private readonly AsyncCommand _grantTakeoverConsentCommand;
    private readonly AsyncCommand _executeTakeoverCommand;
    private readonly AsyncCommand _releaseOwnershipCommand;
    private readonly AsyncCommand _previewAfterburnerImportCommand;
    private readonly AsyncCommand _saveAfterburnerImportCommand;
    private readonly AsyncCommand _previewFanControlImportCommand;
    private readonly AsyncCommand _saveFanControlImportCommand;
    private AdapterCoordinator? _localCoordinator;
    private readonly DesktopOsdController _desktopOsd = new();
    private readonly Dictionary<string, Queue<SensorSample>> _liveSensorHistory = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notifiedHealthAlertIds = new(StringComparer.Ordinal);
    private HardwareSnapshot? _snapshot;
    private ServiceStatus? _status;
    private ServiceRuntimeCompatibilityV1 _serviceCompatibility = ServiceRuntimeCompatibility.Unavailable(
        RuntimeVersion.Get(typeof(MainViewModel).Assembly),
        "Waiting for the RigPilot service handshake.");
    private HashSet<string> _serviceFeatures = new(StringComparer.OrdinalIgnoreCase);
    private HardwareOperationStatus? _operation;
    private DateTimeOffset _lastLocalProbe = DateTimeOffset.MinValue;
    private DateTimeOffset _lastServiceControlPlaneRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastServiceDiagnosticsRefresh = DateTimeOffset.MinValue;
    private bool _isServiceOnline;
    private bool _isBusy = true;
    private bool _refreshing;
    private bool _disposed;
    private string _busyMessage = "Connecting to the RigPilot service";
    private string _serviceStatusText = "Connecting\u2026";
    private string _activeProfileName = "None";
    private string _currentPageTitle = "Overview";
    private string _currentPageSubtitle = "Live health, ownership, and safety state";
    private string _safetySummary = "Waiting for service data.";
    private string _safetyTone = "Neutral";
    private string _lastUpdatedText = "Not updated yet";
    private string _dataSourceLabel = "Connecting";
    private string _deviceSearchText = string.Empty;
    private string _deviceResultSummary = "No inventory loaded";
    private string _deviceCompatibilitySummary = "No classified desktop families detected";
    private string _noticeText = string.Empty;
    private string _noticeTone = "Info";
    private bool _hasNotice;
    // True while the visible notice reports a service-recovery condition, so it can
    // be retired automatically once the service clears that condition instead of
    // lingering until the user dismisses it by hand.
    private bool _recoveryNoticeActive;
    private OperationTargetDisplay? _selectedCalibrationTarget;
    private OperationTargetDisplay? _selectedTuneTarget;
    private bool _advancedWritesAcknowledged;
    private bool _calibrationDeviceAcknowledged;
    private bool _tuneDeviceAcknowledged;
    private bool _profileDeviceAcknowledged;
    private bool _manualVoltageAcknowledged;
    private bool _allowCaseFanStop;
    private int _calibrationSettlingSeconds = 5;
    private int _calibrationRestartCycleCount = 2;
    private TuningObjective _selectedTuneObjective = TuningObjective.Efficiency;
    private string _tuneTemperatureCeilingText = "85";
    private string _tunePowerCeilingText = string.Empty;
    private bool _openRgbEnabled;
    private bool _openRgbConnected;
    private string _openRgbStatus = "Bridge disabled. No SDK connection has been attempted.";
    private string _openRgbColour = "#4EA1FF";
    private string _openRgbBrightnessText = "100";
    private int _openRgbControllerCount;
    private bool _isRgbSyncRunning;
    private string _rgbSyncStatus = "Sync everything uses each ready endpoint once and reports blocked or unverified routes separately.";
    private AutomationTriggerKind _newRuleTriggerKind = AutomationTriggerKind.Process;
    private ProfileV1? _newRuleProfile;
    private string _newRuleName = "Application profile";
    private string _newRuleTriggerValue = string.Empty;
    private string _newRulePriorityText = "100";
    private string _automationStatus = "No automation rules are active.";
    private string _profileActivationStatus = "No profile bundle has been applied in this session.";
    private string _profileDryRunStatus = "Select Dry run on a profile to inspect prerequisites, conflicts, omitted optional actions, and rollback behavior without writing hardware.";
    private string _gameBundleActivationStatus = "No game bundle has been applied in this session.";
    private string? _manualProfileId;
    private string? _pendingAutomationHotkey;
    private bool _automationEvaluating;
    private bool _automationMachineInitialised;
    private bool _sessionLocked;
    private DateTimeOffset _lastAutomationEvaluation = DateTimeOffset.MinValue;
    private bool _automationServiceSupported;
    private bool _isAdvancedLab;
    private bool _isUserAgentOnline;
    private bool _interactiveFanPreflightSupported;
    private string _userAgentStatus = "Connecting to user agent";
    private string _dynamicLightingStatus = "Dynamic Lighting has not been probed.";
    private FanCommissioningSessionV1? _selectedFanCommissioningSession;
    private string _commissioningHeaderName = string.Empty;
    private string _commissioningNotes = string.Empty;
    private bool _commissioningHeaderConfirmed;
    private bool _commissioningObserverReady;
    private string _commissioningObservation = "Select a cooling target, then start a guided commissioning session.";
    private CoolingOutputRole _selectedCoolingOutputRole = CoolingOutputRole.Unknown;
    private string _coolingOutputHeaderName = string.Empty;
    private bool _removeCoolingSafetyProtectionAcknowledged;
    private DynamicLightingDevice? _selectedDynamicLightingDevice;
    private LightingSceneV1? _selectedLightingScene;
    private string _lightingLayoutName = "Desk layout";
    private string _lightingZoneName = "Zone";
    private string _lightingZoneLedIndices = "0";
    private string _lightingZoneXText = "0";
    private string _lightingZoneYText = "0";
    private string _lightingZoneWidthText = "1";
    private string _lightingZoneHeightText = "1";
    private MacroRecordingSessionV1? _activeMacroRecording;
    private MacroV1? _selectedMacro;
    private readonly List<MacroStepV1> _macroEditorSteps = [];
    private string _macroRecordingName = "Recorded macro";
    private int _macroRecordingDurationSeconds = 30;
    private string _macroRecordingStatus = "No input is being recorded.";
    private string _macroEditorName = string.Empty;
    private string _macroEditorKeyCodeText = "65";
    private string _macroEditorDelayMillisecondsText = "15";
    private string _macroEditorSummary = "Select a saved macro to review or edit its typed key presses.";
    private string _rtssBridgeStatus = "RTSS discovery has not run.";
    private string _rtssOsdPublishStatus = "The RigPilot sensor line is not published to RTSS. Publishing writes only an OSD slot RigPilot owns; it never injects into any process.";
    private bool _isRtssOsdPublishing;
    private int _rtssOsdRefreshBusy;
    private string _gameBarBridgeStatus = "Game Bar discovery has not run.";
    private string _captureBridgeStatus = "Windows Graphics Capture discovery has not run.";
    private string _desktopOsdStatus = "Desktop OSD is hidden. It uses a local non-activating window, not RTSS or injection.";
    private string _desktopSnapshotStatus = "Select a display or window, then explicitly save a PNG to Pictures\\RigPilot\\Snapshots.";
    private string _monitorBrightnessStatus = "Waiting for the signed-in user agent before enumerating displays.";
    private string _hidInventoryStatus = "Peripheral scan has not run.";
    private string _monitorBrightnessPercentText = "50";
    private bool _monitorBrightnessDeviceConfirmed;
    private MonitorBrightnessDeviceV1? _selectedMonitorBrightnessDevice;
    private string _osdHotkeyText = "Ctrl+Alt+O";
    private string _osdOpacityText = "92";
    private string _osdScaleText = "100";
    private OsdScreenAnchor _selectedOsdAnchor = OsdScreenAnchor.TopLeft;
    private CaptureTargetV1? _selectedOsdMonitor;
    private OsdPresentationSettingsV1 _osdPresentationSettings = new(
        OsdPresentationSettingsV1.CurrentSchemaVersion,
        OsdPresentationSettingsV1.DefaultId,
        null,
        OsdScreenAnchor.TopLeft,
        null,
        null,
        "Ctrl+Alt+O",
        Enabled: true);
    private MonitoringPreferencesV1 _monitoringPreferences = new(
        MonitoringPreferencesV1.CurrentSchemaVersion,
        MonitoringPreferencesV1.DefaultId,
        [],
        [],
        DateTimeOffset.UtcNow);
    private MonitoringComparisonLayoutV1 _monitoringComparisonLayout = new(
        MonitoringComparisonLayoutV1.CurrentSchemaVersion,
        MonitoringComparisonLayoutV1.DefaultId,
        [],
        NormalizeEachSeries: true,
        DateTimeOffset.UtcNow);
    private SensorTrendDisplay? _selectedMonitoringTrend;
    private SensorTrendDisplay? _selectedMonitoringComparisonTrend;
    private MonitoringTrendScope _selectedMonitoringTrendScope = MonitoringTrendScope.All;
    private TimelineScope _selectedTimelineScope = TimelineScope.All;
    private string _sensorAliasText = string.Empty;
    private bool _selectedSensorPinned;
    private string _monitoringComparisonStatus = "Choose up to four live sensors to compare recent movement. Native values remain visible in the legend.";
    private HealthRuleConditionKind _newHealthRuleCondition = HealthRuleConditionKind.SensorAbove;
    private HealthRuleActionKind _newHealthRuleAction = HealthRuleActionKind.NotifyOnly;
    private SensorTrendDisplay? _selectedHealthTrend;
    private ProfileV1? _selectedEmergencyProfile;
    private string _newHealthRuleName = "CPU thermal ceiling";
    private string _newHealthThresholdText = "85";
    private string _newHealthConsecutiveText = "3";
    private string _newHealthCooldownText = "30";
    private string _healthRecommendationStatus = "Recommended rules are notify-only and are created only after this explicit action.";
    private string _safeModeReason = "Operator requested safe mode.";
    private SafetyRecoveryStatusV1? _safetyRecoveryStatus;
    private WgcRecordingPreflightV1? _wgcRecordingPreflight;
    private string _updatePlatformStatus = "Driver update executor has not been queried.";
    private int _pendingUpdateCount;
    private GameEntryV1? _selectedGame;
    private ProfileV1? _selectedGameProfile;
    private LightingSceneV1? _selectedGameLightingScene;
    private MacroV1? _selectedGameMacro;
    private OsdLayoutV1? _selectedGameOsdLayout;
    private CapturePresetV1? _selectedGameCapturePreset;
    private OsdLayoutV1? _selectedDesktopOsdLayout;
    private CaptureTargetV1? _selectedCaptureTarget;
    private bool _isVideoRecording;
    private string _videoRecordingStatus = "No recording has been started. Recording is explicit, bounded to 5 minutes, and saved only to Videos\\RigPilot\\Recordings.";
    private OwnershipOverview? _ownershipOverview;
    private TakeoverPlanV1? _takeoverPreview;
    private TakeoverProcessIdentity? _selectedTakeoverTarget;
    private bool _takeoverAllowForceTermination = true;
    private bool _takeoverDisableStartup = true;
    private bool _takeoverExactProcessesConfirmed;
    private string _ownershipStatus = "No ownership preview has been requested.";
    private string _afterburnerImportPath = string.Empty;
    private string _afterburnerImportSection = "Profile1";
    private ProfileImportPreviewV1? _afterburnerImportPreview;
    private string _afterburnerImportStatus = "Select an MSI Afterburner CFG file to produce a read-only mapping preview.";
    private string _fanControlImportPath = string.Empty;
    private string _fanControlSensorMappings = string.Empty;
    private string _fanControlControlMappings = string.Empty;
    private CoolingImportPreviewV1? _fanControlImportPreview;
    private string _fanControlImportStatus = "Map each Fan Control source to a detected RigPilot ID before saving a graph.";
    private string _customCoolingCurveName = "Custom curve";
    private string _customCoolingCurvePoints = "35:25\n50:40\n70:70\n85:100";
    private string _customCoolingCurveHysteresisUpText = "1";
    private string _customCoolingCurveHysteresisDownText = "2";
    private string _customCoolingCurveResponseUpSecondsText = "1";
    private string _customCoolingCurveResponseDownSecondsText = "5";

    /// <summary>
    /// Portable (no-service, read-only) mode: the dashboard never attempts a
    /// service connection, so it runs from any directory without an installed
    /// runtime. All hardware data comes from the in-process read-only local
    /// probe; every service-owned write, profile, automation, and update path
    /// stays disabled because <see cref="IsServiceOnline"/> can never become
    /// true. Set once at startup (via --portable) before InitialiseAsync.
    /// </summary>
    public bool IsPortableMode { get; init; }

    public MainViewModel()
    {
        _toggleHardwareControlCommand = new AsyncCommand(
            ToggleHardwareControlAsync,
            _ => CanUseServiceWrites && !IsHardwareControlChanging,
            ReportError,
            _ => ShowNotice(
                IsHardwareControlChanging
                    ? "The service is still applying and reading back the previous hardware-control request."
                    : GetServiceWriteBlockReason(),
                "Warning"));
        _refreshCommand = new AsyncCommand(_ => RefreshWithFeedbackAsync(), onError: ReportError);
        _applyGpuControlCommand = new AsyncCommand(
            parameter => ApplyGpuControlAsync((GpuControlSlider)parameter!),
            parameter => CanRunHardwareAction() && parameter is GpuControlSlider,
            ReportError,
            parameter =>
            {
                if (parameter is not GpuControlSlider)
                {
                    ShowNotice("Select a detected GPU or fan control first.", "Warning");
                    return;
                }

                ShowHardwareActionBlocked();
            });
        _startGpuAutoOcCommand = new AsyncCommand(
            _ => StartGpuAutoOcAsync(),
            _ => CanRunHardwareAction(requireIdle: true),
            ReportError,
            _ => ShowHardwareActionBlocked(requireIdle: true));
        SetKrakenPumpCommand = new AsyncCommand(
            _ => SetKrakenPumpAsync(),
            _ => CanRunHardwareAction(),
            ReportError,
            _ => ShowHardwareActionBlocked());
        _enableGpuFanAutoModeCommand = new AsyncCommand(
            parameter => StartAutomaticCoolingAsync(gpuFans: true, ParseCoolingCurveMode(parameter)),
            _ => CanRunHardwareAction(),
            ReportError,
            _ => ShowHardwareActionBlocked());
        _enableCaseFansAutoModeCommand = new AsyncCommand(
            parameter => StartAutomaticCoolingAsync(gpuFans: false, ParseCoolingCurveMode(parameter)),
            _ => CanRunHardwareAction(),
            ReportError,
            _ => ShowHardwareActionBlocked());
        _applyProfileCommand = new AsyncCommand(
            parameter => ApplyProfileCardAsync((ProfileCardDisplay)parameter!),
            parameter => CanUseServiceWrites
                && parameter is ProfileCardDisplay card
                && !card.IsActive
                && (!card.IsExperimental || (AdvancedWritesAcknowledged && ProfileDeviceAcknowledged))
                && (!card.RequiresManualAcknowledgement
                    || (AdvancedWritesAcknowledged && ProfileDeviceAcknowledged && ManualVoltageAcknowledged)),
            ReportError,
            parameter =>
            {
                string reason = !CanUseServiceWrites
                    ? GetServiceWriteBlockReason()
                    : parameter is not ProfileCardDisplay card
                        ? "Select a profile before applying it."
                        : card.IsActive
                            ? $"{card.Name} is already active."
                            : card.IsExperimental && !(AdvancedWritesAcknowledged && ProfileDeviceAcknowledged)
                                ? "Experimental profiles require both the advanced-write acknowledgement and exact-device confirmation."
                                : card.RequiresManualAcknowledgement
                                    && !(AdvancedWritesAcknowledged && ProfileDeviceAcknowledged && ManualVoltageAcknowledged)
                                        ? "This profile requires the advanced-write, exact-device, and manual-voltage acknowledgements."
                                        : "The profile cannot be applied in the current state.";
                ShowNotice(reason, "Warning");
            });
        _previewProfileCommand = new AsyncCommand(
            parameter => PreviewProfileCardAsync((ProfileCardDisplay)parameter!),
            parameter => IsServiceOnline
                && _serviceFeatures.Contains(ServiceRuntimeFeatures.ProfileDryRunV1)
                && parameter is ProfileCardDisplay,
            ReportError,
            _ => ShowNotice(
                !IsServiceOnline
                    ? "Profile dry run requires the RigPilot service. Local-probe mode cannot resolve service-owned capability and rollback state."
                    : !_serviceFeatures.Contains(ServiceRuntimeFeatures.ProfileDryRunV1)
                        ? "The installed service does not advertise profile dry-run support. Update the app and service together."
                        : "Select a profile before running its read-only dry run.",
                "Warning"));
        _closeBlockersCommand = new AsyncCommand(
            _ => CloseBlockersCoreAsync(),
            _ => IsServiceOnline && RunningConflictCount > 0 && CloseBlockersAcknowledged,
            ReportError);
        _resetVerifiedCommand = new AsyncCommand(
            _ => ResetVerifiedControlsCoreAsync(),
            _ => CanUseServiceWrites && ResettableVerifiedControlCount > 0,
            ReportError,
            _ => ShowNotice(
                !CanUseServiceWrites
                    ? GetServiceWriteBlockReason()
                    : "No Verified control currently exposes an independently read-back-verified default reset.",
                "Warning"));
        _startCalibrationCommand = new AsyncCommand(
            _ => StartCalibrationCoreAsync(),
            _ => CanStartCalibration,
            ReportError,
            _ => ShowNotice(
                HasActiveOperation
                    ? "Another hardware operation is active. Abort it and wait for restoration before starting calibration."
                    : CalibrationEligibilityReason,
                "Warning"));
        _startTuneCommand = new AsyncCommand(
            _ => StartSelectedTuneAsync(),
            _ => CanStartTune,
            ReportError,
            _ => ShowNotice(
                HasActiveOperation
                    ? "Another hardware operation is active. Abort it and wait for restoration before starting tuning."
                    : TuneEligibilityReason,
                "Warning"));
        _abortOperationCommand = new AsyncCommand(
            _ => AbortOperationCoreAsync(),
            _ => IsServiceOnline && HasActiveOperation,
            ReportError,
            _ => ShowNotice(
                !IsServiceOnline
                    ? GetServiceWriteBlockReason()
                    : "There is no active hardware operation to abort or restore.",
                "Info"));
        _probeOpenRgbCommand = new AsyncCommand(
            _ => ProbeOpenRgbCoreAsync(),
            _ => OpenRgbEnabled,
            ReportError);
        _applyOpenRgbCommand = new AsyncCommand(
            _ => ApplyOpenRgbCoreAsync(turnOff: false),
            _ => OpenRgbEnabled && OpenRgbConnected && HasReadyOpenRgbRoutes && AreOpenRgbInputsValid,
            ReportError);
        _turnOffOpenRgbCommand = new AsyncCommand(
            _ => ApplyOpenRgbCoreAsync(turnOff: true),
            _ => OpenRgbEnabled && OpenRgbConnected && HasReadyOpenRgbRoutes,
            ReportError);
        ApplyRazerChromaCommand = new AsyncCommand(
            _ => ApplyRazerChromaAsync(),
            _ => AreOpenRgbInputsValid,
            ReportError);
        _probeDynamicLightingCommand = new AsyncCommand(
            _ => ProbeDynamicLightingCoreAsync(),
            onError: ReportError);
        _addAutomationRuleCommand = new AsyncCommand(
            _ => AddAutomationRuleCoreAsync(),
            _ => IsServiceOnline && AutomationServiceSupported && CanAddAutomationRule,
            ReportError);
        _deleteAutomationRuleCommand = new AsyncCommand(
            parameter => DeleteAutomationRuleCoreAsync(((AutomationRuleDisplay)parameter!).Rule),
            parameter => IsServiceOnline && AutomationServiceSupported && parameter is AutomationRuleDisplay,
            ReportError);
        _scanGamesCommand = new AsyncCommand(
            _ => ScanGamesCoreAsync(),
            _ => IsUserAgentOnline,
            ReportError);
        _beginFanCommissioningCommand = new AsyncCommand(
            _ => BeginFanCommissioningCoreAsync(),
            _ => CanUseServiceWrites && CanBeginFanCommissioning,
            ReportError);
        _pulseFanCommissioningCommand = new AsyncCommand(
            _ => PulseFanCommissioningCoreAsync(),
            _ => CanUseServiceWrites && CanPulseFanCommissioning,
            ReportError);
        _observeFanCommissioningCommand = new AsyncCommand(
            _ => ObserveFanCommissioningCoreAsync(),
            _ => IsServiceOnline && SelectedFanCommissioningSession is not null,
            ReportError);
        _runInteractiveFanPreflightCommand = new AsyncCommand(
            _ => RunInteractiveFanPreflightCoreAsync(),
            _ => CanRunInteractiveFanPreflight,
            ReportError);
        _confirmFanCommissioningCommand = new AsyncCommand(
            _ => ConfirmFanCommissioningCoreAsync(),
            _ => CanUseServiceWrites && CanConfirmFanCommissioning,
            ReportError);
        _completeFanCommissioningCommand = new AsyncCommand(
            _ => CompleteFanCommissioningCoreAsync(),
            _ => CanUseServiceWrites && CanCompleteFanCommissioning,
            ReportError);
        _createAdaptiveCoolingProfileCommand = new AsyncCommand(
            _ => CreateAdaptiveCoolingProfileCoreAsync(),
            _ => CanUseServiceWrites && CanCreateAdaptiveCoolingProfile,
            ReportError);
        _saveCustomCoolingCurveCommand = new AsyncCommand(
            _ => SaveCustomCoolingCurveCoreAsync(),
            _ => CanUseServiceWrites && CanSaveCustomCoolingCurve,
            ReportError);
        _cancelFanCommissioningCommand = new AsyncCommand(
            _ => CancelFanCommissioningCoreAsync(),
            _ => IsServiceOnline && CanCancelFanCommissioning,
            ReportError);
        _recoverFanCommissioningCommand = new AsyncCommand(
            _ => RecoverFanCommissioningCoreAsync(),
            _ => IsServiceOnline && CanRecoverFanCommissioning,
            ReportError);
        _saveCoolingOutputAssignmentCommand = new AsyncCommand(
            _ => SaveCoolingOutputAssignmentCoreAsync(),
            _ => CanUseServiceWrites && CanSaveCoolingOutputAssignment,
            ReportError);
        _addLightingZoneCommand = new AsyncCommand(
            _ => AddLightingZoneCoreAsync(),
            _ => IsUserAgentOnline && CanAddLightingZone,
            ReportError);
        _saveLightingLayoutCommand = new AsyncCommand(
            _ => SaveLightingLayoutCoreAsync(),
            _ => IsUserAgentOnline && CanSaveLightingLayout,
            ReportError);
        _applyDynamicLightingSceneCommand = new AsyncCommand(
            _ => ApplyDynamicLightingSceneCoreAsync(),
            _ => SelectedLightingScene is not null && HasReadyDynamicLightingRoutes && AreOpenRgbInputsValid,
            ReportError);
        _startMacroRecordingCommand = new AsyncCommand(
            _ => StartMacroRecordingCoreAsync(),
            _ => IsUserAgentOnline && CanStartMacroRecording,
            ReportError);
        _stopMacroRecordingCommand = new AsyncCommand(
            _ => StopMacroRecordingCoreAsync(),
            _ => IsUserAgentOnline && ActiveMacroRecording is not null,
            ReportError);
        _cancelMacroRecordingCommand = new AsyncCommand(
            _ => CancelMacroRecordingCoreAsync(),
            _ => IsUserAgentOnline && ActiveMacroRecording is not null,
            ReportError);
        _testMacroCommand = new AsyncCommand(
            _ => TestMacroCoreAsync(),
            _ => IsUserAgentOnline && SelectedMacro is not null && ActiveMacroRecording is null,
            ReportError);
        _addMacroKeyPressCommand = new AsyncCommand(
            _ => AddMacroKeyPressCoreAsync(),
            _ => IsUserAgentOnline && CanAddMacroKeyPress,
            ReportError);
        _removeMacroKeyPressCommand = new AsyncCommand(
            _ => RemoveMacroKeyPressCoreAsync(),
            _ => IsUserAgentOnline && CanRemoveMacroKeyPress,
            ReportError);
        _saveMacroEditCommand = new AsyncCommand(
            _ => SaveMacroEditCoreAsync(),
            _ => IsUserAgentOnline && CanSaveMacroEdit,
            ReportError);
        _saveGameBundleCommand = new AsyncCommand(
            _ => SaveGameBundleCoreAsync(),
            _ => IsUserAgentOnline && SelectedGame is not null,
            ReportError);
        _applyGameBundleCommand = new AsyncCommand(
            _ => ApplyGameBundleCoreAsync(),
            _ => SelectedGame is not null,
            ReportError,
            _ => ShowNotice("Select a game before applying its bundle.", "Warning"));
        _showDesktopOsdCommand = new AsyncCommand(
            _ => ShowDesktopOsdCoreAsync(),
            _ => CanShowDesktopOsd,
            ReportError);
        _hideDesktopOsdCommand = new AsyncCommand(
            _ => HideDesktopOsdCoreAsync(),
            _ => IsDesktopOsdVisible,
            ReportError);
        _captureDesktopSnapshotCommand = new AsyncCommand(
            _ => CaptureDesktopSnapshotCoreAsync(),
            _ => CanCaptureDesktopSnapshot,
            ReportError);
        _startVideoRecordingCommand = new AsyncCommand(
            _ => StartVideoRecordingCoreAsync(),
            _ => CanStartVideoRecording,
            ReportError);
        _stopVideoRecordingCommand = new AsyncCommand(
            _ => StopVideoRecordingCoreAsync(),
            _ => CanStopVideoRecording,
            ReportError);
        _publishRtssOsdCommand = new AsyncCommand(
            _ => PublishRtssOsdCoreAsync(),
            _ => IsUserAgentOnline && !IsRtssOsdPublishing,
            ReportError);
        _releaseRtssOsdCommand = new AsyncCommand(
            _ => ReleaseRtssOsdCoreAsync(),
            _ => IsUserAgentOnline && IsRtssOsdPublishing,
            ReportError);
        _refreshMonitorBrightnessCommand = new AsyncCommand(
            _ => RefreshMonitorBrightnessCoreAsync(showNotice: true),
            _ => IsUserAgentOnline,
            ReportError);
        _scanHidInventoryCommand = new AsyncCommand(
            _ => ScanHidInventoryCoreAsync(),
            _ => IsServiceOnline,
            ReportError);
        _readKrakenTelemetryCommand = new AsyncCommand(
            _ => ReadKrakenTelemetryCoreAsync(),
            _ => IsServiceOnline,
            ReportError);
        _readRtssFrameStatsCommand = new AsyncCommand(
            _ => ReadRtssFrameStatsCoreAsync(),
            _ => IsUserAgentOnline,
            ReportError);
        _startFrametimeBenchmarkCommand = new AsyncCommand(
            _ => StartFrametimeBenchmarkCoreAsync(),
            _ => IsUserAgentOnline && !IsFrametimeBenchmarkRunning,
            ReportError);
        _stopFrametimeBenchmarkCommand = new AsyncCommand(
            _ => StopFrametimeBenchmarkCoreAsync(),
            _ => IsUserAgentOnline && IsFrametimeBenchmarkRunning,
            ReportError);
        _startPresentMonBenchmarkCommand = new AsyncCommand(
            _ => StartPresentMonBenchmarkCoreAsync(),
            _ => IsUserAgentOnline && !IsPresentMonBenchmarkRunning,
            ReportError);
        _stopPresentMonBenchmarkCommand = new AsyncCommand(
            _ => StopPresentMonBenchmarkCoreAsync(),
            _ => IsUserAgentOnline && IsPresentMonBenchmarkRunning,
            ReportError);
        _setMonitorBrightnessCommand = new AsyncCommand(
            _ => SetMonitorBrightnessCoreAsync(),
            _ => CanSetMonitorBrightness,
            ReportError);
        _saveOsdPresentationCommand = new AsyncCommand(
            _ => SaveOsdPresentationCoreAsync(),
            _ => IsUserAgentOnline && CanSaveOsdPresentation,
            ReportError);
        _saveMonitoringPreferencesCommand = new AsyncCommand(
            _ => SaveMonitoringPreferencesCoreAsync(),
            _ => IsUserAgentOnline && SelectedMonitoringTrend is not null,
            ReportError);
        _addMonitoringComparisonSensorCommand = new AsyncCommand(
            _ => AddMonitoringComparisonSensorCoreAsync(),
            _ => IsUserAgentOnline && CanAddMonitoringComparisonSensor,
            ReportError);
        _removeMonitoringComparisonSensorCommand = new AsyncCommand(
            parameter => RemoveMonitoringComparisonSensorCoreAsync((SensorComparisonSeriesDisplay)parameter!),
            parameter => IsUserAgentOnline && parameter is SensorComparisonSeriesDisplay,
            ReportError);
        _saveMonitoringComparisonLayoutCommand = new AsyncCommand(
            _ => SaveMonitoringComparisonLayoutCoreAsync(),
            _ => IsUserAgentOnline && CanSaveMonitoringComparisonLayout,
            ReportError);
        _saveHealthRuleCommand = new AsyncCommand(
            _ => SaveHealthRuleCoreAsync(),
            _ => IsServiceOnline && CanSaveHealthRule,
            ReportError);
        _addRecommendedHealthRulesCommand = new AsyncCommand(
            _ => AddRecommendedHealthRulesCoreAsync(),
            _ => IsServiceOnline && CanAddRecommendedHealthRules,
            ReportError);
        _deleteHealthRuleCommand = new AsyncCommand(
            parameter => DeleteHealthRuleCoreAsync(((HealthRuleDisplay)parameter!).Rule),
            parameter => IsServiceOnline && parameter is HealthRuleDisplay,
            ReportError);
        _acknowledgeHealthAlertCommand = new AsyncCommand(
            parameter => AcknowledgeHealthAlertCoreAsync(((HealthAlertDisplay)parameter!).Alert),
            parameter => IsServiceOnline && parameter is HealthAlertDisplay display && display.CanAcknowledge,
            ReportError);
        _enableSafeModeCommand = new AsyncCommand(
            _ => SetSafeModeCoreAsync(enabled: true),
            _ => IsServiceOnline && !IsSafeModeEnabled,
            ReportError);
        _disableSafeModeCommand = new AsyncCommand(
            _ => SetSafeModeCoreAsync(enabled: false),
            _ => IsServiceOnline && IsSafeModeEnabled,
            ReportError);
        _previewTakeoverCommand = new AsyncCommand(
            _ => PreviewTakeoverCoreAsync(),
            _ => IsServiceOnline && !HasActiveOperation,
            ReportError);
        _grantTakeoverConsentCommand = new AsyncCommand(
            _ => GrantTakeoverConsentCoreAsync(),
            _ => IsServiceOnline && SelectedTakeoverTarget is not null,
            ReportError);
        _executeTakeoverCommand = new AsyncCommand(
            _ => ExecuteTakeoverCoreAsync(),
            _ => IsServiceOnline && CanExecuteTakeover,
            ReportError);
        _releaseOwnershipCommand = new AsyncCommand(
            _ => ReleaseOwnershipCoreAsync(),
            _ => IsServiceOnline && ActiveOwnershipTransaction is not null && !HasActiveOperation,
            ReportError);
        _previewAfterburnerImportCommand = new AsyncCommand(
            _ => PreviewAfterburnerImportCoreAsync(),
            _ => IsServiceOnline && CanPreviewAfterburnerImport,
            ReportError);
        _saveAfterburnerImportCommand = new AsyncCommand(
            _ => SaveAfterburnerImportCoreAsync(),
            _ => IsServiceOnline && AfterburnerImportPreview?.Profile is not null,
            ReportError);
        _previewFanControlImportCommand = new AsyncCommand(
            _ => PreviewFanControlImportCoreAsync(),
            _ => IsServiceOnline && CanPreviewFanControlImport,
            ReportError);
        _saveFanControlImportCommand = new AsyncCommand(
            _ => SaveFanControlImportCoreAsync(),
            _ => IsServiceOnline && FanControlImportPreview?.Graph is not null,
            ReportError);
        DismissNoticeCommand = new RelayCommand(_ => DismissNotice(), _ => HasNotice);
        ClearDeviceSearchCommand = new RelayCommand(_ => DeviceSearchText = string.Empty, _ => !string.IsNullOrEmpty(DeviceSearchText));
        ResumeAutomationCommand = new RelayCommand(_ => ResumeAutomation(), _ => HasManualOverride);
        ToggleAdvancedLabCommand = new RelayCommand(_ => IsAdvancedLab = !IsAdvancedLab);

        _refreshTimer = new System.Threading.Timer(
            _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                async () =>
                {
                    // Footprint sampling is local and independent of the service
                    // fetch, so it keeps updating even if a refresh round fails.
                    UpdateFootprint();
                    await RefreshAsync(full: false, userInitiated: false);
                }),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? OsdHotkeyChanged;

    public ObservableCollection<SensorDisplay> ImportantSensors { get; } = new BatchedObservableCollection<SensorDisplay>();

    public ObservableCollection<SensorDisplay> CoolingSensors { get; } = new BatchedObservableCollection<SensorDisplay>();

    public ObservableCollection<SensorDisplay> PerformanceSensors { get; } = new BatchedObservableCollection<SensorDisplay>();

    public ObservableCollection<ProfileV1> Profiles { get; } = new BatchedObservableCollection<ProfileV1>();

    public ObservableCollection<ProfileCardDisplay> ProfileCards { get; } = new BatchedObservableCollection<ProfileCardDisplay>();

    public ObservableCollection<CapabilityDisplay> CoolingCapabilities { get; } = new BatchedObservableCollection<CapabilityDisplay>();

    public ObservableCollection<CapabilityDisplay> PerformanceCapabilities { get; } = new BatchedObservableCollection<CapabilityDisplay>();

    public ObservableCollection<CapabilityDisplay> CapabilityDecisions { get; } = new BatchedObservableCollection<CapabilityDisplay>();

    /// <summary>
    /// A deliberately narrow view of capabilities that are labelled Experimental.
    /// It does not turn a capability into a write path: it only explains whether
    /// the existing cooling commissioning workflow can safely inspect it.
    /// </summary>
    public ObservableCollection<ExperimentalControlDisplay> ExperimentalControls { get; } = new BatchedObservableCollection<ExperimentalControlDisplay>();

    public ObservableCollection<DeviceDisplay> Devices { get; } = new BatchedObservableCollection<DeviceDisplay>();

    public ObservableCollection<DiagnosticDisplay> Diagnostics { get; } = new BatchedObservableCollection<DiagnosticDisplay>();

    public ObservableCollection<AdapterHealthDisplay> AdapterHealth { get; } = new BatchedObservableCollection<AdapterHealthDisplay>();

    public ObservableCollection<AdapterTraceDisplay> AdapterTrace { get; } = new BatchedObservableCollection<AdapterTraceDisplay>();

    public ObservableCollection<HealthRuleDisplay> HealthRules { get; } = new BatchedObservableCollection<HealthRuleDisplay>();

    public ObservableCollection<HealthAlertDisplay> HealthAlerts { get; } = new BatchedObservableCollection<HealthAlertDisplay>();

    public ObservableCollection<SensorTrendDisplay> MonitoringTrends { get; } = new BatchedObservableCollection<SensorTrendDisplay>();

    public ObservableCollection<SensorTrendDisplay> VisibleMonitoringTrends { get; } = new BatchedObservableCollection<SensorTrendDisplay>();

    public ObservableCollection<SensorComparisonSeriesDisplay> MonitoringComparisonSeries { get; } = new BatchedObservableCollection<SensorComparisonSeriesDisplay>();

    public ObservableCollection<TimelineEventDisplay> TimelineEvents { get; } = new BatchedObservableCollection<TimelineEventDisplay>();

    public ObservableCollection<TimelineEventDisplay> VisibleTimelineEvents { get; } = new BatchedObservableCollection<TimelineEventDisplay>();

    public ObservableCollection<CoolingQualificationReportV1> CoolingQualificationReports { get; } = new BatchedObservableCollection<CoolingQualificationReportV1>();

    public ObservableCollection<DeviceQualificationPlanV1> DeviceQualificationPlans { get; } = new BatchedObservableCollection<DeviceQualificationPlanV1>();

    public IReadOnlyList<DeviceQualificationPlanV1> TuningQualificationPlans => DeviceQualificationPlans
        .Where(plan => plan.Kind is DeviceQualificationKind.CpuTuning or DeviceQualificationKind.GpuTuning)
        .ToArray();

    public IReadOnlyList<DeviceQualificationPlanV1> LightingQualificationPlans => DeviceQualificationPlans
        .Where(plan => plan.Kind == DeviceQualificationKind.Lighting)
        .ToArray();

    public ObservableCollection<OperationTargetDisplay> CalibrationTargets { get; } = new BatchedObservableCollection<OperationTargetDisplay>();

    public ObservableCollection<FanCommissioningSessionV1> FanCommissioningSessions { get; } = new BatchedObservableCollection<FanCommissioningSessionV1>();

    public ObservableCollection<CoolingOutputAssignmentV1> CoolingOutputAssignments { get; } = new BatchedObservableCollection<CoolingOutputAssignmentV1>();

    public IReadOnlyList<MonitoringTrendScope> MonitoringTrendScopes { get; } = Enum.GetValues<MonitoringTrendScope>();

    public IReadOnlyList<TimelineScope> TimelineScopes { get; } = Enum.GetValues<TimelineScope>();

    public IReadOnlyList<int> CalibrationSettlingOptions { get; } = [3, 5, 7, 10];

    public IReadOnlyList<int> CalibrationRestartCycleOptions { get; } = [2, 3];

    public IReadOnlyList<CoolingOutputRole> CoolingOutputRoles { get; } = Enum.GetValues<CoolingOutputRole>();

    public IReadOnlyList<int> MacroRecordingDurationOptions { get; } = [10, 30, 60, 120, 300];

    public ObservableCollection<OperationTargetDisplay> TuneTargets { get; } = new BatchedObservableCollection<OperationTargetDisplay>();

    public ObservableCollection<AutomationRuleDisplay> AutomationRules { get; } = new BatchedObservableCollection<AutomationRuleDisplay>();

    public ObservableCollection<GameEntryV1> Games { get; } = new BatchedObservableCollection<GameEntryV1>();

    public ObservableCollection<AutomationWorkflowV1> Workflows { get; } = new BatchedObservableCollection<AutomationWorkflowV1>();

    public ObservableCollection<LightingSceneV1> LightingScenes { get; } = new BatchedObservableCollection<LightingSceneV1>();

    public ObservableCollection<EffectGraphV1> EffectGraphs { get; } = new BatchedObservableCollection<EffectGraphV1>();

    public ObservableCollection<MacroV1> Macros { get; } = new BatchedObservableCollection<MacroV1>();

    public ObservableCollection<ScriptActionV1> Scripts { get; } = new BatchedObservableCollection<ScriptActionV1>();

    public ObservableCollection<OsdLayoutV1> OsdLayouts { get; } = new BatchedObservableCollection<OsdLayoutV1>();

    public ObservableCollection<CapturePresetV1> CapturePresets { get; } = new BatchedObservableCollection<CapturePresetV1>();

    public ObservableCollection<CaptureTargetV1> CaptureTargets { get; } = new BatchedObservableCollection<CaptureTargetV1>();

    public ObservableCollection<MonitorBrightnessDeviceV1> MonitorBrightnessDevices { get; } = new BatchedObservableCollection<MonitorBrightnessDeviceV1>();

    public ObservableCollection<DynamicLightingDevice> DynamicLightingDevices { get; } = new BatchedObservableCollection<DynamicLightingDevice>();

    public ObservableCollection<RgbRouteAssessment> RgbRouteAssessments { get; } = new BatchedObservableCollection<RgbRouteAssessment>();

    public ObservableCollection<RgbApplyOutcome> LastRgbApplyOutcomes { get; } = new BatchedObservableCollection<RgbApplyOutcome>();

    public ObservableCollection<LightingZoneV1> DraftLightingZones { get; } = new BatchedObservableCollection<LightingZoneV1>();

    public ObservableCollection<MacroRecordingSessionV1> MacroRecordingSessions { get; } = new BatchedObservableCollection<MacroRecordingSessionV1>();

    public IReadOnlyList<TuningObjective> TuneObjectives { get; } = Enum.GetValues<TuningObjective>();

    public IReadOnlyList<AutomationTriggerKind> AutomationTriggerKinds { get; } = Enum.GetValues<AutomationTriggerKind>();

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand ApplyProfileCommand => _applyProfileCommand;

    public ICommand PreviewProfileCommand => _previewProfileCommand;

    public ICommand ResetVerifiedCommand => _resetVerifiedCommand;

    public ICommand StartCalibrationCommand => _startCalibrationCommand;

    public ICommand StartTuneCommand => _startTuneCommand;

    public ICommand AbortOperationCommand => _abortOperationCommand;

    public ICommand ProbeOpenRgbCommand => _probeOpenRgbCommand;

    public ICommand ApplyOpenRgbCommand => _applyOpenRgbCommand;

    public ICommand TurnOffOpenRgbCommand => _turnOffOpenRgbCommand;

    public ICommand ProbeDynamicLightingCommand => _probeDynamicLightingCommand;

    public ICommand AddAutomationRuleCommand => _addAutomationRuleCommand;

    public ICommand DeleteAutomationRuleCommand => _deleteAutomationRuleCommand;

    public ICommand ScanGamesCommand => _scanGamesCommand;

    public ICommand BeginFanCommissioningCommand => _beginFanCommissioningCommand;

    public ICommand PulseFanCommissioningCommand => _pulseFanCommissioningCommand;

    public ICommand ObserveFanCommissioningCommand => _observeFanCommissioningCommand;

    public ICommand RunInteractiveFanPreflightCommand => _runInteractiveFanPreflightCommand;

    public ICommand ConfirmFanCommissioningCommand => _confirmFanCommissioningCommand;

    public ICommand CompleteFanCommissioningCommand => _completeFanCommissioningCommand;

    public ICommand CreateAdaptiveCoolingProfileCommand => _createAdaptiveCoolingProfileCommand;

    public ICommand SaveCustomCoolingCurveCommand => _saveCustomCoolingCurveCommand;

    public ICommand CancelFanCommissioningCommand => _cancelFanCommissioningCommand;

    public ICommand RecoverFanCommissioningCommand => _recoverFanCommissioningCommand;

    public ICommand SaveCoolingOutputAssignmentCommand => _saveCoolingOutputAssignmentCommand;

    public ICommand AddLightingZoneCommand => _addLightingZoneCommand;

    public ICommand SaveLightingLayoutCommand => _saveLightingLayoutCommand;

    public ICommand ApplyDynamicLightingSceneCommand => _applyDynamicLightingSceneCommand;

    public ICommand StartMacroRecordingCommand => _startMacroRecordingCommand;

    public ICommand StopMacroRecordingCommand => _stopMacroRecordingCommand;

    public ICommand CancelMacroRecordingCommand => _cancelMacroRecordingCommand;

    public ICommand TestMacroCommand => _testMacroCommand;

    public ICommand AddMacroKeyPressCommand => _addMacroKeyPressCommand;

    public ICommand RemoveMacroKeyPressCommand => _removeMacroKeyPressCommand;

    public ICommand SaveMacroEditCommand => _saveMacroEditCommand;

    public ICommand SaveGameBundleCommand => _saveGameBundleCommand;

    public ICommand ApplyGameBundleCommand => _applyGameBundleCommand;

    public ICommand ShowDesktopOsdCommand => _showDesktopOsdCommand;

    public ICommand HideDesktopOsdCommand => _hideDesktopOsdCommand;

    public ICommand CaptureDesktopSnapshotCommand => _captureDesktopSnapshotCommand;

    public ICommand StartVideoRecordingCommand => _startVideoRecordingCommand;

    public ICommand PublishRtssOsdCommand => _publishRtssOsdCommand;

    public ICommand ReleaseRtssOsdCommand => _releaseRtssOsdCommand;

    public ICommand ReadKrakenTelemetryCommand => _readKrakenTelemetryCommand;

    public ICommand ReadRtssFrameStatsCommand => _readRtssFrameStatsCommand;

    public string RtssFrameStatsStatus
    {
        get => _rtssFrameStatsStatus;
        private set => Set(ref _rtssFrameStatsStatus, value);
    }

    public ICommand StartFrametimeBenchmarkCommand => _startFrametimeBenchmarkCommand;

    public ICommand StopFrametimeBenchmarkCommand => _stopFrametimeBenchmarkCommand;

    public bool IsFrametimeBenchmarkRunning
    {
        get => _isFrametimeBenchmarkRunning;
        private set
        {
            if (Set(ref _isFrametimeBenchmarkRunning, value))
            {
                _startFrametimeBenchmarkCommand.RaiseCanExecuteChanged();
                _stopFrametimeBenchmarkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FrametimeBenchmarkStatus
    {
        get => _frametimeBenchmarkStatus;
        private set => Set(ref _frametimeBenchmarkStatus, value);
    }

    public ICommand StartPresentMonBenchmarkCommand => _startPresentMonBenchmarkCommand;

    public ICommand StopPresentMonBenchmarkCommand => _stopPresentMonBenchmarkCommand;

    public bool IsPresentMonBenchmarkRunning
    {
        get => _isPresentMonBenchmarkRunning;
        private set
        {
            if (Set(ref _isPresentMonBenchmarkRunning, value))
            {
                _startPresentMonBenchmarkCommand.RaiseCanExecuteChanged();
                _stopPresentMonBenchmarkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PresentMonBenchmarkStatus
    {
        get => _presentMonBenchmarkStatus;
        private set => Set(ref _presentMonBenchmarkStatus, value);
    }

    public string KrakenTelemetryStatus
    {
        get => _krakenTelemetryStatus;
        private set => Set(ref _krakenTelemetryStatus, value);
    }

    public ICommand StopVideoRecordingCommand => _stopVideoRecordingCommand;

    public ICommand RefreshMonitorBrightnessCommand => _refreshMonitorBrightnessCommand;
    public ICommand ScanHidInventoryCommand => _scanHidInventoryCommand;

    public ICommand SetMonitorBrightnessCommand => _setMonitorBrightnessCommand;

    public ICommand SaveOsdPresentationCommand => _saveOsdPresentationCommand;

    public ICommand SaveMonitoringPreferencesCommand => _saveMonitoringPreferencesCommand;

    public ICommand AddMonitoringComparisonSensorCommand => _addMonitoringComparisonSensorCommand;

    public ICommand RemoveMonitoringComparisonSensorCommand => _removeMonitoringComparisonSensorCommand;

    public ICommand SaveMonitoringComparisonLayoutCommand => _saveMonitoringComparisonLayoutCommand;

    public ICommand SaveHealthRuleCommand => _saveHealthRuleCommand;

    public ICommand AddRecommendedHealthRulesCommand => _addRecommendedHealthRulesCommand;

    public ICommand DeleteHealthRuleCommand => _deleteHealthRuleCommand;

    public ICommand AcknowledgeHealthAlertCommand => _acknowledgeHealthAlertCommand;

    public ICommand EnableSafeModeCommand => _enableSafeModeCommand;

    public ICommand DisableSafeModeCommand => _disableSafeModeCommand;

    public ICommand PreviewTakeoverCommand => _previewTakeoverCommand;

    public ICommand GrantTakeoverConsentCommand => _grantTakeoverConsentCommand;

    public ICommand ExecuteTakeoverCommand => _executeTakeoverCommand;

    public ICommand ReleaseOwnershipCommand => _releaseOwnershipCommand;

    public ICommand PreviewAfterburnerImportCommand => _previewAfterburnerImportCommand;

    public ICommand SaveAfterburnerImportCommand => _saveAfterburnerImportCommand;

    public ICommand PreviewFanControlImportCommand => _previewFanControlImportCommand;

    public ICommand SaveFanControlImportCommand => _saveFanControlImportCommand;

    public ICommand ToggleAdvancedLabCommand { get; }

    public ICommand ResumeAutomationCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    public ICommand ClearDeviceSearchCommand { get; }

    public bool IsAdvancedLab
    {
        get => _isAdvancedLab;
        set
        {
            if (Set(ref _isAdvancedLab, value))
            {
                OnPropertyChanged(nameof(InterfaceModeLabel));
                OnPropertyChanged(nameof(InterfaceModeActionLabel));
                OnPropertyChanged(nameof(InterfaceModeDescription));
            }
        }
    }

    public string InterfaceModeLabel => IsAdvancedLab ? "Advanced Lab" : "Simple";

    public string InterfaceModeActionLabel => IsAdvancedLab ? "Use Simple mode" : "Open Advanced Lab";

    public string InterfaceModeDescription => IsAdvancedLab
        ? "Exact capabilities, bounds, ownership, imports, calibration, and experimental gates are visible."
        : "Daily controls only. Calibration, imports, ownership, raw evidence, and experimental controls are hidden.";

    public bool IsUserAgentOnline
    {
        get => _isUserAgentOnline;
        private set
        {
            if (Set(ref _isUserAgentOnline, value))
            {
                _scanGamesCommand.RaiseCanExecuteChanged();
                _addLightingZoneCommand.RaiseCanExecuteChanged();
                _saveLightingLayoutCommand.RaiseCanExecuteChanged();
                _startMacroRecordingCommand.RaiseCanExecuteChanged();
                _stopMacroRecordingCommand.RaiseCanExecuteChanged();
                _cancelMacroRecordingCommand.RaiseCanExecuteChanged();
                _testMacroCommand.RaiseCanExecuteChanged();
                _saveGameBundleCommand.RaiseCanExecuteChanged();
                _captureDesktopSnapshotCommand.RaiseCanExecuteChanged();
                _startVideoRecordingCommand.RaiseCanExecuteChanged();
                _stopVideoRecordingCommand.RaiseCanExecuteChanged();
                _publishRtssOsdCommand.RaiseCanExecuteChanged();
                _releaseRtssOsdCommand.RaiseCanExecuteChanged();
                _readRtssFrameStatsCommand.RaiseCanExecuteChanged();
                _startFrametimeBenchmarkCommand.RaiseCanExecuteChanged();
                _stopFrametimeBenchmarkCommand.RaiseCanExecuteChanged();
                _startPresentMonBenchmarkCommand.RaiseCanExecuteChanged();
                _stopPresentMonBenchmarkCommand.RaiseCanExecuteChanged();
                _refreshMonitorBrightnessCommand.RaiseCanExecuteChanged();
                _scanHidInventoryCommand.RaiseCanExecuteChanged();
                _readKrakenTelemetryCommand.RaiseCanExecuteChanged();
                _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
                _saveOsdPresentationCommand.RaiseCanExecuteChanged();
                _saveMonitoringPreferencesCommand.RaiseCanExecuteChanged();
                _addMonitoringComparisonSensorCommand.RaiseCanExecuteChanged();
                _removeMonitoringComparisonSensorCommand.RaiseCanExecuteChanged();
                _saveMonitoringComparisonLayoutCommand.RaiseCanExecuteChanged();
                _runInteractiveFanPreflightCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanCaptureDesktopSnapshot));
                OnPropertyChanged(nameof(CanStartVideoRecording));
                OnPropertyChanged(nameof(CanStopVideoRecording));
                OnPropertyChanged(nameof(CanSetMonitorBrightness));
                OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
                OnPropertyChanged(nameof(CanSaveOsdPresentation));
                OnPropertyChanged(nameof(CanSaveMonitoringPreferences));
                OnPropertyChanged(nameof(CanAddMonitoringComparisonSensor));
                OnPropertyChanged(nameof(CanSaveMonitoringComparisonLayout));
                OnPropertyChanged(nameof(UserAgentConnectionLabel));
            }
        }
    }

    public string UserAgentConnectionLabel => IsUserAgentOnline ? "USER AGENT ONLINE" : "USER AGENT OFFLINE";

    public string UserAgentStatus
    {
        get => _userAgentStatus;
        private set => Set(ref _userAgentStatus, value);
    }

    private bool InteractiveFanPreflightSupported
    {
        get => _interactiveFanPreflightSupported;
        set
        {
            if (Set(ref _interactiveFanPreflightSupported, value))
            {
                OnPropertyChanged(nameof(CanRunInteractiveFanPreflight));
                _runInteractiveFanPreflightCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<OsdScreenAnchor> OsdScreenAnchors { get; } = Enum.GetValues<OsdScreenAnchor>();

    public IReadOnlyList<HealthRuleConditionKind> HealthRuleConditions { get; } = Enum.GetValues<HealthRuleConditionKind>();

    public IReadOnlyList<HealthRuleActionKind> HealthRuleActions { get; } = Enum.GetValues<HealthRuleActionKind>();

    public IReadOnlyList<CaptureTargetV1> OsdMonitors => CaptureTargets
        .Where(target => target.Kind == CaptureTargetKind.Display)
        .ToArray();

    public CaptureTargetV1? SelectedOsdMonitor
    {
        get => _selectedOsdMonitor;
        set
        {
            if (Set(ref _selectedOsdMonitor, value))
            {
                NotifyOsdPresentationProperties();
            }
        }
    }

    public OsdScreenAnchor SelectedOsdAnchor
    {
        get => _selectedOsdAnchor;
        set
        {
            if (Set(ref _selectedOsdAnchor, value))
            {
                NotifyOsdPresentationProperties();
            }
        }
    }

    public string OsdHotkeyText
    {
        get => _osdHotkeyText;
        set
        {
            if (Set(ref _osdHotkeyText, value))
            {
                NotifyOsdPresentationProperties();
            }
        }
    }

    public string OsdOpacityText
    {
        get => _osdOpacityText;
        set
        {
            if (Set(ref _osdOpacityText, value))
            {
                NotifyOsdPresentationProperties();
            }
        }
    }

    public string OsdScaleText
    {
        get => _osdScaleText;
        set
        {
            if (Set(ref _osdScaleText, value))
            {
                NotifyOsdPresentationProperties();
            }
        }
    }

    public bool CanSaveOsdPresentation => TryBuildOsdPresentationSettings(out _);

    public SensorTrendDisplay? SelectedMonitoringTrend
    {
        get => _selectedMonitoringTrend;
        set
        {
            if (Set(ref _selectedMonitoringTrend, value))
            {
                string? alias = value is null
                    ? null
                    : _monitoringPreferences.Aliases.FirstOrDefault(item => item.SensorId == value.SensorId)?.Alias;
                SensorAliasText = alias ?? string.Empty;
                SelectedSensorPinned = value is not null && _monitoringPreferences.PinnedSensorIds.Contains(value.SensorId, StringComparer.Ordinal);
                OnPropertyChanged(nameof(CanSaveMonitoringPreferences));
                _saveMonitoringPreferencesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SensorAliasText
    {
        get => _sensorAliasText;
        set
        {
            if (Set(ref _sensorAliasText, value))
            {
                OnPropertyChanged(nameof(CanSaveMonitoringPreferences));
                _saveMonitoringPreferencesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool SelectedSensorPinned
    {
        get => _selectedSensorPinned;
        set
        {
            if (Set(ref _selectedSensorPinned, value))
            {
                OnPropertyChanged(nameof(CanSaveMonitoringPreferences));
                _saveMonitoringPreferencesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanSaveMonitoringPreferences => SelectedMonitoringTrend is not null
        && SensorAliasText.Trim().Length <= 80;

    public MonitoringTrendScope SelectedMonitoringTrendScope
    {
        get => _selectedMonitoringTrendScope;
        set
        {
            if (Set(ref _selectedMonitoringTrendScope, value))
            {
                ApplyMonitoringTrendFilter();
            }
        }
    }

    public string MonitoringTrendScopeLabel => SelectedMonitoringTrendScope switch
    {
        MonitoringTrendScope.All => "All live trends",
        MonitoringTrendScope.Pinned => "Pinned trends",
        MonitoringTrendScope.Temperature => "Temperature trends",
        MonitoringTrendScope.Fan => "Fan RPM trends",
        MonitoringTrendScope.Power => "Power trends",
        _ => "Live trends"
    };

    public string MonitoringTrendFilterSummary => $"Showing {VisibleMonitoringTrends.Count} of {MonitoringTrends.Count} live trends.";

    public SensorTrendDisplay? SelectedMonitoringComparisonTrend
    {
        get => _selectedMonitoringComparisonTrend;
        set
        {
            if (Set(ref _selectedMonitoringComparisonTrend, value))
            {
                NotifyMonitoringComparisonProperties();
            }
        }
    }

    public int MonitoringComparisonSensorCount => MonitoringComparisonSeries.Count;

    public string MonitoringComparisonStatus
    {
        get => _monitoringComparisonStatus;
        private set => Set(ref _monitoringComparisonStatus, value);
    }

    public bool CanAddMonitoringComparisonSensor => SelectedMonitoringComparisonTrend is not null
        && MonitoringComparisonSeries.Count < 4
        && !MonitoringComparisonSeries.Any(series => string.Equals(
            series.SensorId,
            SelectedMonitoringComparisonTrend.SensorId,
            StringComparison.Ordinal));

    public bool CanSaveMonitoringComparisonLayout => _monitoringComparisonLayout.SensorIds.Count <= 4;

    public TimelineScope SelectedTimelineScope
    {
        get => _selectedTimelineScope;
        set
        {
            if (Set(ref _selectedTimelineScope, value))
            {
                ApplyTimelineFilter();
            }
        }
    }

    public string TimelineScopeLabel => SelectedTimelineScope switch
    {
        TimelineScope.All => "All events",
        TimelineScope.Health => "Safety and health",
        TimelineScope.Profile => "Profile changes",
        TimelineScope.Conflict => "Control conflicts",
        TimelineScope.Adapter => "Adapter activity",
        _ => "Events"
    };

    public string TimelineFilterSummary => $"Showing {VisibleTimelineEvents.Count} of {TimelineEvents.Count} recent events.";

    public HealthRuleConditionKind NewHealthRuleCondition
    {
        get => _newHealthRuleCondition;
        set
        {
            if (Set(ref _newHealthRuleCondition, value))
            {
                if (value is HealthRuleConditionKind.WheaEvent or HealthRuleConditionKind.DisplayDriverReset)
                {
                    _newHealthConsecutiveText = "1";
                    OnPropertyChanged(nameof(NewHealthConsecutiveText));
                }
                NotifyHealthRuleProperties();
            }
        }
    }

    public HealthRuleActionKind NewHealthRuleAction
    {
        get => _newHealthRuleAction;
        set
        {
            if (Set(ref _newHealthRuleAction, value))
            {
                NotifyHealthRuleProperties();
            }
        }
    }

    public SensorTrendDisplay? SelectedHealthTrend
    {
        get => _selectedHealthTrend;
        set
        {
            if (Set(ref _selectedHealthTrend, value))
            {
                NotifyHealthRuleProperties();
            }
        }
    }

    public ProfileV1? SelectedEmergencyProfile
    {
        get => _selectedEmergencyProfile;
        set
        {
            if (Set(ref _selectedEmergencyProfile, value))
            {
                NotifyHealthRuleProperties();
            }
        }
    }

    public string NewHealthRuleName
    {
        get => _newHealthRuleName;
        set
        {
            if (Set(ref _newHealthRuleName, value))
            {
                NotifyHealthRuleProperties();
            }
        }
    }

    public string NewHealthThresholdText
    {
        get => _newHealthThresholdText;
        set
        {
            if (Set(ref _newHealthThresholdText, value))
            {
                NotifyHealthRuleProperties();
            }
        }
    }

    public string NewHealthConsecutiveText
    {
        get => _newHealthConsecutiveText;
        set
        {
            if (Set(ref _newHealthConsecutiveText, value))
            {
                NotifyHealthRuleProperties();
            }
        }
    }

    public string NewHealthCooldownText
    {
        get => _newHealthCooldownText;
        set
        {
            if (Set(ref _newHealthCooldownText, value))
            {
                NotifyHealthRuleProperties();
            }
        }
    }

    public string SafeModeReason
    {
        get => _safeModeReason;
        set => Set(ref _safeModeReason, value);
    }

    public bool CanSaveHealthRule => TryBuildHealthRule(out _);

    public string HealthRecommendationStatus
    {
        get => _healthRecommendationStatus;
        private set => Set(ref _healthRecommendationStatus, value);
    }

    public bool CanAddRecommendedHealthRules => HealthRuleRecommendations.Build(
            MonitoringTrends.Select(trend => trend.Trend).ToArray())
        .Any(recommendation => !HealthRules.Any(existing => SameHealthRule(existing.Rule, recommendation.Rule)));

    public bool IsSafeModeEnabled => _safetyRecoveryStatus?.State.SafeModeEnabled == true;

    public string SafetyRecoveryGuidance => _safetyRecoveryStatus?.Guidance
        ?? "The connected service has not supplied recovery-console state.";

    public string SafeModeLabel => IsSafeModeEnabled ? "SAFE MODE ACTIVE" : "SAFE MODE OFF";

    public string WgcRecordingStatus => _wgcRecordingPreflight?.Message
        ?? "Windows Graphics Capture recording preflight has not run.";

    public bool IsWgcRecordingReady => _wgcRecordingPreflight?.GraphicsCaptureSupported == true
        && _wgcRecordingPreflight.EncoderConfigured;

    public int ActiveHealthAlertCount => HealthAlerts.Count(alert => alert.State is "Active" or "Acknowledged");

    public bool HasHealthAlerts => HealthAlerts.Count > 0;

    public bool HasTimelineEvents => TimelineEvents.Count > 0;

    public bool HasVisibleTimelineEvents => VisibleTimelineEvents.Count > 0;

    public bool HasVisibleMonitoringTrends => VisibleMonitoringTrends.Count > 0;

    public TakeoverPlanV1? TakeoverPreview
    {
        get => _takeoverPreview;
        private set
        {
            if (Set(ref _takeoverPreview, value))
            {
                if (value?.Processes.Contains(SelectedTakeoverTarget) != true)
                {
                    SelectedTakeoverTarget = value is { Processes.Count: > 0 }
                        ? value.Processes[0]
                        : null;
                }
                OnPropertyChanged(nameof(TakeoverTargets));
                OnPropertyChanged(nameof(TakeoverPlanSummary));
                NotifyOwnershipProperties();
            }
        }
    }

    public IReadOnlyList<TakeoverProcessIdentity> TakeoverTargets => TakeoverPreview?.Processes ?? [];

    public TakeoverProcessIdentity? SelectedTakeoverTarget
    {
        get => _selectedTakeoverTarget;
        set
        {
            if (Set(ref _selectedTakeoverTarget, value))
            {
                NotifyOwnershipProperties();
            }
        }
    }

    public bool TakeoverAllowForceTermination
    {
        get => _takeoverAllowForceTermination;
        set
        {
            if (Set(ref _takeoverAllowForceTermination, value))
            {
                NotifyOwnershipProperties();
            }
        }
    }

    public bool TakeoverDisableStartup
    {
        get => _takeoverDisableStartup;
        set
        {
            if (Set(ref _takeoverDisableStartup, value))
            {
                NotifyOwnershipProperties();
            }
        }
    }

    public bool TakeoverExactProcessesConfirmed
    {
        get => _takeoverExactProcessesConfirmed;
        set
        {
            if (Set(ref _takeoverExactProcessesConfirmed, value))
            {
                NotifyOwnershipProperties();
            }
        }
    }

    public string OwnershipStatus
    {
        get => _ownershipStatus;
        private set => Set(ref _ownershipStatus, value);
    }

    public string TakeoverPlanSummary => TakeoverPreview is null
        ? "Preview the exact running competitors before any consent can be stored."
        : $"{TakeoverPreview.Processes.Count} exact process(es), {TakeoverPreview.ControlsToReset.Count} resettable control(s), {TakeoverPreview.Warnings.Count} warning(s).";

    public TakeoverExecutionStatusV1? TakeoverExecutorStatus => _ownershipOverview?.ExecutorStatus;

    public TakeoverTransactionV1? ActiveOwnershipTransaction => _ownershipOverview?.Transactions?
        .Where(transaction => transaction.State == TakeoverTransactionState.Completed && !string.IsNullOrWhiteSpace(transaction.LeaseId))
        .OrderByDescending(transaction => transaction.UpdatedAt)
        .FirstOrDefault();

    public bool CanExecuteTakeover => TakeoverPreview is not null
        && TakeoverExecutorStatus?.CanExecute == true
        && TakeoverExactProcessesConfirmed
        && TakeoverPreview.Processes.Count > 0
        && TakeoverPreview.ControlsToReset.Count > 0
        && TakeoverPreview.Processes.All(HasStoredTakeoverConsent);

    public string AfterburnerImportPath
    {
        get => _afterburnerImportPath;
        set
        {
            if (Set(ref _afterburnerImportPath, value))
            {
                NotifyImportProperties();
            }
        }
    }

    public string AfterburnerImportSection
    {
        get => _afterburnerImportSection;
        set
        {
            if (Set(ref _afterburnerImportSection, value))
            {
                NotifyImportProperties();
            }
        }
    }

    public ProfileImportPreviewV1? AfterburnerImportPreview
    {
        get => _afterburnerImportPreview;
        private set
        {
            if (Set(ref _afterburnerImportPreview, value))
            {
                OnPropertyChanged(nameof(AfterburnerImportSummary));
                NotifyImportProperties();
            }
        }
    }

    public string AfterburnerImportStatus
    {
        get => _afterburnerImportStatus;
        private set => Set(ref _afterburnerImportStatus, value);
    }

    public bool CanPreviewAfterburnerImport => !string.IsNullOrWhiteSpace(AfterburnerImportPath)
        && !string.IsNullOrWhiteSpace(AfterburnerImportSection)
        && File.Exists(AfterburnerImportPath);

    public string AfterburnerImportSummary => AfterburnerImportPreview is null
        ? "No profile mapping has been previewed."
        : $"{AfterburnerImportPreview.Settings.Count(setting => setting.State == ImportMappingState.Mapped)} mapped, {AfterburnerImportPreview.Settings.Count(setting => setting.State == ImportMappingState.ManualOnly)} manual-only, {AfterburnerImportPreview.Settings.Count(setting => setting.State is ImportMappingState.Blocked or ImportMappingState.Invalid or ImportMappingState.Unmapped)} unavailable.";

    public string FanControlImportPath
    {
        get => _fanControlImportPath;
        set
        {
            if (Set(ref _fanControlImportPath, value))
            {
                NotifyImportProperties();
            }
        }
    }

    public string FanControlSensorMappings
    {
        get => _fanControlSensorMappings;
        set
        {
            if (Set(ref _fanControlSensorMappings, value))
            {
                NotifyImportProperties();
            }
        }
    }

    public string FanControlControlMappings
    {
        get => _fanControlControlMappings;
        set
        {
            if (Set(ref _fanControlControlMappings, value))
            {
                NotifyImportProperties();
            }
        }
    }

    public CoolingImportPreviewV1? FanControlImportPreview
    {
        get => _fanControlImportPreview;
        private set
        {
            if (Set(ref _fanControlImportPreview, value))
            {
                OnPropertyChanged(nameof(FanControlImportSummary));
                NotifyImportProperties();
            }
        }
    }

    public string FanControlImportStatus
    {
        get => _fanControlImportStatus;
        private set => Set(ref _fanControlImportStatus, value);
    }

    public bool CanPreviewFanControlImport => !string.IsNullOrWhiteSpace(FanControlImportPath)
        && File.Exists(FanControlImportPath);

    public string FanControlImportSummary => FanControlImportPreview is null
        ? "No cooling graph has been previewed."
        : $"{FanControlImportPreview.Calibrations.Count} calibration(s), {FanControlImportPreview.Graph?.Nodes.Count ?? 0} node(s), {FanControlImportPreview.Graph?.Outputs.Count ?? 0} output(s).";

    public bool HasGames => Games.Count > 0;

    public int WorkflowCount => Workflows.Count;

    public int LightingSceneCount => LightingScenes.Count;

    public int EffectGraphCount => EffectGraphs.Count;

    public int MacroCount => Macros.Count;

    public int MacroRecordingSessionCount => MacroRecordingSessions.Count;

    public int ScriptCount => Scripts.Count;

    public int OsdLayoutCount => OsdLayouts.Count;

    public int CapturePresetCount => CapturePresets.Count;

    public int AdapterTraceCount => AdapterTrace.Count;

    public bool HasAdapterTrace => AdapterTrace.Count > 0;

    public string DynamicLightingStatus
    {
        get => _dynamicLightingStatus;
        private set => Set(ref _dynamicLightingStatus, value);
    }

    public int DynamicLightingDeviceCount => DynamicLightingDevices.Count;

    public bool HasRgbRouteAssessments => RgbRouteAssessments.Count > 0;

    public int RgbReadyRouteCount => RgbRouteAssessments.Count(route => route.State == RgbRouteState.Ready);

    public int RgbSetupRouteCount => RgbRouteAssessments.Count(route => route.State == RgbRouteState.SetupRequired);

    public int RgbReadOnlyRouteCount => RgbRouteAssessments.Count(route => route.State == RgbRouteState.ReadOnly);

    public int RgbBlockedRouteCount => RgbRouteAssessments.Count(route => route.State == RgbRouteState.Blocked);

    public bool HasReadyOpenRgbRoutes => RgbRouteAssessments.Any(route =>
        route.Route == RgbRouteKind.OpenRgbBridge && route.State == RgbRouteState.Ready);

    public bool HasReadyDynamicLightingRoutes => RgbRouteAssessments.Any(route =>
        route.Route == RgbRouteKind.WindowsDynamicLighting && route.State == RgbRouteState.Ready);

    public string RgbCompatibilitySummary => !HasRgbRouteAssessments
        ? "Waiting for RGB inventory and standard-bridge discovery."
        : $"{RgbReadyRouteCount} ready · {RgbSetupRouteCount} setup needed · {RgbReadOnlyRouteCount} direct qualification · {RgbBlockedRouteCount} blocked. Manufacturer recognition never enables a raw USB write by itself.";

    public bool IsRgbSyncRunning
    {
        get => _isRgbSyncRunning;
        private set
        {
            if (Set(ref _isRgbSyncRunning, value))
            {
                OnPropertyChanged(nameof(CanSyncAllRgb));
                _syncAllRgbCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanSyncAllRgb => AreOpenRgbInputsValid && !IsRgbSyncRunning;

    public string RgbSyncStatus
    {
        get => _rgbSyncStatus;
        private set => Set(ref _rgbSyncStatus, value);
    }

    public bool HasRgbApplyOutcomes => LastRgbApplyOutcomes.Count > 0;

    public string ProfileActivationStatus
    {
        get => _profileActivationStatus;
        private set => Set(ref _profileActivationStatus, value);
    }

    public string ProfileDryRunStatus
    {
        get => _profileDryRunStatus;
        private set => Set(ref _profileDryRunStatus, value);
    }

    public DynamicLightingDevice? SelectedDynamicLightingDevice
    {
        get => _selectedDynamicLightingDevice;
        set
        {
            if (Set(ref _selectedDynamicLightingDevice, value))
            {
                _addLightingZoneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public LightingSceneV1? SelectedLightingScene
    {
        get => _selectedLightingScene;
        set
        {
            if (Set(ref _selectedLightingScene, value))
            {
                _applyDynamicLightingSceneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LightingLayoutName
    {
        get => _lightingLayoutName;
        set
        {
            if (Set(ref _lightingLayoutName, value))
            {
                _saveLightingLayoutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LightingZoneName
    {
        get => _lightingZoneName;
        set
        {
            if (Set(ref _lightingZoneName, value))
            {
                _addLightingZoneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LightingZoneLedIndices
    {
        get => _lightingZoneLedIndices;
        set
        {
            if (Set(ref _lightingZoneLedIndices, value))
            {
                _addLightingZoneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LightingZoneXText
    {
        get => _lightingZoneXText;
        set
        {
            if (Set(ref _lightingZoneXText, value))
            {
                _addLightingZoneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LightingZoneYText
    {
        get => _lightingZoneYText;
        set
        {
            if (Set(ref _lightingZoneYText, value))
            {
                _addLightingZoneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LightingZoneWidthText
    {
        get => _lightingZoneWidthText;
        set
        {
            if (Set(ref _lightingZoneWidthText, value))
            {
                _addLightingZoneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LightingZoneHeightText
    {
        get => _lightingZoneHeightText;
        set
        {
            if (Set(ref _lightingZoneHeightText, value))
            {
                _addLightingZoneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAddLightingZone => SelectedDynamicLightingDevice is not null
        && !string.IsNullOrWhiteSpace(LightingZoneName)
        && !string.IsNullOrWhiteSpace(LightingZoneLedIndices);

    public bool CanSaveLightingLayout => !string.IsNullOrWhiteSpace(LightingLayoutName)
        && DraftLightingZones.Count > 0;

    public MacroRecordingSessionV1? ActiveMacroRecording
    {
        get => _activeMacroRecording;
        private set
        {
            if (Set(ref _activeMacroRecording, value))
            {
                OnPropertyChanged(nameof(IsMacroRecording));
                OnPropertyChanged(nameof(CanStartMacroRecording));
                _startMacroRecordingCommand.RaiseCanExecuteChanged();
                _stopMacroRecordingCommand.RaiseCanExecuteChanged();
                _cancelMacroRecordingCommand.RaiseCanExecuteChanged();
                _testMacroCommand.RaiseCanExecuteChanged();
                NotifyMacroEditorProperties();
            }
        }
    }

    public bool IsMacroRecording => ActiveMacroRecording is not null;

    public string MacroRecordingName
    {
        get => _macroRecordingName;
        set
        {
            if (Set(ref _macroRecordingName, value))
            {
                OnPropertyChanged(nameof(CanStartMacroRecording));
                _startMacroRecordingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int MacroRecordingDurationSeconds
    {
        get => _macroRecordingDurationSeconds;
        set => Set(ref _macroRecordingDurationSeconds, value);
    }

    public string MacroRecordingStatus
    {
        get => _macroRecordingStatus;
        private set => Set(ref _macroRecordingStatus, value);
    }

    public string RtssBridgeStatus
    {
        get => _rtssBridgeStatus;
        private set => Set(ref _rtssBridgeStatus, value);
    }

    public string RtssOsdPublishStatus
    {
        get => _rtssOsdPublishStatus;
        private set => Set(ref _rtssOsdPublishStatus, value);
    }

    public bool IsRtssOsdPublishing
    {
        get => _isRtssOsdPublishing;
        private set
        {
            if (Set(ref _isRtssOsdPublishing, value))
            {
                _publishRtssOsdCommand.RaiseCanExecuteChanged();
                _releaseRtssOsdCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string GameBarBridgeStatus
    {
        get => _gameBarBridgeStatus;
        private set => Set(ref _gameBarBridgeStatus, value);
    }

    public string CaptureBridgeStatus
    {
        get => _captureBridgeStatus;
        private set => Set(ref _captureBridgeStatus, value);
    }

    public OsdLayoutV1? SelectedDesktopOsdLayout
    {
        get => _selectedDesktopOsdLayout;
        set
        {
            if (Set(ref _selectedDesktopOsdLayout, value) && IsDesktopOsdVisible)
            {
                RefreshDesktopOsd();
            }
        }
    }

    public CaptureTargetV1? SelectedCaptureTarget
    {
        get => _selectedCaptureTarget;
        set
        {
            if (Set(ref _selectedCaptureTarget, value))
            {
                OnPropertyChanged(nameof(CanCaptureDesktopSnapshot));
                OnPropertyChanged(nameof(CanStartVideoRecording));
                _captureDesktopSnapshotCommand.RaiseCanExecuteChanged();
                _startVideoRecordingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDesktopOsdVisible => _desktopOsd.IsVisible;

    public bool CanShowDesktopOsd => (_snapshot?.Sensors.Count ?? 0) > 0
        && _osdPresentationSettings.Enabled
        && !IsDesktopOsdVisible;

    public bool CanCaptureDesktopSnapshot => IsUserAgentOnline && SelectedCaptureTarget is not null;

    public bool CanStartVideoRecording => IsUserAgentOnline
        && SelectedCaptureTarget is not null
        && IsWgcRecordingReady
        && !IsVideoRecording;

    public bool CanStopVideoRecording => IsUserAgentOnline && IsVideoRecording;

    public bool IsVideoRecording
    {
        get => _isVideoRecording;
        private set
        {
            if (Set(ref _isVideoRecording, value))
            {
                OnPropertyChanged(nameof(CanStartVideoRecording));
                OnPropertyChanged(nameof(CanStopVideoRecording));
                _startVideoRecordingCommand.RaiseCanExecuteChanged();
                _stopVideoRecordingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string VideoRecordingStatus
    {
        get => _videoRecordingStatus;
        private set => Set(ref _videoRecordingStatus, value);
    }

    public MonitorBrightnessDeviceV1? SelectedMonitorBrightnessDevice
    {
        get => _selectedMonitorBrightnessDevice;
        set
        {
            if (Set(ref _selectedMonitorBrightnessDevice, value))
            {
                MonitorBrightnessDeviceConfirmed = false;
                if (value?.CurrentPercent is int currentPercent)
                {
                    MonitorBrightnessPercentText = currentPercent.ToString(CultureInfo.InvariantCulture);
                }
                OnPropertyChanged(nameof(CanSetMonitorBrightness));
                OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
                _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Slider-friendly view of <see cref="MonitorBrightnessPercentText"/>; whole percent, clamped 0–100.</summary>
    public double MonitorBrightnessPercentValue
    {
        get => int.TryParse(MonitorBrightnessPercentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent)
            ? Math.Clamp(percent, 0, 100)
            : 50;
        set => MonitorBrightnessPercentText = ((int)Math.Round(Math.Clamp(value, 0, 100))).ToString(CultureInfo.InvariantCulture);
    }

    public string MonitorBrightnessPercentText
    {
        get => _monitorBrightnessPercentText;
        set
        {
            if (Set(ref _monitorBrightnessPercentText, value))
            {
                OnPropertyChanged(nameof(MonitorBrightnessPercentValue));
                OnPropertyChanged(nameof(CanSetMonitorBrightness));
                _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool MonitorBrightnessDeviceConfirmed
    {
        get => _monitorBrightnessDeviceConfirmed;
        set
        {
            if (Set(ref _monitorBrightnessDeviceConfirmed, value))
            {
                OnPropertyChanged(nameof(CanSetMonitorBrightness));
                _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanSetMonitorBrightness => IsUserAgentOnline
        && MonitorBrightnessDeviceConfirmed
        && SelectedMonitorBrightnessDevice?.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified
        && int.TryParse(MonitorBrightnessPercentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent)
        && percent is >= 0 and <= 100;

    public bool IsSelectedMonitorBrightnessWritable => IsUserAgentOnline
        && SelectedMonitorBrightnessDevice?.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified;

    public string MonitorBrightnessStatus
    {
        get => _monitorBrightnessStatus;
        private set => Set(ref _monitorBrightnessStatus, value);
    }

    public ObservableCollection<HidDeviceDisplay> HidDevices { get; } = [];

    public string HidInventoryStatus
    {
        get => _hidInventoryStatus;
        private set => Set(ref _hidInventoryStatus, value);
    }

    public string DesktopOsdStatus
    {
        get => _desktopOsdStatus;
        private set => Set(ref _desktopOsdStatus, value);
    }

    public string DesktopSnapshotStatus
    {
        get => _desktopSnapshotStatus;
        private set => Set(ref _desktopSnapshotStatus, value);
    }

    public string UpdatePlatformStatus
    {
        get => _updatePlatformStatus;
        private set => Set(ref _updatePlatformStatus, value);
    }

    public int PendingUpdateCount
    {
        get => _pendingUpdateCount;
        private set => Set(ref _pendingUpdateCount, value);
    }

    public bool CanStartMacroRecording => ActiveMacroRecording is null
        && !string.IsNullOrWhiteSpace(MacroRecordingName)
        && MacroRecordingDurationSeconds is >= 1 and <= 600;

    public string MacroEditorName
    {
        get => _macroEditorName;
        set
        {
            if (Set(ref _macroEditorName, value))
            {
                NotifyMacroEditorProperties();
            }
        }
    }

    public string MacroEditorKeyCodeText
    {
        get => _macroEditorKeyCodeText;
        set
        {
            if (Set(ref _macroEditorKeyCodeText, value))
            {
                NotifyMacroEditorProperties();
            }
        }
    }

    public string MacroEditorDelayMillisecondsText
    {
        get => _macroEditorDelayMillisecondsText;
        set
        {
            if (Set(ref _macroEditorDelayMillisecondsText, value))
            {
                NotifyMacroEditorProperties();
            }
        }
    }

    public string MacroEditorSummary
    {
        get => _macroEditorSummary;
        private set => Set(ref _macroEditorSummary, value);
    }

    public bool CanAddMacroKeyPress => SelectedMacro is not null
        && ActiveMacroRecording is null
        && TryGetMacroEditorKeyPress(out _, out _);

    public bool CanRemoveMacroKeyPress => SelectedMacro is not null
        && ActiveMacroRecording is null
        && HasTrailingEditableKeyPress();

    public bool CanSaveMacroEdit => SelectedMacro is not null
        && ActiveMacroRecording is null
        && !string.IsNullOrWhiteSpace(MacroEditorName)
        && _macroEditorSteps.Count > 0;

    public MacroV1? SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (Set(ref _selectedMacro, value))
            {
                LoadMacroEditor(value);
                _testMacroCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public GameEntryV1? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!Set(ref _selectedGame, value))
            {
                return;
            }

            _selectedGameProfile = Profiles.FirstOrDefault(profile => profile.Id == value?.ProfileId);
            _selectedGameLightingScene = LightingScenes.FirstOrDefault(scene => scene.Id == value?.LightingSceneId);
            _selectedGameMacro = Macros.FirstOrDefault(macro => (value?.MacroIds ?? []).Contains(macro.Id, StringComparer.Ordinal));
            _selectedGameOsdLayout = OsdLayouts.FirstOrDefault(layout => layout.Id == value?.OsdLayoutId);
            _selectedGameCapturePreset = CapturePresets.FirstOrDefault(preset => preset.Id == value?.CapturePresetId);
            OnPropertyChanged(nameof(SelectedGameProfile));
            OnPropertyChanged(nameof(SelectedGameLightingScene));
            OnPropertyChanged(nameof(SelectedGameMacro));
            OnPropertyChanged(nameof(SelectedGameOsdLayout));
            OnPropertyChanged(nameof(SelectedGameCapturePreset));
            OnPropertyChanged(nameof(GameBundleSummary));
            _saveGameBundleCommand.RaiseCanExecuteChanged();
            _applyGameBundleCommand.RaiseCanExecuteChanged();
        }
    }

    public ProfileV1? SelectedGameProfile
    {
        get => _selectedGameProfile;
        set
        {
            if (Set(ref _selectedGameProfile, value))
            {
                OnPropertyChanged(nameof(GameBundleSummary));
            }
        }
    }

    public LightingSceneV1? SelectedGameLightingScene
    {
        get => _selectedGameLightingScene;
        set
        {
            if (Set(ref _selectedGameLightingScene, value))
            {
                OnPropertyChanged(nameof(GameBundleSummary));
            }
        }
    }

    public MacroV1? SelectedGameMacro
    {
        get => _selectedGameMacro;
        set
        {
            if (Set(ref _selectedGameMacro, value))
            {
                OnPropertyChanged(nameof(GameBundleSummary));
            }
        }
    }

    public OsdLayoutV1? SelectedGameOsdLayout
    {
        get => _selectedGameOsdLayout;
        set
        {
            if (Set(ref _selectedGameOsdLayout, value))
            {
                OnPropertyChanged(nameof(GameBundleSummary));
            }
        }
    }

    public CapturePresetV1? SelectedGameCapturePreset
    {
        get => _selectedGameCapturePreset;
        set
        {
            if (Set(ref _selectedGameCapturePreset, value))
            {
                OnPropertyChanged(nameof(GameBundleSummary));
            }
        }
    }

    public string GameBundleSummary => SelectedGame is null
        ? "Select a local game to assign a profile, lighting scene, macro, OSD, and capture preset."
        : $"{SelectedGameProfile?.Name ?? "No profile"} · {SelectedGameLightingScene?.Name ?? "No scene"} · {SelectedGameMacro?.Name ?? "No macro"} · {SelectedGameOsdLayout?.Name ?? "No OSD"} · {SelectedGameCapturePreset?.Name ?? "No capture"}";

    public string GameBundleActivationStatus
    {
        get => _gameBundleActivationStatus;
        private set => Set(ref _gameBundleActivationStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => Set(ref _busyMessage, value);
    }

    public string ServiceStatusText
    {
        get => _serviceStatusText;
        private set => Set(ref _serviceStatusText, value);
    }

    public bool IsServiceOnline
    {
        get => _isServiceOnline;
        private set
        {
            if (!Set(ref _isServiceOnline, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ServiceStateLabel));
            OnPropertyChanged(nameof(ConnectionTone));
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(CanUseServiceWrites));
            OnPropertyChanged(nameof(IsRecoveryRequired));
            OnPropertyChanged(nameof(WriteStateLabel));
            OnPropertyChanged(nameof(WriteStateTone));
            _toggleHardwareControlCommand.RaiseCanExecuteChanged();
            _applyGpuControlCommand.RaiseCanExecuteChanged();
            _startGpuAutoOcCommand.RaiseCanExecuteChanged();
            _enableGpuFanAutoModeCommand.RaiseCanExecuteChanged();
            _enableCaseFansAutoModeCommand.RaiseCanExecuteChanged();
            _applyProfileCommand.RaiseCanExecuteChanged();
            _previewProfileCommand.RaiseCanExecuteChanged();
            _resetVerifiedCommand.RaiseCanExecuteChanged();
            _closeBlockersCommand.RaiseCanExecuteChanged();
            _startCalibrationCommand.RaiseCanExecuteChanged();
            _startTuneCommand.RaiseCanExecuteChanged();
            _abortOperationCommand.RaiseCanExecuteChanged();
            _addAutomationRuleCommand.RaiseCanExecuteChanged();
            _deleteAutomationRuleCommand.RaiseCanExecuteChanged();
            _beginFanCommissioningCommand.RaiseCanExecuteChanged();
            _observeFanCommissioningCommand.RaiseCanExecuteChanged();
            _confirmFanCommissioningCommand.RaiseCanExecuteChanged();
            _completeFanCommissioningCommand.RaiseCanExecuteChanged();
            _cancelFanCommissioningCommand.RaiseCanExecuteChanged();
            _recoverFanCommissioningCommand.RaiseCanExecuteChanged();
            _saveHealthRuleCommand.RaiseCanExecuteChanged();
            _deleteHealthRuleCommand.RaiseCanExecuteChanged();
            _acknowledgeHealthAlertCommand.RaiseCanExecuteChanged();
            _enableSafeModeCommand.RaiseCanExecuteChanged();
            _disableSafeModeCommand.RaiseCanExecuteChanged();
            NotifyOwnershipProperties();
            NotifyImportProperties();
            RebuildExperimentalControlCenter();
            UpdateSafetySummary();
        }
    }

    public string ServiceStateLabel => IsServiceOnline
        ? "Connected"
        : _snapshot is null ? "Connecting" : "Local read-only";

    public string ConnectionTone => IsServiceOnline
        ? CanUseServiceWrites ? "Safe" : "Warning"
        : _snapshot is null ? "Neutral" : "Warning";

    public ServiceRuntimeCompatibilityV1 ServiceCompatibility => _serviceCompatibility;

    public string ServiceCompatibilityLabel => _serviceCompatibility.State switch
    {
        ServiceCompatibilityState.Ready => "Runtime ready",
        ServiceCompatibilityState.ReadOnly => "Read-only runtime",
        ServiceCompatibilityState.UpgradeRequired => "Runtime update required",
        _ => "Runtime unavailable"
    };

    public string ServiceCompatibilityMessage => _serviceCompatibility.Summary;

    public string ServiceCompatibilityTone => _serviceCompatibility.State == ServiceCompatibilityState.Ready
        ? "Safe"
        : _serviceCompatibility.State == ServiceCompatibilityState.Unavailable && _snapshot is null
            ? "Neutral"
            : "Warning";

    public bool CanUseServiceWrites => IsServiceOnline
        && _serviceCompatibility.CanUseServiceWrites
        && _status is { WritesEnabled: true, RecoveryRequired: false };

    public bool IsRecoveryRequired => _status?.RecoveryRequired == true;

    public bool IsServiceUpgradeRequired => _serviceCompatibility.State is ServiceCompatibilityState.UpgradeRequired or ServiceCompatibilityState.ReadOnly;

    public string ActiveProfileName
    {
        get => _activeProfileName;
        private set => Set(ref _activeProfileName, value);
    }

    public string CurrentPageTitle
    {
        get => _currentPageTitle;
        private set => Set(ref _currentPageTitle, value);
    }

    public string CurrentPageSubtitle
    {
        get => _currentPageSubtitle;
        private set => Set(ref _currentPageSubtitle, value);
    }

    public string SafetySummary
    {
        get => _safetySummary;
        private set => Set(ref _safetySummary, value);
    }

    public string SafetyTone
    {
        get => _safetyTone;
        private set => Set(ref _safetyTone, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => Set(ref _lastUpdatedText, value);
    }

    public string DataSourceLabel
    {
        get => _dataSourceLabel;
        private set => Set(ref _dataSourceLabel, value);
    }

    public string DeviceSearchText
    {
        get => _deviceSearchText;
        set
        {
            if (Set(ref _deviceSearchText, value))
            {
                ApplyDeviceFilter();
                (ClearDeviceSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string DeviceResultSummary
    {
        get => _deviceResultSummary;
        private set => Set(ref _deviceResultSummary, value);
    }

    public string DeviceCompatibilitySummary
    {
        get => _deviceCompatibilitySummary;
        private set => Set(ref _deviceCompatibilitySummary, value);
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    public string NoticeTone
    {
        get => _noticeTone;
        private set => Set(ref _noticeTone, value);
    }

    public bool HasNotice
    {
        get => _hasNotice;
        private set
        {
            if (Set(ref _hasNotice, value))
            {
                (DismissNoticeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public OperationTargetDisplay? SelectedCalibrationTarget
    {
        get => _selectedCalibrationTarget;
        set
        {
            if (!Set(ref _selectedCalibrationTarget, value))
            {
                return;
            }

            CalibrationDeviceAcknowledged = false;
            CommissioningObserverReady = false;
            ApplyCoolingOutputAssignmentForTarget();
            SelectCommissioningForTarget();
            NotifyOperationEligibility();
        }
    }

    public FanCommissioningSessionV1? SelectedFanCommissioningSession
    {
        get => _selectedFanCommissioningSession;
        set
        {
            if (!Set(ref _selectedFanCommissioningSession, value))
            {
                return;
            }

            if (value is not null)
            {
                CommissioningHeaderName = value.HeaderName;
                CommissioningNotes = value.Notes ?? string.Empty;
                CommissioningHeaderConfirmed = value.PhysicalHeaderObserved;
            }
            CommissioningObserverReady = false;
            OnPropertyChanged(nameof(CommissioningStateLabel));
            OnPropertyChanged(nameof(CanConfirmFanCommissioning));
            OnPropertyChanged(nameof(CanPulseFanCommissioning));
            OnPropertyChanged(nameof(CanCompleteFanCommissioning));
            OnPropertyChanged(nameof(CanCancelFanCommissioning));
            OnPropertyChanged(nameof(CanRecoverFanCommissioning));
            OnPropertyChanged(nameof(CanRunInteractiveFanPreflight));
            _observeFanCommissioningCommand.RaiseCanExecuteChanged();
            _runInteractiveFanPreflightCommand.RaiseCanExecuteChanged();
            _pulseFanCommissioningCommand.RaiseCanExecuteChanged();
            _confirmFanCommissioningCommand.RaiseCanExecuteChanged();
            _completeFanCommissioningCommand.RaiseCanExecuteChanged();
            _cancelFanCommissioningCommand.RaiseCanExecuteChanged();
            _recoverFanCommissioningCommand.RaiseCanExecuteChanged();
            NotifyOperationEligibility();
        }
    }

    public string CommissioningHeaderName
    {
        get => _commissioningHeaderName;
        set
        {
            if (Set(ref _commissioningHeaderName, value))
            {
                OnPropertyChanged(nameof(CommissioningPreflight));
                OnPropertyChanged(nameof(CanBeginFanCommissioning));
                OnPropertyChanged(nameof(CanConfirmFanCommissioning));
                _beginFanCommissioningCommand.RaiseCanExecuteChanged();
                _confirmFanCommissioningCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CommissioningNotes
    {
        get => _commissioningNotes;
        set => Set(ref _commissioningNotes, value);
    }

    public CoolingOutputRole SelectedCoolingOutputRole
    {
        get => _selectedCoolingOutputRole;
        set
        {
            if (!Set(ref _selectedCoolingOutputRole, value))
            {
                return;
            }

            if (CoolingOutputAssignmentPolicy.IsSafetyCritical(value))
            {
                AllowCaseFanStop = false;
            }
            NotifyCoolingOutputAssignmentProperties();
        }
    }

    public string CoolingOutputHeaderName
    {
        get => _coolingOutputHeaderName;
        set
        {
            if (Set(ref _coolingOutputHeaderName, value))
            {
                NotifyCoolingOutputAssignmentProperties();
            }
        }
    }

    public bool RemoveCoolingSafetyProtectionAcknowledged
    {
        get => _removeCoolingSafetyProtectionAcknowledged;
        set
        {
            if (Set(ref _removeCoolingSafetyProtectionAcknowledged, value))
            {
                NotifyCoolingOutputAssignmentProperties();
            }
        }
    }

    public bool IsSelectedCoolingOutputProtected =>
        CoolingOutputAssignmentPolicy.IsSafetyCritical(SelectedCoolingOutputRole)
        || GetSelectedCoolingOutputAssignment() is { IsSafetyCritical: true };

    public bool CanAllowCaseFanStop => !IsSelectedCoolingOutputProtected;

    public bool CanSaveCoolingOutputAssignment
    {
        get
        {
            if (SelectedCalibrationTarget is null)
            {
                return false;
            }

            CoolingOutputAssignmentV1? existing = GetSelectedCoolingOutputAssignment();
            bool removingSafetyProtection = existing is { IsSafetyCritical: true }
                && !CoolingOutputAssignmentPolicy.IsSafetyCritical(SelectedCoolingOutputRole);
            return (SelectedCoolingOutputRole == CoolingOutputRole.Unknown
                    || !string.IsNullOrWhiteSpace(CoolingOutputHeaderName))
                && (!removingSafetyProtection || RemoveCoolingSafetyProtectionAcknowledged);
        }
    }

    public string CoolingOutputRoleStatus
    {
        get
        {
            if (SelectedCalibrationTarget is null)
            {
                return "Select a cooling output to store its physical safety role.";
            }

            CoolingOutputAssignmentV1? existing = GetSelectedCoolingOutputAssignment();
            if (existing is not null)
            {
                string protection = existing.Role == CoolingOutputRole.Pump
                    ? "Pump policy is active: this output stays read-only until an exact pump-specific qualification path exists."
                    : existing.IsSafetyCritical
                    ? "Service protection is active: no identification pulse, zero-RPM calibration, cooling-graph zero floor, or automatic tuning."
                    : "This output remains a case-fan candidate; physical header identity is still required before a pulse.";
                if (existing.Role != SelectedCoolingOutputRole || !string.Equals(existing.HeaderName, CoolingOutputHeaderName.Trim(), StringComparison.Ordinal))
                {
                    return $"Draft change: {existing.HeaderName} is stored as {SplitWords(existing.Role.ToString())}; save to update the service policy. {protection}";
                }
                return $"Stored: {existing.HeaderName} is {SplitWords(existing.Role.ToString())}. {protection}";
            }

            return SelectedCoolingOutputRole == CoolingOutputRole.Unknown
                ? "No physical role is stored. Generic Super I/O labels never imply a chassis header."
                : $"Draft: mark this output as {SplitWords(SelectedCoolingOutputRole.ToString())} and save. No fan command is sent by this action.";
        }
    }

    public bool CommissioningHeaderConfirmed
    {
        get => _commissioningHeaderConfirmed;
        set
        {
            if (Set(ref _commissioningHeaderConfirmed, value))
            {
                OnPropertyChanged(nameof(CanConfirmFanCommissioning));
                _confirmFanCommissioningCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CommissioningObservation
    {
        get => _commissioningObservation;
        private set => Set(ref _commissioningObservation, value);
    }

    public bool CommissioningObserverReady
    {
        get => _commissioningObserverReady;
        set
        {
            if (Set(ref _commissioningObserverReady, value))
            {
                NotifyCommissioningProperties();
            }
        }
    }

    public string CommissioningStateLabel => SelectedFanCommissioningSession is null
        ? "No commissioning session"
        : SelectedFanCommissioningSession is { State: FanCommissioningState.ReadyForCalibration, PhysicalHeaderObserved: false }
            ? "Declared header; observation pending"
            : SplitWords(SelectedFanCommissioningSession.State.ToString());

    public bool CanBeginFanCommissioning => CanUseServiceWrites
        && SelectedCalibrationTarget?.RpmSensorId is not null
        && SelectedCalibrationTarget.IsAvailable
        && !string.IsNullOrWhiteSpace(CommissioningHeaderName)
        && (SelectedFanCommissioningSession?.State is null
            or FanCommissioningState.Completed
            or FanCommissioningState.Cancelled
            or FanCommissioningState.Failed
            or FanCommissioningState.RecoveryRequired);

    public bool CanConfirmFanCommissioning => CanUseServiceWrites
        && SelectedFanCommissioningSession?.State == FanCommissioningState.AwaitingIdentification
        && CommissioningHeaderConfirmed
        && !string.IsNullOrWhiteSpace(CommissioningHeaderName);

    public bool CanPulseFanCommissioning => CanUseServiceWrites
        && SelectedFanCommissioningSession?.State == FanCommissioningState.AwaitingIdentification
        && !IsSelectedCoolingOutputProtected
        && !HasActiveOperation
        && AdvancedWritesAcknowledged
        && CalibrationDeviceAcknowledged
        && CommissioningObserverReady;

    public bool CanCompleteFanCommissioning => CanUseServiceWrites
        && SelectedFanCommissioningSession is { State: FanCommissioningState.ReadyForCalibration, PhysicalHeaderObserved: true }
        && _operation?.CapabilityId == SelectedFanCommissioningSession.CapabilityId
        && _operation.State == HardwareOperationState.Completed
        && _operation.CalibrationResult is { } calibration
        && FanCalibrationPolicy.SupportsNonZeroCurve(calibration);

    public bool CanCreateAdaptiveCoolingProfile => CanUseServiceWrites
        && SelectedFanCommissioningSession is { State: FanCommissioningState.Completed, PhysicalHeaderObserved: true } session
        && HasAdaptiveCoolingCalibration(session)
        && _snapshot?.Capabilities.Any(capability => capability.Id == session.CapabilityId) == true;

    public string AdaptiveCoolingProfileEligibilityReason
    {
        get
        {
            if (!CanUseServiceWrites)
            {
                return ServiceCompatibilityMessage;
            }
            if (SelectedFanCommissioningSession is not FanCommissioningSessionV1 session)
            {
                return "Save a physically observed commissioning report before creating an adaptive curve.";
            }
            if (session.State != FanCommissioningState.Completed || !session.PhysicalHeaderObserved)
            {
                return "A completed commissioning report with a visually observed physical header is required.";
            }
            if (_snapshot is null || !_snapshot.Capabilities.Any(capability => capability.Id == session.CapabilityId))
            {
                return "The commissioned output is not present in the current hardware inventory.";
            }
            if (!HasAdaptiveCoolingCalibration(session))
            {
                return "No stable calibration linked to this commissioning report is available. Repeat calibration before creating a curve.";
            }

            return "Creates an inactive conservative CPU/GPU mixed profile for this exact output. It does not apply a fan command.";
        }
    }

    public string CustomCoolingCurveName
    {
        get => _customCoolingCurveName;
        set
        {
            if (Set(ref _customCoolingCurveName, value))
            {
                NotifyCustomCoolingCurveProperties();
            }
        }
    }

    public string CustomCoolingCurvePoints
    {
        get => _customCoolingCurvePoints;
        set
        {
            if (Set(ref _customCoolingCurvePoints, value))
            {
                NotifyCustomCoolingCurveProperties();
            }
        }
    }

    public string CustomCoolingCurveHysteresisUpText
    {
        get => _customCoolingCurveHysteresisUpText;
        set
        {
            if (Set(ref _customCoolingCurveHysteresisUpText, value))
            {
                NotifyCustomCoolingCurveProperties();
            }
        }
    }

    public string CustomCoolingCurveHysteresisDownText
    {
        get => _customCoolingCurveHysteresisDownText;
        set
        {
            if (Set(ref _customCoolingCurveHysteresisDownText, value))
            {
                NotifyCustomCoolingCurveProperties();
            }
        }
    }

    public string CustomCoolingCurveResponseUpSecondsText
    {
        get => _customCoolingCurveResponseUpSecondsText;
        set
        {
            if (Set(ref _customCoolingCurveResponseUpSecondsText, value))
            {
                NotifyCustomCoolingCurveProperties();
            }
        }
    }

    public string CustomCoolingCurveResponseDownSecondsText
    {
        get => _customCoolingCurveResponseDownSecondsText;
        set
        {
            if (Set(ref _customCoolingCurveResponseDownSecondsText, value))
            {
                NotifyCustomCoolingCurveProperties();
            }
        }
    }

    public bool CanSaveCustomCoolingCurve => CanCreateAdaptiveCoolingProfile
        && TryReadCustomCoolingCurve(out CustomCoolingCurveDefinition? definition, out _)
        && GetCustomCoolingCurveDraftError(definition!) is null;

    public string CustomCoolingCurveEligibilityReason
    {
        get
        {
            if (!CanCreateAdaptiveCoolingProfile)
            {
                return AdaptiveCoolingProfileEligibilityReason;
            }
            if (!TryReadCustomCoolingCurve(out CustomCoolingCurveDefinition? definition, out string error))
            {
                return error;
            }

            return GetCustomCoolingCurveDraftError(definition!)
                ?? "Saves an inactive calibrated nonzero CPU/GPU mixed curve. The controller floor and thermal ceiling stay fixed.";
        }
    }

    public string CustomCoolingCurvePreview => TryReadCustomCoolingCurve(out CustomCoolingCurveDefinition? definition, out string error)
        ? $"CPU/GPU maximum input · {string.Join("  ·  ", definition!.Points.Select(point => $"{point.Input:0.#} °C → {point.Output:0.#}%"))}"
        : error;

    public WpfPointCollection CustomCoolingCurvePreviewGeometry
    {
        get
        {
            if (!TryReadCustomCoolingCurve(out CustomCoolingCurveDefinition? definition, out _))
            {
                return [];
            }

            (double minimumDuty, double maximumDuty) = GetCustomCoolingCurveDutyRange();
            const double width = 320;
            const double top = 12;
            const double height = 96;
            return new WpfPointCollection(definition!.Points.Select(point =>
            {
                double x = Math.Clamp(point.Input / 110d, 0, 1) * width;
                double ratio = maximumDuty > minimumDuty
                    ? Math.Clamp((point.Output - minimumDuty) / (maximumDuty - minimumDuty), 0, 1)
                    : 0.5;
                return new WpfPoint(x, top + (1 - ratio) * height);
            }));
        }
    }

    public string CustomCoolingCurveAxisLabel
    {
        get
        {
            (double minimumDuty, double maximumDuty) = GetCustomCoolingCurveDutyRange();
            return $"0–110 °C  ·  {minimumDuty:0.#}–{maximumDuty:0.#}% controller range";
        }
    }

    public bool CanCancelFanCommissioning => SelectedFanCommissioningSession is not null
        && SelectedFanCommissioningSession.State is not (FanCommissioningState.Completed or FanCommissioningState.Cancelled)
        && !(_operation?.CapabilityId == SelectedFanCommissioningSession.CapabilityId && HasActiveOperation);

    public bool CanRecoverFanCommissioning => SelectedFanCommissioningSession?.State == FanCommissioningState.RecoveryRequired
        && !HasActiveOperation;

    public bool CanRunInteractiveFanPreflight => IsUserAgentOnline
        && _interactiveFanPreflightSupported
        && !IsSelectedCoolingOutputProtected
        && SelectedFanCommissioningSession is
        {
            State: FanCommissioningState.Failed,
            IsCpuOrPump: false
        } session
        && FanCommissioningWorkflow.IsDeclaredChassisHeader(session.HeaderName)
        && ElevatedInteractiveFanPreflightLauncher.IsSupportedCapabilityId(session.CapabilityId);

    public string CommissioningTargetSummary
    {
        get
        {
            if (SelectedCalibrationTarget is not OperationTargetDisplay target)
            {
                return "Select a cooling control with an exact-device RPM sensor pairing.";
            }

            string rpm = target.RpmSensorId is null ? "No same-device RPM pairing." : "Same-device RPM pairing found.";
            string protectedOutput = GetSelectedCoolingOutputAssignment()?.Role == CoolingOutputRole.Pump
                ? "Pump policy applies; all direct control remains blocked pending exact qualification."
                : IsSelectedCoolingOutputProtected
                || target.Descriptor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || target.Descriptor.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)
                ? "CPU/pump protection applies; fan stop is forbidden."
                : "Header role is not yet physically confirmed; do not assume a generic label is a chassis fan.";
            return $"{target.DisplayName}. {rpm} {protectedOutput}";
        }
    }

    public string CommissioningPreflight
    {
        get
        {
            if (!CanUseServiceWrites)
            {
                return ServiceCompatibilityMessage;
            }
            if (SelectedCalibrationTarget is null)
            {
                return "Step 1 of 4: select one candidate fan control and its paired RPM sensor.";
            }
            if (IsSelectedCoolingOutputProtected)
            {
                return GetSelectedCoolingOutputAssignment()?.Role == CoolingOutputRole.Pump
                    ? "This output is persistently classified as a pump. Commissioning, calibration, graphs, profiles, tuning, and the elevated diagnostic remain blocked pending exact pump qualification."
                    : "This output is persistently classified as a CPU fan. Identification pulses, zero-RPM testing, and the elevated diagnostic are blocked.";
            }
            if (SelectedFanCommissioningSession is null)
            {
                return FanCommissioningWorkflow.IsDeclaredChassisHeader(CommissioningHeaderName)
                    ? "Step 1 of 4: start setup. This does not change fan speed."
                    : "Step 1 of 4: check the motherboard wiring and enter a physical chassis label such as CHA_FAN1. Generic labels such as Fan #1 cannot be pulsed.";
            }
            return SelectedFanCommissioningSession.State switch
            {
                FanCommissioningState.AwaitingIdentification when !CommissioningObserverReady =>
                    "Step 2 of 4: confirm that a person can see the intended physical fan before enabling the two-second identification pulse.",
                FanCommissioningState.AwaitingIdentification =>
                    "Step 2 of 4: the pulse uses 60% duty for two seconds, verifies software read-back, then restores firmware/default control. Watch the fan now.",
                FanCommissioningState.ReadyForCalibration when !SelectedFanCommissioningSession.PhysicalHeaderObserved =>
                    "Step 3 of 4: the header label is declared, but the physical fan has not been observed. Do not start calibration until the pulse is witnessed and the header is explicitly confirmed.",
                FanCommissioningState.ReadyForCalibration =>
                    "Step 3 of 4: the physical header is confirmed. Review calibration and restart-test limits before any longer operation.",
                FanCommissioningState.Completed =>
                    "Step 4 of 4: commissioning evidence is saved. The control remains Experimental until wider qualification passes.",
                FanCommissioningState.Failed when CanRunInteractiveFanPreflight =>
                    "The LocalSystem no-write preflight failed before a fan command. You may run the explicit UAC diagnostic below; it performs Prepare only and does not unblock the service.",
                FanCommissioningState.Failed =>
                    "The no-write controller preflight failed before a fan command. This session is closed until the adapter execution-context fault is corrected.",
                FanCommissioningState.RecoveryRequired =>
                    "Recovery is required. Restore firmware/default control before starting any new operation.",
                _ => "Review the saved session state before continuing."
            };
        }
    }

    public OperationTargetDisplay? SelectedTuneTarget
    {
        get => _selectedTuneTarget;
        set
        {
            if (!Set(ref _selectedTuneTarget, value))
            {
                return;
            }

            TuneDeviceAcknowledged = false;
            NotifyOperationEligibility();
        }
    }

    public bool AdvancedWritesAcknowledged
    {
        get => _advancedWritesAcknowledged;
        set
        {
            if (Set(ref _advancedWritesAcknowledged, value))
            {
                RebuildExperimentalControlCenter();
                UpdateSafetySummary();
                NotifyOperationEligibility();
                _applyProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CalibrationDeviceAcknowledged
    {
        get => _calibrationDeviceAcknowledged;
        set
        {
            if (Set(ref _calibrationDeviceAcknowledged, value))
            {
                NotifyOperationEligibility();
            }
        }
    }

    public bool TuneDeviceAcknowledged
    {
        get => _tuneDeviceAcknowledged;
        set
        {
            if (Set(ref _tuneDeviceAcknowledged, value))
            {
                NotifyOperationEligibility();
            }
        }
    }

    public bool ProfileDeviceAcknowledged
    {
        get => _profileDeviceAcknowledged;
        set
        {
            if (Set(ref _profileDeviceAcknowledged, value))
            {
                _applyProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// A volatile acknowledgement for a user-created Manual Only profile. It
    /// is intentionally not persisted and is cleared after use so it cannot
    /// silently authorise voltage actions at startup or through automation.
    /// </summary>
    public bool ManualVoltageAcknowledged
    {
        get => _manualVoltageAcknowledged;
        set
        {
            if (Set(ref _manualVoltageAcknowledged, value))
            {
                _applyProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool AllowCaseFanStop
    {
        get => _allowCaseFanStop;
        set
        {
            if (value && IsSelectedCoolingOutputProtected)
            {
                value = false;
            }
            if (Set(ref _allowCaseFanStop, value))
            {
                NotifyOperationEligibility();
            }
        }
    }

    public int CalibrationSettlingSeconds
    {
        get => _calibrationSettlingSeconds;
        set => Set(ref _calibrationSettlingSeconds, value);
    }

    public int CalibrationRestartCycleCount
    {
        get => _calibrationRestartCycleCount;
        set => Set(ref _calibrationRestartCycleCount, value);
    }

    public TuningObjective SelectedTuneObjective
    {
        get => _selectedTuneObjective;
        set => Set(ref _selectedTuneObjective, value);
    }

    public string TuneTemperatureCeilingText
    {
        get => _tuneTemperatureCeilingText;
        set
        {
            if (Set(ref _tuneTemperatureCeilingText, value))
            {
                NotifyOperationEligibility();
            }
        }
    }

    public string TunePowerCeilingText
    {
        get => _tunePowerCeilingText;
        set
        {
            if (Set(ref _tunePowerCeilingText, value))
            {
                NotifyOperationEligibility();
            }
        }
    }

    public bool OpenRgbEnabled
    {
        get => _openRgbEnabled;
        set
        {
            if (!Set(ref _openRgbEnabled, value))
            {
                return;
            }

            if (!value)
            {
                OpenRgbConnected = false;
                OpenRgbStatus = "Bridge disabled. No SDK connection will be attempted.";
                SetOpenRgbControllers([]);
            }
            else
            {
                OpenRgbStatus = HasBroadLightingConflict
                    ? LightingConflictReason
                    : "Bridge enabled. Test the local SDK server; endpoint-specific owners will be skipped instead of blocking unrelated devices.";
                RebuildRgbRouteAssessments();
            }

            NotifyOpenRgbProperties();
        }
    }

    public bool OpenRgbConnected
    {
        get => _openRgbConnected;
        private set
        {
            if (Set(ref _openRgbConnected, value))
            {
                NotifyOpenRgbProperties();
                RebuildRgbRouteAssessments();
            }
        }
    }

    public string OpenRgbStatus
    {
        get => _openRgbStatus;
        private set => Set(ref _openRgbStatus, value);
    }

    public string OpenRgbColour
    {
        get => _openRgbColour;
        set
        {
            if (Set(ref _openRgbColour, value))
            {
                NotifyOpenRgbProperties();
            }
        }
    }

    public ICommand ApplyKrakenLightingCommand => _applyKrakenLightingCommand ??= new AsyncCommand(
        parameter => ApplyKrakenLightingAsync(turnOff: string.Equals(parameter as string, "off", StringComparison.Ordinal)),
        _ => CanRunHardwareAction(),
        ReportError,
        _ => ShowHardwareActionBlocked());

    private AsyncCommand? _applyKrakenLightingCommand;

    public string KrakenLightingStatus
    {
        get => _krakenLightingStatus;
        private set => Set(ref _krakenLightingStatus, value);
    }

    private string _krakenLightingStatus = "RigPilot's own Kraken X3 adapter writes a fixed ring+logo colour directly over HID — no OpenRGB needed. Lighting only; the pump is untouched. Requires Hardware control.";

    /// <summary>
    /// Native (non-OpenRGB) Kraken X3 lighting write through the service and
    /// the crash-contained Adapter Host child. Uses the static colour field;
    /// lighting has no firmware read-back, so success reports the write as
    /// issued and asks for visual confirmation.
    /// </summary>
    public async Task ApplyKrakenLightingAsync(bool turnOff)
    {
        if (!CanRunHardwareAction())
        {
            ShowHardwareActionBlocked();
            return;
        }

        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetKrakenLighting,
                new KrakenLightingRequestV1(
                    KrakenLightingRequestV1.CurrentSchemaVersion,
                    OpenRgbColour,
                    turnOff,
                    ConfirmExperimental: true,
                    KrakenLightingRequestV1.ExactDeviceId)),
            _lifetime.Token);
        EnsureSuccess(response);
        KrakenLightingResultV1 result = IpcJson.FromElement<KrakenLightingResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty Kraken lighting result.");
        KrakenLightingStatus = result.Message;
        ShowNotice(result.Message, result.Outcome == KrakenLightingOutcome.WriteIssued ? "Success" : "Warning");
    }

    public static IReadOnlyList<LightingColourways.Colourway> Colourways => LightingColourways.All;

    public LightingColourways.Colourway? SelectedColourway
    {
        get => _selectedColourway;
        set => Set(ref _selectedColourway, value);
    }

    private LightingColourways.Colourway? _selectedColourway = LightingColourways.All[0];

    public string OpenRgbBrightnessText
    {
        get => _openRgbBrightnessText;
        set
        {
            if (Set(ref _openRgbBrightnessText, value))
            {
                NotifyOpenRgbProperties();
            }
        }
    }

    public int OpenRgbControllerCount
    {
        get => _openRgbControllerCount;
        private set => Set(ref _openRgbControllerCount, value);
    }

    public bool HasLightingConflict => _snapshot?.Conflicts.Any(conflict =>
        conflict.IsRunning
        && !string.Equals(conflict.Id, "openrgb", StringComparison.OrdinalIgnoreCase)
        && conflict.ResourceFamilies.Contains("Lighting", StringComparer.OrdinalIgnoreCase)) == true;

    public bool HasBroadLightingConflict => RgbConflictPolicy.FindBroadBlockingOwners(
        _snapshot?.Conflicts).Count > 0;

    public bool HasScopedLightingConflict => HasLightingConflict && !HasBroadLightingConflict;

    public bool HasDynamicLightingConflict => HasLightingConflict
        || (OpenRgbEnabled && OpenRgbConnected && _openRgbControllers.Count > 0);

    public string LightingConflictReason
    {
        get
        {
            string[] owners = _snapshot?.Conflicts.Where(conflict =>
                    conflict.IsRunning
                    && !string.Equals(conflict.Id, "openrgb", StringComparison.OrdinalIgnoreCase)
                    && conflict.ResourceFamilies.Contains("Lighting", StringComparer.OrdinalIgnoreCase))
                .Select(conflict => conflict.DisplayName)
                .ToArray() ?? [];
            return owners.Length == 0
                ? "No competing lighting writer detected."
                : HasBroadLightingConflict
                    ? $"Lighting output is broadly blocked by {string.Join(", ", owners)}. RigPilot will not terminate another writer."
                    : $"Endpoint-specific owner detected: {string.Join(", ", owners)}. RigPilot skips only matching device families and can still apply unrelated ready routes.";
        }
    }

    public string DynamicLightingConflictReason => HasLightingConflict
        ? LightingConflictReason
        : OpenRgbEnabled && OpenRgbConnected && _openRgbControllers.Count > 0
            ? "Windows Dynamic Lighting is paused while the connected local OpenRGB bridge owns one or more controller routes."
            : "No competing lighting writer or local OpenRGB bridge is active.";

    public string OpenRgbConnectionLabel => OpenRgbConnected
        ? $"Connected · {OpenRgbControllerCount} controller{(OpenRgbControllerCount == 1 ? string.Empty : "s")}"
        : OpenRgbEnabled ? "Enabled · not connected" : "Disabled";

    public bool AreOpenRgbInputsValid => TryParseOpenRgbInputs(out _, out _);

    public AutomationTriggerKind NewRuleTriggerKind
    {
        get => _newRuleTriggerKind;
        set
        {
            if (Set(ref _newRuleTriggerKind, value))
            {
                OnPropertyChanged(nameof(NewRuleTriggerHint));
                NotifyAutomationEditor();
            }
        }
    }

    public ProfileV1? NewRuleProfile
    {
        get => _newRuleProfile;
        set
        {
            if (Set(ref _newRuleProfile, value))
            {
                NotifyAutomationEditor();
            }
        }
    }

    public string NewRuleName
    {
        get => _newRuleName;
        set
        {
            if (Set(ref _newRuleName, value))
            {
                NotifyAutomationEditor();
            }
        }
    }

    public string NewRuleTriggerValue
    {
        get => _newRuleTriggerValue;
        set
        {
            if (Set(ref _newRuleTriggerValue, value))
            {
                NotifyAutomationEditor();
            }
        }
    }

    public string NewRulePriorityText
    {
        get => _newRulePriorityText;
        set
        {
            if (Set(ref _newRulePriorityText, value))
            {
                NotifyAutomationEditor();
            }
        }
    }

    public string NewRuleTriggerHint => NewRuleTriggerKind switch
    {
        AutomationTriggerKind.Process => "Process name, for example game.exe",
        AutomationTriggerKind.ForegroundApplication => "Foreground process name, for example blender.exe",
        AutomationTriggerKind.Schedule => "24-hour range, for example 22:00-06:00",
        AutomationTriggerKind.SessionLock => "locked or unlocked",
        AutomationTriggerKind.Idle => "Idle threshold in minutes",
        AutomationTriggerKind.Hotkey => "Ctrl+Alt+1, Ctrl+Alt+2, or Ctrl+Alt+3",
        _ => "Trigger value"
    };

    public string AutomationStatus
    {
        get => _automationStatus;
        private set => Set(ref _automationStatus, value);
    }

    public bool HasAutomationRules => AutomationRules.Count > 0;

    public bool AutomationServiceSupported => _automationServiceSupported;

    public bool HasManualOverride => !string.IsNullOrWhiteSpace(_manualProfileId);

    public string ManualOverrideLabel => HasManualOverride
        ? $"Manual override · {Profiles.FirstOrDefault(profile => profile.Id == _manualProfileId)?.Name ?? _manualProfileId}"
        : "Automation owns profile selection";

    public bool CanAddAutomationRule
    {
        get
        {
            if (NewRuleProfile is null
                || string.IsNullOrWhiteSpace(NewRuleName)
                || string.IsNullOrWhiteSpace(NewRuleTriggerValue)
                || !int.TryParse(NewRulePriorityText, out int priority))
            {
                return false;
            }

            AutomationRuleV1 candidate = CreateAutomationRule("preview", priority);
            return AutomationRuleMatcher.Validate(
                candidate,
                Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal)) is null;
        }
    }

    public int DeviceCount => _snapshot?.Devices.Count ?? 0;

    public int SensorCount => _snapshot?.Sensors.Count ?? 0;

    public int VerifiedControlCount => CountCapabilities(CapabilityAccessState.Verified);

    public int ReadOnlyControlCount => CountCapabilities(CapabilityAccessState.ReadOnly);

    public int ExperimentalControlCount => ExperimentalControls.Count;

    public int ExperimentalCoolingControlCount => ExperimentalControls.Count(item => item.IsCoolingControl);

    public int CommissioningEligibleExperimentalControlCount => ExperimentalControls.Count(item => item.CanOpenCoolingCommissioning);

    public int ProtectedExperimentalControlCount => ExperimentalControls.Count(item => item.IsProtected);

    public bool HasExperimentalControls => ExperimentalControlCount > 0;

    public int RestrictedControlCount => _snapshot?.Capabilities.Count(capability => capability.State is
        CapabilityAccessState.Experimental or CapabilityAccessState.Blocked or CapabilityAccessState.Unsupported or CapabilityAccessState.Faulted) ?? 0;

    public int BlockedOrUnsupportedControlCount => _snapshot?.Capabilities.Count(capability => capability.State is
        CapabilityAccessState.Blocked or CapabilityAccessState.Unsupported or CapabilityAccessState.Faulted) ?? 0;

    public int ResettableVerifiedControlCount => _snapshot?.Capabilities.Count(capability =>
        capability.State == CapabilityAccessState.Verified && capability.CanResetToDefault) ?? 0;

    public int WarningCount => Diagnostics.Count(item => item.Severity is "Warning" or "Critical");

    public int RunningConflictCount => _snapshot?.Conflicts.Count(conflict => conflict.IsRunning) ?? 0;

    public bool HasRunningConflicts => RunningConflictCount > 0;

    /// <summary>
    /// True when a running conflict competes for at least one of the given control families.
    /// Used so a per-card "Blocked" indicator reflects only conflicts that affect that card's
    /// controls — an AIO/lighting-only app (e.g. NZXT CAM) must not mark the GPU tuning card
    /// blocked. Family strings match <see cref="ConflictDetector"/> (GpuTuning, GpuFan,
    /// MotherboardFan, Aio, Lighting, UsbFan, ...).
    /// </summary>
    private bool HasRunningConflictAffecting(params string[] families) =>
        _snapshot?.Conflicts.Any(conflict => conflict.IsRunning
            && conflict.ResourceFamilies.Any(family => families.Contains(family, StringComparer.OrdinalIgnoreCase))) ?? false;

    /// <summary>Running conflicts competing for GPU tuning or the GPU fan (not AIO/lighting-only apps).</summary>
    public bool HasRunningGpuConflicts => HasRunningConflictAffecting("GpuTuning", "GpuFan");

    /// <summary>Running conflicts competing for motherboard, GPU, or AIO fan control.</summary>
    public bool HasRunningFanConflicts => HasRunningConflictAffecting("MotherboardFan", "GpuFan", "Aio");

    public string CloseBlockersLabel => RunningConflictCount switch
    {
        0 => "No blocking apps running",
        1 => "Close 1 blocking app",
        int count => $"Close {count} blocking apps"
    };

    public ICommand CloseBlockersCommand => _closeBlockersCommand;

    /// <summary>
    /// Quick, one-click "close conflicting apps" for the Performance and Cooling
    /// pages (G-Helper style): available whenever a detected controller is
    /// running, without the separate Diagnostics acknowledgement checkbox — the
    /// click is the explicit action, and it is exactly equivalent to closing the
    /// app in Task Manager (relaunching hands control back).
    /// </summary>
    public ICommand CloseConflictingAppsCommand => _closeConflictingAppsCommand ??= new AsyncCommand(
        _ => TerminateConflictingProcessesAsync(),
        _ => IsServiceOnline && RunningConflictCount > 0,
        ReportError);

    private AsyncCommand? _closeConflictingAppsCommand;

    /// <summary>The distinct running conflicting-controller names, for button labels and tooltips.</summary>
    public string RunningConflictSummary => string.Join(", ", _snapshot?.Conflicts
        .Where(conflict => conflict.IsRunning)
        .Select(conflict => conflict.DisplayName)
        .Distinct(StringComparer.OrdinalIgnoreCase) ?? []);

    /// <summary>Distinct running conflicts that compete for a given control family set — for the
    /// per-page conflict banners, so the GPU banner never names an AIO/lighting-only app.</summary>
    private string RunningConflictSummaryFor(params string[] families) => string.Join(", ", _snapshot?.Conflicts
        .Where(conflict => conflict.IsRunning
            && conflict.ResourceFamilies.Any(family => families.Contains(family, StringComparer.OrdinalIgnoreCase)))
        .Select(conflict => conflict.DisplayName)
        .Distinct(StringComparer.OrdinalIgnoreCase) ?? []);

    /// <summary>Running GPU-tuning/GPU-fan conflicts, for the GPU-page conflict banner.</summary>
    public string RunningGpuConflictSummary => RunningConflictSummaryFor("GpuTuning", "GpuFan");

    /// <summary>Running fan conflicts (motherboard/GPU/AIO), for the fan-page conflict banner.</summary>
    public string RunningFanConflictSummary => RunningConflictSummaryFor("MotherboardFan", "GpuFan", "Aio");

    public string CloseConflictingAppsLabel => RunningConflictCount switch
    {
        0 => "No conflicting apps",
        1 => "Close conflicting app",
        int count => $"Close {count} conflicting apps"
    };

    public bool CloseBlockersAcknowledged
    {
        get => _closeBlockersAcknowledged;
        set
        {
            if (Set(ref _closeBlockersAcknowledged, value))
            {
                _closeBlockersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task CloseBlockersCoreAsync()
    {
        if (!CloseBlockersAcknowledged)
        {
            return;
        }

        await TerminateConflictingProcessesAsync();
        CloseBlockersAcknowledged = false;
    }

    /// <summary>
    /// Terminates the running processes of detected conflicting controllers so
    /// they release the device handles that block RigPilot's gated writes. This
    /// takes over no hardware control (distinct from the takeover executor) and
    /// runs through the LocalSystem service, which can close the elevated apps.
    /// </summary>
    private async Task TerminateConflictingProcessesAsync()
    {
        if (RunningConflictCount == 0)
        {
            return;
        }

        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.StopConflictingProcesses,
                new StopConflictingProcessesRequestV1(
                    StopConflictingProcessesRequestV1.CurrentSchemaVersion, [], Confirm: true)),
            _lifetime.Token);
        EnsureSuccess(response);
        StopConflictingProcessesResultV1 result = IpcJson.FromElement<StopConflictingProcessesResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty close-blockers result.");
        ShowNotice(result.Message, result.TerminatedCount > 0 ? "Success" : "Warning");
        await RefreshAsync(full: true, userInitiated: false);
    }

    public bool HasImportantSensors => ImportantSensors.Count > 0;

    public bool HasCoolingSensors => CoolingSensors.Count > 0;

    public bool HasPerformanceSensors => PerformanceSensors.Count > 0;

    public bool HasCoolingCapabilities => CoolingCapabilities.Count > 0;

    public bool HasPerformanceCapabilities => PerformanceCapabilities.Count > 0;

    public bool HasCapabilityDecisions => CapabilityDecisions.Count > 0;

    public string CapabilityDecisionSummary
    {
        get
        {
            int verified = CapabilityDecisions.Count(item => item.AccessState == CapabilityAccessState.Verified);
            int experimental = CapabilityDecisions.Count(item => item.AccessState == CapabilityAccessState.Experimental);
            int blocked = CapabilityDecisions.Count(item => item.AccessState is CapabilityAccessState.Blocked or CapabilityAccessState.Faulted);
            int readOnly = CapabilityDecisions.Count(item => item.AccessState is CapabilityAccessState.ReadOnly or CapabilityAccessState.Unsupported);
            return $"{verified} verified · {experimental} experimental · {blocked} blocked/faulted · {readOnly} read-only/unsupported";
        }
    }

    public string ExperimentalControlSummary => ExperimentalControlCount == 0
        ? "No Experimental hardware controls were detected."
        : $"{ExperimentalControlCount} detected · {CommissioningEligibleExperimentalControlCount} can enter the cooling commissioning workflow · {ProtectedExperimentalControlCount} protected";

    public string ExperimentalGateBadge => ExperimentalControlCount == 0
        ? "NO EXPERIMENTAL CONTROLS"
        : !CanUseServiceWrites
            ? "SERVICE PATH LOCKED"
            : !AdvancedWritesAcknowledged
                ? "SESSION ACKNOWLEDGEMENT REQUIRED"
                : "SELECT AN EXACT DEVICE";

    public string ExperimentalGateSummary
    {
        get
        {
            if (ExperimentalControlCount == 0)
            {
                return "No Experimental controls are present in the current snapshot.";
            }

            if (!CanUseServiceWrites)
            {
                return "The service write path is not ready. You can inspect the evidence, but commissioning remains unavailable.";
            }

            if (!AdvancedWritesAcknowledged)
            {
                return "No hardware action is authorised. Record a session-only acknowledgement, then select one exact non-protected control in Cooling.";
            }

            return CommissioningEligibleExperimentalControlCount > 0
                ? "The session acknowledgement is recorded. Select one exact control; its RPM pairing, physical header, witnessed pulse, and per-device confirmation are still required."
                : "Every detected Experimental control is protected or missing the bounded reset path required for commissioning.";
        }
    }

    public string ExperimentalAcknowledgementDetail => AdvancedWritesAcknowledged
        ? "Session acknowledgement recorded. It expires when the dashboard closes and does not bypass per-device confirmation or safety roles."
        : "This acknowledgement is session-only. It does not select a device, change hardware, or bypass pump/CPU protections.";

    public bool HasDevices => Devices.Count > 0;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public bool HasAdapterHealth => AdapterHealth.Count > 0;

    public bool HasCalibrationTargets => CalibrationTargets.Count > 0;

    public bool HasTuneTargets => TuneTargets.Count > 0;

    public bool HasOperation => _operation is not null;

    public bool HasActiveOperation => _operation?.State is
        HardwareOperationState.Pending or
        HardwareOperationState.Running or
        HardwareOperationState.Screening;

    public string OperationTitle => _operation is null
        ? "No operation has run"
        : $"{SplitWords(_operation.Kind.ToString())} · {SplitWords(_operation.State.ToString())}";

    public string OperationMessage => _operation?.Message ?? "Select an eligible control to begin.";

    public string OperationError => _operation?.Error ?? string.Empty;

    public bool HasOperationError => !string.IsNullOrWhiteSpace(_operation?.Error);

    public double OperationProgress => _operation?.ProgressPercent ?? 0;

    public string OperationProgressText => _operation is null ? "—" : $"{_operation.ProgressPercent:0}%";

    public bool HasCalibrationResult => _operation?.CalibrationResult is not null;

    public string CalibrationResultSummary
    {
        get
        {
            FanCalibrationResult? result = _operation?.CalibrationResult;
            if (result is null)
            {
                return "No completed calibration result.";
            }

            string restart = result.RestartVerified
                ? $"Restart verified {result.RestartVerificationCyclesCompleted} times from {result.VerifiedStopDutyPercent:0}% at {result.RestartDutyPercent:0}% duty."
                : result.NonStopFloorObserved
                    ? $"The fan stayed running at the controller minimum. A {result.MinimumDutyPercent:0}% nonzero floor is eligible after physical-header confirmation; zero-RPM remains disabled."
                    : result.StallDutyPercent is null
                        ? "The fan did not reach the controller minimum, so its nonzero operating floor is not yet verified."
                        : "Restart was not verified; zero-RPM use and curve activation remain disabled.";
            if (!FanCalibrationPolicy.SupportsNonZeroCurve(result))
            {
                return $"Calibration measurements are recorded only; maximum observed {result.MaximumRpm:0} RPM. {restart}";
            }
            return $"Recommended floor {result.MinimumDutyPercent:0}% · maximum observed {result.MaximumRpm:0} RPM · {restart}";
        }
    }

    public string CalibrationOperatingEnvelopeSummary
    {
        get
        {
            FanCalibrationResult? result = _operation?.CalibrationResult;
            if (result is null)
            {
                return "Each output learns its own duty-to-RPM response, low-speed plateau, and whether a stop/restart path was proven.";
            }

            if (result.RestartVerified)
            {
                return $"0% is permitted only at an explicit graph stop point. Any positive command is raised to at least {result.MinimumDutyPercent:0}% for reliable restart.";
            }

            if (result.NonStopFloorObserved)
            {
                string responsive = result.FirstResponsiveDutyPercent is double duty
                    ? $" The first measured rise above the low-speed plateau was {duty:0}% duty."
                    : string.Empty;
                return $"This controller maintained RPM at its minimum command. Use a nonzero graph floor of at least {result.MinimumDutyPercent:0}%; zero-RPM remains prohibited.{responsive}";
            }

            return "The calibration did not produce a safe operating envelope. Keep firmware/default control and repeat the full-range scan after checking the paired tachometer.";
        }
    }

    public string CalibrationStabilitySummary
    {
        get
        {
            FanCalibrationResult? result = _operation?.CalibrationResult;
            if (result is null)
            {
                return "Each curve point seeks a stable median RPM window; restart uses consecutive running/stopped samples.";
            }

            int variablePoints = result.Measurements.Count(point => !point.Stable);
            string confidence = result.AllMeasurementsStable
                ? "All curve points settled."
                : $"{variablePoints} low-speed point{(variablePoints == 1 ? string.Empty : "s")} remained variable and should not be used for a precision RPM curve.";
            return $"{result.StableSampleCount} samples per window, {result.SampleInterval?.TotalMilliseconds:0} ms interval, {result.StabilityTolerancePercent:0.#}% tolerance. {confidence}";
        }
    }

    public string CalibrationAvailabilityLabel => CalibrationTargets.Count == 0
        ? "No detected calibration targets"
        : CalibrationTargets.Any(target => target.IsAvailable)
            ? "Calibration engine ready"
            : "No write-eligible controls";

    public string TuneAvailabilityLabel => TuneTargets.Count == 0
        ? "No bounded tuning targets"
        : TuneTargets.Any(target => target.IsAvailable)
            ? "Auto-tuning engine ready"
            : "No write-eligible controls";

    public string CalibrationAvailabilityTone => CalibrationTargets.Any(target => target.IsAvailable) ? "Safe" : "Warning";

    public string TuneAvailabilityTone => TuneTargets.Any(target => target.IsAvailable) ? "Safe" : "Warning";

    public string CalibrationEligibilityReason => GetCalibrationEligibility().Reason;

    public string TuneEligibilityReason => GetTuneEligibility().Reason;

    public bool CanStartCalibration => CanUseServiceWrites
        && !HasActiveOperation
        && GetCalibrationEligibility().Eligible;

    public bool CanStartTune => CanUseServiceWrites
        && !HasActiveOperation
        && GetTuneEligibility().Eligible
        && TryReadTuneLimits(out _, out _);

    public bool CanWrite => CanUseServiceWrites;

    public string WriteStateLabel => !IsServiceOnline
        ? "Hardware writes locked"
        : IsRecoveryRequired
            ? "Recovery required"
        : !CanUseServiceWrites
            ? "Service update required"
            : CanWrite ? "Service write path ready" : "Hardware writes locked";

    /// <summary>
    /// Badge tone for <see cref="WriteStateLabel"/>. It tracks the write-path
    /// state the badge actually names, not the broader <c>SafetyTone</c>: a ready
    /// write path must not render amber just because Experimental controls were
    /// detected (that posture is carried by the summary text below the badge).
    /// </summary>
    public string WriteStateTone => !IsServiceOnline
        ? "Warning"
        : IsRecoveryRequired
            ? "Critical"
        : !CanUseServiceWrites
            ? "Warning"
            : CanWrite ? "Safe" : "Warning";

    public string ServiceVersion => _serviceCompatibility.ServiceVersion ?? _status?.Version ?? "Unavailable";

    public string AppVersion => _serviceCompatibility.ClientVersion;

    public string StateRevisionText => _status is null ? "—" : _status.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string ServiceUptimeText
    {
        get
        {
            if (_status is null)
            {
                return "Unavailable";
            }

            TimeSpan uptime = DateTimeOffset.UtcNow - _status.StartedAt;
            return uptime.TotalDays >= 1
                ? $"{(int)uptime.TotalDays} d {uptime.Hours} h"
                : uptime.TotalHours >= 1
                    ? $"{(int)uptime.TotalHours} h {uptime.Minutes} min"
                    : $"{Math.Max(0, uptime.Minutes)} min";
        }
    }

    public async Task InitialiseAsync(bool startAutomaticRefresh = true)
    {
        BusyMessage = IsPortableMode
            ? Localization.L10n.Get("Portable_BusyMessage")
            : "Connecting to the RigPilot service";
        IsBusy = true;
        try
        {
            await RefreshAsync(full: true, userInitiated: false);
            await RefreshUserFeaturesAsync();
        }
        finally
        {
            IsBusy = false;
            if (!_disposed && startAutomaticRefresh)
            {
                _refreshTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }
    }

    public void SetPage(string title, string subtitle)
    {
        CurrentPageTitle = title;
        CurrentPageSubtitle = subtitle;
    }

    public async Task ApplyBuiltInAsync(string profileId)
    {
        if (_suiteProfilesById.TryGetValue(profileId, out ProfileV2? suiteProfile))
        {
            try
            {
                await ApplyProfileV2Async(suiteProfile);
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }
            return;
        }

        ProfileV1? profile = Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            ShowNotice("That profile is not available yet.", "Warning");
            return;
        }

        try
        {
            await ApplyProfileAsync(profile);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    public async Task ResetVerifiedControlsAsync()
    {
        try
        {
            await ResetVerifiedControlsCoreAsync();
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    public async Task<CompatibilityReportV1> GetReportPreviewAsync()
    {
        if (IsServiceOnline)
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.ExportReport),
                _lifetime.Token);
            EnsureSuccess(response);
            return IpcJson.FromElement<CompatibilityReportV1>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty report.");
        }

        HardwareSnapshot snapshot = _snapshot
            ?? throw new InvalidOperationException("No hardware data is available yet.");
        return CompatibilityReportBuilder.Build(
            snapshot,
            typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            new Dictionary<string, string>
            {
                ["framework"] = Environment.Version.ToString(),
                ["osVersion"] = Environment.OSVersion.VersionString
            },
            [],
            userApproved: false);
    }

    public async Task<HardwareEvidenceReportV1> GetHardwareEvidenceAsync()
    {
        if (!IsServiceOnline)
        {
            throw new InvalidOperationException("Hardware evidence requires the RigPilot service. Local-probe mode is read-only and does not have service traces or recovery state.");
        }
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetHardwareEvidence),
            _lifetime.Token);
        EnsureSuccess(response);
        return IpcJson.FromElement<HardwareEvidenceReportV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty hardware-evidence report.");
    }

    private async Task ScanHidInventoryCoreAsync()
    {
        if (!IsServiceOnline)
        {
            HidInventoryStatus = "Connected-peripheral scanning requires the RigPilot service. Local-probe mode is read-only.";
            return;
        }

        HidInventoryStatus = "Scanning connected peripherals…";
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.DiscoverHidInventory),
            _lifetime.Token);
        EnsureSuccess(response);
        HidInventoryResultV1 result = IpcJson.FromElement<HidInventoryResultV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty HID inventory result.");

        HidDevices.Clear();
        if (result.Outcome != HidInventoryOutcome.Succeeded)
        {
            HidInventoryStatus = $"Peripheral scan did not complete: {result.Detail}";
            return;
        }

        // Collapse the raw per-interface HID records (a device often exposes several) into one
        // row per physical device, aggregating its interface classes for a clean read-only view.
        HidDeviceDisplay[] devices = result.Devices
            .GroupBy(device => (device.VendorId, device.ProductId, device.ProductName))
            .Select(group => new HidDeviceDisplay(
                string.IsNullOrWhiteSpace(group.Key.ProductName) ? "Unnamed HID device" : group.Key.ProductName!,
                $"VID {group.Key.VendorId:X4}  ·  PID {group.Key.ProductId:X4}",
                string.Join(", ", group
                    .Select(device => device.DeviceClass)
                    .Distinct()
                    .OrderBy(value => value, StringComparer.Ordinal))))
            .OrderBy(device => device.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (HidDeviceDisplay device in devices)
        {
            HidDevices.Add(device);
        }

        int classCount = result.Devices.Select(device => device.DeviceClass).Distinct().Count();
        HidInventoryStatus = devices.Length == 0
            ? "No HID peripherals were enumerated."
            : $"{devices.Length} connected peripheral{(devices.Length == 1 ? string.Empty : "s")} across " +
              $"{classCount} device class{(classCount == 1 ? string.Empty : "es")}. Read-only inventory; no write capability.";
    }

    private async Task ReadKrakenTelemetryCoreAsync()
    {
        if (!IsServiceOnline)
        {
            KrakenTelemetryStatus = "Liquid-cooler telemetry requires the RigPilot service.";
            return;
        }

        KrakenTelemetryStatus = "Reading streamed Kraken status…";
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.ReadKrakenTelemetry),
            _lifetime.Token);
        EnsureSuccess(response);
        KrakenTelemetryV1 result = IpcJson.FromElement<KrakenTelemetryV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty Kraken telemetry result.");
        KrakenTelemetryStatus = result.Outcome == KrakenTelemetryOutcome.Succeeded
            ? $"{result.ProductName ?? "Kraken"}: liquid {result.LiquidTemperatureCelsius:0.0} °C, pump {result.PumpSpeedRpm} rpm at {result.PumpDutyPercent}% duty. Read-only; no report was written to the cooler."
            : result.Message;
    }

    public string BuildMonitoringCsv()
    {
        System.Text.StringBuilder output = new();
        output.AppendLine("sensor_id,display_name,unit,timestamp_utc,value");
        foreach (SensorTrendDisplay trend in MonitoringTrends)
        {
            foreach (SensorTrendPointV1 point in trend.Trend.Points)
            {
                output.Append(Csv(trend.SensorId)).Append(',')
                    .Append(Csv(trend.DisplayName)).Append(',')
                    .Append(Csv(trend.Unit)).Append(',')
                    .Append(point.Timestamp.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
            }
        }
        return output.ToString();
    }

    public async Task ToggleDesktopOsdFromHotkeyAsync()
    {
        try
        {
            if (IsDesktopOsdVisible)
            {
                await HideDesktopOsdCoreAsync();
            }
            else
            {
                await ShowDesktopOsdCoreAsync();
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    public string BuildDiagnosticSummary()
    {
        return string.Join(
            Environment.NewLine,
            "RigPilot diagnostic summary",
            $"Connection: {ServiceStateLabel} ({DataSourceLabel})",
            $"Runtime contract: {ServiceCompatibilityLabel} ({ServiceCompatibilityMessage})",
            $"Dashboard version: {AppVersion}",
            $"Service version: {ServiceVersion}",
            $"State revision: {StateRevisionText}",
            $"Devices: {DeviceCount}",
            $"Sensors: {SensorCount}",
            $"Verified controls: {VerifiedControlCount}",
            $"Read-only controls: {ReadOnlyControlCount}",
            $"Experimental controls: {ExperimentalControlCount} ({CommissioningEligibleExperimentalControlCount} commissioning candidates, {ProtectedExperimentalControlCount} protected)",
            $"Restricted controls: {RestrictedControlCount}",
            $"Warnings: {WarningCount}",
            $"Competing writers: {RunningConflictCount}",
            $"Health alerts: {ActiveHealthAlertCount}",
            $"Recovery: {SafeModeLabel}",
            $"Active profile: {ActiveProfileName}",
            $"Safety: {SafetySummary}");
    }

    public void ShowNotice(string message, string tone = "Info", bool clearsWhenRecovered = false)
    {
        NoticeText = message;
        NoticeTone = tone;
        HasNotice = true;
        // Any new notice supersedes a prior recovery notice; only a notice that
        // opts in stays tied to the service-recovery state.
        _recoveryNoticeActive = clearsWhenRecovered;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Dispose();
        _desktopOsd.Dispose();
        CancelAmbientLightingForDisposal();
        _lifetime.Cancel();
        _lifetime.Dispose();
        DisposeLocalCoordinator();
    }

    private async Task RefreshWithFeedbackAsync()
    {
        BusyMessage = "Refreshing hardware state";
        IsBusy = true;
        try
        {
            await RefreshAsync(full: true, userInitiated: true);
            await RefreshUserFeaturesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync(bool full, bool userInitiated)
    {
        if (_refreshing || _disposed)
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (IsPortableMode)
            {
                // Portable mode never touches the service pipe: no connection
                // attempt, no compatibility probe, no install prompting.
                IsServiceOnline = false;
                ServiceStatusText = Localization.L10n.Get("Portable_ServiceStatus");
                UpdatePlatformStatus = Localization.L10n.Get("Portable_UpdateStatus");
                bool forcePortableProbe = full || _snapshot is null || DateTimeOffset.UtcNow - _lastLocalProbe >= LocalProbeInterval;
                await RefreshFromLocalAdaptersAsync(forcePortableProbe);
                if (_snapshot is not null)
                {
                    DataSourceLabel = LocalProbeLabel;
                }
                return;
            }

            CancellationToken token = _lifetime.Token;
            DateTimeOffset refreshTime = DateTimeOffset.UtcNow;
            bool refreshControlPlane = full
                || userInitiated
                || !IsServiceOnline
                || _status is null
                // A flagged recovery is often a transient escalation the service clears on
                // its own retry (e.g. the GPU-fan NVAPI restore settling). Re-read status on
                // every 1s tick while it is set so the banner reflects the live state within a
                // second, instead of lingering up to a full control-plane interval after the
                // service has already recovered.
                || _status.RecoveryRequired
                // Poll on every tick while a recovery notice is up too, so it is
                // retired within a second of the service clearing the condition.
                || _recoveryNoticeActive
                || refreshTime - _lastServiceControlPlaneRefresh >= ServiceControlPlaneRefreshInterval;
            if (refreshControlPlane)
            {
                await RefreshServiceCompatibilityAsync(token);
                if (!_serviceCompatibility.IsServiceReachable)
                {
                    throw new InvalidOperationException(_serviceCompatibility.Summary);
                }
                IpcResponse statusResponse = await _client.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
                    token);
                EnsureSuccess(statusResponse);
                _status = IpcJson.FromElement<ServiceStatus>(statusResponse.Payload)
                    ?? throw new InvalidDataException("Service returned an empty status response.");
                NotifyCoolingRuntimeProperties();
                if (_status.RecoveryRequired)
                {
                    _hardwareControlArmedThisConnection = false;
                    SetHardwareControlState(false);
                }
                else if (_recoveryNoticeActive)
                {
                    // The service has cleared the recovery condition the notice
                    // reported (it self-recovers, e.g. after the GPU-fan NVAPI
                    // restore settles), so retire the now-stale banner rather than
                    // leaving it up until the user dismisses it.
                    DismissNotice();
                }
                NotifyServiceWriteStateChanged();
                // CanUseServiceWrites intentionally includes the live connection
                // state. Set it only after a valid status reply, before composing
                // the user-facing message, so a ready service is not described as
                // update-locked for the rest of this refresh.
                IsServiceOnline = true;
                ServiceStatusText = !_serviceCompatibility.CanUseServiceWrites
                    ? "The service is reachable, but this dashboard has locked all service-owned writes until the matching runtime is installed."
                    : _status.Message;
                await RefreshOperationStatusAsync(token);
                await RefreshFanCommissioningAsync(token);
                await RefreshCoolingOutputAssignmentsAsync(token);
                await RefreshUpdateStatusAsync(token);
                _lastServiceControlPlaneRefresh = refreshTime;
            }
            else if (HasActiveOperation)
            {
                // Progress remains one-second live while calibration or tuning is
                // active; otherwise this control-plane request follows the slower
                // cadence above.
                await RefreshOperationStatusAsync(token);
            }

            if (full || _snapshot is null)
            {
                IpcResponse snapshotResponse = await _client.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetInventory),
                    token);
                EnsureSuccess(snapshotResponse);
                _snapshot = IpcJson.FromElement<HardwareSnapshot>(snapshotResponse.Payload)
                    ?? throw new InvalidDataException("Service returned an empty inventory response.");

                await RefreshProfilesAsync(token);
                await RefreshAutomationRulesAsync(token);
                await RefreshOwnershipAsync(token);
            }
            else
            {
                IpcResponse sensorsResponse = await _client.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.SubscribeSensors),
                    token);
                EnsureSuccess(sensorsResponse);
                IReadOnlyList<SensorSample> sensors = IpcJson.FromElement<IReadOnlyList<SensorSample>>(sensorsResponse.Payload) ?? [];
                _snapshot = _snapshot with { Sensors = sensors, CapturedAt = DateTimeOffset.UtcNow };

                string? generatedProfileId = _operation?.TuneResult?.GeneratedProfile?.Id;
                if (generatedProfileId is not null
                    && Profiles.All(profile => !string.Equals(profile.Id, generatedProfileId, StringComparison.Ordinal)))
                {
                    await RefreshProfilesAsync(token);
                }
            }

            DataSourceLabel = "Service";
            await EnsureHardwareControlArmedAsync();
            DisposeLocalCoordinator();
            UpdateDisplays();
            ApplyCoolingOutputAssignmentForTarget();
            if (full
                || userInitiated
                || refreshTime - _lastServiceDiagnosticsRefresh >= ServiceDiagnosticsRefreshInterval)
            {
                await RefreshAdapterTraceAsync(token);
                await RefreshReliabilityAsync(token);
                _lastServiceDiagnosticsRefresh = refreshTime;
            }
            LastUpdatedText = $"Updated {DateTimeOffset.Now:HH:mm:ss}";
            if (userInitiated)
            {
                ShowNotice("Hardware state refreshed.", "Success");
            }

            await EvaluateAutomationAsync();
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            IsServiceOnline = false;
            _hardwareControlArmedThisConnection = false;
            _hardwareControlArmAttemptedThisConnection = false;
            SetHardwareControlState(false);
            _status = null;
            NotifyCoolingRuntimeProperties();
            _operation = null;
            NotifyOperationProperties();
            ServiceStatusText = _serviceCompatibility.State == ServiceCompatibilityState.Unavailable
                ? _serviceCompatibility.Summary
                : DescribeServiceFailure(exception);
            UpdatePlatformStatus = "Driver update execution is unavailable while the service is offline.";
            PendingUpdateCount = 0;
            Replace(AdapterTrace, []);
            OnPropertyChanged(nameof(AdapterTraceCount));
            OnPropertyChanged(nameof(HasAdapterTrace));
            ClearReliabilityDisplays();
            bool forceProbe = full || _snapshot is null || DateTimeOffset.UtcNow - _lastLocalProbe >= LocalProbeInterval;
            await RefreshFromLocalAdaptersAsync(forceProbe);
            if (userInitiated)
            {
                ShowNotice(ServiceStatusText, "Warning");
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshServiceCompatibilityAsync(CancellationToken cancellationToken)
    {
        string clientVersion = RuntimeVersion.Get(typeof(MainViewModel).Assembly);
        IpcResponse response;
        try
        {
            response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.Handshake,
                    new HandshakeRequestV2(
                        ProductBrand.Name,
                        clientVersion,
                        ProtocolConstants.Version,
                        ProtocolConstants.Version)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            _serviceFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SetServiceCompatibility(ServiceRuntimeCompatibility.Unavailable(
                clientVersion,
                $"The service handshake could not complete: {DescribeServiceFailure(exception)}"));
            return;
        }

        if (!response.Success)
        {
            _serviceFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string detail = string.IsNullOrWhiteSpace(response.Error)
                ? response.ErrorCode ?? "unknown service error"
                : $"{response.ErrorCode}: {response.Error}";
            SetServiceCompatibility(ServiceRuntimeCompatibility.Unavailable(
                clientVersion,
                $"The installed service rejected the protocol-2 handshake ({detail}). Update the app and service together."));
            return;
        }

        HandshakeResponseV2? current = IpcJson.FromElement<HandshakeResponseV2>(response.Payload);
        if (current is { SelectedProtocolVersion: > 0 })
        {
            _serviceFeatures = current.Features.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _previewProfileCommand.RaiseCanExecuteChanged();
            SetServiceCompatibility(ServiceRuntimeCompatibility.Evaluate(clientVersion, current));
            return;
        }

        _serviceFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _previewProfileCommand.RaiseCanExecuteChanged();
        HandshakeResponse? legacy = IpcJson.FromElement<HandshakeResponse>(response.Payload);
        SetServiceCompatibility(ServiceRuntimeCompatibility.EvaluateLegacy(clientVersion, legacy));
    }

    private void SetServiceCompatibility(ServiceRuntimeCompatibilityV1 compatibility)
    {
        if (_serviceCompatibility == compatibility)
        {
            return;
        }

        _serviceCompatibility = compatibility;
        OnPropertyChanged(nameof(ServiceCompatibility));
        OnPropertyChanged(nameof(ServiceCompatibilityLabel));
        OnPropertyChanged(nameof(ServiceCompatibilityMessage));
        OnPropertyChanged(nameof(ServiceCompatibilityTone));
        OnPropertyChanged(nameof(IsServiceUpgradeRequired));
        OnPropertyChanged(nameof(CanUseServiceWrites));
        OnPropertyChanged(nameof(IsRecoveryRequired));
        OnPropertyChanged(nameof(ServiceStateLabel));
        OnPropertyChanged(nameof(ConnectionTone));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(WriteStateLabel));
        OnPropertyChanged(nameof(WriteStateTone));
        OnPropertyChanged(nameof(ServiceVersion));
        OnPropertyChanged(nameof(AppVersion));
        RebuildExperimentalControlCenter();
        UpdateSafetySummary();
        NotifyOperationEligibility();
        _applyProfileCommand.RaiseCanExecuteChanged();
        _resetVerifiedCommand.RaiseCanExecuteChanged();
        _toggleHardwareControlCommand.RaiseCanExecuteChanged();
    }

    private void NotifyServiceWriteStateChanged()
    {
        OnPropertyChanged(nameof(CanUseServiceWrites));
        OnPropertyChanged(nameof(IsRecoveryRequired));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(WriteStateLabel));
        OnPropertyChanged(nameof(WriteStateTone));
        OnPropertyChanged(nameof(ConnectionTone));
        _toggleHardwareControlCommand.RaiseCanExecuteChanged();
        RaiseHardwareControlCanExecuteChanged();
        _applyProfileCommand.RaiseCanExecuteChanged();
        _resetVerifiedCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshAdapterTraceAsync(CancellationToken cancellationToken)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetAdapterTrace),
            cancellationToken);
        if (!response.Success)
        {
            Replace(AdapterTrace, []);
            OnPropertyChanged(nameof(AdapterTraceCount));
            OnPropertyChanged(nameof(HasAdapterTrace));
            return;
        }
        IReadOnlyList<AdapterTraceEvent> trace = IpcJson.FromElement<IReadOnlyList<AdapterTraceEvent>>(response.Payload) ?? [];
        Replace(AdapterTrace, trace
            .OrderByDescending(item => item.Timestamp)
            .Take(32)
            .Select(AdapterTraceDisplay.From));
        OnPropertyChanged(nameof(AdapterTraceCount));
        OnPropertyChanged(nameof(HasAdapterTrace));
    }

    private async Task RefreshReliabilityAsync(CancellationToken cancellationToken)
    {
        Task<IpcResponse> rulesTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetHealthRules), cancellationToken);
        Task<IpcResponse> alertsTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetHealthAlerts), cancellationToken);
        Task<IpcResponse> recoveryTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetSafetyRecoveryStatus), cancellationToken);
        Task<IpcResponse> coolingTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetCoolingQualificationReports), cancellationToken);
        Task<IpcResponse> plansTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetDeviceQualificationPlans), cancellationToken);
        await Task.WhenAll(rulesTask, alertsTask, recoveryTask, coolingTask, plansTask);

        if (!rulesTask.Result.Success
            || !alertsTask.Result.Success
            || !recoveryTask.Result.Success
            || !coolingTask.Result.Success
            || !plansTask.Result.Success)
        {
            // A dashboard can connect to one service upgrade behind. Keep the
            // old service usable and make the absent reliability surface clear.
            ClearReliabilityDisplays();
            return;
        }

        IReadOnlyList<HealthRuleV1> rules = IpcJson.FromElement<IReadOnlyList<HealthRuleV1>>(rulesTask.Result.Payload) ?? [];
        IReadOnlyList<HealthAlertEventV1> alerts = IpcJson.FromElement<IReadOnlyList<HealthAlertEventV1>>(alertsTask.Result.Payload) ?? [];
        _safetyRecoveryStatus = IpcJson.FromElement<SafetyRecoveryStatusV1>(recoveryTask.Result.Payload);
        IReadOnlyList<CoolingQualificationReportV1> cooling = IpcJson.FromElement<IReadOnlyList<CoolingQualificationReportV1>>(coolingTask.Result.Payload) ?? [];
        IReadOnlyList<DeviceQualificationPlanV1> plans = IpcJson.FromElement<IReadOnlyList<DeviceQualificationPlanV1>>(plansTask.Result.Payload) ?? [];

        Replace(HealthRules, rules
            .Where(rule => rule.SchemaVersion == HealthRuleV1.CurrentSchemaVersion)
            .OrderBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .Select(HealthRuleDisplay.From));
        UpdateHealthRecommendationStatus();
        Replace(HealthAlerts, alerts
            .Where(alert => alert.SchemaVersion == HealthAlertEventV1.CurrentSchemaVersion)
            .OrderByDescending(alert => alert.UpdatedAt)
            .Select(HealthAlertDisplay.From));
        Replace(CoolingQualificationReports, cooling
            .Where(report => report.SchemaVersion == CoolingQualificationReportV1.CurrentSchemaVersion)
            .OrderBy(report => report.HeaderName, StringComparer.OrdinalIgnoreCase));
        Replace(DeviceQualificationPlans, plans
            .Where(plan => plan.SchemaVersion == DeviceQualificationPlanV1.CurrentSchemaVersion)
            .OrderBy(plan => plan.Kind)
            .ThenBy(plan => plan.DeviceName, StringComparer.OrdinalIgnoreCase));
        UpdateTimeline();

        foreach (HealthAlertDisplay alert in HealthAlerts.Where(item => item.Alert.State == HealthAlertState.Active))
        {
            if (_notifiedHealthAlertIds.Add(alert.Alert.Id)
                && System.Windows.Application.Current is App app)
            {
                app.ShowTrayNotification($"{ProductBrand.Name} health alert", alert.Message);
            }
        }

        UpdateSafetySummary();
        NotifyReliabilityProperties();
    }

    private void ClearReliabilityDisplays()
    {
        Replace(HealthRules, []);
        Replace(HealthAlerts, []);
        Replace(TimelineEvents, []);
        Replace(VisibleTimelineEvents, []);
        Replace(CoolingQualificationReports, []);
        Replace(DeviceQualificationPlans, []);
        _safetyRecoveryStatus = null;
        _notifiedHealthAlertIds.Clear();
        NotifyReliabilityProperties();
    }

    private async Task RefreshUpdateStatusAsync(CancellationToken cancellationToken)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetUpdateStatus),
            cancellationToken);
        if (!response.Success)
        {
            PendingUpdateCount = 0;
            UpdatePlatformStatus = "This service does not expose the exact-driver update executor.";
            return;
        }

        UpdateStatusV1? status = IpcJson.FromElement<UpdateStatusV1>(response.Payload);
        if (status is null || status.SchemaVersion != UpdateStatusV1.CurrentSchemaVersion)
        {
            PendingUpdateCount = 0;
            UpdatePlatformStatus = "The service returned an unsupported driver update status.";
            return;
        }

        PendingUpdateCount = status.Transactions.Count(transaction => transaction.State is
            UpdateTransactionState.Planned or
            UpdateTransactionState.Validated or
            UpdateTransactionState.Staged or
            UpdateTransactionState.Applying or
            UpdateTransactionState.PendingReboot or
            UpdateTransactionState.Verifying or
            UpdateTransactionState.RecoveryRequired);
        UpdatePlatformStatus = status.ProductionExecutionReady
            ? status.ExecutionMessage
            : $"{status.ExecutionMessage} Package inspection remains available; installation is locked.";
    }

    private async Task RefreshProfilesAsync(CancellationToken cancellationToken)
    {
        Task<IpcResponse> legacyTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetProfiles),
            cancellationToken);
        Task<IpcResponse> suiteTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetProfilesV2),
            cancellationToken);
        Task<IpcResponse> graphTask = _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetCoolingGraphs),
            cancellationToken);
        Task<IpcResponse>? validationTask = _serviceFeatures.Contains(ServiceRuntimeFeatures.AutoOcValidationV1)
            ? _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetAutoOcProfileValidations),
                cancellationToken)
            : null;
        List<Task<IpcResponse>> requests = [legacyTask, suiteTask, graphTask];
        if (validationTask is not null)
        {
            requests.Add(validationTask);
        }
        await Task.WhenAll(requests);

        IpcResponse legacyResponse = await legacyTask;
        EnsureSuccess(legacyResponse);
        IReadOnlyList<ProfileV1> legacyProfiles = IpcJson.FromElement<IReadOnlyList<ProfileV1>>(legacyResponse.Payload) ?? [];

        // V2 is optional during the one-cycle read-only compatibility window.
        // A legacy service must leave the dashboard usable rather than forcing
        // it into local-probe mode simply because it cannot enumerate V2 data.
        IReadOnlyList<ProfileV2> suiteProfiles = [];
        IpcResponse suiteResponse = await suiteTask;
        if (suiteResponse.Success)
        {
            suiteProfiles = IpcJson.FromElement<IReadOnlyList<ProfileV2>>(suiteResponse.Payload) ?? [];
        }

        IReadOnlyList<CoolingGraphV1> coolingGraphs = [];
        IpcResponse graphResponse = await graphTask;
        if (graphResponse.Success)
        {
            coolingGraphs = IpcJson.FromElement<IReadOnlyList<CoolingGraphV1>>(graphResponse.Payload) ?? [];
        }
        IReadOnlyList<AutoOcProfileValidationV1> autoOcValidations = [];
        if (validationTask is not null)
        {
            IpcResponse validationResponse = await validationTask;
            if (validationResponse.Success)
            {
                autoOcValidations = IpcJson.FromElement<IReadOnlyList<AutoOcProfileValidationV1>>(validationResponse.Payload) ?? [];
            }
        }

        string? selectedRuleId = NewRuleProfile?.Id;
        string? selectedGameProfileId = SelectedGameProfile?.Id;
        _suiteProfilesById.Clear();
        foreach (ProfileV2 profile in suiteProfiles.Where(profile => profile.SchemaVersion == ProfileV2.CurrentSchemaVersion))
        {
            _suiteProfilesById[profile.Id] = profile;
        }
        _autoOcValidationsByProfileId.Clear();
        foreach (AutoOcProfileValidationV1 validation in autoOcValidations.Where(item =>
                     item.SchemaVersion == AutoOcProfileValidationV1.CurrentSchemaVersion))
        {
            _autoOcValidationsByProfileId[validation.ProfileId] = validation;
        }
        _coolingGraphsById.Clear();
        foreach (CoolingGraphV1 graph in coolingGraphs.Where(graph => graph.SchemaVersion == CoolingGraphV1.CurrentSchemaVersion))
        {
            _coolingGraphsById[graph.Id] = graph;
        }

        Dictionary<string, ProfileV1> merged = legacyProfiles
            .Where(profile => profile.SchemaVersion == ProfileV1.CurrentSchemaVersion)
            .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        foreach (ProfileV2 suiteProfile in _suiteProfilesById.Values)
        {
            // The legacy shape keeps existing automation and game selectors
            // compatible, while command dispatch below still applies the V2
            // transaction with cooling/lighting/OSD references intact.
            merged[suiteProfile.Id] = ProfileMigration.Downgrade(suiteProfile);
        }

        Replace(Profiles, merged.Values
            .OrderBy(ProfileDisplayRank)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase));
        NewRuleProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedRuleId)
            ?? Profiles.FirstOrDefault(profile => profile.Id == "balanced")
            ?? Profiles.FirstOrDefault();
        if (selectedGameProfileId is not null)
        {
            SelectedGameProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedGameProfileId);
        }
        SelectedEmergencyProfile = Profiles.FirstOrDefault(profile => profile.Id == SelectedEmergencyProfile?.Id)
            ?? Profiles.FirstOrDefault(profile => profile.Id == "balanced")
            ?? Profiles.FirstOrDefault();
        NotifyAutomationProperties();
        NotifyHealthRuleProperties();
    }

    private async Task RefreshUserFeaturesAsync()
    {
        try
        {
            IpcResponse handshake = await _userAgentClient.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.Handshake),
                _lifetime.Token);
            EnsureSuccess(handshake);
            HandshakeResponseV2? userAgentHandshake = IpcJson.FromElement<HandshakeResponseV2>(handshake.Payload);
            InteractiveFanPreflightSupported = userAgentHandshake?.Features.Contains("interactive-fan-preflight", StringComparer.OrdinalIgnoreCase) == true;
            Replace(Games, await GetUserEntitiesAsync<GameEntryV1>(IpcCommand.GetGames));
            Replace(Workflows, await GetUserEntitiesAsync<AutomationWorkflowV1>(IpcCommand.GetWorkflows));
            Replace(LightingScenes, await GetUserEntitiesAsync<LightingSceneV1>(IpcCommand.GetLightingScenes));
            Replace(EffectGraphs, await GetUserEntitiesAsync<EffectGraphV1>(IpcCommand.GetEffectGraphs));
            Replace(Macros, await GetUserEntitiesAsync<MacroV1>(IpcCommand.GetMacros));
            Replace(Scripts, await GetUserEntitiesAsync<ScriptActionV1>(IpcCommand.GetScripts));
            string? selectedDesktopOsdId = SelectedDesktopOsdLayout?.Id;
            Replace(OsdLayouts, await GetUserEntitiesAsync<OsdLayoutV1>(IpcCommand.GetOsdLayouts));
            SelectedDesktopOsdLayout = OsdLayouts.FirstOrDefault(layout => string.Equals(layout.Id, selectedDesktopOsdId, StringComparison.Ordinal))
                ?? SelectedDesktopOsdLayout
                ?? OsdLayouts.FirstOrDefault();
            Replace(CapturePresets, await GetUserEntitiesAsync<CapturePresetV1>(IpcCommand.GetCapturePresets));
            if (userAgentHandshake?.Features.Contains("osd-presentation", StringComparer.OrdinalIgnoreCase) == true)
            {
                OsdPresentationSettingsV1? settings = await GetUserValueAsync<OsdPresentationSettingsV1>(IpcCommand.GetOsdPresentationSettings);
                if (settings is { SchemaVersion: OsdPresentationSettingsV1.CurrentSchemaVersion })
                {
                    ApplyOsdPresentationSettings(settings);
                }
            }
            if (userAgentHandshake?.Features.Contains("monitoring-preferences", StringComparer.OrdinalIgnoreCase) == true)
            {
                MonitoringPreferencesV1? preferences = await GetUserValueAsync<MonitoringPreferencesV1>(IpcCommand.GetMonitoringPreferences);
                if (preferences is { SchemaVersion: MonitoringPreferencesV1.CurrentSchemaVersion })
                {
                    _monitoringPreferences = preferences;
                    UpdateMonitoringTrends();
                }
            }
            if (userAgentHandshake?.Features.Contains("monitoring-comparison", StringComparer.OrdinalIgnoreCase) == true)
            {
                MonitoringComparisonLayoutV1? layout = await GetUserValueAsync<MonitoringComparisonLayoutV1>(IpcCommand.GetMonitoringComparisonLayout);
                if (layout is { SchemaVersion: MonitoringComparisonLayoutV1.CurrentSchemaVersion }
                    && string.Equals(layout.Id, MonitoringComparisonLayoutV1.DefaultId, StringComparison.Ordinal))
                {
                    _monitoringComparisonLayout = layout;
                    UpdateMonitoringComparison();
                }
            }
            else
            {
                _monitoringComparisonLayout = DefaultMonitoringComparisonLayout();
                UpdateMonitoringComparison();
            }
            if (userAgentHandshake?.Features.Contains("wgc-recording-preflight", StringComparer.OrdinalIgnoreCase) == true)
            {
                _wgcRecordingPreflight = await GetUserValueAsync<WgcRecordingPreflightV1>(IpcCommand.GetWgcRecordingPreflight);
            }
            else
            {
                _wgcRecordingPreflight = null;
            }
            if (userAgentHandshake?.Features.Contains("macro-recording", StringComparer.OrdinalIgnoreCase) == true)
            {
                Replace(MacroRecordingSessions, await GetUserEntitiesAsync<MacroRecordingSessionV1>(IpcCommand.GetMacroRecordingSessions));
                IpcResponse recordingResponse = await _userAgentClient.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetMacroRecordingStatus),
                    _lifetime.Token);
                EnsureSuccess(recordingResponse);
                MacroRecordingStatusV1 recording = IpcJson.FromElement<MacroRecordingStatusV1>(recordingResponse.Payload)
                    ?? new MacroRecordingStatusV1(null, false, "Macro recording status was unavailable.");
                ActiveMacroRecording = recording.ActiveSession;
                MacroRecordingStatus = recording.Guidance;
            }
            else
            {
                Replace(MacroRecordingSessions, []);
                ActiveMacroRecording = null;
                MacroRecordingStatus = "The connected user agent predates visible macro recording. Upgrade it to use this feature.";
            }
            if (userAgentHandshake?.Features.Contains("overlay-status", StringComparer.OrdinalIgnoreCase) == true)
            {
                IpcResponse overlayResponse = await _userAgentClient.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetOverlayStatus),
                    _lifetime.Token);
                EnsureSuccess(overlayResponse);
                OverlayBridgeStatusV1 overlay = IpcJson.FromElement<OverlayBridgeStatusV1>(overlayResponse.Payload)
                    ?? throw new InvalidDataException("User agent returned an empty overlay status.");
                RtssBridgeStatus = overlay.Rtss.Message;
                GameBarBridgeStatus = overlay.GameBarMessage;
                CaptureBridgeStatus = overlay.CaptureMessage;
            }
            else
            {
                RtssBridgeStatus = "The connected user agent predates RTSS discovery.";
                GameBarBridgeStatus = "The connected user agent predates Game Bar discovery.";
                CaptureBridgeStatus = "The connected user agent predates Windows Graphics Capture discovery.";
            }
            if (userAgentHandshake?.Features.Contains("rtss-osd", StringComparer.OrdinalIgnoreCase) == true)
            {
                IpcResponse rtssOsdResponse = await _userAgentClient.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetRtssOsdBridgeStatus),
                    _lifetime.Token);
                EnsureSuccess(rtssOsdResponse);
                RtssOsdBridgeStatusV1 rtssOsd = IpcJson.FromElement<RtssOsdBridgeStatusV1>(rtssOsdResponse.Payload)
                    ?? throw new InvalidDataException("User agent returned an empty RTSS OSD status.");
                IsRtssOsdPublishing = rtssOsd.Publishing;
                RtssOsdPublishStatus = rtssOsd.Message;
            }
            else
            {
                IsRtssOsdPublishing = false;
                RtssOsdPublishStatus = "The connected user agent predates the RTSS OSD bridge. Upgrade it to publish RigPilot's sensor line.";
            }
            if (userAgentHandshake?.Features.Contains("desktop-snapshot", StringComparer.OrdinalIgnoreCase) == true)
            {
                string? selectedTargetId = SelectedCaptureTarget?.StableId;
                Replace(CaptureTargets, await GetUserEntitiesAsync<CaptureTargetV1>(IpcCommand.GetCaptureTargets));
                SelectedCaptureTarget = CaptureTargets.FirstOrDefault(target => string.Equals(target.StableId, selectedTargetId, StringComparison.OrdinalIgnoreCase))
                    ?? CaptureTargets.FirstOrDefault();
                OnPropertyChanged(nameof(OsdMonitors));
                SelectedOsdMonitor = ResolveOsdMonitor(_osdPresentationSettings.MonitorStableId);
                DesktopSnapshotStatus = CaptureTargets.Count == 0
                    ? "No visible display or eligible window target is available for a local snapshot."
                    : "Choose a display or visible window. Capture is explicit and saves a PNG only under Pictures\\RigPilot\\Snapshots.";
            }
            else
            {
                Replace(CaptureTargets, []);
                SelectedCaptureTarget = null;
                SelectedOsdMonitor = null;
                OnPropertyChanged(nameof(OsdMonitors));
                DesktopSnapshotStatus = "The connected user agent predates explicit desktop snapshot support.";
            }
            if (userAgentHandshake?.Features.Contains("monitor-brightness", StringComparer.OrdinalIgnoreCase) == true)
            {
                await RefreshMonitorBrightnessCoreAsync(showNotice: false);
            }
            else
            {
                Replace(MonitorBrightnessDevices, []);
                SelectedMonitorBrightnessDevice = null;
                MonitorBrightnessStatus = "The connected user agent predates monitor brightness support.";
                OnPropertyChanged(nameof(CanSetMonitorBrightness));
                OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
            }
            SelectedGame ??= Games.FirstOrDefault();
            SelectedLightingScene ??= LightingScenes.FirstOrDefault();
            SelectedMacro ??= Macros.FirstOrDefault();
            IsUserAgentOnline = true;
            UserAgentStatus = "User-session bridges and automation host are available.";
            NotifyUserFeatureProperties();
            NotifyOsdPresentationProperties();
            OnPropertyChanged(nameof(WgcRecordingStatus));
            OnPropertyChanged(nameof(IsWgcRecordingReady));
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException && !_disposed)
        {
            InteractiveFanPreflightSupported = false;
            IsUserAgentOnline = false;
            UserAgentStatus = $"User-agent features are unavailable: {exception.Message}";
            Replace(MonitorBrightnessDevices, []);
            SelectedMonitorBrightnessDevice = null;
            MonitorBrightnessStatus = "Start or update RigPilot in this signed-in Windows session, then refresh monitors.";
            OnPropertyChanged(nameof(CanSetMonitorBrightness));
            OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
        }
    }

    private async Task<IReadOnlyList<T>> GetUserEntitiesAsync<T>(IpcCommand command)
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(command),
            _lifetime.Token);
        EnsureSuccess(response);
        return IpcJson.FromElement<IReadOnlyList<T>>(response.Payload) ?? [];
    }

    private async Task<T?> GetUserValueAsync<T>(IpcCommand command)
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(command),
            _lifetime.Token);
        EnsureSuccess(response);
        return IpcJson.FromElement<T>(response.Payload);
    }

    private async Task ScanGamesCoreAsync()
    {
        List<GameScanRoot> roots = [];
        Add(GameStoreKind.Steam, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        Add(GameStoreKind.Steam, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        Add(GameStoreKind.Epic, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        Add(GameStoreKind.Gog, @"C:\GOG Games");
        Add(GameStoreKind.MicrosoftXbox, @"C:\XboxGames");
        // Battle.net has no per-game manifest store; the scanner walks for NGDP
        // marker files with a bounded, access-safe search.
        Add(GameStoreKind.BattleNet, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net games"));
        Add(GameStoreKind.BattleNet, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        if (roots.Count == 0)
        {
            ShowNotice("No supported local game-library roots were found.", "Info");
            return;
        }

        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.ScanGames,
            (IReadOnlyList<GameScanRoot>)roots,
            expectedRevision: null,
            idempotencyKey: Guid.NewGuid().ToString("N"));
        IpcResponse response = await _userAgentClient.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        GameScanResult result = IpcJson.FromElement<GameScanResult>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty game scan result.");
        Replace(Games, await GetUserEntitiesAsync<GameEntryV1>(IpcCommand.GetGames));
        NotifyUserFeatureProperties();
        string warning = result.Warnings.Count == 0 ? string.Empty : $" {result.Warnings.Count} location(s) need review.";
        ShowNotice($"Indexed {result.Games.Count} local game(s).{warning}", result.Warnings.Count == 0 ? "Success" : "Warning");

        void Add(GameStoreKind store, string path)
        {
            if (Directory.Exists(path) && roots.All(root => !root.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(new GameScanRoot(store, path));
            }
        }
    }

    private void NotifyUserFeatureProperties()
    {
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(WorkflowCount));
        OnPropertyChanged(nameof(LightingSceneCount));
        OnPropertyChanged(nameof(EffectGraphCount));
        OnPropertyChanged(nameof(MacroCount));
        OnPropertyChanged(nameof(MacroRecordingSessionCount));
        OnPropertyChanged(nameof(ScriptCount));
        OnPropertyChanged(nameof(OsdLayoutCount));
        OnPropertyChanged(nameof(CapturePresetCount));
        OnPropertyChanged(nameof(CanCaptureDesktopSnapshot));
        OnPropertyChanged(nameof(CanSetMonitorBrightness));
        OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
        OnPropertyChanged(nameof(GameBundleSummary));
        _addLightingZoneCommand.RaiseCanExecuteChanged();
        _saveLightingLayoutCommand.RaiseCanExecuteChanged();
        _applyDynamicLightingSceneCommand.RaiseCanExecuteChanged();
        _testMacroCommand.RaiseCanExecuteChanged();
        NotifyMacroEditorProperties();
        _saveGameBundleCommand.RaiseCanExecuteChanged();
        _captureDesktopSnapshotCommand.RaiseCanExecuteChanged();
        _refreshMonitorBrightnessCommand.RaiseCanExecuteChanged();
        _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
    }

    private void NotifyMacroEditorProperties()
    {
        OnPropertyChanged(nameof(CanAddMacroKeyPress));
        OnPropertyChanged(nameof(CanRemoveMacroKeyPress));
        OnPropertyChanged(nameof(CanSaveMacroEdit));
        _addMacroKeyPressCommand.RaiseCanExecuteChanged();
        _removeMacroKeyPressCommand.RaiseCanExecuteChanged();
        _saveMacroEditCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshOperationStatusAsync(CancellationToken cancellationToken)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetOperationStatus),
            cancellationToken);
        if (!response.Success)
        {
            bool olderService = string.Equals(response.ErrorCode, "NOT_IMPLEMENTED", StringComparison.Ordinal)
                || (string.Equals(response.ErrorCode, "IPC_ERROR", StringComparison.Ordinal)
                    && response.Error?.Contains("IpcRequest", StringComparison.OrdinalIgnoreCase) == true);
            if (olderService)
            {
                _operation = null;
                NotifyOperationProperties();
                return;
            }

            EnsureSuccess(response);
        }

        _operation = IpcJson.FromElement<HardwareOperationStatus>(response.Payload);
        NotifyOperationProperties();
    }

    private async Task RefreshAutomationRulesAsync(CancellationToken cancellationToken)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetAutomationRules),
            cancellationToken);
        if (!response.Success)
        {
            bool olderService = string.Equals(response.ErrorCode, "NOT_IMPLEMENTED", StringComparison.Ordinal)
                || (string.Equals(response.ErrorCode, "IPC_ERROR", StringComparison.Ordinal)
                    && response.Error?.Contains("IpcRequest", StringComparison.OrdinalIgnoreCase) == true);
            if (olderService)
            {
                _automationServiceSupported = false;
                Replace(AutomationRules, []);
                AutomationStatus = "The connected service predates automation-rule storage. Upgrade the service to use this page.";
                NotifyAutomationProperties();
                return;
            }

            EnsureSuccess(response);
        }

        IReadOnlyList<AutomationRuleV1> rules = IpcJson.FromElement<IReadOnlyList<AutomationRuleV1>>(response.Payload) ?? [];
        _automationServiceSupported = true;
        Replace(AutomationRules, rules
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .Select(AutomationRuleDisplay.From));
        NotifyAutomationProperties();
    }

    private async Task RefreshOwnershipAsync(CancellationToken cancellationToken)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetOwnership),
            cancellationToken);
        if (!response.Success)
        {
            OwnershipStatus = "The connected service does not expose the ownership protocol.";
            _ownershipOverview = null;
            NotifyOwnershipProperties();
            return;
        }

        _ownershipOverview = IpcJson.FromElement<OwnershipOverview>(response.Payload)
            ?? new OwnershipOverview([], [], [], null);
        UpdateStateRevision(response);
        TakeoverExecutionStatusV1? executor = _ownershipOverview.ExecutorStatus;
        OwnershipStatus = executor is null
            ? "Ownership preview is available; this service predates production executor status reporting."
            : executor.CanExecute
                ? $"{_ownershipOverview.Consents.Count} stored exact consent(s); {executor.Message}"
                : $"{_ownershipOverview.Consents.Count} stored exact consent(s). Automatic takeover is blocked: {executor.Message}";
        NotifyOwnershipProperties();
    }

    private async Task RefreshFromLocalAdaptersAsync(bool force)
    {
        if (!force && _snapshot is not null)
        {
            DataSourceLabel = LocalProbeLabel;
            OnPropertyChanged(nameof(ServiceStateLabel));
            OnPropertyChanged(nameof(ConnectionTone));
            return;
        }

        try
        {
            _localCoordinator ??= new AdapterCoordinator(
            [
                new SystemInventoryAdapter(),
                new WindowsPowerAdapter(),
                new NvmlTelemetryAdapter(),
                new IntelGraphicsControlAdapter(),
                new AmdGraphicsControlAdapter(),
                new VendorControlEligibilityAdapter(),
                new WindowsPeripheralInventoryAdapter(),
                new LibreHardwareMonitorAdapter()
            ]);
            AdapterCoordinator coordinator = _localCoordinator;
            CancellationToken cancellationToken = _lifetime.Token;
            HardwareSnapshot localSnapshot = await Task.Run(
                () => coordinator.CaptureAsync(cancellationToken),
                cancellationToken);

            // This continuation deliberately resumes on the WPF Dispatcher. The
            // synchronous WMI/native probe above runs entirely on a worker thread;
            // only its completed immutable snapshot reaches UI-bound state.
            _snapshot = localSnapshot;
            _lastLocalProbe = DateTimeOffset.UtcNow;
            DataSourceLabel = LocalProbeLabel;
            if (Profiles.Count == 0)
            {
                Replace(Profiles, BuiltInProfiles.Create());
                NewRuleProfile = Profiles.FirstOrDefault(profile => profile.Id == "balanced") ?? Profiles.FirstOrDefault();
            }

            UpdateDisplays();
            LastUpdatedText = $"{LocalProbeLabel} {DateTimeOffset.Now:HH:mm:ss}";
            SafetySummary = IsPortableMode
                ? Localization.L10n.Get("Portable_SafetySummary")
                : "The service is unavailable. Monitoring is local and read-only; no hardware writes can be issued.";
            SafetyTone = "Warning";
            OnPropertyChanged(nameof(ServiceStateLabel));
            OnPropertyChanged(nameof(ConnectionTone));
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            DataSourceLabel = "Unavailable";
            SafetySummary = $"The service is unavailable and the local probe failed: {exception.Message}";
            SafetyTone = "Critical";
            LastUpdatedText = "No hardware data available";
        }
    }

    private static string DescribeServiceFailure(Exception exception) => exception switch
    {
        TimeoutException or FileNotFoundException => "The service is not running; showing local read-only data.",
        UnauthorizedAccessException => "Service access was denied. Sign out and back in to refresh the installed operator-group membership.",
        IOException => "The service connection was interrupted; showing local read-only data.",
        _ => exception.Message
    };

    private void DisposeLocalCoordinator()
    {
        if (_localCoordinator is not AdapterCoordinator coordinator)
        {
            return;
        }

        _localCoordinator = null;
        _ = coordinator.DisposeAsync().AsTask();
    }

    private void RefreshDesktopOsd()
    {
        if (!IsDesktopOsdVisible || _snapshot is null)
        {
            return;
        }
        try
        {
            OsdLayoutV1 layout = ResolveDesktopOsdLayout();
            OsdPresentationSettingsV1 presentation = TryBuildOsdPresentationSettings(out OsdPresentationSettingsV1 settings)
                ? settings
                : _osdPresentationSettings;
            _desktopOsd.Update(layout, _snapshot.Sensors, presentation);
        }
        catch (Exception exception)
        {
            _desktopOsd.Close();
            DesktopOsdStatus = $"Desktop OSD stopped safely: {exception.Message}";
            NotifyDesktopOsdProperties();
        }
    }

    private void NotifyDesktopOsdProperties()
    {
        OnPropertyChanged(nameof(IsDesktopOsdVisible));
        OnPropertyChanged(nameof(CanShowDesktopOsd));
        _showDesktopOsdCommand.RaiseCanExecuteChanged();
        _hideDesktopOsdCommand.RaiseCanExecuteChanged();
    }

    private void NotifyOsdPresentationProperties()
    {
        OnPropertyChanged(nameof(CanSaveOsdPresentation));
        OnPropertyChanged(nameof(CanShowDesktopOsd));
        _saveOsdPresentationCommand.RaiseCanExecuteChanged();
        _showDesktopOsdCommand.RaiseCanExecuteChanged();
    }

    private void NotifyHealthRuleProperties()
    {
        OnPropertyChanged(nameof(CanSaveHealthRule));
        OnPropertyChanged(nameof(CanAddRecommendedHealthRules));
        _saveHealthRuleCommand.RaiseCanExecuteChanged();
        _addRecommendedHealthRulesCommand.RaiseCanExecuteChanged();
    }

    private void NotifyMonitoringComparisonProperties()
    {
        OnPropertyChanged(nameof(MonitoringComparisonSensorCount));
        OnPropertyChanged(nameof(CanAddMonitoringComparisonSensor));
        OnPropertyChanged(nameof(CanSaveMonitoringComparisonLayout));
        _addMonitoringComparisonSensorCommand.RaiseCanExecuteChanged();
        _removeMonitoringComparisonSensorCommand.RaiseCanExecuteChanged();
        _saveMonitoringComparisonLayoutCommand.RaiseCanExecuteChanged();
    }

    private void NotifyReliabilityProperties()
    {
        OnPropertyChanged(nameof(ActiveHealthAlertCount));
        OnPropertyChanged(nameof(HasHealthAlerts));
        OnPropertyChanged(nameof(HasTimelineEvents));
        OnPropertyChanged(nameof(HasVisibleTimelineEvents));
        OnPropertyChanged(nameof(TimelineFilterSummary));
        OnPropertyChanged(nameof(HasVisibleTimelineEvents));
        OnPropertyChanged(nameof(TimelineFilterSummary));
        OnPropertyChanged(nameof(IsSafeModeEnabled));
        OnPropertyChanged(nameof(SafeModeLabel));
        OnPropertyChanged(nameof(SafetyRecoveryGuidance));
        OnPropertyChanged(nameof(TuningQualificationPlans));
        OnPropertyChanged(nameof(LightingQualificationPlans));
        OnPropertyChanged(nameof(WgcRecordingStatus));
        OnPropertyChanged(nameof(IsWgcRecordingReady));
        _deleteHealthRuleCommand.RaiseCanExecuteChanged();
        _acknowledgeHealthAlertCommand.RaiseCanExecuteChanged();
        _enableSafeModeCommand.RaiseCanExecuteChanged();
        _disableSafeModeCommand.RaiseCanExecuteChanged();
        NotifyAutomationProperties();
        NotifyHealthRuleProperties();
    }

    private static string OsdFormatFor(string unit) => unit switch
    {
        "RPM" or "%" => "0",
        "W" => "F1",
        "MHz" => "0",
        _ => "F1"
    };

    private static string OsdColourFor(string unit) => unit switch
    {
        "°C" => "#FFB26B",
        "RPM" => "#7EE7C5",
        "W" => "#8EC5FF",
        "%" => "#D5AEFF",
        _ => "#EAF1FA"
    };

    private void UpdateStateRevision(IpcResponse response)
    {
        if (_status is not null)
        {
            _status = _status with { StateRevision = response.StateRevision };
            OnPropertyChanged(nameof(StateRevisionText));
        }
    }

    private static bool TryParseDouble(string value, out double result) =>
        double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture,
            out result)
        || double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);

    private void UpdateDisplays()
    {
        if (_snapshot is null)
        {
            return;
        }

        // CollectionChanged drives both WPF item generation and the ScottPlot
        // comparison redraw. Hold every high-frequency collection until the
        // complete snapshot has been reconciled, then publish at most one Reset
        // per changed collection. Keep the plot collection first so the reverse
        // disposal order refreshes it after the supporting lists are current.
        using IDisposable collectionBatch = CollectionNotificationBatch.Defer(
            MonitoringComparisonSeries,
            MonitoringTrends,
            VisibleMonitoringTrends,
            ImportantSensors,
            CoolingSensors,
            PerformanceSensors,
            ProfileCards,
            CoolingCapabilities,
            PerformanceCapabilities,
            CapabilityDecisions,
            ExperimentalControls,
            Devices,
            Diagnostics,
            AdapterHealth,
            CalibrationTargets,
            TuneTargets,
            RgbRouteAssessments);

        UpdateMonitoringTrends();
        RebuildGpuControlSliders();
        SynchronizeAutomaticCoolingSelection();
        ActiveProfileName = Profiles.FirstOrDefault(profile => profile.Id == _status?.ActiveProfileId)?.Name ?? "None";
        Replace(ProfileCards, Profiles.Select(profile =>
        {
            _suiteProfilesById.TryGetValue(profile.Id, out ProfileV2? suiteProfile);
            _autoOcValidationsByProfileId.TryGetValue(profile.Id, out AutoOcProfileValidationV1? validation);
            return ProfileCardDisplay.From(profile, profile.Id == _status?.ActiveProfileId, suiteProfile, validation);
        }), card => card.Profile.Id, StringComparer.Ordinal);

        Replace(ImportantSensors, SelectImportantSensors(_snapshot).Select(sensor => new SensorDisplay(
            sensor.Name,
            FindDevice(sensor.DeviceId),
            FormatSensorValue(sensor),
            TemperatureSeverity(sensor),
            SensorGlyph(sensor.Unit))), sensor => (sensor.Device, sensor.Name));
        Replace(CoolingSensors, SelectCoolingSensors(_snapshot).Select(sensor => new SensorDisplay(
            sensor.Name,
            FindDevice(sensor.DeviceId),
            FormatSensorValue(sensor),
            TemperatureSeverity(sensor),
            SensorGlyph(sensor.Unit))), sensor => (sensor.Device, sensor.Name));
        Replace(PerformanceSensors, SelectPerformanceSensors(_snapshot).Select(sensor => new SensorDisplay(
            sensor.Name,
            FindDevice(sensor.DeviceId),
            FormatSensorValue(sensor),
            TemperatureSeverity(sensor),
            SensorGlyph(sensor.Unit))), sensor => (sensor.Device, sensor.Name));

        _allDevices.Clear();
        _allDevices.AddRange(_snapshot.Devices
            .OrderBy(device => DeviceRank(device.Kind))
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(DeviceDisplay.From));
        ApplyDeviceFilter();

        Replace(CoolingCapabilities, _snapshot.Capabilities
            .Where(IsCoolingCapability)
            .Where(capability => !IsInformationalDuplicate(capability, _snapshot.Capabilities))
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CapabilityDisplay.From), capability => capability.Id, StringComparer.Ordinal);
        Replace(PerformanceCapabilities, _snapshot.Capabilities
            .Where(capability => !IsCoolingCapability(capability) && capability.Domain != ControlDomain.Lighting)
            .Where(capability => !IsInformationalDuplicate(capability, _snapshot.Capabilities))
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CapabilityDisplay.From), capability => capability.Id, StringComparer.Ordinal);
        Replace(CapabilityDecisions, _snapshot.Capabilities
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Domain)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CapabilityDisplay.From), capability => capability.Id, StringComparer.Ordinal);
        UpdateOperationTargets();
        RebuildExperimentalControlCenter();

        List<DiagnosticDisplay> diagnostics = _snapshot.Warnings.Select(DiagnosticDisplay.From).ToList();
        diagnostics.AddRange(_snapshot.Conflicts.Where(conflict => conflict.IsRunning).Select(DiagnosticDisplay.From));
        Replace(
            Diagnostics,
            diagnostics.OrderBy(DiagnosticDisplay.Rank).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            diagnostic => diagnostic.Title,
            StringComparer.Ordinal);
        RebuildHardwareOwnership();
        Replace(AdapterHealth, _snapshot.AdapterHealth
            .OrderBy(health => health.Healthy ? 1 : 0)
            .ThenBy(health => health.AdapterId, StringComparer.OrdinalIgnoreCase)
            .Select(AdapterHealthDisplay.From), health => health.Name, StringComparer.Ordinal);
        if (OpenRgbEnabled && HasBroadLightingConflict)
        {
            OpenRgbStatus = LightingConflictReason;
        }

        RebuildRgbRouteAssessments();

        UpdateSafetySummary();

        RefreshDesktopOsd();
        RefreshRtssOsdPublish();
        NotifySnapshotProperties();
    }

    private void RebuildExperimentalControlCenter()
    {
        CapabilityDescriptor[] experimental = _snapshot?.Capabilities
            .Where(capability => capability.State == CapabilityAccessState.Experimental)
            .OrderBy(capability => IsCoolingCapability(capability) ? 0 : 1)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
        Replace(
            ExperimentalControls,
            experimental.Select(capability => ExperimentalControlDisplay.From(
                capability,
                FindDevice(capability.DeviceId),
                GetCoolingOutputAssignment(capability),
                CanUseServiceWrites,
                AdvancedWritesAcknowledged)),
            control => control.Descriptor.Id,
            StringComparer.Ordinal);
        NotifyExperimentalControlProperties();
    }

    private void NotifyExperimentalControlProperties()
    {
        OnPropertyChanged(nameof(ExperimentalControlCount));
        OnPropertyChanged(nameof(ExperimentalCoolingControlCount));
        OnPropertyChanged(nameof(CommissioningEligibleExperimentalControlCount));
        OnPropertyChanged(nameof(ProtectedExperimentalControlCount));
        OnPropertyChanged(nameof(HasExperimentalControls));
        OnPropertyChanged(nameof(ExperimentalControlSummary));
        OnPropertyChanged(nameof(ExperimentalGateBadge));
        OnPropertyChanged(nameof(ExperimentalGateSummary));
        OnPropertyChanged(nameof(ExperimentalAcknowledgementDetail));
    }

    /// <summary>
    /// Selects a bounded cooling control for the existing commissioning wizard.
    /// This method is intentionally a navigation aid only: it never writes a
    /// controller, acknowledges the device, or changes a persisted safety role.
    /// </summary>
    public bool SelectExperimentalCoolingControl(ExperimentalControlDisplay control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!control.CanOpenCoolingCommissioning)
        {
            ShowNotice(control.NextSafeStep, control.IsProtected ? "Warning" : "Info");
            return false;
        }

        OperationTargetDisplay? target = CalibrationTargets.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, control.Descriptor.Id, StringComparison.Ordinal));
        if (target is null)
        {
            ShowNotice("This Experimental control is no longer present in the cooling-target list. Refresh hardware discovery before commissioning.", "Warning");
            return false;
        }

        SelectedCalibrationTarget = target;
        ShowNotice(
            $"Selected {target.DisplayName} for commissioning. No hardware command was sent; enter the physical header and complete the two acknowledgements in Cooling.",
            "Info");
        return true;
    }

    private void UpdateSafetySummary()
    {
        if (IsServiceOnline && !CanUseServiceWrites)
        {
            SafetySummary = $"Service writes are locked: {ServiceCompatibilityMessage}";
            SafetyTone = "Warning";
        }
        else if (_status?.EmergencyMode == true)
        {
            SafetySummary = "Emergency mode is active because rollback recovery is incomplete. Further hardware writes are blocked.";
            SafetyTone = "Critical";
        }
        else if (IsSafeModeEnabled)
        {
            SafetySummary = "Safe mode is active. Automation and health-rule profile requests are suspended until recovery is reviewed.";
            SafetyTone = "Warning";
        }
        else if (ExperimentalControlCount > 0)
        {
            SafetySummary = AdvancedWritesAcknowledged
                ? $"{ExperimentalControlCount} Experimental control{(ExperimentalControlCount == 1 ? string.Empty : "s")} detected. Session acknowledgement is recorded, but every action still needs an exact-device confirmation, commissioning evidence, and hardware qualification. {ProtectedExperimentalControlCount} protected output{(ProtectedExperimentalControlCount == 1 ? string.Empty : "s")} remain blocked."
                : $"{ExperimentalControlCount} Experimental control{(ExperimentalControlCount == 1 ? string.Empty : "s")} detected. No hardware action is authorised until a session acknowledgement and exact-device confirmation are recorded; CPU-fan and pump protections remain in force.";
            SafetyTone = "Warning";
        }
        else
        {
            SafetySummary = "Unqualified hardware controls are locked. Monitoring and Verified Windows controls remain available.";
            SafetyTone = "Safe";
        }
    }

    private void UpdateMonitoringTrends()
    {
        if (_snapshot is null)
        {
            Replace(MonitoringTrends, []);
            Replace(VisibleMonitoringTrends, []);
            Replace(MonitoringComparisonSeries, []);
            UpdateHealthRecommendationStatus();
            NotifyMonitoringComparisonProperties();
            OnPropertyChanged(nameof(MonitoringTrendFilterSummary));
            OnPropertyChanged(nameof(HasVisibleMonitoringTrends));
            return;
        }

        foreach (SensorSample sample in _snapshot.Sensors.Where(sample => sample.Value is double value && double.IsFinite(value)))
        {
            if (!_liveSensorHistory.TryGetValue(sample.SensorId, out Queue<SensorSample>? history))
            {
                history = new Queue<SensorSample>();
                _liveSensorHistory.Add(sample.SensorId, history);
            }

            // The service already retains the authoritative history. This local
            // rolling view is deliberately small and exists only for a smooth
            // dashboard trend while the page is open.
            history.Enqueue(sample);
            while (history.Count > 48)
            {
                history.Dequeue();
            }
        }

        string? selectedId = SelectedMonitoringTrend?.SensorId;
        string? healthSelectedId = SelectedHealthTrend?.SensorId;
        IReadOnlyList<SensorTrendV1> trends = MonitoringWorkspace.BuildTrends(
            _liveSensorHistory.Values.SelectMany(history => history).ToArray(),
            _monitoringPreferences,
            maximumPoints: 24);
        Replace(MonitoringTrends, trends.Take(32).Select(trend => SensorTrendDisplay.From(trend)
            .WithPinned(_monitoringPreferences.PinnedSensorIds.Contains(trend.SensorId, StringComparer.Ordinal))),
            trend => trend.SensorId,
            StringComparer.Ordinal);
        SelectedMonitoringTrend = MonitoringTrends.FirstOrDefault(item => item.SensorId == selectedId)
            ?? MonitoringTrends.FirstOrDefault(item => item.IsPinned)
            ?? MonitoringTrends.FirstOrDefault();
        SelectedHealthTrend = MonitoringTrends.FirstOrDefault(item => item.SensorId == healthSelectedId)
            ?? SelectedMonitoringTrend;
        ApplyMonitoringTrendFilter();
        UpdateMonitoringComparison();
        UpdateHealthRecommendationStatus();
        OnPropertyChanged(nameof(CanSaveMonitoringPreferences));
        _saveMonitoringPreferencesCommand.RaiseCanExecuteChanged();
        NotifyHealthRuleProperties();
    }

    private void ApplyMonitoringTrendFilter()
    {
        HashSet<string> included = MonitoringWorkspace.FilterTrends(
                MonitoringTrends.Select(item => item.Trend).ToArray(),
                SelectedMonitoringTrendScope,
                _monitoringPreferences.PinnedSensorIds)
            .Select(item => item.SensorId)
            .ToHashSet(StringComparer.Ordinal);
        Replace(
            VisibleMonitoringTrends,
            MonitoringTrends.Where(item => included.Contains(item.SensorId)),
            trend => trend.SensorId,
            StringComparer.Ordinal);
        OnPropertyChanged(nameof(MonitoringTrendScopeLabel));
        OnPropertyChanged(nameof(MonitoringTrendFilterSummary));
        OnPropertyChanged(nameof(HasVisibleMonitoringTrends));
    }

    private void UpdateMonitoringComparison()
    {
        string? selectedId = SelectedMonitoringComparisonTrend?.SensorId;
        Dictionary<string, SensorTrendDisplay> trendsById = MonitoringTrends
            .GroupBy(trend => trend.SensorId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        string[] selectedIds = (_monitoringComparisonLayout.SensorIds ?? [])
            .Where(id => trendsById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        Replace(MonitoringComparisonSeries, selectedIds
            .Select((id, index) => SensorComparisonSeriesDisplay.From(trendsById[id], index)),
            series => series.SensorId,
            StringComparer.Ordinal);
        HashSet<string> selected = new(selectedIds, StringComparer.Ordinal);
        SelectedMonitoringComparisonTrend = MonitoringTrends.FirstOrDefault(trend => trend.SensorId == selectedId)
            ?? MonitoringTrends.FirstOrDefault(trend => !selected.Contains(trend.SensorId))
            ?? MonitoringTrends.FirstOrDefault();
        if (MonitoringComparisonSeries.Count > 0
            && !MonitoringComparisonStatus.StartsWith("Comparison changed locally", StringComparison.Ordinal))
        {
            MonitoringComparisonStatus = $"{MonitoringComparisonSeries.Count} normalized series selected. Compare movement; read native values and units in the legend.";
        }
        else if (MonitoringComparisonSeries.Count == 0
            && !MonitoringComparisonStatus.StartsWith("Comparison changed locally", StringComparison.Ordinal))
        {
            MonitoringComparisonStatus = "Choose up to four live sensors to compare recent movement. Native values remain visible in the legend.";
        }
        NotifyMonitoringComparisonProperties();
    }

    private void UpdateHealthRecommendationStatus()
    {
        IReadOnlyList<HealthRuleRecommendation> recommendations = HealthRuleRecommendations.Build(
            MonitoringTrends.Select(trend => trend.Trend).ToArray());
        int pending = recommendations.Count(recommendation => !HealthRules.Any(existing => SameHealthRule(existing.Rule, recommendation.Rule)));
        if (pending > 0)
        {
            HealthRecommendationStatus = $"{pending} conservative notify-only baseline rule(s) are available for the currently detected sensors and Windows event log.";
        }
        else if (recommendations.Count > 0)
        {
            HealthRecommendationStatus = "All currently applicable notify-only baseline rules are installed.";
        }
        else
        {
            HealthRecommendationStatus = "No suitable live temperature or pump sensors are available yet. Windows event rules will appear after the next service refresh.";
        }
        NotifyHealthRuleProperties();
    }

    private void UpdateTimeline()
    {
        List<TimelineEventDisplay> entries = [];
        entries.AddRange(HealthAlerts.Select(TimelineEventDisplay.From));
        entries.AddRange(AdapterTrace.Take(24).Select(TimelineEventDisplay.From));
        if (_snapshot is not null)
        {
            entries.AddRange(_snapshot.Conflicts
                .Where(conflict => conflict.IsRunning)
                .Select(conflict => new TimelineEventDisplay(
                    DateTimeOffset.UtcNow,
                    "Conflict",
                    conflict.DisplayName,
                    conflict.Guidance,
                    "Warning")));
        }
        if (!string.Equals(ActiveProfileName, "None", StringComparison.OrdinalIgnoreCase))
        {
            entries.Add(new TimelineEventDisplay(
                _status?.StartedAt ?? DateTimeOffset.UtcNow,
                "Profile",
                ActiveProfileName,
                "Current service profile.",
                "Info"));
        }
        Replace(TimelineEvents, entries
            .OrderByDescending(item => item.When)
            .Take(64));
        ApplyTimelineFilter();
        OnPropertyChanged(nameof(HasTimelineEvents));
    }

    private void ApplyTimelineFilter()
    {
        IEnumerable<TimelineEventDisplay> filtered = SelectedTimelineScope switch
        {
            TimelineScope.All => TimelineEvents,
            TimelineScope.Health => TimelineEvents.Where(item => string.Equals(item.Source, "Health", StringComparison.OrdinalIgnoreCase)),
            TimelineScope.Profile => TimelineEvents.Where(item => string.Equals(item.Source, "Profile", StringComparison.OrdinalIgnoreCase)),
            TimelineScope.Conflict => TimelineEvents.Where(item => string.Equals(item.Source, "Conflict", StringComparison.OrdinalIgnoreCase)),
            TimelineScope.Adapter => TimelineEvents.Where(item => string.Equals(item.Source, "Adapter", StringComparison.OrdinalIgnoreCase)),
            _ => []
        };
        Replace(VisibleTimelineEvents, filtered);
        OnPropertyChanged(nameof(TimelineScopeLabel));
        OnPropertyChanged(nameof(TimelineFilterSummary));
        OnPropertyChanged(nameof(HasVisibleTimelineEvents));
    }

    private void UpdateOperationTargets()
    {
        if (_snapshot is null)
        {
            return;
        }

        string? calibrationId = SelectedCalibrationTarget?.Descriptor.Id;
        string? tuneId = SelectedTuneTarget?.Descriptor.Id;
        OperationTargetDisplay[] calibrationTargets = _snapshot.Capabilities
            .Where(IsCoolingCapability)
            .Where(capability => capability.ValueKind == ControlValueKind.Numeric && capability.Range is not null)
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(capability => OperationTargetDisplay.From(
                capability,
                FindDevice(capability.DeviceId),
                FindMatchingRpmSensor(capability)))
            .ToArray();
        OperationTargetDisplay[] tuneTargets = _snapshot.Capabilities
            .Where(capability => capability.Domain is ControlDomain.Cooling or ControlDomain.Cpu or ControlDomain.Gpu)
            .Where(capability => capability.ValueKind == ControlValueKind.Numeric
                && capability.Range is not null
                && !capability.Name.Contains("voltage", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(capability.Unit, "V", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(capability.Unit, "mV", StringComparison.OrdinalIgnoreCase))
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(capability => OperationTargetDisplay.From(capability, FindDevice(capability.DeviceId), null))
            .ToArray();
        Replace(CalibrationTargets, calibrationTargets, target => target.Descriptor.Id, StringComparer.Ordinal);
        Replace(TuneTargets, tuneTargets, target => target.Descriptor.Id, StringComparer.Ordinal);

        OperationTargetDisplay? nextCalibration = calibrationTargets.FirstOrDefault(
            target => string.Equals(target.Descriptor.Id, calibrationId, StringComparison.Ordinal))
            ?? calibrationTargets.FirstOrDefault(target => target.IsAvailable)
            ?? calibrationTargets.FirstOrDefault();
        OperationTargetDisplay? nextTune = tuneTargets.FirstOrDefault(
            target => string.Equals(target.Descriptor.Id, tuneId, StringComparison.Ordinal))
            ?? tuneTargets.FirstOrDefault(target => target.IsAvailable)
            ?? tuneTargets.FirstOrDefault();
        if (!string.Equals(SelectedCalibrationTarget?.Descriptor.Id, nextCalibration?.Descriptor.Id, StringComparison.Ordinal))
        {
            SelectedCalibrationTarget = nextCalibration;
        }
        else
        {
            _selectedCalibrationTarget = nextCalibration;
            OnPropertyChanged(nameof(SelectedCalibrationTarget));
        }

        if (!string.Equals(SelectedTuneTarget?.Descriptor.Id, nextTune?.Descriptor.Id, StringComparison.Ordinal))
        {
            SelectedTuneTarget = nextTune;
        }
        else
        {
            _selectedTuneTarget = nextTune;
            OnPropertyChanged(nameof(SelectedTuneTarget));
        }

        NotifyOperationEligibility();
    }

    private string? FindMatchingRpmSensor(CapabilityDescriptor capability)
    {
        if (_snapshot is null)
        {
            return null;
        }

        SensorSample[] candidates = _snapshot.Sensors
            .Where(sensor => string.Equals(sensor.AdapterId, capability.AdapterId, StringComparison.Ordinal)
                && string.Equals(sensor.DeviceId, capability.DeviceId, StringComparison.Ordinal)
                && string.Equals(sensor.Unit, "RPM", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        SensorSample? exactName = candidates.FirstOrDefault(
            sensor => string.Equals(sensor.Name, capability.Name, StringComparison.OrdinalIgnoreCase));
        if (exactName is not null)
        {
            return exactName.SensorId;
        }

        string? controlIndex = IdentifierIndex(capability.Id, "/control/");
        SensorSample? exactIndex = controlIndex is null
            ? null
            : candidates.FirstOrDefault(sensor =>
                string.Equals(IdentifierIndex(sensor.SensorId, "/fan/"), controlIndex, StringComparison.Ordinal));
        return exactIndex?.SensorId ?? (candidates.Length == 1 ? candidates[0].SensorId : null);
    }

    private static string? IdentifierIndex(string value, string marker)
    {
        int start = value.LastIndexOf(marker, StringComparison.Ordinal);
        return start < 0 ? null : value[(start + marker.Length)..];
    }

    private void ApplyDeviceFilter()
    {
        IEnumerable<DeviceDisplay> filtered = _allDevices;
        string query = DeviceSearchText.Trim();
        if (query.Length > 0)
        {
            filtered = filtered.Where(device => device.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        Replace(Devices, filtered, device => device.Id, StringComparer.Ordinal);
        DeviceResultSummary = _allDevices.Count == 0
            ? "No inventory loaded"
            : string.IsNullOrEmpty(query)
                ? $"{Devices.Count} devices"
                : $"{Devices.Count} of {_allDevices.Count} devices";
        string[] compatibleFamilies = _allDevices
            .Select(device => device.CompatibilityLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        int totalCompatibleFamilies = _allDevices
            .Select(device => device.CompatibilityLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        DeviceCompatibilitySummary = compatibleFamilies.Length == 0
            ? "No classified desktop families detected"
            : totalCompatibleFamilies > compatibleFamilies.Length
                ? $"{string.Join(" · ", compatibleFamilies)} +{totalCompatibleFamilies - compatibleFamilies.Length}"
                : string.Join(" · ", compatibleFamilies);
        OnPropertyChanged(nameof(HasDevices));
    }

    private void NotifySnapshotProperties()
    {
        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(SensorCount));
        OnPropertyChanged(nameof(VerifiedControlCount));
        OnPropertyChanged(nameof(ReadOnlyControlCount));
        OnPropertyChanged(nameof(RestrictedControlCount));
        OnPropertyChanged(nameof(BlockedOrUnsupportedControlCount));
        OnPropertyChanged(nameof(ResettableVerifiedControlCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(RunningConflictCount));
        OnPropertyChanged(nameof(HasRunningConflicts));
        OnPropertyChanged(nameof(HasRunningGpuConflicts));
        OnPropertyChanged(nameof(HasRunningFanConflicts));
        OnPropertyChanged(nameof(CloseBlockersLabel));
        OnPropertyChanged(nameof(CloseConflictingAppsLabel));
        OnPropertyChanged(nameof(RunningConflictSummary));
        OnPropertyChanged(nameof(RunningGpuConflictSummary));
        OnPropertyChanged(nameof(RunningFanConflictSummary));
        _closeBlockersCommand.RaiseCanExecuteChanged();
        _closeConflictingAppsCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasImportantSensors));
        OnPropertyChanged(nameof(HasCoolingSensors));
        OnPropertyChanged(nameof(HasPerformanceSensors));
        OnPropertyChanged(nameof(HasCoolingCapabilities));
        OnPropertyChanged(nameof(HasPerformanceCapabilities));
        OnPropertyChanged(nameof(HasCapabilityDecisions));
        OnPropertyChanged(nameof(CapabilityDecisionSummary));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasAdapterHealth));
        OnPropertyChanged(nameof(HasCalibrationTargets));
        OnPropertyChanged(nameof(HasTuneTargets));
        OnPropertyChanged(nameof(CalibrationAvailabilityLabel));
        OnPropertyChanged(nameof(TuneAvailabilityLabel));
        OnPropertyChanged(nameof(CalibrationAvailabilityTone));
        OnPropertyChanged(nameof(TuneAvailabilityTone));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(WriteStateLabel));
        OnPropertyChanged(nameof(WriteStateTone));
        OnPropertyChanged(nameof(ServiceVersion));
        OnPropertyChanged(nameof(StateRevisionText));
        OnPropertyChanged(nameof(ServiceUptimeText));
        NotifyOpenRgbProperties();
        NotifyAutomationProperties();
        _applyProfileCommand.RaiseCanExecuteChanged();
        _resetVerifiedCommand.RaiseCanExecuteChanged();
        NotifyDesktopOsdProperties();
        NotifyOperationEligibility();
    }

    private void NotifyOperationEligibility()
    {
        OnPropertyChanged(nameof(CalibrationEligibilityReason));
        OnPropertyChanged(nameof(TuneEligibilityReason));
        OnPropertyChanged(nameof(CanStartCalibration));
        OnPropertyChanged(nameof(CanStartTune));
        _startCalibrationCommand.RaiseCanExecuteChanged();
        _startTuneCommand.RaiseCanExecuteChanged();
        NotifyCommissioningProperties();
    }

    private void NotifyCommissioningProperties()
    {
        OnPropertyChanged(nameof(CommissioningStateLabel));
        OnPropertyChanged(nameof(CommissioningTargetSummary));
        OnPropertyChanged(nameof(CommissioningPreflight));
        OnPropertyChanged(nameof(CanBeginFanCommissioning));
        OnPropertyChanged(nameof(CanPulseFanCommissioning));
        OnPropertyChanged(nameof(CanConfirmFanCommissioning));
        OnPropertyChanged(nameof(CanCompleteFanCommissioning));
        OnPropertyChanged(nameof(CanCreateAdaptiveCoolingProfile));
        OnPropertyChanged(nameof(AdaptiveCoolingProfileEligibilityReason));
        NotifyCustomCoolingCurveProperties();
        OnPropertyChanged(nameof(CanCancelFanCommissioning));
        OnPropertyChanged(nameof(CanRecoverFanCommissioning));
        OnPropertyChanged(nameof(CanRunInteractiveFanPreflight));
        _beginFanCommissioningCommand.RaiseCanExecuteChanged();
        _pulseFanCommissioningCommand.RaiseCanExecuteChanged();
        _observeFanCommissioningCommand.RaiseCanExecuteChanged();
        _runInteractiveFanPreflightCommand.RaiseCanExecuteChanged();
        _confirmFanCommissioningCommand.RaiseCanExecuteChanged();
        _completeFanCommissioningCommand.RaiseCanExecuteChanged();
        _createAdaptiveCoolingProfileCommand.RaiseCanExecuteChanged();
        _cancelFanCommissioningCommand.RaiseCanExecuteChanged();
        _recoverFanCommissioningCommand.RaiseCanExecuteChanged();
    }

    private void NotifyCustomCoolingCurveProperties()
    {
        OnPropertyChanged(nameof(CanSaveCustomCoolingCurve));
        OnPropertyChanged(nameof(CustomCoolingCurveEligibilityReason));
        OnPropertyChanged(nameof(CustomCoolingCurvePreview));
        OnPropertyChanged(nameof(CustomCoolingCurvePreviewGeometry));
        OnPropertyChanged(nameof(CustomCoolingCurveHandles));
        OnPropertyChanged(nameof(CustomCoolingCurveAxisLabel));
        _saveCustomCoolingCurveCommand.RaiseCanExecuteChanged();
    }

    private void NotifyOperationProperties()
    {
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(HasActiveOperation));
        OnPropertyChanged(nameof(OperationTitle));
        OnPropertyChanged(nameof(OperationMessage));
        OnPropertyChanged(nameof(OperationError));
        OnPropertyChanged(nameof(HasOperationError));
        OnPropertyChanged(nameof(OperationProgress));
        OnPropertyChanged(nameof(OperationProgressText));
        OnPropertyChanged(nameof(HasCalibrationResult));
        OnPropertyChanged(nameof(CalibrationResultSummary));
        OnPropertyChanged(nameof(CalibrationOperatingEnvelopeSummary));
        OnPropertyChanged(nameof(CalibrationStabilitySummary));
        _abortOperationCommand.RaiseCanExecuteChanged();
        NotifyOperationEligibility();
        NotifyOwnershipProperties();
    }

    private void NotifyOpenRgbProperties()
    {
        OnPropertyChanged(nameof(HasLightingConflict));
        OnPropertyChanged(nameof(HasBroadLightingConflict));
        OnPropertyChanged(nameof(HasScopedLightingConflict));
        OnPropertyChanged(nameof(LightingConflictReason));
        OnPropertyChanged(nameof(HasDynamicLightingConflict));
        OnPropertyChanged(nameof(DynamicLightingConflictReason));
        OnPropertyChanged(nameof(OpenRgbConnectionLabel));
        OnPropertyChanged(nameof(AreOpenRgbInputsValid));
        OnPropertyChanged(nameof(CanSyncAllRgb));
        _probeOpenRgbCommand.RaiseCanExecuteChanged();
        _applyOpenRgbCommand.RaiseCanExecuteChanged();
        _turnOffOpenRgbCommand.RaiseCanExecuteChanged();
        _applyDynamicLightingSceneCommand.RaiseCanExecuteChanged();
        _syncAllRgbCommand?.RaiseCanExecuteChanged();
    }

    private void NotifyRgbRoutingProperties()
    {
        OnPropertyChanged(nameof(HasRgbRouteAssessments));
        OnPropertyChanged(nameof(RgbReadyRouteCount));
        OnPropertyChanged(nameof(RgbSetupRouteCount));
        OnPropertyChanged(nameof(RgbReadOnlyRouteCount));
        OnPropertyChanged(nameof(RgbBlockedRouteCount));
        OnPropertyChanged(nameof(HasReadyOpenRgbRoutes));
        OnPropertyChanged(nameof(HasReadyDynamicLightingRoutes));
        OnPropertyChanged(nameof(RgbCompatibilitySummary));
        _applyOpenRgbCommand.RaiseCanExecuteChanged();
        _turnOffOpenRgbCommand.RaiseCanExecuteChanged();
        _applyDynamicLightingSceneCommand.RaiseCanExecuteChanged();
        _syncAllRgbCommand?.RaiseCanExecuteChanged();
    }

    private void NotifyAutomationEditor()
    {
        OnPropertyChanged(nameof(CanAddAutomationRule));
        _addAutomationRuleCommand.RaiseCanExecuteChanged();
    }

    private void NotifyAutomationProperties()
    {
        OnPropertyChanged(nameof(HasAutomationRules));
        OnPropertyChanged(nameof(AutomationServiceSupported));
        OnPropertyChanged(nameof(HasManualOverride));
        OnPropertyChanged(nameof(ManualOverrideLabel));
        OnPropertyChanged(nameof(CanAddAutomationRule));
        (ResumeAutomationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _addAutomationRuleCommand.RaiseCanExecuteChanged();
        _deleteAutomationRuleCommand.RaiseCanExecuteChanged();
    }

    private bool HasStoredTakeoverConsent(TakeoverProcessIdentity target) => (_ownershipOverview?.Consents ?? [])
        .Any(consent => string.Equals(consent.ExecutablePath, target.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(consent.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(consent.ProductName, target.ProductName, StringComparison.Ordinal)
            && string.Equals(consent.Publisher, target.Publisher, StringComparison.Ordinal)
            && string.Equals(consent.SignerThumbprint ?? string.Empty, target.SignerThumbprint ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(consent.Sha256, target.Sha256, StringComparison.OrdinalIgnoreCase));

    private void NotifyOwnershipProperties()
    {
        OnPropertyChanged(nameof(TakeoverTargets));
        OnPropertyChanged(nameof(TakeoverPlanSummary));
        OnPropertyChanged(nameof(TakeoverExecutorStatus));
        OnPropertyChanged(nameof(ActiveOwnershipTransaction));
        OnPropertyChanged(nameof(CanExecuteTakeover));
        _previewTakeoverCommand.RaiseCanExecuteChanged();
        _grantTakeoverConsentCommand.RaiseCanExecuteChanged();
        _executeTakeoverCommand.RaiseCanExecuteChanged();
        _releaseOwnershipCommand.RaiseCanExecuteChanged();
    }

    private void NotifyImportProperties()
    {
        OnPropertyChanged(nameof(CanPreviewAfterburnerImport));
        OnPropertyChanged(nameof(CanPreviewFanControlImport));
        OnPropertyChanged(nameof(AfterburnerImportSummary));
        OnPropertyChanged(nameof(FanControlImportSummary));
        _previewAfterburnerImportCommand.RaiseCanExecuteChanged();
        _saveAfterburnerImportCommand.RaiseCanExecuteChanged();
        _previewFanControlImportCommand.RaiseCanExecuteChanged();
        _saveFanControlImportCommand.RaiseCanExecuteChanged();
    }

    private static Dictionary<string, string> ParseImportMappings(string input, string kind)
    {
        Dictionary<string, string> mappings = new(StringComparer.OrdinalIgnoreCase);
        string[] lines = input.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 256)
        {
            throw new InvalidOperationException($"At most 256 {kind} mappings may be imported at once.");
        }
        foreach (string line in lines)
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException($"Each {kind} mapping must use source=RigPilotId on its own line.");
            }
            string source = line[..separator].Trim();
            string target = line[(separator + 1)..].Trim();
            if (source.Length is 0 or > 512 || target.Length is 0 or > 512 || !mappings.TryAdd(source, target))
            {
                throw new InvalidOperationException($"{kind} mappings must have unique, bounded source and target IDs.");
            }
        }
        return mappings;
    }

    private int CountCapabilities(CapabilityAccessState state) =>
        _snapshot?.Capabilities.Count(capability => capability.State == state) ?? 0;

    private string FindDevice(string id) =>
        _snapshot?.Devices.FirstOrDefault(device => device.Id == id)?.Name ?? "Hardware";

    private static string FormatSensorValue(SensorSample sensor)
    {
        if (sensor.Value is not double value || !double.IsFinite(value))
        {
            return "Unavailable";
        }

        return $"{value:0.#} {NormaliseUnit(sensor.Unit)}";
    }

    private static string NormaliseUnit(string unit) => unit
        .Replace("\u00C2\u00B0C", "\u00B0C", StringComparison.Ordinal)
        .Replace("Celsius", "\u00B0C", StringComparison.OrdinalIgnoreCase);

    private static MonitoringComparisonLayoutV1 DefaultMonitoringComparisonLayout() => new(
        MonitoringComparisonLayoutV1.CurrentSchemaVersion,
        MonitoringComparisonLayoutV1.DefaultId,
        [],
        NormalizeEachSeries: true,
        DateTimeOffset.UtcNow);

    private static bool SameHealthRule(HealthRuleV1 left, HealthRuleV1 right) =>
        left.Condition == right.Condition
        && string.Equals(left.SensorId, right.SensorId, StringComparison.Ordinal)
        && Nullable.Equals(left.Threshold, right.Threshold)
        && left.Action == right.Action
        && string.Equals(left.EmergencyProfileId, right.EmergencyProfileId, StringComparison.Ordinal);

    private static string SplitWords(string value) => DisplayText.Humanize(value);

    private static string SensorGlyph(string unit) => NormaliseUnit(unit) switch
    {
        "\u00B0C" => "\uE9CA",
        "RPM" => "\uE9CA",
        "W" => "\uE945",
        "%" => "\uE9D2",
        _ => "\uE9D9"
    };

    private static bool IsCoolingCapability(CapabilityDescriptor capability) =>
        capability.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
        || capability.Name.Contains("fan", StringComparison.OrdinalIgnoreCase)
        || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase);

    private static int CapabilityRank(CapabilityDescriptor capability) => capability.State switch
    {
        CapabilityAccessState.Verified => 0,
        CapabilityAccessState.ReadOnly => 1,
        CapabilityAccessState.Experimental => 2,
        CapabilityAccessState.Blocked => 3,
        CapabilityAccessState.Faulted => 4,
        _ => 5
    };

    private static int ProfileDisplayRank(ProfileV1 profile) => profile.Id.ToLowerInvariant() switch
    {
        "quiet" => 0,
        "efficiency" => 1,
        "balanced" => 2,
        "performance" => 3,
        _ => 4
    };

    private static int DeviceRank(DeviceKind kind) => kind switch
    {
        DeviceKind.Cpu => 0,
        DeviceKind.Gpu => 1,
        DeviceKind.Motherboard => 2,
        DeviceKind.Bios => 3,
        DeviceKind.Memory => 4,
        DeviceKind.Storage => 5,
        DeviceKind.Cooling => 6,
        DeviceKind.Controller => 7,
        DeviceKind.Lighting => 8,
        DeviceKind.OperatingSystem => 9,
        DeviceKind.Network => 10,
        _ => 11
    };

    /// <summary>
    /// Picks a curated, deduplicated set of live readings: primary temperatures ordered by
    /// device importance, then fan speeds, then power draw. Static limits and implausible
    /// readings are excluded.
    /// </summary>
    private static IEnumerable<SensorSample> SelectImportantSensors(HardwareSnapshot snapshot)
    {
        Dictionary<string, HardwareDevice> devices = snapshot.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .ToDictionary(device => device.Id);

        List<SensorSample> candidates = snapshot.Sensors
            .Where(sensor => sensor.Quality == SensorQuality.Good && sensor.Value is double value && double.IsFinite(value))
            .Where(sensor => devices.ContainsKey(sensor.DeviceId))
            .Where(sensor => !sensor.Name.Contains("critical", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains("max", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains("limit", StringComparison.OrdinalIgnoreCase))
            .GroupBy(sensor => (sensor.DeviceId, sensor.Name))
            .Select(group => group.First())
            .ToList();

        int Rank(SensorSample sensor) => DeviceRank(devices[sensor.DeviceId].Kind);

        IEnumerable<SensorSample> temperatures = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "\u00B0C" && sensor.Value > 1 && sensor.Value < 130)
            .OrderBy(Rank)
            .ThenBy(sensor => SensorNameRank(sensor.Name))
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8);
        IEnumerable<SensorSample> fans = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "RPM" && sensor.Value > 0)
            .OrderBy(Rank)
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3);
        IEnumerable<SensorSample> power = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "W" && sensor.Value > 0)
            .OrderBy(Rank)
            .ThenBy(sensor => SensorNameRank(sensor.Name))
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3);
        return temperatures.Concat(fans).Concat(power);
    }

    /// <summary>
    /// Live read-only readings for the Simple Cooling page: spinning fans and
    /// pumps (RPM), their commanded duties (%), and liquid/coolant temperatures.
    /// </summary>
    // Fan sensor ids that have been observed spinning this session. A fan in
    // zero-RPM idle mode (the GPU fans stop when the card is cool) still reports 0,
    // so without this a filter of Value > 0 would drop it from the list and it
    // would flicker back the moment it spun up. Once seen spinning, a fan stays
    // listed at its live value — 0 while idle — which is stable and honest.
    private readonly HashSet<string> _seenSpinningFanIds = new(StringComparer.Ordinal);

    private IEnumerable<SensorSample> SelectCoolingSensors(HardwareSnapshot snapshot)
    {
        List<SensorSample> candidates = GoodSensors(snapshot);

        foreach (SensorSample sample in candidates)
        {
            if (NormaliseUnit(sample.Unit) == "RPM" && sample.Value > 0)
            {
                _seenSpinningFanIds.Add(sample.SensorId);
            }
        }

        // Every fan that is spinning now, plus every fan that has spun before and
        // is currently idling at 0 — so a zero-RPM fan holds its place instead of
        // vanishing. Headers that have never spun (empty motherboard connectors)
        // are still hidden. The generous cap fits every real fan on a big rig.
        IEnumerable<SensorSample> speeds = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "RPM"
                && (sensor.Value > 0 || _seenSpinningFanIds.Contains(sensor.SensorId)))
            .OrderBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(16);
        IEnumerable<SensorSample> duties = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "%"
                && (sensor.Name.Contains("fan", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("control", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(6);
        IEnumerable<SensorSample> liquid = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "°C" && sensor.Value > 1 && sensor.Value < 130
                && (sensor.Name.Contains("liquid", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("coolant", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("water", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(2);
        return liquid.Concat(speeds).Concat(duties);
    }

    /// <summary>
    /// Live read-only readings for the Simple Performance page: CPU/GPU
    /// utilisation, headline clocks, and power draw.
    /// </summary>
    private static IEnumerable<SensorSample> SelectPerformanceSensors(HardwareSnapshot snapshot)
    {
        Func<SensorSample, bool> cpuOrGpu = FromDeviceKinds(snapshot, DeviceKind.Cpu, DeviceKind.Gpu);
        List<SensorSample> candidates = [.. GoodSensors(snapshot).Where(cpuOrGpu)];

        IEnumerable<SensorSample> loads = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "%"
                && (sensor.Name.Contains("total", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("d3d 3d", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(sensor => SensorNameRank(sensor.Name))
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3);
        IEnumerable<SensorSample> clocks = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "MHz" && sensor.Value > 0
                && (sensor.Name.Contains("core", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("memory", StringComparison.OrdinalIgnoreCase))
                && !sensor.Name.Contains("effective", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains('#', StringComparison.Ordinal))
            .OrderBy(sensor => SensorNameRank(sensor.Name))
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(4);
        IEnumerable<SensorSample> power = candidates
            .Where(sensor => NormaliseUnit(sensor.Unit) == "W" && sensor.Value > 0
                && (sensor.Name.Contains("package", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("total", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(sensor => SensorNameRank(sensor.Name))
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3);
        return loads.Concat(clocks).Concat(power);
    }

    /// <summary>
    /// Deduplicated finite Good-quality samples from named devices — the shared
    /// candidate pool for curated cards. The unit is part of the dedupe key: a
    /// fan tachometer (RPM) and its duty control (%) legitimately share a name.
    /// </summary>
    private static List<SensorSample> GoodSensors(HardwareSnapshot snapshot)
    {
        HashSet<string> namedDevices = [.. snapshot.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .Select(device => device.Id)];
        return [.. snapshot.Sensors
            .Where(sensor => sensor.Quality == SensorQuality.Good && sensor.Value is double value && double.IsFinite(value))
            .Where(sensor => namedDevices.Contains(sensor.DeviceId))
            .GroupBy(sensor => (sensor.DeviceId, sensor.Name, NormaliseUnit(sensor.Unit)))
            .Select(group => group.First())];
    }

    /// <summary>Sensor filter for devices of the given kinds (e.g. keep storage activity off the Performance card).</summary>
    private static Func<SensorSample, bool> FromDeviceKinds(HardwareSnapshot snapshot, params DeviceKind[] kinds)
    {
        HashSet<string> matching = [.. snapshot.Devices
            .Where(device => kinds.Contains(device.Kind))
            .Select(device => device.Id)];
        return sensor => matching.Contains(sensor.DeviceId);
    }

    private static string TemperatureSeverity(SensorSample sensor)
    {
        if (NormaliseUnit(sensor.Unit) != "\u00B0C" || sensor.Value is not double celsius)
        {
            return "Normal";
        }

        return celsius switch
        {
            >= 85 => "Hot",
            >= 72 => "Warm",
            _ => "Normal"
        };
    }

    private static int SensorNameRank(string name)
    {
        if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("package", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)
            || name.Contains("composite", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // Hotspot (either spelling) and coolant temperature are the two most
        // actionable secondary readings: hotspot-to-core delta reveals paste or
        // mount problems, and liquid temperature is the AIO's true load signal.
        return name.Contains("hot spot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("hotspot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("liquid", StringComparison.OrdinalIgnoreCase)
            || name.Contains("coolant", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 2;
    }

    private static void EnsureSuccess(IpcResponse response)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException($"{response.ErrorCode}: {response.Error}");
        }
    }

    private static string Csv(string value)
    {
        string escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        if (target is BatchedObservableCollection<T> batched)
        {
            batched.Synchronize(items);
            return;
        }

        T[] desired = items.ToArray();
        if (target.SequenceEqual(desired))
        {
            return;
        }

        target.Clear();
        foreach (T item in desired)
        {
            target.Add(item);
        }
    }

    private static void Replace<T, TKey>(
        ObservableCollection<T> target,
        IEnumerable<T> items,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        if (target is BatchedObservableCollection<T> batched)
        {
            batched.SynchronizeByKey(items, keySelector, keyComparer);
            return;
        }

        Replace(target, items);
    }

    private void ReportError(Exception exception) => ShowNotice(exception.Message, "Error");

    private void DismissNotice()
    {
        HasNotice = false;
        NoticeText = string.Empty;
        _recoveryNoticeActive = false;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

