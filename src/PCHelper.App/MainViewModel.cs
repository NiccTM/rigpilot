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

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan LocalProbeInterval = TimeSpan.FromSeconds(3);

    private readonly NamedPipeRequestClient _client = new(ProtocolConstants.ServicePipeName, TimeSpan.FromSeconds(3));
    private readonly NamedPipeRequestClient _userAgentClient = new(ProtocolConstants.UserAgentPipeName, TimeSpan.FromSeconds(3));
    private readonly System.Threading.Timer _refreshTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AutomationRuleStateMachine _automationMachine = new();
    private readonly List<DeviceDisplay> _allDevices = [];
    private readonly Dictionary<string, ProfileV2> _suiteProfilesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoolingGraphV1> _coolingGraphsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FanCalibrationV2> _fanCalibrationsByCapability = new(StringComparer.Ordinal);
    private readonly List<OpenRgbController> _openRgbControllers = [];
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _applyProfileCommand;
    private readonly AsyncCommand _resetVerifiedCommand;
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
    private readonly AsyncCommand _showDesktopOsdCommand;
    private readonly AsyncCommand _hideDesktopOsdCommand;
    private readonly AsyncCommand _captureDesktopSnapshotCommand;
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
    private HardwareOperationStatus? _operation;
    private DateTimeOffset _lastLocalProbe = DateTimeOffset.MinValue;
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
    private AutomationTriggerKind _newRuleTriggerKind = AutomationTriggerKind.Process;
    private ProfileV1? _newRuleProfile;
    private string _newRuleName = "Application profile";
    private string _newRuleTriggerValue = string.Empty;
    private string _newRulePriorityText = "100";
    private string _automationStatus = "No automation rules are active.";
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

    public MainViewModel()
    {
        _refreshCommand = new AsyncCommand(_ => RefreshWithFeedbackAsync(), onError: ReportError);
        _applyProfileCommand = new AsyncCommand(
            parameter => ApplyProfileCardAsync((ProfileCardDisplay)parameter!),
            parameter => CanUseServiceWrites
                && parameter is ProfileCardDisplay card
                && !card.IsActive
                && (!card.IsExperimental || (AdvancedWritesAcknowledged && ProfileDeviceAcknowledged))
                && (!card.RequiresManualAcknowledgement
                    || (AdvancedWritesAcknowledged && ProfileDeviceAcknowledged && ManualVoltageAcknowledged)),
            ReportError);
        _resetVerifiedCommand = new AsyncCommand(
            _ => ResetVerifiedControlsCoreAsync(),
            _ => CanUseServiceWrites && ResettableVerifiedControlCount > 0,
            ReportError);
        _startCalibrationCommand = new AsyncCommand(
            _ => StartCalibrationCoreAsync(),
            _ => CanStartCalibration,
            ReportError);
        _startTuneCommand = new AsyncCommand(
            _ => StartTuneCoreAsync(),
            _ => CanStartTune,
            ReportError);
        _abortOperationCommand = new AsyncCommand(
            _ => AbortOperationCoreAsync(),
            _ => IsServiceOnline && HasActiveOperation,
            ReportError);
        _probeOpenRgbCommand = new AsyncCommand(
            _ => ProbeOpenRgbCoreAsync(),
            _ => OpenRgbEnabled && !HasLightingConflict,
            ReportError);
        _applyOpenRgbCommand = new AsyncCommand(
            _ => ApplyOpenRgbCoreAsync(turnOff: false),
            _ => OpenRgbEnabled && OpenRgbConnected && !HasLightingConflict && AreOpenRgbInputsValid,
            ReportError);
        _turnOffOpenRgbCommand = new AsyncCommand(
            _ => ApplyOpenRgbCoreAsync(turnOff: true),
            _ => OpenRgbEnabled && OpenRgbConnected && !HasLightingConflict,
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
            _ => SelectedLightingScene is not null && DynamicLightingDevices.Count > 0 && AreOpenRgbInputsValid && !HasDynamicLightingConflict,
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
        _refreshMonitorBrightnessCommand = new AsyncCommand(
            _ => RefreshMonitorBrightnessCoreAsync(showNotice: true),
            _ => IsUserAgentOnline,
            ReportError);
        _scanHidInventoryCommand = new AsyncCommand(
            _ => ScanHidInventoryCoreAsync(),
            _ => IsServiceOnline,
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
                async () => await RefreshAsync(full: false, userInitiated: false)),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? OsdHotkeyChanged;

    public ObservableCollection<SensorDisplay> ImportantSensors { get; } = [];

    public ObservableCollection<ProfileV1> Profiles { get; } = [];

    public ObservableCollection<ProfileCardDisplay> ProfileCards { get; } = [];

    public ObservableCollection<CapabilityDisplay> CoolingCapabilities { get; } = [];

    public ObservableCollection<CapabilityDisplay> PerformanceCapabilities { get; } = [];

    public ObservableCollection<CapabilityDisplay> CapabilityDecisions { get; } = [];

    /// <summary>
    /// A deliberately narrow view of capabilities that are labelled Experimental.
    /// It does not turn a capability into a write path: it only explains whether
    /// the existing cooling commissioning workflow can safely inspect it.
    /// </summary>
    public ObservableCollection<ExperimentalControlDisplay> ExperimentalControls { get; } = [];

    public ObservableCollection<DeviceDisplay> Devices { get; } = [];

    public ObservableCollection<DiagnosticDisplay> Diagnostics { get; } = [];

    public ObservableCollection<AdapterHealthDisplay> AdapterHealth { get; } = [];

    public ObservableCollection<AdapterTraceDisplay> AdapterTrace { get; } = [];

    public ObservableCollection<HealthRuleDisplay> HealthRules { get; } = [];

    public ObservableCollection<HealthAlertDisplay> HealthAlerts { get; } = [];

    public ObservableCollection<SensorTrendDisplay> MonitoringTrends { get; } = [];

    public ObservableCollection<SensorTrendDisplay> VisibleMonitoringTrends { get; } = [];

    public ObservableCollection<SensorComparisonSeriesDisplay> MonitoringComparisonSeries { get; } = [];

    public ObservableCollection<TimelineEventDisplay> TimelineEvents { get; } = [];

    public ObservableCollection<TimelineEventDisplay> VisibleTimelineEvents { get; } = [];

    public ObservableCollection<CoolingQualificationReportV1> CoolingQualificationReports { get; } = [];

    public ObservableCollection<DeviceQualificationPlanV1> DeviceQualificationPlans { get; } = [];

    public IReadOnlyList<DeviceQualificationPlanV1> TuningQualificationPlans => DeviceQualificationPlans
        .Where(plan => plan.Kind is DeviceQualificationKind.CpuTuning or DeviceQualificationKind.GpuTuning)
        .ToArray();

    public IReadOnlyList<DeviceQualificationPlanV1> LightingQualificationPlans => DeviceQualificationPlans
        .Where(plan => plan.Kind == DeviceQualificationKind.Lighting)
        .ToArray();

    public ObservableCollection<OperationTargetDisplay> CalibrationTargets { get; } = [];

    public ObservableCollection<FanCommissioningSessionV1> FanCommissioningSessions { get; } = [];

    public ObservableCollection<CoolingOutputAssignmentV1> CoolingOutputAssignments { get; } = [];

    public IReadOnlyList<MonitoringTrendScope> MonitoringTrendScopes { get; } = Enum.GetValues<MonitoringTrendScope>();

    public IReadOnlyList<TimelineScope> TimelineScopes { get; } = Enum.GetValues<TimelineScope>();

    public IReadOnlyList<int> CalibrationSettlingOptions { get; } = [3, 5, 7, 10];

    public IReadOnlyList<int> CalibrationRestartCycleOptions { get; } = [2, 3];

    public IReadOnlyList<CoolingOutputRole> CoolingOutputRoles { get; } = Enum.GetValues<CoolingOutputRole>();

    public IReadOnlyList<int> MacroRecordingDurationOptions { get; } = [10, 30, 60, 120, 300];

    public ObservableCollection<OperationTargetDisplay> TuneTargets { get; } = [];

    public ObservableCollection<AutomationRuleDisplay> AutomationRules { get; } = [];

    public ObservableCollection<GameEntryV1> Games { get; } = [];

    public ObservableCollection<AutomationWorkflowV1> Workflows { get; } = [];

    public ObservableCollection<LightingSceneV1> LightingScenes { get; } = [];

    public ObservableCollection<EffectGraphV1> EffectGraphs { get; } = [];

    public ObservableCollection<MacroV1> Macros { get; } = [];

    public ObservableCollection<ScriptActionV1> Scripts { get; } = [];

    public ObservableCollection<OsdLayoutV1> OsdLayouts { get; } = [];

    public ObservableCollection<CapturePresetV1> CapturePresets { get; } = [];

    public ObservableCollection<CaptureTargetV1> CaptureTargets { get; } = [];

    public ObservableCollection<MonitorBrightnessDeviceV1> MonitorBrightnessDevices { get; } = [];

    public ObservableCollection<DynamicLightingDevice> DynamicLightingDevices { get; } = [];

    public ObservableCollection<RgbRouteAssessment> RgbRouteAssessments { get; } = [];

    public ObservableCollection<LightingZoneV1> DraftLightingZones { get; } = [];

    public ObservableCollection<MacroRecordingSessionV1> MacroRecordingSessions { get; } = [];

    public IReadOnlyList<TuningObjective> TuneObjectives { get; } = Enum.GetValues<TuningObjective>();

    public IReadOnlyList<AutomationTriggerKind> AutomationTriggerKinds { get; } = Enum.GetValues<AutomationTriggerKind>();

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand ApplyProfileCommand => _applyProfileCommand;

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

    public ICommand ShowDesktopOsdCommand => _showDesktopOsdCommand;

    public ICommand HideDesktopOsdCommand => _hideDesktopOsdCommand;

    public ICommand CaptureDesktopSnapshotCommand => _captureDesktopSnapshotCommand;

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
                _refreshMonitorBrightnessCommand.RaiseCanExecuteChanged();
                _scanHidInventoryCommand.RaiseCanExecuteChanged();
                _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
                _saveOsdPresentationCommand.RaiseCanExecuteChanged();
                _saveMonitoringPreferencesCommand.RaiseCanExecuteChanged();
                _addMonitoringComparisonSensorCommand.RaiseCanExecuteChanged();
                _removeMonitoringComparisonSensorCommand.RaiseCanExecuteChanged();
                _saveMonitoringComparisonLayoutCommand.RaiseCanExecuteChanged();
                _runInteractiveFanPreflightCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanCaptureDesktopSnapshot));
                OnPropertyChanged(nameof(CanSetMonitorBrightness));
                OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
                OnPropertyChanged(nameof(MonitorBrightnessSummary));
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

    public string RgbCompatibilitySummary => !HasRgbRouteAssessments
        ? "Waiting for RGB inventory and standard-bridge discovery."
        : $"{RgbReadyRouteCount} ready · {RgbSetupRouteCount} setup needed · {RgbReadOnlyRouteCount} direct qualification · {RgbBlockedRouteCount} blocked. Manufacturer recognition never enables a raw USB write by itself.";

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
                _captureDesktopSnapshotCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDesktopOsdVisible => _desktopOsd.IsVisible;

    public bool CanShowDesktopOsd => (_snapshot?.Sensors.Count ?? 0) > 0
        && _osdPresentationSettings.Enabled
        && !IsDesktopOsdVisible;

    public bool CanCaptureDesktopSnapshot => IsUserAgentOnline && SelectedCaptureTarget is not null;

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

    public string MonitorBrightnessPercentText
    {
        get => _monitorBrightnessPercentText;
        set
        {
            if (Set(ref _monitorBrightnessPercentText, value))
            {
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

    public string MonitorBrightnessSummary => MonitorBrightnessDevices.Count == 0
        ? IsUserAgentOnline
            ? "Windows did not return any displays for this signed-in session."
            : "User-agent offline; monitor discovery is not running."
        : $"{MonitorBrightnessDevices.Count} display(s) detected; {MonitorBrightnessDevices.Count(device => device.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified)} expose a bounded brightness path.";

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
            OnPropertyChanged(nameof(WriteStateLabel));
            _applyProfileCommand.RaiseCanExecuteChanged();
            _resetVerifiedCommand.RaiseCanExecuteChanged();
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

    public bool CanUseServiceWrites => IsServiceOnline && _serviceCompatibility.CanUseServiceWrites;

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
                OpenRgbStatus = HasLightingConflict
                    ? LightingConflictReason
                    : "Bridge enabled. Test the local SDK server before applying lighting.";
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
                : $"Lighting bridge blocked by {string.Join(", ", owners)}. RigPilot will not terminate another writer.";
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

    public bool HasImportantSensors => ImportantSensors.Count > 0;

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

    public string CalibrationEligibilityReason => GetCalibrationEligibility().Reason;

    public string TuneEligibilityReason => GetTuneEligibility().Reason;

    public bool CanStartCalibration => CanUseServiceWrites
        && !HasActiveOperation
        && GetCalibrationEligibility().Eligible;

    public bool CanStartTune => CanUseServiceWrites
        && !HasActiveOperation
        && GetTuneEligibility().Eligible
        && TryReadTuneLimits(out _, out _);

    public bool CanWrite => CanUseServiceWrites && _status?.WritesEnabled == true;

    public string WriteStateLabel => !IsServiceOnline
        ? "Hardware writes locked"
        : !CanUseServiceWrites
            ? "Service update required"
            : CanWrite ? "Service write path ready" : "Hardware writes locked";

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

    public async Task InitialiseAsync()
    {
        BusyMessage = "Connecting to the RigPilot service";
        IsBusy = true;
        try
        {
            await RefreshAsync(full: true, userInitiated: false);
            await RefreshUserFeaturesAsync();
        }
        finally
        {
            IsBusy = false;
            if (!_disposed)
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

    public void ShowNotice(string message, string tone = "Info")
    {
        NoticeText = message;
        NoticeTone = tone;
        HasNotice = true;
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
            CancellationToken token = _lifetime.Token;
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
            // CanUseServiceWrites intentionally includes the live connection
            // state. Set it only after a valid status reply, before composing
            // the user-facing message, so a ready service is not described as
            // update-locked for the rest of this refresh.
            IsServiceOnline = true;
            ServiceStatusText = CanUseServiceWrites
                ? _status.Message
                : "The service is reachable, but this dashboard has locked all service-owned writes until the matching runtime is installed.";
            await RefreshOperationStatusAsync(token);
            await RefreshFanCommissioningAsync(token);
            await RefreshCoolingOutputAssignmentsAsync(token);
            await RefreshUpdateStatusAsync(token);

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
            DisposeLocalCoordinator();
            UpdateDisplays();
            ApplyCoolingOutputAssignmentForTarget();
            await RefreshAdapterTraceAsync(token);
            await RefreshReliabilityAsync(token);
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
            _status = null;
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
            SetServiceCompatibility(ServiceRuntimeCompatibility.Unavailable(
                clientVersion,
                $"The service handshake could not complete: {DescribeServiceFailure(exception)}"));
            return;
        }

        if (!response.Success)
        {
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
            SetServiceCompatibility(ServiceRuntimeCompatibility.Evaluate(clientVersion, current));
            return;
        }

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
        OnPropertyChanged(nameof(ServiceStateLabel));
        OnPropertyChanged(nameof(ConnectionTone));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(WriteStateLabel));
        OnPropertyChanged(nameof(ServiceVersion));
        OnPropertyChanged(nameof(AppVersion));
        RebuildExperimentalControlCenter();
        UpdateSafetySummary();
        NotifyOperationEligibility();
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
        IpcResponse legacyResponse = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetProfiles),
            cancellationToken);
        EnsureSuccess(legacyResponse);
        IReadOnlyList<ProfileV1> legacyProfiles = IpcJson.FromElement<IReadOnlyList<ProfileV1>>(legacyResponse.Payload) ?? [];

        // V2 is optional during the one-cycle read-only compatibility window.
        // A legacy service must leave the dashboard usable rather than forcing
        // it into local-probe mode simply because it cannot enumerate V2 data.
        IReadOnlyList<ProfileV2> suiteProfiles = [];
        IpcResponse suiteResponse = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetProfilesV2),
            cancellationToken);
        if (suiteResponse.Success)
        {
            suiteProfiles = IpcJson.FromElement<IReadOnlyList<ProfileV2>>(suiteResponse.Payload) ?? [];
        }

        IReadOnlyList<CoolingGraphV1> coolingGraphs = [];
        IpcResponse graphResponse = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetCoolingGraphs),
            cancellationToken);
        if (graphResponse.Success)
        {
            coolingGraphs = IpcJson.FromElement<IReadOnlyList<CoolingGraphV1>>(graphResponse.Payload) ?? [];
        }

        string? selectedRuleId = NewRuleProfile?.Id;
        string? selectedGameProfileId = SelectedGameProfile?.Id;
        _suiteProfilesById.Clear();
        foreach (ProfileV2 profile in suiteProfiles.Where(profile => profile.SchemaVersion == ProfileV2.CurrentSchemaVersion))
        {
            _suiteProfilesById[profile.Id] = profile;
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
                OnPropertyChanged(nameof(MonitorBrightnessSummary));
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
            OnPropertyChanged(nameof(MonitorBrightnessSummary));
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
        OnPropertyChanged(nameof(MonitorBrightnessSummary));
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

    private async Task PreviewTakeoverCoreAsync()
    {
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.PreviewTakeover,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        TakeoverPreviewResultV1 result = IpcJson.FromElement<TakeoverPreviewResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty takeover preview.");
        TakeoverPreview = result.Plan;
        UpdateStateRevision(response);
        await RefreshOwnershipAsync(_lifetime.Token);
        OwnershipStatus = result.ExecutorStatus.CanExecute
            ? $"Previewed {result.Plan.Processes.Count} exact process(es). Store consent for every target before execution."
            : $"Previewed {result.Plan.Processes.Count} exact process(es). {result.ExecutorStatus.Message}";
        ShowNotice(
            result.Plan.Processes.Count == 0
                ? "No exact competing process could be previewed. Nothing was changed."
                : "Takeover preview created. No process, startup entry, or hardware control was changed.",
            result.Plan.Processes.Count == 0 ? "Info" : "Success");
    }

    private async Task GrantTakeoverConsentCoreAsync()
    {
        TakeoverProcessIdentity target = SelectedTakeoverTarget
            ?? throw new InvalidOperationException("Select an exact takeover target first.");
        OwnershipConsentV1 consent = TakeoverConsentValidator.Create(
            target,
            TakeoverAllowForceTermination,
            TakeoverDisableStartup,
            DateTimeOffset.UtcNow);
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.GrantOwnershipConsent,
            new GrantOwnershipConsentRequest(consent),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshOwnershipAsync(_lifetime.Token);
        ShowNotice($"Stored exact consent for '{target.DisplayName}'. It will be invalidated if the file identity changes.", "Success");
    }

    private async Task ExecuteTakeoverCoreAsync()
    {
        TakeoverPlanV1 plan = TakeoverPreview
            ?? throw new InvalidOperationException("Preview the exact competing processes before execution.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.ExecuteTakeover,
            new ExecuteTakeoverRequest(plan.Id, TakeoverExactProcessesConfirmed),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        if (!response.Success)
        {
            await RefreshOwnershipAsync(CancellationToken.None);
            EnsureSuccess(response);
        }
        UpdateStateRevision(response);
        await RefreshAsync(full: true, userInitiated: false);
        ShowNotice("Exact competing controls were reset and ownership was acquired.", "Success");
    }

    private async Task ReleaseOwnershipCoreAsync()
    {
        TakeoverTransactionV1 transaction = ActiveOwnershipTransaction
            ?? throw new InvalidOperationException("There is no active ownership lease to release.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.ReleaseOwnership,
            new ReleaseOwnershipRequest(transaction.Id),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        if (!response.Success)
        {
            await RefreshOwnershipAsync(CancellationToken.None);
            EnsureSuccess(response);
        }
        UpdateStateRevision(response);
        await RefreshAsync(full: true, userInitiated: false);
        ShowNotice("RigPilot returned affected controls to firmware/default mode and restored backed-up startup entries.", "Success");
    }

    private async Task PreviewAfterburnerImportCoreAsync()
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.PreviewAfterburnerImport,
                new AfterburnerImportRequest(AfterburnerImportPath, AfterburnerImportSection)),
            _lifetime.Token);
        EnsureSuccess(response);
        AfterburnerImportPreview = IpcJson.FromElement<ProfileImportPreviewV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty Afterburner import preview.");
        AfterburnerImportStatus = AfterburnerImportPreview.Warnings.Count == 0
            ? "Preview complete. Review every mapped and manual-only action before saving."
            : $"Preview complete with {AfterburnerImportPreview.Warnings.Count} warning(s). Unsupported settings are not saved.";
        ShowNotice("Afterburner profile was parsed without applying hardware changes.", "Success");
    }

    private async Task SaveAfterburnerImportCoreAsync()
    {
        ProfileV2 profile = AfterburnerImportPreview?.Profile
            ?? throw new InvalidOperationException("Preview a valid Afterburner profile before saving it.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveProfileV2,
            profile,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshAsync(full: true, userInitiated: false);
        AfterburnerImportStatus = $"Saved '{profile.Name}'. Manual-only actions remain blocked from boot and automation.";
        ShowNotice($"Saved imported profile '{profile.Name}'. It was not applied.", "Success");
    }

    private async Task PreviewFanControlImportCoreAsync()
    {
        FanControlImportRequest import = new(
            FanControlImportPath,
            ParseImportMappings(FanControlSensorMappings, "sensor"),
            ParseImportMappings(FanControlControlMappings, "control"));
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.PreviewFanControlImport, import),
            _lifetime.Token);
        EnsureSuccess(response);
        FanControlImportPreview = IpcJson.FromElement<CoolingImportPreviewV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty Fan Control import preview.");
        FanControlImportStatus = FanControlImportPreview.Warnings.Count == 0
            ? "Preview complete. Verify the graph source/output mapping before saving."
            : $"Preview complete with {FanControlImportPreview.Warnings.Count} warning(s). Unmapped outputs remain unavailable.";
        ShowNotice("Fan Control configuration was parsed without applying a cooling curve.", "Success");
    }

    private async Task SaveFanControlImportCoreAsync()
    {
        CoolingGraphV1 graph = FanControlImportPreview?.Graph
            ?? throw new InvalidOperationException("Preview a valid Fan Control graph before saving it.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveCoolingGraph,
            graph,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        FanControlImportStatus = $"Saved '{graph.Name}'. It is not applied until a profile explicitly selects it.";
        ShowNotice($"Saved imported cooling graph '{graph.Name}'.", "Success");
    }

    private async Task RefreshFanCommissioningAsync(CancellationToken cancellationToken)
    {
        try
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetFanCommissioningSessions),
                cancellationToken);
            if (!response.Success)
            {
                _fanCalibrationsByCapability.Clear();
                return;
            }
            string? selectedId = SelectedFanCommissioningSession?.Id;
            FanCommissioningSessionV1[] sessions = (IpcJson.FromElement<IReadOnlyList<FanCommissioningSessionV1>>(response.Payload) ?? [])
                .OrderByDescending(session => session.UpdatedAt)
                .ToArray();
            IpcResponse calibrationResponse = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetFanCalibrations),
                cancellationToken);
            _fanCalibrationsByCapability.Clear();
            if (calibrationResponse.Success)
            {
                IReadOnlyList<FanCalibrationV2> calibrations = IpcJson.FromElement<IReadOnlyList<FanCalibrationV2>>(calibrationResponse.Payload) ?? [];
                foreach (FanCalibrationV2 calibration in calibrations.Where(calibration => calibration.SchemaVersion is > 0 and <= FanCalibrationV2.CurrentSchemaVersion))
                {
                    _fanCalibrationsByCapability[calibration.CapabilityId] = calibration;
                }
            }
            Replace(FanCommissioningSessions, sessions);
            FanCommissioningSessionV1? next = sessions.FirstOrDefault(session => session.Id == selectedId);
            if (next is not null)
            {
                _selectedFanCommissioningSession = next;
                OnPropertyChanged(nameof(SelectedFanCommissioningSession));
            }
            else
            {
                SelectCommissioningForTarget();
            }
            NotifyCommissioningProperties();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // The service can be upgraded independently. Calibration remains
            // unavailable until the commissioning protocol is present.
            CommissioningObservation = "The connected service does not expose the 0.4 commissioning protocol.";
            Replace(FanCommissioningSessions, []);
            _fanCalibrationsByCapability.Clear();
            NotifyCommissioningProperties();
        }
    }

    private async Task RefreshCoolingOutputAssignmentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetCoolingOutputAssignments),
                cancellationToken);
            if (!response.Success)
            {
                return;
            }

            CoolingOutputAssignmentV1[] assignments = (IpcJson.FromElement<IReadOnlyList<CoolingOutputAssignmentV1>>(response.Payload) ?? [])
                .Where(assignment => assignment.SchemaVersion == CoolingOutputAssignmentV1.CurrentSchemaVersion)
                .OrderBy(assignment => assignment.HeaderName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Replace(CoolingOutputAssignments, assignments);
            ApplyCoolingOutputAssignmentForTarget();
            RebuildExperimentalControlCenter();
            UpdateSafetySummary();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // This registry is an additive 0.4 safety feature. A compatible
            // older service stays usable, but it cannot persist a physical
            // role until its matching runtime is installed.
            Replace(CoolingOutputAssignments, []);
            ApplyCoolingOutputAssignmentForTarget();
            RebuildExperimentalControlCenter();
            UpdateSafetySummary();
        }
    }

    private async Task SaveCoolingOutputAssignmentCoreAsync()
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedCalibrationTarget
            ?? throw new InvalidOperationException("Select a cooling output before assigning its physical role.");
        CoolingOutputAssignmentV1 assignment = new(
            CoolingOutputAssignmentV1.CurrentSchemaVersion,
            target.Descriptor.Id,
            target.Descriptor.Id,
            target.Descriptor.AdapterId,
            target.Descriptor.DeviceId,
            target.RpmSensorId,
            CoolingOutputHeaderName.Trim(),
            SelectedCoolingOutputRole,
            DateTimeOffset.UtcNow,
            "User-confirmed physical cooling-output role.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveCoolingOutputAssignment,
            new CoolingOutputAssignmentUpdateRequest(assignment, RemoveCoolingSafetyProtectionAcknowledged),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        CoolingOutputAssignmentSaveResultV1 result = IpcJson.FromElement<CoolingOutputAssignmentSaveResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty cooling-output assignment result.");
        UpdateStateRevision(response);
        CoolingOutputAssignmentV1? existing = CoolingOutputAssignments.FirstOrDefault(item =>
            string.Equals(item.Id, result.Assignment.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            CoolingOutputAssignments.Remove(existing);
        }
        if (!result.Removed)
        {
            CoolingOutputAssignments.Add(result.Assignment);
        }
        RemoveCoolingSafetyProtectionAcknowledged = false;
        ApplyCoolingOutputAssignmentForTarget();
        RebuildExperimentalControlCenter();
        UpdateSafetySummary();
        CommissioningObservation = result.Removed
            ? "Physical safety role cleared. Generic labels still do not authorise a fan pulse. No fan command was sent."
            : $"Stored {SplitWords(result.Assignment.Role.ToString())} role for {result.Assignment.HeaderName}. No fan command was sent.";
        ShowNotice(
            result.Removed ? "Cooling-output safety role cleared." : "Cooling-output safety role saved.",
            result.Removed ? "Warning" : "Success");
    }

    private async Task AddAutomationRuleCoreAsync()
    {
        if (!CanAddAutomationRule
            || !int.TryParse(NewRulePriorityText, out int priority))
        {
            throw new InvalidOperationException("Complete the rule fields using the trigger format shown.");
        }

        AutomationRuleV1 rule = CreateAutomationRule(Guid.NewGuid().ToString("N"), priority);
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveAutomationRule,
            rule,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        AutomationRuleV1 saved = IpcJson.FromElement<AutomationRuleV1>(response.Payload) ?? rule;
        AutomationRuleDisplay? existing = AutomationRules.FirstOrDefault(item => item.Rule.Id == saved.Id);
        if (existing is not null)
        {
            AutomationRules.Remove(existing);
        }

        AutomationRules.Add(AutomationRuleDisplay.From(saved));
        ReorderAutomationRules();
        NewRuleTriggerValue = string.Empty;
        AutomationStatus = $"Saved rule '{saved.Name}'. Entry debounce is 5 seconds.";
        NotifyAutomationProperties();
        ShowNotice($"Automation rule '{saved.Name}' saved.", "Success");
    }

    private async Task DeleteAutomationRuleCoreAsync(AutomationRuleV1 rule)
    {
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.DeleteAutomationRule,
            new DeleteAutomationRuleRequest(rule.Id),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        AutomationRuleDisplay? display = AutomationRules.FirstOrDefault(item => item.Rule.Id == rule.Id);
        if (display is not null)
        {
            AutomationRules.Remove(display);
        }

        AutomationStatus = $"Deleted rule '{rule.Name}'.";
        NotifyAutomationProperties();
    }

    private async Task EvaluateAutomationAsync()
    {
        if (_automationEvaluating
            || !IsServiceOnline
            || !AutomationServiceSupported
            || IsSafeModeEnabled
            || HasActiveOperation
            || AutomationRules.Count == 0
            || DateTimeOffset.UtcNow - _lastAutomationEvaluation < TimeSpan.FromSeconds(1))
        {
            if (IsSafeModeEnabled)
            {
                AutomationStatus = "Safe mode is active; automation is suspended until an operator exits recovery mode.";
            }
            return;
        }

        _automationEvaluating = true;
        _lastAutomationEvaluation = DateTimeOffset.UtcNow;
        try
        {
            if (!_automationMachineInitialised)
            {
                _automationMachine.SetCurrentProfile(_status?.ActiveProfileId);
                _automationMachineInitialised = true;
            }

            string? hotkey = Interlocked.Exchange(ref _pendingAutomationHotkey, null);
            AutomationObservation observation = AutomationObservationProvider.Capture(_sessionLocked, hotkey);
            AutomationDecision decision = _automationMachine.Evaluate(
                AutomationRules.Select(item => item.Rule).ToArray(),
                observation,
                _manualProfileId,
                defaultProfileId: "balanced");
            AutomationStatus = decision.Reason;
            if (!decision.ShouldSwitch
                || decision.ProfileId is null
                || string.Equals(decision.ProfileId, _status?.ActiveProfileId, StringComparison.Ordinal))
            {
                return;
            }

            if (_suiteProfilesById.TryGetValue(decision.ProfileId, out ProfileV2? suiteProfile))
            {
                await ApplyProfileV2Async(suiteProfile, manualSelection: false);
                AutomationStatus = $"Automation applied {suiteProfile.Name}. {decision.Reason}";
                return;
            }

            ProfileV1? profile = Profiles.FirstOrDefault(item => string.Equals(item.Id, decision.ProfileId, StringComparison.Ordinal));
            if (profile is null)
            {
                AutomationStatus = $"Resolved profile '{decision.ProfileId}' is unavailable.";
                return;
            }

            await ApplyProfileAsync(profile, manualSelection: false);
            AutomationStatus = $"Automation applied {profile.Name}. {decision.Reason}";
        }
        catch (Exception exception)
        {
            AutomationStatus = $"Automation evaluation failed: {exception.Message}";
        }
        finally
        {
            _automationEvaluating = false;
        }
    }

    public void NotifyAutomationHotkey(string hotkey)
    {
        _pendingAutomationHotkey = hotkey;
        AutomationStatus = $"Hotkey {hotkey} received; evaluating matching rules.";
    }

    public void SetSessionLocked(bool locked)
    {
        _sessionLocked = locked;
        AutomationStatus = locked ? "Session locked; evaluating rules." : "Session unlocked; evaluating rules.";
    }

    private void ResumeAutomation()
    {
        _manualProfileId = null;
        AutomationStatus = "Manual override cleared; automation will resume on the next evaluation.";
        NotifyAutomationProperties();
    }

    private AutomationRuleV1 CreateAutomationRule(string id, int priority) => new(
        AutomationRuleV1.CurrentSchemaVersion,
        id,
        NewRuleName.Trim(),
        Enabled: true,
        NewRuleTriggerKind,
        NewRuleTriggerValue.Trim(),
        NewRuleProfile?.Id ?? string.Empty,
        priority);

    private void ReorderAutomationRules()
    {
        AutomationRuleDisplay[] ordered = AutomationRules
            .OrderByDescending(item => item.Rule.Priority)
            .ThenBy(item => item.Rule.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Replace(AutomationRules, ordered);
    }

    private async Task RefreshFromLocalAdaptersAsync(bool force)
    {
        if (!force && _snapshot is not null)
        {
            DataSourceLabel = "Local probe";
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
                new VendorControlEligibilityAdapter(),
                new WindowsPeripheralInventoryAdapter(),
                new LibreHardwareMonitorAdapter()
            ]);
            _snapshot = await _localCoordinator.CaptureAsync(_lifetime.Token);
            _lastLocalProbe = DateTimeOffset.UtcNow;
            DataSourceLabel = "Local probe";
            if (Profiles.Count == 0)
            {
                Replace(Profiles, BuiltInProfiles.Create());
                NewRuleProfile = Profiles.FirstOrDefault(profile => profile.Id == "balanced") ?? Profiles.FirstOrDefault();
            }

            UpdateDisplays();
            LastUpdatedText = $"Local probe {DateTimeOffset.Now:HH:mm:ss}";
            SafetySummary = "The service is unavailable. Monitoring is local and read-only; no hardware writes can be issued.";
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

    private async Task ApplyProfileAsync(ProfileV1 profile, bool manualSelection = true)
    {
        EnsureServiceWritesAvailable();
        if (!IsServiceOnline)
        {
            throw new InvalidOperationException(
                "Profiles require the RigPilot service. The current local-probe mode is read-only.");
        }

        BusyMessage = $"Applying {profile.Name}";
        IsBusy = true;
        try
        {
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.ApplyProfile,
                new ApplyProfileRequest(
                    profile,
                    ConfirmExperimental: profile.IsExperimental && AdvancedWritesAcknowledged,
                    ConfirmDevices: profile.IsExperimental && ProfileDeviceAcknowledged),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            ApplyProfileResult result = IpcJson.FromElement<ApplyProfileResult>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty profile result.");
            if (_status is not null)
            {
                _status = _status with
                {
                    StateRevision = response.StateRevision,
                    ActiveProfileId = result.ActiveProfileId
                };
            }

            _automationMachine.SetCurrentProfile(result.ActiveProfileId);
            if (manualSelection)
            {
                _manualProfileId = profile.Id;
                await RefreshAsync(full: true, userInitiated: false);
                ShowNotice($"{profile.Name} is now the active profile. Manual override is active.", "Success");
            }
            else
            {
                UpdateDisplays();
            }

            NotifyAutomationProperties();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ApplyProfileCardAsync(ProfileCardDisplay card)
    {
        if (_suiteProfilesById.TryGetValue(card.Profile.Id, out ProfileV2? suiteProfile))
        {
            return ApplyProfileV2Async(suiteProfile);
        }
        return ApplyProfileAsync(card.Profile);
    }

    private async Task ApplyProfileV2Async(ProfileV2 profile, bool manualSelection = true)
    {
        EnsureServiceWritesAvailable();
        if (!IsServiceOnline)
        {
            throw new InvalidOperationException(
                "Profiles require the RigPilot service. The current local-probe mode is read-only.");
        }

        BusyMessage = $"Applying {profile.Name}";
        IsBusy = true;
        try
        {
            bool requiresManualAcknowledgement = profile.ManualOnlyActionIds.Count > 0;
            IReadOnlyList<string> confirmedDeviceIds = (profile.IsExperimental || requiresManualAcknowledgement)
                && ProfileDeviceAcknowledged
                ? GetProfileDeviceIds(profile)
                : [];
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.ApplyProfileV2,
                new ApplyProfileV2Request(
                    profile,
                    manualSelection ? ProfileActivationSource.Manual : ProfileActivationSource.Automation,
                    ConfirmExperimental: profile.IsExperimental && AdvancedWritesAcknowledged,
                    confirmedDeviceIds,
                    ConfirmManualVoltage: requiresManualAcknowledgement
                        && ManualVoltageAcknowledged
                        && ProfileDeviceAcknowledged),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            ApplyProfileResult result = IpcJson.FromElement<ApplyProfileResult>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty profile result.");
            UpdateAppliedProfileStatus(response, result);
            if (manualSelection)
            {
                _manualProfileId = profile.Id;
                await RefreshAsync(full: true, userInitiated: false);
                ShowNotice($"{profile.Name} is now the active profile. Manual override is active.", "Success");
            }
            else
            {
                UpdateDisplays();
            }

            NotifyAutomationProperties();
            if (requiresManualAcknowledgement)
            {
                ManualVoltageAcknowledged = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string[] GetProfileDeviceIds(ProfileV2 profile)
    {
        IEnumerable<string> capabilityIds = profile.HardwareActions.Select(action => action.CapabilityId);
        if (profile.CoolingGraphId is string graphId
            && _coolingGraphsById.TryGetValue(graphId, out CoolingGraphV1? graph))
        {
            capabilityIds = capabilityIds.Concat(graph.Outputs.Select(output => output.CapabilityId));
        }

        return capabilityIds
        .Select(capabilityId => _snapshot?.Capabilities.FirstOrDefault(capability => capability.Id == capabilityId)?.DeviceId)
        .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
        .Cast<string>()
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    }

    private void UpdateAppliedProfileStatus(IpcResponse response, ApplyProfileResult result)
    {
        if (_status is not null)
        {
            _status = _status with
            {
                StateRevision = response.StateRevision,
                ActiveProfileId = result.ActiveProfileId
            };
        }
        _automationMachine.SetCurrentProfile(result.ActiveProfileId);
    }

    private async Task ResetVerifiedControlsCoreAsync()
    {
        EnsureServiceWritesAvailable();
        if (!IsServiceOnline)
        {
            throw new InvalidOperationException("Reset requires the RigPilot service; local-probe mode cannot write hardware state.");
        }

        CapabilityDescriptor[] resettable = _snapshot?.Capabilities.Where(
            item => item.State == CapabilityAccessState.Verified && item.CanResetToDefault).ToArray() ?? [];
        if (resettable.Length == 0)
        {
            ShowNotice("No resettable Verified controls are currently available.", "Info");
            return;
        }

        BusyMessage = "Restoring verified controls";
        IsBusy = true;
        try
        {
            foreach (CapabilityDescriptor capability in resettable)
            {
                IpcRequest request = NamedPipeRequestClient.CreateRequest(
                    IpcCommand.ResetHardware,
                    capability.Id,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N"));
                IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
                EnsureSuccess(response);
            }

            await RefreshAsync(full: true, userInitiated: false);
            ShowNotice($"Restored {resettable.Length} Verified control{(resettable.Length == 1 ? string.Empty : "s")} to default.", "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BeginFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedCalibrationTarget
            ?? throw new InvalidOperationException("Select a cooling control before starting commissioning.");
        if (target.RpmSensorId is null)
        {
            throw new InvalidOperationException("This control has no RPM sensor from the same exact device.");
        }

        BeginFanCommissioningRequest payload = new(
            target.Descriptor.Id,
            target.RpmSensorId,
            CommissioningHeaderName.Trim(),
            IsSelectedCoolingOutputProtected
                || target.Descriptor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || target.Descriptor.Name.Contains("pump", StringComparison.OrdinalIgnoreCase),
            AllowCaseFanStop,
            string.IsNullOrWhiteSpace(CommissioningNotes) ? null : CommissioningNotes.Trim());
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.BeginFanCommissioning,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 session = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(session, select: true);
        CommissioningHeaderConfirmed = false;
        CommissioningObservation = "Identity check is pending. Use a short, bounded pulse only while watching the physical fan, then explicitly confirm the header.";
        ShowNotice("Commissioning session started. No fan speed changed during setup.", "Info");
    }

    private async Task PulseFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Start a commissioning session before issuing a header-identification pulse.");
        PulseFanCommissioningRequest payload = new(
            session.Id,
            ConfirmExperimental: AdvancedWritesAcknowledged,
            ConfirmDevice: CalibrationDeviceAcknowledged,
            Duration: TimeSpan.FromSeconds(2));
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.PulseFanCommissioning,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        HardwareOperationStatus status = IpcJson.FromElement<HardwareOperationStatus>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty header-pulse operation.");
        UpdateStateRevision(response);
        _operation = status;
        NotifyOperationProperties();
        CommissioningObservation = "A 2-second bounded identification pulse is running. Watch the physical fan, then use Observe and explicitly confirm its header. Firmware/default control will be restored automatically.";
        ShowNotice("Header-identification pulse started. Calibration remains locked until you confirm the physical fan.", "Info");
    }

    private async Task RunInteractiveFanPreflightCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select the failed commissioning session before running the elevated diagnostic.");
        if (!CanRunInteractiveFanPreflight)
        {
            throw new InvalidOperationException("This session is not eligible for the explicit elevated no-write diagnostic.");
        }

        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.RunInteractiveFanPreflight,
                new InteractiveFanPreflightRequestV1(
                    InteractiveFanPreflightRequestV1.CurrentSchemaVersion,
                    session.CapabilityId),
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        InteractiveFanPreflightResultV1 result = IpcJson.FromElement<InteractiveFanPreflightResultV1>(response.Payload)
            ?? throw new InvalidDataException("The elevated diagnostic returned an empty result.");
        CommissioningObservation = result.Prepared
            ? "Elevated user-session Prepare passed with no hardware command. The LocalSystem service remains blocked; this result cannot enable a pulse or calibration."
            : result.Summary;
        ShowNotice(
            result.Prepared
                ? "Elevated no-write diagnostic passed. Service fan control remains locked."
                : "Elevated no-write diagnostic did not pass; no hardware command was issued.",
            result.Prepared ? "Info" : "Warning");
    }

    private async Task ObserveFanCommissioningCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.ObserveFanCommissioning, new FanCommissioningSessionRequest(session.Id)),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningObservationV1 observation = IpcJson.FromElement<FanCommissioningObservationV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning observation.");
        UpsertCommissioningSession(observation.Session, select: true);
        string rpm = observation.RpmSample?.Value is double value
            ? $" Paired RPM: {value:0} RPM."
            : " Paired RPM is unavailable.";
        string thermal = observation.ThermalSamples.Count == 0
            ? string.Empty
            : $" Observing {observation.ThermalSamples.Count} local thermal sensor(s).";
        CommissioningObservation = observation.Guidance + rpm + thermal;
    }

    private async Task ConfirmFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Start a commissioning session first.");
        ConfirmFanCommissioningRequest payload = new(
            session.Id,
            CommissioningHeaderConfirmed,
            CommissioningHeaderName.Trim(),
            string.IsNullOrWhiteSpace(CommissioningNotes) ? null : CommissioningNotes.Trim(),
            PhysicalHeaderObserved: CommissioningHeaderConfirmed);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.ConfirmFanCommissioning,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 updated = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty confirmed commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(updated, select: true);
        CommissioningObservation = "Header identity is confirmed. The bounded calibration control is now enabled; CPU fans and pumps still cannot stop.";
        ShowNotice("Header identity confirmed. Review the experimental-write acknowledgement before calibration.", "Success");
    }

    private async Task CompleteFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.CompleteFanCommissioning,
                new FanCommissioningSessionRequest(session.Id),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 completed = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty completed commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(completed, select: true);
        bool zeroRpmVerified = _operation?.CalibrationResult is { } calibration
            && FanCalibrationPolicy.SupportsVerifiedFanStop(calibration);
        CommissioningObservation = zeroRpmVerified
            ? "Qualification report saved. Zero-RPM remains limited to this exact verified stop/restart path; the control stays Experimental until broader independent hardware evidence exists."
            : "Qualification report saved. This exact output is approved only for a calibrated nonzero curve; zero-RPM remains disabled.";
        ShowNotice(zeroRpmVerified ? "Fan commissioning report saved." : "Nonzero-only fan commissioning report saved.", "Success");
    }

    private async Task CreateAdaptiveCoolingProfileCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Save a completed commissioning report before creating a cooling profile.");
        await SaveCoolingProfileDraftAsync(
            CreateAdaptiveCoolingDraft(session),
            "Saving adaptive cooling curve");
    }

    private async Task SaveCustomCoolingCurveCoreAsync()
    {
        EnsureServiceWritesAvailable();
        if (!TryReadCustomCoolingCurve(out CustomCoolingCurveDefinition? definition, out string error))
        {
            throw new InvalidOperationException(error);
        }

        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Save a completed commissioning report before creating a cooling profile.");
        await SaveCoolingProfileDraftAsync(
            CreateCustomCoolingDraft(session, definition!),
            "Saving manual cooling curve");
    }

    private AdaptiveCoolingProfileDraft CreateAdaptiveCoolingDraft(FanCommissioningSessionV1 session)
    {
        ValidateCompletedCommissioningSession(session);
        CapabilityDescriptor output = GetCommissionedCoolingOutput(session);
        if (_operation is { CapabilityId: var operationCapabilityId, CalibrationResult: { } operationCalibration }
            && string.Equals(operationCapabilityId, session.CapabilityId, StringComparison.Ordinal)
            && FanCalibrationPolicy.SupportsNonZeroCurve(operationCalibration))
        {
            return AdaptiveCoolingProfileFactory.Create(output, operationCalibration, session.HeaderName, _snapshot?.Sensors ?? []);
        }
        if (GetCommissionedCalibration(session) is { } persistedCalibration
            && FanCalibrationPolicy.SupportsNonZeroCurve(persistedCalibration))
        {
            return AdaptiveCoolingProfileFactory.Create(output, persistedCalibration, session.HeaderName, _snapshot?.Sensors ?? []);
        }

        throw new InvalidOperationException("No stable calibration linked to this commissioning report is available.");
    }

    private AdaptiveCoolingProfileDraft CreateCustomCoolingDraft(
        FanCommissioningSessionV1 session,
        CustomCoolingCurveDefinition definition)
    {
        ValidateCompletedCommissioningSession(session);
        CapabilityDescriptor output = GetCommissionedCoolingOutput(session);
        if (_operation is { CapabilityId: var operationCapabilityId, CalibrationResult: { } operationCalibration }
            && string.Equals(operationCapabilityId, session.CapabilityId, StringComparison.Ordinal)
            && FanCalibrationPolicy.SupportsNonZeroCurve(operationCalibration))
        {
            return CustomCoolingCurveFactory.Create(output, operationCalibration, session.HeaderName, _snapshot?.Sensors ?? [], definition);
        }
        if (GetCommissionedCalibration(session) is { } persistedCalibration
            && FanCalibrationPolicy.SupportsNonZeroCurve(persistedCalibration))
        {
            return CustomCoolingCurveFactory.Create(output, persistedCalibration, session.HeaderName, _snapshot?.Sensors ?? [], definition);
        }

        throw new InvalidOperationException("No stable calibration linked to this commissioning report is available.");
    }

    private string? GetCustomCoolingCurveDraftError(CustomCoolingCurveDefinition definition)
    {
        if (SelectedFanCommissioningSession is not FanCommissioningSessionV1 session)
        {
            return "Save a physically observed commissioning report before creating a manual curve.";
        }

        try
        {
            _ = CreateCustomCoolingDraft(session, definition);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    private static void ValidateCompletedCommissioningSession(FanCommissioningSessionV1 session)
    {
        if (session.State != FanCommissioningState.Completed || !session.PhysicalHeaderObserved)
        {
            throw new InvalidOperationException("A physically observed completed commissioning report is required before creating a cooling profile.");
        }
    }

    private CapabilityDescriptor GetCommissionedCoolingOutput(FanCommissioningSessionV1 session) =>
        _snapshot?.Capabilities.FirstOrDefault(capability => capability.Id == session.CapabilityId)
            ?? throw new InvalidOperationException("The commissioned cooling output is no longer present in the current inventory.");

    private async Task SaveCoolingProfileDraftAsync(AdaptiveCoolingProfileDraft draft, string busyMessage)
    {
        BusyMessage = busyMessage;
        IsBusy = true;
        try
        {
            IpcResponse graphResponse = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SaveCoolingGraph,
                    draft.Graph,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(graphResponse);
            UpdateStateRevision(graphResponse);
            _coolingGraphsById[draft.Graph.Id] = draft.Graph;

            IpcResponse profileResponse = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SaveProfileV2,
                    draft.Profile,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(profileResponse);
            UpdateStateRevision(profileResponse);
            await RefreshAsync(full: true, userInitiated: false);
            ShowNotice(
                $"Saved '{draft.Profile.Name}'. It is not active; review the exact-device acknowledgement, then apply it from Profiles.",
                "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryReadCustomCoolingCurve(
        out CustomCoolingCurveDefinition? definition,
        out string error)
    {
        definition = null;
        string name = CustomCoolingCurveName.Trim();
        if (name.Length is 0 or > 48)
        {
            error = "Enter a curve name from 1 to 48 characters.";
            return false;
        }

        List<CurvePoint> points = [];
        string[] lines = CustomCoolingCurvePoints.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 2 or > 8)
        {
            error = "Provide two to eight points, one per line, as temperature:duty (for example 70:70).";
            return false;
        }
        foreach (string line in lines)
        {
            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double duty))
            {
                error = $"'{line}' is not a valid temperature:duty point.";
                return false;
            }
            points.Add(new CurvePoint(temperature, duty));
        }

        if (!TryReadCurveValue(CustomCoolingCurveHysteresisUpText, "rise hysteresis", out double hysteresisUp, out error)
            || !TryReadCurveValue(CustomCoolingCurveHysteresisDownText, "fall hysteresis", out double hysteresisDown, out error)
            || !TryReadCurveValue(CustomCoolingCurveResponseUpSecondsText, "rise response time", out double responseUp, out error)
            || !TryReadCurveValue(CustomCoolingCurveResponseDownSecondsText, "fall response time", out double responseDown, out error))
        {
            return false;
        }

        definition = new CustomCoolingCurveDefinition(
            name,
            points,
            hysteresisUp,
            hysteresisDown,
            responseUp,
            responseDown);
        error = string.Empty;
        return true;
    }

    private static bool TryReadCurveValue(
        string text,
        string label,
        out double value,
        out string error)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || !double.IsFinite(value))
        {
            error = $"Enter a finite {label} value.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private (double MinimumDuty, double MaximumDuty) GetCustomCoolingCurveDutyRange()
    {
        string? capabilityId = SelectedFanCommissioningSession?.CapabilityId
            ?? SelectedCalibrationTarget?.Descriptor.Id;
        NumericRange? range = capabilityId is null
            ? null
            : _snapshot?.Capabilities.FirstOrDefault(capability => capability.Id == capabilityId)?.Range;
        return range is { } numeric && numeric.Maximum > numeric.Minimum
            ? (numeric.Minimum, numeric.Maximum)
            : (0, 100);
    }

    private bool HasAdaptiveCoolingCalibration(FanCommissioningSessionV1 session) =>
        (_operation is { CapabilityId: var operationCapabilityId, CalibrationResult: { } operationCalibration }
            && string.Equals(operationCapabilityId, session.CapabilityId, StringComparison.Ordinal)
            && FanCalibrationPolicy.SupportsNonZeroCurve(operationCalibration))
        || (GetCommissionedCalibration(session) is { } persistedCalibration
            && FanCalibrationPolicy.SupportsNonZeroCurve(persistedCalibration));

    private FanCalibrationV2? GetCommissionedCalibration(FanCommissioningSessionV1 session) =>
        _fanCalibrationsByCapability.TryGetValue(session.CapabilityId, out FanCalibrationV2? calibration)
        && string.Equals(calibration.CommissioningSessionId, session.Id, StringComparison.Ordinal)
            ? calibration
            : null;

    private async Task CancelFanCommissioningCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.CancelFanCommissioning,
                new FanCommissioningSessionRequest(session.Id),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 cancelled = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty cancelled commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(cancelled, select: true);
        CommissioningObservation = "Commissioning was cancelled. No active calibration was interrupted; firmware/default control remains responsible for the fan.";
        ShowNotice("Commissioning session cancelled.", "Info");
    }

    private async Task RecoverFanCommissioningCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session requiring recovery first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.RecoverFanCommissioning,
                new FanCommissioningSessionRequest(session.Id),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 recovered = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty recovered commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(recovered, select: true);
        CommissioningObservation = "Firmware/default recovery completed. Start a new commissioning session before another calibration.";
        ShowNotice("Recovered the controller to its firmware/default policy.", "Success");
    }

    private async Task StartMacroRecordingCoreAsync()
    {
        BeginMacroRecordingRequest payload = new(MacroRecordingName.Trim(), TimeSpan.FromSeconds(MacroRecordingDurationSeconds));
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.BeginMacroRecording, payload, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        ActiveMacroRecording = IpcJson.FromElement<MacroRecordingSessionV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty recording session.");
        MacroRecordingStatus = "Recording is active. Keyboard, clicks, and pointer movement are captured only until Stop or Cancel.";
        ShowNotice("Macro recording started. Stop to save, or cancel to discard all captured input.", "Warning");
    }

    private async Task StopMacroRecordingCoreAsync()
    {
        MacroRecordingSessionV1 session = ActiveMacroRecording
            ?? throw new InvalidOperationException("No macro recording is active.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.StopMacroRecording, new StopMacroRecordingRequest(session.Id), idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        MacroRecordingResultV1 result = IpcJson.FromElement<MacroRecordingResultV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty recording result.");
        ActiveMacroRecording = null;
        MacroRecordingStatus = $"Saved {result.Session.StepCount} recorded input step(s) as '{result.Macro?.Name ?? result.Session.Name}'.";
        await RefreshUserFeaturesAsync();
        SelectedMacro = Macros.FirstOrDefault(macro => macro.Id == result.Macro?.Id) ?? SelectedMacro;
        ShowNotice(MacroRecordingStatus, "Success");
    }

    private async Task CancelMacroRecordingCoreAsync()
    {
        MacroRecordingSessionV1 session = ActiveMacroRecording
            ?? throw new InvalidOperationException("No macro recording is active.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.CancelMacroRecording, new CancelMacroRecordingRequest(session.Id), idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        ActiveMacroRecording = null;
        MacroRecordingStatus = "Recording cancelled. Captured input was discarded and no macro was saved.";
        await RefreshUserFeaturesAsync();
        ShowNotice(MacroRecordingStatus, "Info");
    }

    private async Task TestMacroCoreAsync()
    {
        MacroV1 macro = SelectedMacro ?? throw new InvalidOperationException("Select a saved macro to test.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.ExecuteMacro, new ExecuteMacroRequest(macro.Id, ConfirmedVisibleSession: true)),
            _lifetime.Token);
        EnsureSuccess(response);
        MacroExecutionResultV1 result = IpcJson.FromElement<MacroExecutionResultV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty macro execution result.");
        ShowNotice($"Macro test completed: {result.ExecutedSteps} input step(s).", "Success");
    }

    private Task AddMacroKeyPressCoreAsync()
    {
        if (!TryGetMacroEditorKeyPress(out int keyCode, out int delayMilliseconds))
        {
            throw new InvalidOperationException("Enter a virtual-key code from 1 to 255 and a release delay from 0 to 5000 ms.");
        }

        _macroEditorSteps.Add(new MacroStepV1(MacroStepKind.KeyDown, keyCode, 0, 0, 0, TimeSpan.Zero));
        _macroEditorSteps.Add(new MacroStepV1(MacroStepKind.KeyUp, keyCode, 0, 0, 0, TimeSpan.FromMilliseconds(delayMilliseconds)));
        RefreshMacroEditorSummary();
        NotifyMacroEditorProperties();
        return Task.CompletedTask;
    }

    private Task RemoveMacroKeyPressCoreAsync()
    {
        if (!HasTrailingEditableKeyPress())
        {
            throw new InvalidOperationException("Only the last key press added by this editor can be removed safely. Re-record a macro to remove recorded pointer or media input.");
        }

        _macroEditorSteps.RemoveRange(_macroEditorSteps.Count - 2, 2);
        RefreshMacroEditorSummary();
        NotifyMacroEditorProperties();
        return Task.CompletedTask;
    }

    private async Task SaveMacroEditCoreAsync()
    {
        MacroV1 current = SelectedMacro ?? throw new InvalidOperationException("Select a saved macro before saving edits.");
        MacroV1 edited = current with
        {
            Name = MacroEditorName.Trim(),
            Steps = _macroEditorSteps.ToArray()
        };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.SaveMacro, edited, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        await RefreshUserFeaturesAsync();
        SelectedMacro = Macros.FirstOrDefault(macro => macro.Id == edited.Id) ?? edited;
        ShowNotice($"Saved typed edits to '{edited.Name}'.", "Success");
    }

    private void LoadMacroEditor(MacroV1? macro)
    {
        _macroEditorSteps.Clear();
        if (macro is null)
        {
            MacroEditorName = string.Empty;
            MacroEditorSummary = "Select a saved macro to review or edit its typed key presses.";
        }
        else
        {
            _macroEditorSteps.AddRange(macro.Steps);
            MacroEditorName = macro.Name;
            RefreshMacroEditorSummary();
        }
        NotifyMacroEditorProperties();
    }

    private bool TryGetMacroEditorKeyPress(out int keyCode, out int delayMilliseconds)
    {
        keyCode = 0;
        delayMilliseconds = 0;
        if (!int.TryParse(MacroEditorKeyCodeText, out keyCode)
            || keyCode is < 1 or > 255
            || !int.TryParse(MacroEditorDelayMillisecondsText, out delayMilliseconds)
            || delayMilliseconds is < 0 or > 5000)
        {
            keyCode = 0;
            delayMilliseconds = 0;
            return false;
        }
        return true;
    }

    private bool HasTrailingEditableKeyPress() => _macroEditorSteps.Count >= 2
        && _macroEditorSteps[^2] is { Kind: MacroStepKind.KeyDown } down
        && _macroEditorSteps[^1] is { Kind: MacroStepKind.KeyUp } up
        && down.Code == up.Code;

    private void RefreshMacroEditorSummary()
    {
        if (_macroEditorSteps.Count == 0)
        {
            MacroEditorSummary = "No typed steps remain. Add a key press or re-record this macro before saving.";
            return;
        }

        string preview = string.Join(
            " · ",
            _macroEditorSteps.Take(6).Select(DescribeMacroStep));
        string suffix = _macroEditorSteps.Count > 6 ? " · …" : string.Empty;
        MacroEditorSummary = $"{_macroEditorSteps.Count} typed step(s): {preview}{suffix}";
    }

    private static string DescribeMacroStep(MacroStepV1 step) => step.Kind switch
    {
        MacroStepKind.KeyDown => $"Key {step.Code} down",
        MacroStepKind.KeyUp => $"Key {step.Code} up",
        MacroStepKind.MouseButtonDown => $"Mouse {step.Code} down",
        MacroStepKind.MouseButtonUp => $"Mouse {step.Code} up",
        MacroStepKind.MouseMove => $"Move {step.X},{step.Y}",
        MacroStepKind.MouseWheel => $"Wheel {step.Delta}",
        MacroStepKind.MediaKey => $"Media {step.Code}",
        _ => $"Delay {step.Delay.TotalMilliseconds:0} ms"
    };

    private async Task AddLightingZoneCoreAsync()
    {
        DynamicLightingDevice device = SelectedDynamicLightingDevice
            ?? throw new InvalidOperationException("Select a Dynamic Lighting device first.");
        int[] indices = ParseLightingLedIndices(LightingZoneLedIndices, device.LampCount);
        if (!TryParseDouble(LightingZoneXText, out double x)
            || !TryParseDouble(LightingZoneYText, out double y)
            || !TryParseDouble(LightingZoneWidthText, out double width)
            || !TryParseDouble(LightingZoneHeightText, out double height)
            || x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Zone position must use non-negative X/Y and positive width/height values.");
        }
        LightingZoneV1 zone = new(
            $"zone.{Guid.NewGuid():N}",
            device.Id,
            indices,
            x,
            y,
            width,
            height);
        DraftLightingZones.Add(zone);
        OnPropertyChanged(nameof(CanSaveLightingLayout));
        _saveLightingLayoutCommand.RaiseCanExecuteChanged();
        ShowNotice($"Added {indices.Length} LED(s) from {device.Name} to the physical layout.", "Success");
    }

    private async Task SaveLightingLayoutCoreAsync()
    {
        if (!TryParseDouble(OpenRgbBrightnessText, out double brightness) || brightness is < 0 or > 100)
        {
            throw new InvalidOperationException("Lighting brightness must be a value from 0 to 100.");
        }
        LightingSceneV1 scene = SelectedLightingScene is null
            ? new LightingSceneV1(
                LightingSceneV1.CurrentSchemaVersion,
                $"scene.{Guid.NewGuid():N}",
                LightingLayoutName.Trim(),
                string.Empty,
                brightness,
                DraftLightingZones.ToArray(),
                DynamicLightingDevices.Where(device => !device.IsEnabled).Select(device => device.Id).ToArray())
            : SelectedLightingScene with
            {
                Name = LightingLayoutName.Trim(),
                BrightnessPercent = brightness,
                Zones = DraftLightingZones.ToArray(),
                DisabledDeviceIds = DynamicLightingDevices.Where(device => !device.IsEnabled).Select(device => device.Id).ToArray()
            };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.SaveLightingScene, scene, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        LightingSceneV1 saved = IpcJson.FromElement<LightingSceneV1>(response.Payload) ?? scene;
        Replace(LightingScenes, LightingScenes.Where(item => item.Id != saved.Id).Append(saved).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        SelectedLightingScene = saved;
        SelectedGameLightingScene = saved;
        NotifyUserFeatureProperties();
        ShowNotice($"Saved physical lighting layout '{saved.Name}'.", "Success");
    }

    private async Task ApplyDynamicLightingSceneCoreAsync()
    {
        LightingSceneV1 scene = SelectedLightingScene
            ?? throw new InvalidOperationException("Select a saved lighting layout first.");
        if (HasDynamicLightingConflict)
        {
            throw new InvalidOperationException(DynamicLightingConflictReason);
        }
        if (!TryParseOpenRgbInputs(out string colour, out _))
        {
            throw new InvalidOperationException("Enter a six-digit RGB colour and brightness from 0 to 100.");
        }
        BusyMessage = "Applying Windows Dynamic Lighting scene";
        IsBusy = true;
        try
        {
            await DynamicLightingBridge.ApplyStaticSceneAsync(scene, colour, _lifetime.Token);
            DynamicLightingStatus = $"Applied '{scene.Name}' through Windows Dynamic Lighting.";
            RebuildRgbRouteAssessments();
            ShowNotice(DynamicLightingStatus, "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveGameBundleCoreAsync()
    {
        GameEntryV1 game = SelectedGame ?? throw new InvalidOperationException("Select a game first.");
        GameEntryV1 updated = game with
        {
            ProfileId = SelectedGameProfile?.Id,
            LightingSceneId = SelectedGameLightingScene?.Id,
            OsdLayoutId = SelectedGameOsdLayout?.Id,
            CapturePresetId = SelectedGameCapturePreset?.Id,
            MacroIds = SelectedGameMacro is null ? [] : [SelectedGameMacro.Id]
        };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.SaveGame, updated, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        GameEntryV1 saved = IpcJson.FromElement<GameEntryV1>(response.Payload) ?? updated;
        Replace(Games, Games.Where(item => item.Id != saved.Id).Append(saved).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        SelectedGame = saved;
        NotifyUserFeatureProperties();
        ShowNotice($"Saved the per-game bundle for '{saved.Name}'.", "Success");
    }

    private Task ShowDesktopOsdCoreAsync()
    {
        if (_snapshot is null || _snapshot.Sensors.Count == 0)
        {
            throw new InvalidOperationException("Live sensor data is required before showing a desktop OSD.");
        }

        OsdLayoutV1 layout = ResolveDesktopOsdLayout();
        OsdPresentationSettingsV1 presentation = TryBuildOsdPresentationSettings(out OsdPresentationSettingsV1 settings)
            ? settings
            : _osdPresentationSettings;
        if (!presentation.Enabled)
        {
            throw new InvalidOperationException("The local OSD is disabled in its presentation settings.");
        }
        _desktopOsd.Show(layout, _snapshot.Sensors, presentation);
        DesktopOsdStatus = $"Showing '{layout.Name}' as a local non-activating desktop overlay. It does not inject into games or write RTSS shared memory.";
        NotifyDesktopOsdProperties();
        ShowNotice("Desktop OSD is visible. Use Hide OSD to remove it.", "Success");
        return Task.CompletedTask;
    }

    private Task HideDesktopOsdCoreAsync()
    {
        _desktopOsd.Close();
        DesktopOsdStatus = "Desktop OSD is hidden. It uses a local non-activating window, not RTSS or injection.";
        NotifyDesktopOsdProperties();
        ShowNotice("Desktop OSD hidden.", "Info");
        return Task.CompletedTask;
    }

    private async Task CaptureDesktopSnapshotCoreAsync()
    {
        CaptureTargetV1 target = SelectedCaptureTarget
            ?? throw new InvalidOperationException("Select a display or visible window before capturing a snapshot.");
        BusyMessage = "Saving explicit desktop snapshot";
        IsBusy = true;
        try
        {
            IpcResponse response = await _userAgentClient.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.CaptureDesktopSnapshot,
                    new CaptureSnapshotRequestV1(
                        CaptureSnapshotRequestV1.CurrentSchemaVersion,
                        target,
                        ConfirmedVisibleCapture: true),
                    idempotencyKey: Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(response);
            CaptureSnapshotResultV1 result = IpcJson.FromElement<CaptureSnapshotResultV1>(response.Payload)
                ?? throw new InvalidDataException("User agent returned an empty desktop snapshot result.");
            string warning = string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : $" {result.Warning}";
            DesktopSnapshotStatus = $"Saved {result.Width} x {result.Height} PNG as {Path.GetFileName(result.OutputPath)} ({Math.Max(1, result.BytesWritten / 1024):N0} KB).{warning}";
            ShowNotice("Desktop snapshot saved only to your Pictures\\RigPilot\\Snapshots folder.", "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshMonitorBrightnessCoreAsync(bool showNotice)
    {
        string? selectedId = SelectedMonitorBrightnessDevice?.Id;
        IReadOnlyList<MonitorBrightnessDeviceV1> devices = await GetUserEntitiesAsync<MonitorBrightnessDeviceV1>(IpcCommand.GetMonitorBrightnesses);
        Replace(MonitorBrightnessDevices, devices);
        SelectedMonitorBrightnessDevice = MonitorBrightnessDevices.FirstOrDefault(device =>
                string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? MonitorBrightnessDevices.FirstOrDefault(device => device.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified)
            ?? MonitorBrightnessDevices.FirstOrDefault();
        // A display may be unplugged, remapped, or replaced while retaining a
        // Windows logical display name. Every discovery pass therefore requires
        // a fresh acknowledgement before another brightness write.
        MonitorBrightnessDeviceConfirmed = false;

        int controllable = MonitorBrightnessDevices.Count(device => device.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified);
        MonitorBrightnessStatus = MonitorBrightnessDevices.Count == 0
            ? "Windows did not return any displays for this signed-in session."
            : controllable == 0
                ? "Displays were recognized, but none expose a verified writable DDC/CI or Windows-panel brightness range."
                : $"Detected {MonitorBrightnessDevices.Count} display(s); {controllable} expose a bounded DDC/CI or Windows-panel brightness path.";
        OnPropertyChanged(nameof(MonitorBrightnessSummary));
        OnPropertyChanged(nameof(CanSetMonitorBrightness));
        OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
        _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
        if (showNotice)
        {
            ShowNotice(MonitorBrightnessStatus, controllable > 0 ? "Info" : "Warning");
        }
    }

    private async Task SetMonitorBrightnessCoreAsync()
    {
        MonitorBrightnessDeviceV1 device = SelectedMonitorBrightnessDevice
            ?? throw new InvalidOperationException("Select a monitor before changing brightness.");
        if (!int.TryParse(MonitorBrightnessPercentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int brightness)
            || brightness is < 0 or > 100)
        {
            throw new InvalidOperationException("Brightness must be a whole percentage from 0 through 100.");
        }

        BusyMessage = $"Applying {brightness}% brightness to {device.DisplayName}";
        IsBusy = true;
        try
        {
            IpcResponse response = await _userAgentClient.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SetMonitorBrightness,
                    new SetMonitorBrightnessRequestV1(
                        SetMonitorBrightnessRequestV1.CurrentSchemaVersion,
                        device.Id,
                        brightness,
                        ConfirmDevice: true),
                    idempotencyKey: Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(response);
            MonitorBrightnessApplyResultV1 result = IpcJson.FromElement<MonitorBrightnessApplyResultV1>(response.Payload)
                ?? throw new InvalidDataException("User agent returned an empty monitor brightness result.");
            if (!result.Applied || !result.ReadBackVerified)
            {
                throw new InvalidOperationException(result.Message);
            }

            await RefreshMonitorBrightnessCoreAsync(showNotice: false);
            MonitorBrightnessStatus = $"{device.DisplayName}: requested {result.RequestedPercent}%, read back {result.ObservedPercent ?? result.RequestedPercent}% through the selected local transport.";
            ShowNotice("Monitor brightness was applied and read back successfully.", "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryBuildOsdPresentationSettings(out OsdPresentationSettingsV1 settings)
    {
        settings = _osdPresentationSettings;
        if (!TryParseDouble(OsdOpacityText, out double opacityPercent)
            || opacityPercent is < 20 or > 100
            || !TryParseDouble(OsdScaleText, out double scalePercent)
            || scalePercent is < 60 or > 250)
        {
            return false;
        }

        string hotkey = OsdHotkeyText.Trim();
        if (hotkey.Length is 0 or > 32 || !hotkey.Contains('+', StringComparison.Ordinal))
        {
            return false;
        }

        settings = new OsdPresentationSettingsV1(
            OsdPresentationSettingsV1.CurrentSchemaVersion,
            OsdPresentationSettingsV1.DefaultId,
            SelectedOsdMonitor?.StableId,
            SelectedOsdAnchor,
            opacityPercent / 100d,
            scalePercent / 100d,
            hotkey,
            Enabled: true);
        return true;
    }

    private void ApplyOsdPresentationSettings(OsdPresentationSettingsV1 settings)
    {
        _osdPresentationSettings = settings;
        _selectedOsdAnchor = settings.Anchor;
        _osdHotkeyText = settings.Hotkey;
        _osdOpacityText = ((settings.OpacityOverride ?? 0.92d) * 100d).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        _osdScaleText = ((settings.ScaleOverride ?? 1d) * 100d).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        _selectedOsdMonitor = ResolveOsdMonitor(settings.MonitorStableId);
        OnPropertyChanged(nameof(SelectedOsdAnchor));
        OnPropertyChanged(nameof(SelectedOsdMonitor));
        OnPropertyChanged(nameof(OsdHotkeyText));
        OnPropertyChanged(nameof(OsdOpacityText));
        OnPropertyChanged(nameof(OsdScaleText));
        NotifyOsdPresentationProperties();
        OsdHotkeyChanged?.Invoke(settings.Hotkey);
        RefreshDesktopOsd();
    }

    private async Task SaveOsdPresentationCoreAsync()
    {
        if (!TryBuildOsdPresentationSettings(out OsdPresentationSettingsV1 settings))
        {
            throw new InvalidOperationException("Use opacity from 20 to 100%, scale from 60 to 250%, and a short modifier hotkey such as Ctrl+Alt+O.");
        }

        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveOsdPresentationSettings,
                settings,
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        ApplyOsdPresentationSettings(IpcJson.FromElement<OsdPresentationSettingsV1>(response.Payload) ?? settings);
        ShowNotice("Saved local OSD placement and hotkey. The overlay remains non-injecting.", "Success");
    }

    private CaptureTargetV1? ResolveOsdMonitor(string? stableId)
    {
        IReadOnlyList<CaptureTargetV1> monitors = OsdMonitors;
        foreach (CaptureTargetV1 monitor in monitors)
        {
            if (string.Equals(monitor.StableId, stableId, StringComparison.OrdinalIgnoreCase))
            {
                return monitor;
            }
        }
        return monitors.Count > 0 ? monitors[0] : null;
    }

    private async Task SaveMonitoringPreferencesCoreAsync()
    {
        SensorTrendDisplay trend = SelectedMonitoringTrend
            ?? throw new InvalidOperationException("Select a live sensor before saving its monitoring preferences.");
        List<SensorAliasV1> aliases = _monitoringPreferences.Aliases
            .Where(alias => !string.Equals(alias.SensorId, trend.SensorId, StringComparison.Ordinal))
            .ToList();
        string aliasText = SensorAliasText.Trim();
        if (!string.IsNullOrWhiteSpace(aliasText))
        {
            aliases.Add(new SensorAliasV1(trend.SensorId, aliasText));
        }

        HashSet<string> pins = new(_monitoringPreferences.PinnedSensorIds, StringComparer.Ordinal);
        if (SelectedSensorPinned)
        {
            pins.Add(trend.SensorId);
        }
        else
        {
            pins.Remove(trend.SensorId);
        }

        MonitoringPreferencesV1 preferences = new(
            MonitoringPreferencesV1.CurrentSchemaVersion,
            MonitoringPreferencesV1.DefaultId,
            aliases,
            pins.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            DateTimeOffset.UtcNow);
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveMonitoringPreferences,
                preferences,
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        _monitoringPreferences = IpcJson.FromElement<MonitoringPreferencesV1>(response.Payload) ?? preferences;
        UpdateMonitoringTrends();
        ShowNotice("Saved the local sensor alias and pin preference.", "Success");
    }

    private Task AddMonitoringComparisonSensorCoreAsync()
    {
        SensorTrendDisplay trend = SelectedMonitoringComparisonTrend
            ?? throw new InvalidOperationException("Select a live sensor to add to the comparison workspace.");
        if (!CanAddMonitoringComparisonSensor)
        {
            throw new InvalidOperationException("Choose a distinct live sensor and keep the comparison workspace to four series or fewer.");
        }

        _monitoringComparisonLayout = _monitoringComparisonLayout with
        {
            SensorIds = _monitoringComparisonLayout.SensorIds.Concat([trend.SensorId]).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        UpdateMonitoringComparison();
        MonitoringComparisonStatus = "Comparison changed locally. Save it to keep this selection for your signed-in user.";
        return Task.CompletedTask;
    }

    private Task RemoveMonitoringComparisonSensorCoreAsync(SensorComparisonSeriesDisplay series)
    {
        _monitoringComparisonLayout = _monitoringComparisonLayout with
        {
            SensorIds = _monitoringComparisonLayout.SensorIds
                .Where(id => !string.Equals(id, series.SensorId, StringComparison.Ordinal))
                .ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        UpdateMonitoringComparison();
        MonitoringComparisonStatus = "Comparison changed locally. Save it to keep this selection for your signed-in user.";
        return Task.CompletedTask;
    }

    private async Task SaveMonitoringComparisonLayoutCoreAsync()
    {
        MonitoringComparisonLayoutV1 layout = _monitoringComparisonLayout with
        {
            SchemaVersion = MonitoringComparisonLayoutV1.CurrentSchemaVersion,
            Id = MonitoringComparisonLayoutV1.DefaultId,
            SensorIds = MonitoringComparisonSeries.Select(series => series.SensorId).ToArray(),
            NormalizeEachSeries = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveMonitoringComparisonLayout,
                layout,
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        _monitoringComparisonLayout = IpcJson.FromElement<MonitoringComparisonLayoutV1>(response.Payload) ?? layout;
        UpdateMonitoringComparison();
        MonitoringComparisonStatus = MonitoringComparisonSeries.Count == 0
            ? "Saved an empty comparison workspace. Add up to four live sensors when you want to compare movement."
            : $"Saved {MonitoringComparisonSeries.Count} normalized comparison series for this signed-in user.";
        ShowNotice("Saved the local monitoring comparison workspace.", "Success");
    }

    private async Task AddRecommendedHealthRulesCoreAsync()
    {
        IReadOnlyList<HealthRuleRecommendation> recommendations = HealthRuleRecommendations.Build(
            MonitoringTrends.Select(trend => trend.Trend).ToArray());
        HealthRuleRecommendation[] pending = recommendations
            .Where(recommendation => !HealthRules.Any(existing => SameHealthRule(existing.Rule, recommendation.Rule)))
            .ToArray();
        if (pending.Length == 0)
        {
            HealthRecommendationStatus = "All currently applicable notify-only baseline rules are already installed.";
            ShowNotice(HealthRecommendationStatus, "Info");
            return;
        }

        foreach (HealthRuleRecommendation recommendation in pending)
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SaveHealthRule,
                    recommendation.Rule,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(response);
            UpdateStateRevision(response);
        }

        await RefreshReliabilityAsync(_lifetime.Token);
        HealthRecommendationStatus = $"Installed {pending.Length} notify-only baseline rule(s). Review the sensor-specific thresholds before relying on them.";
        ShowNotice(HealthRecommendationStatus, "Success");
    }

    private bool TryBuildHealthRule(out HealthRuleV1 rule)
    {
        rule = default!;
        string name = NewHealthRuleName.Trim();
        bool usesSensor = NewHealthRuleCondition is HealthRuleConditionKind.SensorAbove
            or HealthRuleConditionKind.SensorBelow
            or HealthRuleConditionKind.SensorStale
            or HealthRuleConditionKind.FanBelow;
        bool usesThreshold = NewHealthRuleCondition is HealthRuleConditionKind.SensorAbove
            or HealthRuleConditionKind.SensorBelow
            or HealthRuleConditionKind.FanBelow;
        if (name.Length is 0 or > 96
            || (usesSensor && SelectedHealthTrend is null)
            || !int.TryParse(NewHealthConsecutiveText, out int consecutive)
            || consecutive is < 1 or > 60
            || !int.TryParse(NewHealthCooldownText, out int cooldownSeconds)
            || cooldownSeconds is < 0 or > 604800)
        {
            return false;
        }

        double? threshold = null;
        if (usesThreshold)
        {
            if (!TryParseDouble(NewHealthThresholdText, out double parsedThreshold) || !double.IsFinite(parsedThreshold))
            {
                return false;
            }
            threshold = parsedThreshold;
        }

        string? profileId = NewHealthRuleAction == HealthRuleActionKind.RequestEmergencyProfile
            ? SelectedEmergencyProfile?.Id
            : null;
        if (NewHealthRuleAction == HealthRuleActionKind.RequestEmergencyProfile && string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        rule = new HealthRuleV1(
            HealthRuleV1.CurrentSchemaVersion,
            $"health.{Guid.NewGuid():N}",
            name,
            NewHealthRuleCondition,
            usesSensor ? SelectedHealthTrend!.SensorId : null,
            threshold,
            consecutive,
            TimeSpan.FromSeconds(cooldownSeconds),
            NewHealthRuleAction,
            profileId,
            Enabled: true);
        return HealthRuleEngine.Validate(rule).IsValid;
    }

    private async Task SaveHealthRuleCoreAsync()
    {
        if (!TryBuildHealthRule(out HealthRuleV1 rule))
        {
            throw new InvalidOperationException("Complete the rule name, source, threshold (when required), consecutive observations, cooldown, and emergency profile selection.");
        }
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveHealthRule,
                rule,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshReliabilityAsync(_lifetime.Token);
        ShowNotice($"Saved health rule '{rule.Name}'.", "Success");
    }

    private async Task DeleteHealthRuleCoreAsync(HealthRuleV1 rule)
    {
        DeleteHealthRuleRequestV1 payload = new(DeleteHealthRuleRequestV1.CurrentSchemaVersion, rule.Id);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.DeleteHealthRule,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshReliabilityAsync(_lifetime.Token);
        ShowNotice($"Deleted health rule '{rule.Name}'.", "Info");
    }

    private async Task AcknowledgeHealthAlertCoreAsync(HealthAlertEventV1 alert)
    {
        AcknowledgeHealthAlertRequestV1 payload = new(AcknowledgeHealthAlertRequestV1.CurrentSchemaVersion, alert.Id);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.AcknowledgeHealthAlert,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshReliabilityAsync(_lifetime.Token);
        ShowNotice("Health alert acknowledged. Its condition remains monitored until it clears.", "Info");
    }

    private async Task SetSafeModeCoreAsync(bool enabled)
    {
        string reason = SafeModeReason.Trim();
        if (reason.Length is 0 or > 256)
        {
            throw new InvalidOperationException("Provide a safe-mode reason up to 256 characters.");
        }
        SetSafeModeRequestV1 payload = new(SetSafeModeRequestV1.CurrentSchemaVersion, enabled, reason);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetSafeMode,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        _safetyRecoveryStatus = IpcJson.FromElement<SafetyRecoveryStatusV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty safe-mode state.");
        UpdateStateRevision(response);
        NotifyReliabilityProperties();
        ShowNotice(enabled
            ? "Safe mode is active. Automation and alert-driven profile requests are suspended; no hardware reset was assumed."
            : "Safe mode was exited. Review the recovery guidance before enabling automation or experimental writes.",
            enabled ? "Warning" : "Info");
    }

    private OsdLayoutV1 ResolveDesktopOsdLayout()
    {
        if (SelectedDesktopOsdLayout is not null)
        {
            return SelectedDesktopOsdLayout;
        }
        if (_snapshot is null)
        {
            throw new InvalidOperationException("No hardware snapshot is available for the desktop OSD.");
        }

        SensorSample[] selected = SelectImportantSensors(_snapshot).Take(8).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("No usable sensors are available for the automatic desktop OSD.");
        }
        return new OsdLayoutV1(
            OsdLayoutV1.CurrentSchemaVersion,
            "osd.automatic-live",
            "Automatic live sensors",
            selected.Select((sensor, index) => new OsdWidgetV1(
                sensor.SensorId,
                sensor.Name,
                OsdFormatFor(sensor.Unit),
                index / 2,
                index % 2,
                OsdColourFor(sensor.Unit))).ToArray(),
            0.92,
            1,
            ShowGraph: false);
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

    private async Task StartCalibrationCoreAsync()
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedCalibrationTarget
            ?? throw new InvalidOperationException("Select a cooling control first.");
        if (target.RpmSensorId is null)
        {
            throw new InvalidOperationException("The selected control has no matching RPM sensor.");
        }

        HardwareOperationEligibility eligibility = GetCalibrationEligibility();
        if (!eligibility.Eligible)
        {
            throw new InvalidOperationException(eligibility.Reason);
        }

        BusyMessage = "Starting fan calibration";
        IsBusy = true;
        try
        {
            StartCalibrationRequest payload = new(
                target.Descriptor.Id,
                target.RpmSensorId,
                ConfirmExperimental: AdvancedWritesAcknowledged,
                ConfirmDevice: CalibrationDeviceAcknowledged,
                AllowFanStop: AllowCaseFanStop && !IsSelectedCoolingOutputProtected,
                SettlingTime: TimeSpan.FromSeconds(CalibrationSettlingSeconds),
                StableSampleCount: 3,
                MaximumSampleCount: 15,
                SampleInterval: TimeSpan.FromMilliseconds(500),
                StabilityTolerancePercent: 10,
                RestartVerificationCycles: CalibrationRestartCycleCount,
                TemperatureLimits: BuildCalibrationTemperatureLimits(target.Descriptor),
                CommissioningSessionId: SelectedFanCommissioningSession?.Id);
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.StartCalibration,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            _operation = IpcJson.FromElement<HardwareOperationStatus>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty operation response.");
            NotifyOperationProperties();
            ShowNotice("Calibration started. RigPilot will restore the prior policy when it finishes or is cancelled.", "Warning");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartTuneCoreAsync()
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedTuneTarget
            ?? throw new InvalidOperationException("Select a bounded tuning control first.");
        HardwareOperationEligibility eligibility = GetTuneEligibility();
        if (!eligibility.Eligible)
        {
            throw new InvalidOperationException(eligibility.Reason);
        }

        if (!TryReadTuneLimits(out double temperatureCeiling, out double? powerCeiling))
        {
            throw new InvalidOperationException("Enter a temperature ceiling from 40 to 100 °C and an optional positive power ceiling.");
        }

        TunePlan plan = CreateTunePlan(target, temperatureCeiling, powerCeiling);
        TuneDirection direction = target.Descriptor.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
            ? TuneDirection.Minimize
            : SelectedTuneObjective == TuningObjective.Performance
            ? TuneDirection.Maximize
            : TuneDirection.Minimize;
        BusyMessage = "Starting bounded auto-tuning";
        IsBusy = true;
        try
        {
            StartTuneRequest payload = new(
                plan,
                target.Descriptor.Id,
                direction,
                AdvancedWritesAcknowledged,
                TuneDeviceAcknowledged,
                CandidateScreeningTime: TimeSpan.FromSeconds(30),
                MaximumCandidates: 12);
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.StartTune,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            _operation = IpcJson.FromElement<HardwareOperationStatus>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty operation response.");
            NotifyOperationProperties();
            ShowNotice("Auto-tuning started. Candidates are bounded and the prior state will be restored after screening.", "Warning");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AbortOperationCoreAsync()
    {
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.AbortOperation,
            (string?)null,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        _operation = IpcJson.FromElement<HardwareOperationStatus>(response.Payload) ?? _operation;
        NotifyOperationProperties();
        ShowNotice("Cancellation requested. Hardware restoration is still in progress.", "Warning");
    }

    private CoolingOutputAssignmentV1? GetSelectedCoolingOutputAssignment() =>
        SelectedCalibrationTarget is OperationTargetDisplay target
            ? GetCoolingOutputAssignment(target.Descriptor)
            : null;

    private CoolingOutputAssignmentV1? GetCoolingOutputAssignment(CapabilityDescriptor capability) =>
        CoolingOutputAssignments.FirstOrDefault(assignment =>
            string.Equals(assignment.CapabilityId, capability.Id, StringComparison.Ordinal));

    private void ApplyCoolingOutputAssignmentForTarget()
    {
        CoolingOutputAssignmentV1? assignment = GetSelectedCoolingOutputAssignment();
        OperationTargetDisplay? target = SelectedCalibrationTarget;
        _selectedCoolingOutputRole = assignment?.Role ?? CoolingOutputRole.Unknown;
        _coolingOutputHeaderName = assignment?.HeaderName ?? target?.Descriptor.Name ?? string.Empty;
        _removeCoolingSafetyProtectionAcknowledged = false;
        if (assignment is { IsSafetyCritical: true })
        {
            _allowCaseFanStop = false;
            OnPropertyChanged(nameof(AllowCaseFanStop));
        }

        OnPropertyChanged(nameof(SelectedCoolingOutputRole));
        OnPropertyChanged(nameof(CoolingOutputHeaderName));
        OnPropertyChanged(nameof(RemoveCoolingSafetyProtectionAcknowledged));
        NotifyCoolingOutputAssignmentProperties();
    }

    private void NotifyCoolingOutputAssignmentProperties()
    {
        OnPropertyChanged(nameof(IsSelectedCoolingOutputProtected));
        OnPropertyChanged(nameof(CanAllowCaseFanStop));
        OnPropertyChanged(nameof(CanSaveCoolingOutputAssignment));
        OnPropertyChanged(nameof(CoolingOutputRoleStatus));
        OnPropertyChanged(nameof(CommissioningTargetSummary));
        OnPropertyChanged(nameof(CommissioningPreflight));
        _saveCoolingOutputAssignmentCommand.RaiseCanExecuteChanged();
        NotifyOperationEligibility();
    }

    private void SelectCommissioningForTarget()
    {
        string? capabilityId = SelectedCalibrationTarget?.Descriptor.Id;
        FanCommissioningSessionV1? next = capabilityId is null
            ? null
            : FanCommissioningSessions
                .Where(session => string.Equals(session.CapabilityId, capabilityId, StringComparison.Ordinal))
                .OrderByDescending(session => session.UpdatedAt)
                .FirstOrDefault();
        _selectedFanCommissioningSession = next;
        if (next is null && SelectedCalibrationTarget is OperationTargetDisplay target)
        {
            _commissioningHeaderName = target.Descriptor.Name;
            _commissioningNotes = string.Empty;
            _commissioningHeaderConfirmed = false;
            OnPropertyChanged(nameof(CommissioningHeaderName));
            OnPropertyChanged(nameof(CommissioningNotes));
            OnPropertyChanged(nameof(CommissioningHeaderConfirmed));
        }
        else if (next is not null)
        {
            _commissioningHeaderName = next.HeaderName;
            _commissioningNotes = next.Notes ?? string.Empty;
            _commissioningHeaderConfirmed = next.PhysicalHeaderObserved;
            OnPropertyChanged(nameof(CommissioningHeaderName));
            OnPropertyChanged(nameof(CommissioningNotes));
            OnPropertyChanged(nameof(CommissioningHeaderConfirmed));
        }
        OnPropertyChanged(nameof(SelectedFanCommissioningSession));
        NotifyCommissioningProperties();
    }

    private void UpsertCommissioningSession(FanCommissioningSessionV1 session, bool select)
    {
        FanCommissioningSessionV1? existing = FanCommissioningSessions.FirstOrDefault(item => item.Id == session.Id);
        if (existing is not null)
        {
            FanCommissioningSessions.Remove(existing);
        }
        FanCommissioningSessions.Add(session);
        if (select)
        {
            SelectedFanCommissioningSession = session;
        }
        NotifyCommissioningProperties();
    }

    private void UpdateStateRevision(IpcResponse response)
    {
        if (_status is not null)
        {
            _status = _status with { StateRevision = response.StateRevision };
            OnPropertyChanged(nameof(StateRevisionText));
        }
    }

    private static int[] ParseLightingLedIndices(string input, int lampCount)
    {
        if (lampCount <= 0)
        {
            throw new InvalidOperationException("The selected Dynamic Lighting device reports no controllable lamps.");
        }
        HashSet<int> values = [];
        foreach (string token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] range = token.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out int start))
            {
                throw new InvalidOperationException("LED indices must use values such as '0-15, 20, 22-24'.");
            }
            int last = start;
            if (range.Length == 2 && !int.TryParse(range[1], out last))
            {
                throw new InvalidOperationException("LED indices must use values such as '0-15, 20, 22-24'.");
            }
            if (start < 0 || last < start || last >= lampCount || last - start > 2_048)
            {
                throw new InvalidOperationException($"LED indices must be within 0 to {lampCount - 1}.");
            }
            for (int index = start; index <= last; index++)
            {
                values.Add(index);
            }
        }
        return values.Order().ToArray();
    }

    private HardwareOperationEligibility GetCalibrationEligibility()
    {
        if (!CanUseServiceWrites)
        {
            return HardwareOperationEligibility.Deny(ServiceCompatibilityMessage);
        }

        if (SelectedCalibrationTarget is not OperationTargetDisplay target)
        {
            return HardwareOperationEligibility.Deny("Select a detected cooling control.");
        }

        if (target.RpmSensorId is null)
        {
            return HardwareOperationEligibility.Deny("No RPM sensor from the same adapter and exact device could be paired with this control.");
        }

        if (GetCoolingOutputAssignment(target.Descriptor)?.Role == CoolingOutputRole.Pump
            || SelectedCoolingOutputRole == CoolingOutputRole.Pump)
        {
            return HardwareOperationEligibility.Deny("Pump calibration is blocked until an exact device-specific nonzero-floor qualification path exists.");
        }

        if (SelectedFanCommissioningSession is not { } session
            || !string.Equals(session.CapabilityId, target.Descriptor.Id, StringComparison.Ordinal))
        {
            return HardwareOperationEligibility.Deny("Select the matching commissioning session before starting a bounded calibration.");
        }

        if (!FanCommissioningWorkflow.CanRunCalibration(session, out string? commissioningReason))
        {
            return HardwareOperationEligibility.Deny(commissioningReason!);
        }

        if (AllowCaseFanStop
            && (IsSelectedCoolingOutputProtected
                || target.Descriptor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || target.Descriptor.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)))
        {
            return HardwareOperationEligibility.Deny("Zero-RPM calibration is forbidden for CPU fans and pumps.");
        }

        return HardwareOperationEligibilityEvaluator.ForCalibration(
            target.Descriptor,
            AdvancedWritesAcknowledged,
            CalibrationDeviceAcknowledged);
    }

    private FanCalibrationTemperatureLimit[] BuildCalibrationTemperatureLimits(
        CapabilityDescriptor capability) => (_snapshot?.Sensors ?? [])
        .Where(sensor => string.Equals(sensor.AdapterId, capability.AdapterId, StringComparison.Ordinal)
            && string.Equals(sensor.DeviceId, capability.DeviceId, StringComparison.Ordinal)
            && string.Equals(sensor.Unit, "°C", StringComparison.OrdinalIgnoreCase)
            && sensor.Quality == SensorQuality.Good
            && sensor.Value.HasValue
            && !sensor.Name.Contains("Critical Temperature", StringComparison.OrdinalIgnoreCase)
            && !sensor.Name.Contains("Warning Temperature", StringComparison.OrdinalIgnoreCase))
        .OrderBy(sensor => sensor.Name, StringComparer.Ordinal)
        .Take(8)
        .Select(sensor => new FanCalibrationTemperatureLimit(
            sensor.SensorId,
            sensor.Name.Contains("Memory Junction", StringComparison.OrdinalIgnoreCase)
                ? 90
                : sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                    ? 85
                    : sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)
                        ? 80
                        : 90))
        .ToArray();

    private HardwareOperationEligibility GetTuneEligibility()
    {
        if (!CanUseServiceWrites)
        {
            return HardwareOperationEligibility.Deny(ServiceCompatibilityMessage);
        }

        if (SelectedTuneTarget is not OperationTargetDisplay target)
        {
            return HardwareOperationEligibility.Deny("Select a bounded cooling, CPU, or GPU control.");
        }

        if (GetCoolingOutputAssignment(target.Descriptor) is { IsSafetyCritical: true })
        {
            return HardwareOperationEligibility.Deny("Automatic tuning is unavailable for a persisted CPU-fan or pump output.");
        }

        if (!TryReadTuneLimits(out double temperatureCeiling, out double? powerCeiling))
        {
            return HardwareOperationEligibility.Deny(
                "Enter a temperature ceiling from 40 to 100 °C and an optional positive power ceiling.");
        }

        return HardwareOperationEligibilityEvaluator.ForTuning(
            target.Descriptor,
            CreateTunePlan(target, temperatureCeiling, powerCeiling),
            AdvancedWritesAcknowledged,
            TuneDeviceAcknowledged);
    }

    private TunePlan CreateTunePlan(
        OperationTargetDisplay target,
        double temperatureCeiling,
        double? powerCeiling)
    {
        NumericRange range = target.Descriptor.Range
            ?? throw new InvalidOperationException("The selected capability has no numeric bounds.");
        double minimum = target.Descriptor.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
            && SelectedTuneObjective == TuningObjective.Performance
                ? range.Maximum
                : range.Minimum;
        return new TunePlan(
            Guid.NewGuid().ToString("N"),
            target.Descriptor.DeviceId,
            SelectedTuneObjective,
            new Dictionary<string, TuneBounds>(StringComparer.Ordinal)
            {
                [target.Descriptor.Id] = new TuneBounds(minimum, range.Maximum, range.Step)
            },
            TimeSpan.FromMinutes(10),
            temperatureCeiling,
            powerCeiling,
            Provisional: true,
            SoakStartedAt: null,
            ActiveUseRequired: TimeSpan.FromHours(10),
            ColdBootsRequired: 3);
    }

    private bool TryReadTuneLimits(out double temperatureCeiling, out double? powerCeiling)
    {
        powerCeiling = null;
        if (!TryParseDouble(TuneTemperatureCeilingText, out temperatureCeiling)
            || temperatureCeiling is < 40 or > 100)
        {
            return false;
        }

        string powerText = TunePowerCeilingText.Trim();
        if (powerText.Length == 0)
        {
            return true;
        }

        if (!TryParseDouble(powerText, out double parsedPower) || parsedPower <= 0)
        {
            return false;
        }

        powerCeiling = parsedPower;
        return true;
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

    private void EnsureServiceWritesAvailable()
    {
        if (!CanUseServiceWrites)
        {
            throw new InvalidOperationException(ServiceCompatibilityMessage);
        }
    }

    private async Task ProbeDynamicLightingCoreAsync()
    {
        BusyMessage = "Enumerating Windows Dynamic Lighting devices";
        IsBusy = true;
        try
        {
            IReadOnlyList<DynamicLightingDevice> devices = await DynamicLightingBridge.ProbeAsync(_lifetime.Token);
            Replace(DynamicLightingDevices, devices);
            DynamicLightingDevice? matchingDevice = devices.FirstOrDefault(device => device.Id == SelectedDynamicLightingDevice?.Id);
            SelectedDynamicLightingDevice = matchingDevice ?? (devices.Count == 0 ? null : devices[0]);
            DynamicLightingStatus = devices.Count == 0
                ? "Windows reported no LampArray-compatible devices."
                : $"Windows reported {devices.Count} Dynamic Lighting device(s) and {devices.Sum(device => device.LampCount)} lamps.";
            RebuildRgbRouteAssessments();
            OnPropertyChanged(nameof(DynamicLightingDeviceCount));
            _applyDynamicLightingSceneCommand.RaiseCanExecuteChanged();
            ShowNotice(DynamicLightingStatus, devices.Count == 0 ? "Info" : "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProbeOpenRgbCoreAsync()
    {
        if (!OpenRgbEnabled)
        {
            throw new InvalidOperationException("Enable the OpenRGB bridge before connecting.");
        }

        if (HasLightingConflict)
        {
            throw new InvalidOperationException(LightingConflictReason);
        }

        BusyMessage = "Negotiating with the local OpenRGB SDK server";
        IsBusy = true;
        try
        {
            OpenRgbSdkClient client = new();
            OpenRgbConnectionResult result = await client.ProbeAsync(_lifetime.Token);
            SetOpenRgbControllers(result.Controllers);
            OpenRgbConnected = true;
            OpenRgbStatus = result.Message;
            ShowNotice(result.Message, "Success");
        }
        catch (Exception exception)
        {
            OpenRgbConnected = false;
            SetOpenRgbControllers([]);
            OpenRgbStatus = $"Connection failed: {exception.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyOpenRgbCoreAsync(bool turnOff)
    {
        if (!OpenRgbEnabled || !OpenRgbConnected)
        {
            throw new InvalidOperationException("Connect to the local OpenRGB SDK server first.");
        }

        if (HasLightingConflict)
        {
            throw new InvalidOperationException(LightingConflictReason);
        }

        if (!TryParseOpenRgbInputs(out string colour, out int brightness))
        {
            throw new InvalidOperationException("Use a #RRGGBB colour and brightness from 0 to 100%.");
        }

        if (turnOff)
        {
            brightness = 0;
        }

        BusyMessage = turnOff ? "Turning OpenRGB lighting off" : "Applying OpenRGB lighting";
        IsBusy = true;
        try
        {
            OpenRgbSdkClient client = new();
            OpenRgbConnectionResult result = await client.SetStaticColourAsync(
                colour,
                brightness,
                _lifetime.Token);
            SetOpenRgbControllers(result.Controllers);
            OpenRgbConnected = true;
            OpenRgbStatus = turnOff
                ? $"Lighting off on {result.Controllers.Count} controller(s)."
                : result.Message;
            ShowNotice(OpenRgbStatus, "Success");
        }
        catch (Exception exception)
        {
            OpenRgbConnected = false;
            SetOpenRgbControllers([]);
            OpenRgbStatus = $"Lighting update failed: {exception.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryParseOpenRgbInputs(out string colour, out int brightness)
    {
        colour = OpenRgbColour.Trim();
        string hex = colour.TrimStart('#');
        bool validColour = hex.Length == 6
            && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _);
        bool validBrightness = int.TryParse(
            OpenRgbBrightnessText,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.CurrentCulture,
            out brightness)
            && brightness is >= 0 and <= 100;
        if (validColour && !colour.StartsWith('#'))
        {
            colour = $"#{colour}";
        }

        return validColour && validBrightness;
    }

    private void SetOpenRgbControllers(IEnumerable<OpenRgbController> controllers)
    {
        _openRgbControllers.Clear();
        _openRgbControllers.AddRange(controllers
            .GroupBy(controller => controller.Id)
            .Select(group => group.First())
            .OrderBy(controller => controller.Name, StringComparer.OrdinalIgnoreCase));
        OpenRgbControllerCount = _openRgbControllers.Count;
        RebuildRgbRouteAssessments();
    }

    private void RebuildRgbRouteAssessments()
    {
        IReadOnlyList<RgbBridgeEndpoint> dynamicLighting = DynamicLightingDevices
            .Select(device => new RgbBridgeEndpoint(
                device.Id,
                device.Name,
                ResolveRgbFamilyLabel(device.Name),
                device.LampCount,
                device.IsEnabled,
                device.Kind))
            .ToArray();
        IReadOnlyList<RgbBridgeEndpoint> openRgb = _openRgbControllers
            .Select(controller => new RgbBridgeEndpoint(
                controller.Id.ToString(CultureInfo.InvariantCulture),
                controller.Name,
                ResolveRgbFamilyLabel(controller.Name),
                controller.LedCount,
                IsEnabled: true,
                "Enumerated by the local OpenRGB SDK."))
            .ToArray();
        Replace(
            RgbRouteAssessments,
            RgbRoutingPolicy.Assess(
                _snapshot,
                dynamicLighting,
                openRgb,
                OpenRgbEnabled,
                OpenRgbConnected));
        NotifyRgbRoutingProperties();
    }

    private static string? ResolveRgbFamilyLabel(string name)
    {
        HardwareCompatibilityMatch match = HardwareCompatibilityCatalog.ClassifyPeripheral(null, name, null);
        return match.IsRecognized ? match.DisplayName : null;
    }

    private void UpdateDisplays()
    {
        if (_snapshot is null)
        {
            return;
        }

        UpdateMonitoringTrends();
        ActiveProfileName = Profiles.FirstOrDefault(profile => profile.Id == _status?.ActiveProfileId)?.Name ?? "None";
        Replace(ProfileCards, Profiles.Select(profile =>
        {
            _suiteProfilesById.TryGetValue(profile.Id, out ProfileV2? suiteProfile);
            return ProfileCardDisplay.From(profile, profile.Id == _status?.ActiveProfileId, suiteProfile);
        }));

        Replace(ImportantSensors, SelectImportantSensors(_snapshot).Select(sensor => new SensorDisplay(
            sensor.Name,
            FindDevice(sensor.DeviceId),
            FormatSensorValue(sensor),
            TemperatureSeverity(sensor),
            SensorGlyph(sensor.Unit))));

        _allDevices.Clear();
        _allDevices.AddRange(_snapshot.Devices
            .OrderBy(device => DeviceRank(device.Kind))
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(DeviceDisplay.From));
        ApplyDeviceFilter();

        Replace(CoolingCapabilities, _snapshot.Capabilities
            .Where(IsCoolingCapability)
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CapabilityDisplay.From));
        Replace(PerformanceCapabilities, _snapshot.Capabilities
            .Where(capability => !IsCoolingCapability(capability) && capability.Domain != ControlDomain.Lighting)
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CapabilityDisplay.From));
        Replace(CapabilityDecisions, _snapshot.Capabilities
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Domain)
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CapabilityDisplay.From));
        UpdateOperationTargets();
        RebuildExperimentalControlCenter();

        List<DiagnosticDisplay> diagnostics = _snapshot.Warnings.Select(DiagnosticDisplay.From).ToList();
        diagnostics.AddRange(_snapshot.Conflicts.Where(conflict => conflict.IsRunning).Select(DiagnosticDisplay.From));
        Replace(Diagnostics, diagnostics.OrderBy(DiagnosticDisplay.Rank).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase));
        Replace(AdapterHealth, _snapshot.AdapterHealth
            .OrderBy(health => health.Healthy ? 1 : 0)
            .ThenBy(health => health.AdapterId, StringComparer.OrdinalIgnoreCase)
            .Select(AdapterHealthDisplay.From));
        if (OpenRgbEnabled && HasLightingConflict)
        {
            OpenRgbStatus = LightingConflictReason;
        }

        RebuildRgbRouteAssessments();

        UpdateSafetySummary();

        RefreshDesktopOsd();
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
                AdvancedWritesAcknowledged)));
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
            .WithPinned(_monitoringPreferences.PinnedSensorIds.Contains(trend.SensorId, StringComparer.Ordinal))));
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
        Replace(VisibleMonitoringTrends, MonitoringTrends.Where(item => included.Contains(item.SensorId)));
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
            .Select((id, index) => SensorComparisonSeriesDisplay.From(trendsById[id], index)));
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
        Replace(CalibrationTargets, calibrationTargets);
        Replace(TuneTargets, tuneTargets);

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

        Replace(Devices, filtered);
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
        OnPropertyChanged(nameof(HasImportantSensors));
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
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(WriteStateLabel));
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
        OnPropertyChanged(nameof(LightingConflictReason));
        OnPropertyChanged(nameof(HasDynamicLightingConflict));
        OnPropertyChanged(nameof(DynamicLightingConflictReason));
        OnPropertyChanged(nameof(OpenRgbConnectionLabel));
        OnPropertyChanged(nameof(AreOpenRgbInputsValid));
        _probeOpenRgbCommand.RaiseCanExecuteChanged();
        _applyOpenRgbCommand.RaiseCanExecuteChanged();
        _turnOffOpenRgbCommand.RaiseCanExecuteChanged();
        _applyDynamicLightingSceneCommand.RaiseCanExecuteChanged();
    }

    private void NotifyRgbRoutingProperties()
    {
        OnPropertyChanged(nameof(HasRgbRouteAssessments));
        OnPropertyChanged(nameof(RgbReadyRouteCount));
        OnPropertyChanged(nameof(RgbSetupRouteCount));
        OnPropertyChanged(nameof(RgbReadOnlyRouteCount));
        OnPropertyChanged(nameof(RgbBlockedRouteCount));
        OnPropertyChanged(nameof(RgbCompatibilitySummary));
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

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");

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

        return name.Contains("hot spot", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
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
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }

    private void ReportError(Exception exception) => ShowNotice(exception.Message, "Error");

    private void DismissNotice()
    {
        HasNotice = false;
        NoticeText = string.Empty;
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

public sealed record OperationTargetDisplay(
    CapabilityDescriptor Descriptor,
    string DisplayName,
    string State,
    string Range,
    string Reason,
    string? RpmSensorId,
    bool IsAvailable,
    bool IsExperimental)
{
    public static OperationTargetDisplay From(
        CapabilityDescriptor capability,
        string deviceName,
        string? rpmSensorId)
    {
        string range = capability.Range is NumericRange numeric
            ? $"{numeric.Minimum:0.##}–{numeric.Maximum:0.##} {capability.Unit}".TrimEnd()
            : "No numeric range";
        bool available = capability.State is CapabilityAccessState.Verified or CapabilityAccessState.Experimental
            && capability.CanResetToDefault
            && capability.Range is not null;
        return new OperationTargetDisplay(
            capability,
            $"{capability.Name} · {deviceName}",
            SplitWords(capability.State.ToString()),
            range,
            capability.Reason,
            rpmSensorId,
            available,
            capability.State == CapabilityAccessState.Experimental);
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record AutomationRuleDisplay(
    AutomationRuleV1 Rule,
    string Name,
    string Trigger,
    string Profile,
    string Priority,
    string Status)
{
    public static AutomationRuleDisplay From(AutomationRuleV1 rule) => new(
        rule,
        rule.Name,
        $"{SplitWords(rule.TriggerKind.ToString())} · {rule.TriggerValue}",
        rule.ProfileId,
        rule.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
        rule.Enabled ? "Enabled" : "Disabled");

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record SensorDisplay(string Name, string Device, string DisplayValue, string Severity, string Glyph);

public sealed record HidDeviceDisplay(string ProductName, string Identity, string Classes);

public sealed record ProfileCardDisplay(
    ProfileV1 Profile,
    string Name,
    string Description,
    string Objective,
    string Glyph,
    string ActionSummary,
    string StatusLabel,
    bool IsActive,
    bool IsExperimental,
    bool RequiresManualAcknowledgement)
{
    public static ProfileCardDisplay From(ProfileV1 profile, bool active, ProfileV2? suiteProfile = null)
    {
        (string objective, string glyph) = profile.Id.ToLowerInvariant() switch
        {
            "quiet" => ("Lower acoustic target", "\uE708"),
            "performance" => ("Prioritise sustained output", "\uE945"),
            _ => ("Everyday efficiency", "\uE9D2")
        };
        int manualOnlyCount = suiteProfile?.ManualOnlyActionIds.Count ?? 0;
        bool hasCoolingGraph = !string.IsNullOrWhiteSpace(suiteProfile?.CoolingGraphId);
        string actionSummary = profile.Actions.Count == 0
            ? hasCoolingGraph
                ? "Calibrated cooling graph; apply manually"
                : "No hardware writes in this build"
            : manualOnlyCount > 0
                ? $"{profile.Actions.Count} typed action{(profile.Actions.Count == 1 ? string.Empty : "s")} · {manualOnlyCount} Manual Only"
            : $"{profile.Actions.Count} typed action{(profile.Actions.Count == 1 ? string.Empty : "s")}";
        return new ProfileCardDisplay(
            profile,
            profile.Name,
            profile.Description,
            objective,
            glyph,
            actionSummary,
            active ? "Active" : manualOnlyCount > 0 ? "Manual only" : profile.IsExperimental ? "Experimental" : "Stock-safe",
            active,
            profile.IsExperimental,
            manualOnlyCount > 0);
    }
}

public sealed record DeviceDisplay(
    string Id,
    string Name,
    string Kind,
    string Manufacturer,
    string Model,
    string Details,
    string Glyph,
    string? CompatibilityLabel,
    string SearchText)
{
    public static DeviceDisplay From(HardwareDevice device)
    {
        string manufacturer = string.IsNullOrWhiteSpace(device.Manufacturer) ? "Unknown manufacturer" : device.Manufacturer;
        string model = string.IsNullOrWhiteSpace(device.Model) ? "Model not reported" : device.Model;
        device.Properties.TryGetValue("compatibilityLabel", out string? compatibilityLabel);
        device.Properties.TryGetValue("boardPartnerLabel", out string? boardPartnerLabel);
        string details = string.IsNullOrWhiteSpace(compatibilityLabel)
            ? $"{manufacturer} \u00B7 {model}"
            : $"{manufacturer} \u00B7 {model} · {compatibilityLabel}";
        if (!string.IsNullOrWhiteSpace(boardPartnerLabel))
        {
            details = string.Concat(details, " \u00B7 ", boardPartnerLabel);
        }
        string glyph = device.Kind switch
        {
            DeviceKind.Cpu => "\uE950",
            DeviceKind.Gpu => "\uE7F4",
            DeviceKind.Motherboard or DeviceKind.Bios => "\uE950",
            DeviceKind.Memory => "\uE964",
            DeviceKind.Storage => "\uEDA2",
            DeviceKind.Network => "\uE968",
            DeviceKind.Cooling => "\uE9CA",
            DeviceKind.Lighting => "\uE706",
            DeviceKind.OperatingSystem => "\uE782",
            _ => "\uE772"
        };
        return new DeviceDisplay(
            device.Id,
            device.Name,
            SplitWords(device.Kind.ToString()),
            manufacturer,
            model,
            details,
            glyph,
            compatibilityLabel,
            $"{device.Name} {device.Kind} {manufacturer} {model} {compatibilityLabel} {string.Join(' ', device.Properties.Values)}");
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record CapabilityDisplay(
    CapabilityAccessState AccessState,
    string Name,
    string State,
    string Reason,
    string Range,
    string Evidence,
    string Domain,
    string Risk,
    string Owner,
    string NextSafeStep,
    string StateTone)
{
    public static CapabilityDisplay From(CapabilityDescriptor capability)
    {
        string range = capability.Range is NumericRange numeric
            ? $"{numeric.Minimum:0.##}\u2013{numeric.Maximum:0.##} {capability.Unit}".TrimEnd()
            : capability.ValueKind.ToString();
        string owner = string.IsNullOrWhiteSpace(capability.ConflictOwner)
            ? "No competing writer detected"
            : $"Blocked by {capability.ConflictOwner}";
        string nextSafeStep = GetNextSafeStep(capability);
        string tone = capability.State switch
        {
            CapabilityAccessState.Verified => "Safe",
            CapabilityAccessState.Experimental => "Warning",
            CapabilityAccessState.Blocked or CapabilityAccessState.Faulted => "Critical",
            _ => "Neutral"
        };
        return new CapabilityDisplay(
            capability.State,
            capability.Name,
            SplitWords(capability.State.ToString()),
            capability.Reason,
            range,
            SplitWords(capability.Evidence.ToString()),
            SplitWords(capability.Domain.ToString()),
            capability.Risk.ToString(),
            owner,
            nextSafeStep,
            tone);
    }

    private static string GetNextSafeStep(CapabilityDescriptor capability) => capability.State switch
    {
        CapabilityAccessState.Verified => capability.CanResetToDefault
            ? "Use only within the published bounds; firmware/default reset is available."
            : "Use only within the published bounds; reset evidence is still limited.",
        CapabilityAccessState.Experimental => "Keep this control manual and exact-device scoped until apply, read-back, reset, and fault-screening evidence passes.",
        CapabilityAccessState.Blocked when !string.IsNullOrWhiteSpace(capability.ConflictOwner) =>
            "Resolve the named competing writer through the ownership workflow. Never terminate a process by name alone.",
        CapabilityAccessState.Blocked => "Resolve the stated driver, firmware, bounds, or reset gate before any write can be considered.",
        CapabilityAccessState.ReadOnly => "Telemetry and inventory are available; no reviewed write endpoint is published for this exact device.",
        CapabilityAccessState.Unsupported => "No supported adapter path exists for this exact device and software version.",
        CapabilityAccessState.Faulted => "Use recovery and diagnostics, restore firmware/default control, then collect a new exact-device trace.",
        _ => "Review the capability evidence before changing hardware state."
    };

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

/// <summary>
/// Presentation model for the Advanced Lab Experimental Control Center. It
/// makes the current gates explicit without promoting an inventory item to a
/// write capability. Only bounded, resettable, non-protected motherboard-fan
/// outputs can be routed into the existing commissioning wizard.
/// </summary>
public sealed record ExperimentalControlDisplay(
    CapabilityDescriptor Descriptor,
    string Name,
    string Device,
    string Domain,
    string Range,
    string Evidence,
    string Path,
    string Readiness,
    string NextSafeStep,
    bool IsCoolingControl,
    bool IsGpuCoolingControl,
    bool IsProtected,
    bool CanOpenCoolingCommissioning,
    string Tone)
{
    public static ExperimentalControlDisplay From(
        CapabilityDescriptor capability,
        string deviceName,
        CoolingOutputAssignmentV1? assignment,
        bool serviceWritePathReady,
        bool sessionAcknowledged)
    {
        bool cooling = capability.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
            || capability.Name.Contains("fan", StringComparison.OrdinalIgnoreCase)
            || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase);
        bool gpuCooling = cooling
            && (capability.Name.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || capability.DeviceId.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || capability.AdapterId.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("geforce", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("radeon", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("arc", StringComparison.OrdinalIgnoreCase));
        bool protectedByRole = CoolingOutputAssignmentPolicy.IsProtected(assignment, capability);
        bool protectedByName = capability.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase);
        bool protectedOutput = protectedByRole || protectedByName;
        bool boundedResetPath = capability.ValueKind == ControlValueKind.Numeric
            && capability.Range is not null
            && capability.CanResetToDefault;
        bool competingWriter = !string.IsNullOrWhiteSpace(capability.ConflictOwner);
        bool canCommission = capability.State == CapabilityAccessState.Experimental
            && cooling
            && !gpuCooling
            && !protectedOutput
            && !competingWriter
            && boundedResetPath;
        string range = capability.Range is NumericRange numeric
            ? $"{numeric.Minimum:0.##}\u2013{numeric.Maximum:0.##} {capability.Unit}".TrimEnd()
            : "No numeric range";
        string path = protectedOutput
            ? "Safety protected"
            : gpuCooling
                ? "GPU validation"
                : canCommission
                    ? "Header commissioning"
                    : "Evidence required";
        string readiness = protectedOutput
            ? $"Protected as {SplitWords(assignment?.Role.ToString() ?? "CPU/Pump")}; commissioning and fan-stop remain unavailable."
            : gpuCooling
                ? "GPU fan validation is separate from chassis-header commissioning; the conservative floor remains in force."
                : competingWriter
                    ? $"Blocked by {capability.ConflictOwner}; resolve ownership before any write workflow."
                    : !boundedResetPath
                        ? "This adapter does not publish the bounded reset path required for commissioning."
                        : !serviceWritePathReady
                            ? "Service write path is not ready. Evidence can be reviewed, but commissioning is unavailable."
                            : !sessionAcknowledged
                                ? "Session acknowledgement required; no hardware command has been authorised."
                                : "Ready to select in Cooling. Exact-device confirmation, a physical header, RPM pairing, and a witnessed pulse are still required.";
        string nextSafeStep = protectedOutput
            ? "Keep the current safety role. A pump or CPU-fan output cannot use this commissioning path."
            : gpuCooling
                ? "Keep the conservative GPU fan floor. Complete repeated direct restart validation before any lower floor is considered."
                : competingWriter
                    ? "Review the exact competing writer in Devices; never terminate a process by name alone."
                    : !boundedResetPath
                        ? "Collect adapter evidence with bounds, read-back, and reset behaviour before adding a write path."
                    : "Open Cooling, select this exact control, enter its physical chassis header, and begin setup. No hardware command is sent by this selection.";
        string tone = protectedOutput ? "Critical" : canCommission ? "Warning" : "Neutral";
        return new ExperimentalControlDisplay(
            capability,
            capability.Name,
            deviceName,
            SplitWords(capability.Domain.ToString()),
            range,
            SplitWords(capability.Evidence.ToString()),
            path,
            readiness,
            nextSafeStep,
            cooling,
            gpuCooling,
            protectedOutput,
            canCommission,
            tone);
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record DiagnosticDisplay(string Title, string Message, string Severity, string Remediation, string Glyph)
{
    public static DiagnosticDisplay From(DiagnosticWarning warning) => new(
        warning.Code,
        warning.Message,
        NormaliseSeverity(warning.Severity),
        warning.Remediation ?? "Review the Devices page for capability evidence.",
        warning.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ? "\uEA39" : "\uE7BA");

    public static DiagnosticDisplay From(ConflictDescriptor conflict) => new(
        $"{conflict.DisplayName} is running",
        $"Overlapping control families: {string.Join(", ", conflict.ResourceFamilies)}.",
        "Warning",
        conflict.Guidance,
        "\uE7BA");

    public static int Rank(DiagnosticDisplay item) => item.Severity switch
    {
        "Critical" => 0,
        "Warning" => 1,
        _ => 2
    };

    private static string NormaliseSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" or "error" => "Critical",
        "warning" or "warn" => "Warning",
        _ => "Info"
    };
}

public sealed record AdapterHealthDisplay(string Name, string Status, string Message, string Checked, bool Healthy)
{
    public static AdapterHealthDisplay From(AdapterHealth health) => new(
        health.AdapterId,
        health.Healthy ? "Healthy" : "Needs attention",
        health.Message,
        health.CheckedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        health.Healthy);
}

public sealed record SensorTrendDisplay(
    SensorTrendV1 Trend,
    string SensorId,
    string DisplayName,
    string Unit,
    string Latest,
    string Range,
    string Sparkline,
    bool IsPinned)
{
    public static SensorTrendDisplay From(SensorTrendV1 trend) => new(
        trend,
        trend.SensorId,
        trend.DisplayName,
        trend.Unit,
        trend.Latest is double latest
            ? $"{latest:0.##} {trend.Unit}".TrimEnd()
            : "Unavailable",
        trend.Minimum is double minimum && trend.Maximum is double maximum
            ? $"{minimum:0.##}–{maximum:0.##} {trend.Unit}".TrimEnd()
            : "No range",
        trend.Sparkline,
        false);

    public SensorTrendDisplay WithPinned(bool pinned) => this with { IsPinned = pinned };
}

/// <summary>
/// Presentation-only normalized series for the dashboard comparison workspace.
/// It preserves the actual value/range in the legend so unrelated units are
/// never represented as directly comparable magnitudes.
/// </summary>
public sealed record SensorComparisonSeriesDisplay(
    string SensorId,
    string DisplayName,
    string Latest,
    string Range,
    string Unit,
    WpfPointCollection Points,
    WpfBrush Stroke,
    IReadOnlyList<double> Values)
{
    private static readonly WpfColor[] Palette =
    [
        WpfColor.FromRgb(0x68, 0xB0, 0xFF),
        WpfColor.FromRgb(0x5B, 0xD6, 0xB3),
        WpfColor.FromRgb(0xE4, 0xB7, 0x5B),
        WpfColor.FromRgb(0xC2, 0x8C, 0xFF)
    ];

    public static SensorComparisonSeriesDisplay From(SensorTrendDisplay trend, int index)
    {
        SensorTrendPointV1[] source = trend.Trend.Points.ToArray();
        double minimum = trend.Trend.Minimum ?? (source.Length == 0 ? 0 : source.Min(point => point.Value));
        double maximum = trend.Trend.Maximum ?? (source.Length == 0 ? 0 : source.Max(point => point.Value));
        WpfPointCollection points = BuildPoints(source, minimum, maximum);
        WpfSolidColorBrush brush = new(Palette[index % Palette.Length]);
        brush.Freeze();
        return new SensorComparisonSeriesDisplay(
            trend.SensorId,
            trend.DisplayName,
            trend.Latest,
            trend.Range,
            trend.Unit,
            points,
            brush,
            source.Select(point => point.Value).ToArray());
    }

    private static WpfPointCollection BuildPoints(SensorTrendPointV1[] source, double minimum, double maximum)
    {
        const double width = 320;
        const double top = 12;
        const double height = 96;
        if (source.Length == 0)
        {
            return [];
        }

        double ToY(double value)
        {
            double ratio = maximum > minimum
                ? Math.Clamp((value - minimum) / (maximum - minimum), 0, 1)
                : 0.5;
            return top + (1 - ratio) * height;
        }

        if (source.Length == 1)
        {
            double y = ToY(source[0].Value);
            return [new WpfPoint(0, y), new WpfPoint(width, y)];
        }

        WpfPointCollection points = [];
        for (int index = 0; index < source.Length; index++)
        {
            double x = width * index / (source.Length - 1d);
            points.Add(new WpfPoint(x, ToY(source[index].Value)));
        }
        return points;
    }
}

public sealed record HealthRuleDisplay(
    HealthRuleV1 Rule,
    string Name,
    string Condition,
    string Action,
    string Source,
    string Summary)
{
    public static HealthRuleDisplay From(HealthRuleV1 rule)
    {
        string threshold = rule.Threshold is double value ? $" {value:0.##}" : string.Empty;
        string source = string.IsNullOrWhiteSpace(rule.SensorId) ? "System event" : rule.SensorId;
        return new HealthRuleDisplay(
            rule,
            rule.Name,
            SplitWords(rule.Condition.ToString()),
            SplitWords(rule.Action.ToString()),
            source,
            $"{rule.ConsecutiveObservations} consecutive observation(s), {rule.Cooldown.TotalSeconds:0} s cooldown{threshold}.");
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record HealthAlertDisplay(
    HealthAlertEventV1 Alert,
    string RuleName,
    string Message,
    string State,
    string Action,
    string Timestamp,
    string Tone,
    bool CanAcknowledge)
{
    public static HealthAlertDisplay From(HealthAlertEventV1 alert) => new(
        alert,
        alert.RuleName,
        alert.Message,
        alert.State.ToString(),
        alert.ActionResult ?? SplitWords(alert.RequestedAction.ToString()),
        alert.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        alert.State == HealthAlertState.Cleared ? "Safe" : "Warning",
        alert.State == HealthAlertState.Active);

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record TimelineEventDisplay(
    DateTimeOffset When,
    string Source,
    string Title,
    string Message,
    string Tone)
{
    public string Timestamp => When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static TimelineEventDisplay From(HealthAlertDisplay alert) => new(
        alert.Alert.UpdatedAt,
        "Health",
        alert.RuleName,
        alert.Message,
        alert.Tone);

    public static TimelineEventDisplay From(AdapterTraceDisplay trace) => new(
        DateTimeOffset.Now,
        "Adapter",
        $"{trace.Adapter}: {trace.Operation}",
        trace.Message,
        trace.Success ? "Info" : "Warning");
}

public sealed record AdapterTraceDisplay(
    string Adapter,
    string Operation,
    string Target,
    string Status,
    string Message,
    string Timestamp,
    bool Success)
{
    public static AdapterTraceDisplay From(AdapterTraceEvent trace) => new(
        trace.AdapterId,
        trace.Operation,
        string.IsNullOrWhiteSpace(trace.CapabilityId) ? "Adapter" : trace.CapabilityId,
        trace.Success ? "Completed" : "Failed",
        trace.Message,
        trace.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        trace.Success);
}

internal sealed class AsyncCommand(
    Func<object?, Task> execute,
    Func<object?, bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private bool _executing;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_executing && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _executing = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter);
        }
        catch (Exception exception)
        {
            onError?.Invoke(exception);
        }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
