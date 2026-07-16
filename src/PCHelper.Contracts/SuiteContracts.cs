using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<HazardClass>))]
public enum HazardClass
{
    None,
    Cooling,
    Performance,
    Voltage,
    Driver,
    Firmware
}

[JsonConverter(typeof(JsonStringEnumConverter<BootApplyPolicy>))]
public enum BootApplyPolicy
{
    Allowed,
    AfterScreening,
    ManualOnly,
    Never
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileActivationSource>))]
public enum ProfileActivationSource
{
    Manual,
    Automation,
    Startup,
    Recovery
}

[JsonConverter(typeof(JsonStringEnumConverter<ResetGuarantee>))]
public enum ResetGuarantee
{
    None,
    BestEffort,
    ReadBackVerified,
    FirmwareDefaultVerified
}

[JsonConverter(typeof(JsonStringEnumConverter<OwnershipState>))]
public enum OwnershipState
{
    Available,
    OwnedByPcHelper,
    OwnedByAnotherApplication,
    RecoveryRequired
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<AdapterPackAccess>))]
public enum AdapterPackAccess
{
    None = 0,
    Telemetry = 1 << 0,
    HardwareWrite = 1 << 1,
    PawnIo = 1 << 2,
    Hid = 1 << 3,
    RawUsb = 1 << 4,
    VendorLibrary = 1 << 5,
    DriverInstall = 1 << 6,
    FirmwareUpdate = 1 << 7
}

[JsonConverter(typeof(JsonStringEnumConverter<CoolingNodeKind>))]
public enum CoolingNodeKind
{
    Sensor,
    FileSensor,
    Offset,
    TimeAverage,
    Mix,
    Linear,
    Graph,
    Trigger,
    Flat,
    Sync,
    FeedbackAuto
}

[JsonConverter(typeof(JsonStringEnumConverter<CoolingMixFunction>))]
public enum CoolingMixFunction
{
    Maximum,
    Minimum,
    Average,
    Sum,
    Subtract
}

[JsonConverter(typeof(JsonStringEnumConverter<FanOutputMode>))]
public enum FanOutputMode
{
    DutyPercent,
    Rpm
}

/// <summary>
/// A deliberately conservative physical-output classification.  It is stored
/// separately from a controller's generic telemetry name because Super I/O
/// controllers commonly expose labels such as "Fan #5" even when the physical
/// header is an AIO pump or CPU fan.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoolingOutputRole>))]
public enum CoolingOutputRole
{
    Unknown,
    CaseFan,
    CpuFan,
    Pump
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowTriggerKind>))]
public enum WorkflowTriggerKind
{
    ProcessStarted,
    ProcessEnded,
    ForegroundApplication,
    Schedule,
    SessionLocked,
    SessionUnlocked,
    Idle,
    Resume,
    DeviceConnected,
    SensorThreshold,
    Hotkey,
    GameStarted,
    GameEnded
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowActionKind>))]
public enum WorkflowActionKind
{
    ApplyProfile,
    ApplyLightingScene,
    SelectOsdLayout,
    StartCapture,
    StopCapture,
    RunMacro,
    RunScript,
    LaunchApplication,
    OpenUrl
}

[JsonConverter(typeof(JsonStringEnumConverter<MacroStepKind>))]
public enum MacroStepKind
{
    KeyDown,
    KeyUp,
    MouseButtonDown,
    MouseButtonUp,
    MouseMove,
    MouseWheel,
    MediaKey,
    Delay
}

[JsonConverter(typeof(JsonStringEnumConverter<MacroRecordingState>))]
public enum MacroRecordingState
{
    Recording,
    Completed,
    Cancelled,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<FanCommissioningState>))]
public enum FanCommissioningState
{
    AwaitingIdentification,
    ReadyForCalibration,
    Calibrating,
    Completed,
    Cancelled,
    Failed,
    RecoveryRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<EffectNodeKind>))]
public enum EffectNodeKind
{
    Solid,
    Gradient,
    Wave,
    Breathing,
    Spectrum,
    Temperature,
    Notification,
    AudioSpectrum,
    ScreenAmbience,
    GameEvent,
    Blend,
    Script
}

[JsonConverter(typeof(JsonStringEnumConverter<CaptureTargetKind>))]
public enum CaptureTargetKind
{
    Display,
    Window
}

[JsonConverter(typeof(JsonStringEnumConverter<UpdateKind>))]
public enum UpdateKind
{
    Driver,
    DeviceFirmware,
    Bios
}

[JsonConverter(typeof(JsonStringEnumConverter<ProcessorQualificationFamily>))]
public enum ProcessorQualificationFamily
{
    RyzenZen3,
    RyzenZen4,
    RyzenZen5,
    Intel12th,
    Intel13th14th,
    IntelCoreUltra200
}

[JsonConverter(typeof(JsonStringEnumConverter<GraphicsQualificationFamily>))]
public enum GraphicsQualificationFamily
{
    Rtx30,
    Rtx40,
    Rtx50,
    Rx6000,
    Rx7000,
    Rx9000,
    ArcA,
    ArcB
}

[JsonConverter(typeof(JsonStringEnumConverter<PlatformQualificationFamily>))]
public enum PlatformQualificationFamily
{
    Amd,
    Intel
}

[JsonConverter(typeof(JsonStringEnumConverter<UpdateTransactionState>))]
public enum UpdateTransactionState
{
    Planned,
    Validated,
    Staged,
    Applying,
    PendingReboot,
    Verifying,
    Completed,
    RolledBack,
    Failed,
    RecoveryRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<TakeoverTransactionState>))]
public enum TakeoverTransactionState
{
    Planned,
    Validating,
    BackingUpStartup,
    StoppingProcesses,
    ResettingHardware,
    AcquiringOwnership,
    Completed,
    RollingBack,
    RolledBack,
    RecoveryRequired,
    Released
}

[JsonConverter(typeof(JsonStringEnumConverter<ImportSourceKind>))]
public enum ImportSourceKind
{
    MsiAfterburner,
    FanControl,
    OpenRgb
}

[JsonConverter(typeof(JsonStringEnumConverter<ImportMappingState>))]
public enum ImportMappingState
{
    Mapped,
    ManualOnly,
    Unmapped,
    Invalid,
    Blocked
}

[JsonConverter(typeof(JsonStringEnumConverter<SuiteEntityKind>))]
public enum SuiteEntityKind
{
    ProfileV2,
    CoolingGraph,
    SensorGraph,
    FanCalibration,
    FanCommissioningSession,
    CoolingOutputAssignment,
    OwnershipConsent,
    OwnershipLease,
    AutomationWorkflow,
    Macro,
    MacroRecordingSession,
    ScriptAction,
    EffectGraph,
    EffectScript,
    LightingScene,
    GameEntry,
    OsdLayout,
    OsdPresentationSettings,
    MonitoringPreferences,
    MonitoringComparisonLayout,
    HealthRule,
    HealthAlertEvent,
    SafetyRecoveryState,
    CapturePreset,
    UpdateCandidate,
    UpdatePlan,
    UpdateTransaction,
    AdapterPackInspection,
    TakeoverPlan,
    TakeoverTransaction
}

[JsonConverter(typeof(JsonStringEnumConverter<GameStoreKind>))]
public enum GameStoreKind
{
    Steam,
    Epic,
    Gog,
    MicrosoftXbox,
    Standalone
}

public sealed record AdapterPackManifestV1(
    int SchemaVersion,
    string Id,
    string Name,
    string Version,
    string Publisher,
    string PublisherKeyId,
    string Licence,
    int MinimumProtocolVersion,
    int MaximumProtocolVersion,
    string EntryPoint,
    IReadOnlyList<string> SupportedHardwareIds,
    AdapterPackAccess Permissions,
    IReadOnlyDictionary<string, string> PayloadHashes)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ImportedSettingV1(
    string SourceSection,
    string SourceKey,
    string RawValue,
    string? CapabilityId,
    ControlValue? Value,
    ImportMappingState State,
    string Message);

public sealed record ProfileImportPreviewV1(
    int SchemaVersion,
    ImportSourceKind Source,
    string SourcePath,
    string SourceProfile,
    ProfileV2? Profile,
    IReadOnlyList<ImportedSettingV1> Settings,
    IReadOnlyList<string> Warnings)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record CoolingImportPreviewV1(
    int SchemaVersion,
    ImportSourceKind Source,
    string SourcePath,
    CoolingGraphV1? Graph,
    IReadOnlyList<FanCalibrationV2> Calibrations,
    IReadOnlyList<string> Warnings)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record AdapterPackInspection(
    AdapterPackManifestV1? Manifest,
    string PackageSha256,
    bool Valid,
    bool SignatureValid,
    bool DevelopmentTrust,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record CapabilityDescriptorV2(
    int SchemaVersion,
    CapabilityDescriptor Capability,
    HazardClass Hazard,
    string BoundsSource,
    bool SupportsReadBack,
    ResetGuarantee ResetGuarantee,
    OwnershipState OwnershipState,
    BootApplyPolicy BootPolicy,
    ControlValue? DefaultValue,
    IReadOnlyList<string> Dependencies,
    string? MinimumDriverVersion,
    string? MaximumDriverVersion,
    string? RequiredFirmwareVersion)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record ProfileV2(
    int SchemaVersion,
    string Id,
    string Name,
    string Description,
    IReadOnlyList<ProfileAction> HardwareActions,
    SafetyLimits SafetyLimits,
    string? CoolingGraphId,
    string? LightingSceneId,
    string? OsdLayoutId,
    IReadOnlyList<string> ManualOnlyActionIds,
    IReadOnlyList<string> AutomationReferences,
    bool IsBuiltIn,
    bool IsExperimental)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record ApplyProfileV2Request(
    ProfileV2 Profile,
    ProfileActivationSource Source,
    bool ConfirmExperimental,
    IReadOnlyList<string> ConfirmedDeviceIds,
    bool ConfirmManualVoltage);

public sealed record CoolingGraphNodeV1(
    string Id,
    string Name,
    CoolingNodeKind Kind,
    IReadOnlyList<string> InputNodeIds,
    string? SensorId,
    IReadOnlyList<CurvePoint> Points,
    IReadOnlyDictionary<string, double> Parameters,
    string? TextValue = null,
    CoolingMixFunction MixFunction = CoolingMixFunction.Maximum);

public sealed record CoolingGraphOutputV1(
    string CapabilityId,
    string SourceNodeId,
    FanOutputMode Mode,
    double Minimum,
    double Maximum,
    double Offset,
    double StepUpPerSecond,
    double StepDownPerSecond,
    IReadOnlyList<CurvePoint> AvoidBands);

public sealed record CoolingGraphV1(
    int SchemaVersion,
    string Id,
    string Name,
    IReadOnlyList<CoolingGraphNodeV1> Nodes,
    IReadOnlyList<CoolingGraphOutputV1> Outputs)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record SensorGraphV1(
    int SchemaVersion,
    string Id,
    string Name,
    IReadOnlyList<CoolingGraphNodeV1> Nodes,
    string OutputNodeId)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record FanCalibrationV2(
    int SchemaVersion,
    string CapabilityId,
    string RpmSensorId,
    IReadOnlyList<FanCalibrationPoint> Measurements,
    double MaximumRpm,
    double? StallDutyPercent,
    double? RestartDutyPercent,
    double MinimumDutyPercent,
    double KickStartDutyPercent,
    IReadOnlyList<CurvePoint> AvoidBands,
    DateTimeOffset VerifiedAt,
    string? CommissioningSessionId = null,
    double? EffectiveFloorDutyPercent = null,
    double? EffectiveFloorRpm = null,
    double? FirstResponsiveDutyPercent = null,
    bool NonStopFloorObserved = false,
    bool SupportsVerifiedFanStop = false)
{
    public const int CurrentSchemaVersion = 3;
}

/// <summary>
/// Durable, user-confirmed mapping between a physical fan/header and the
/// bounded calibration evidence that was collected for it. It deliberately
/// stores no arbitrary hardware command and never authorises a write by itself.
/// </summary>
public sealed record FanCommissioningSessionV1(
    int SchemaVersion,
    string Id,
    string CapabilityId,
    string RpmSensorId,
    string HeaderName,
    FanCommissioningState State,
    bool IsCpuOrPump,
    bool AllowFanStop,
    bool HeaderConfirmed,
    string? CalibrationId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? Notes,
    string? Error,
    bool PhysicalHeaderObserved = false)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// A user-confirmed, service-owned classification for one exact cooling
/// controller.  The record carries no command or calibration data; it only
/// prevents unsafe workflows such as an identification pulse or zero-RPM
/// testing from being offered to a CPU fan or pump.
/// </summary>
public sealed record CoolingOutputAssignmentV1(
    int SchemaVersion,
    string Id,
    string CapabilityId,
    string AdapterId,
    string DeviceId,
    string? RpmSensorId,
    string HeaderName,
    CoolingOutputRole Role,
    DateTimeOffset ConfirmedAt,
    string? Notes)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsSafetyCritical => Role is CoolingOutputRole.CpuFan or CoolingOutputRole.Pump;
}

/// <summary>
/// Removing a persisted CPU-fan or pump classification is an explicit service
/// operation.  A client cannot silently turn a protected output into a generic
/// case fan by sending a replacement record.
/// </summary>
public sealed record CoolingOutputAssignmentUpdateRequest(
    CoolingOutputAssignmentV1 Assignment,
    bool ConfirmRemoveSafetyProtection);

public sealed record CoolingOutputAssignmentSaveResultV1(
    CoolingOutputAssignmentV1 Assignment,
    bool Removed);

public sealed record FanCommissioningObservationV1(
    FanCommissioningSessionV1 Session,
    SensorSample? RpmSample,
    IReadOnlyList<SensorSample> ThermalSamples,
    HardwareOperationStatus? LatestOperation,
    string Guidance);

/// <summary>
/// Evidence from a deliberately no-write software-control preflight. The
/// false flags are explicit invariants: this route must never apply, roll back,
/// or reset a physical controller.
/// </summary>
public sealed record FanCommissioningPreflightResultV1(
    int SchemaVersion,
    FanCommissioningSessionV1 Session,
    bool Prepared,
    bool ApplyIssued,
    bool RollbackIssued,
    bool ResetIssued,
    DateTimeOffset CheckedAt,
    string OutcomeCode,
    string Summary,
    AdapterHostDiagnosticsV1? AdapterHostDiagnostics)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record TakeoverProcessIdentity(
    string DisplayName,
    string ExecutablePath,
    string ProductName,
    string Publisher,
    string? SignerThumbprint,
    string Sha256,
    string ProcessName,
    IReadOnlyList<string> ResourceFamilies);

public sealed record TakeoverPlanV1(
    int SchemaVersion,
    string Id,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TakeoverProcessIdentity> Processes,
    IReadOnlyList<string> StartupEntries,
    IReadOnlyList<string> ConfigurationBackups,
    IReadOnlyList<string> ControlsToReset,
    IReadOnlyList<string> Warnings)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record OwnershipConsentV1(
    int SchemaVersion,
    string Id,
    string ProcessName,
    string ExecutablePath,
    string ProductName,
    string Publisher,
    string? SignerThumbprint,
    string Sha256,
    bool AllowForceTermination,
    bool DisableStartup,
    DateTimeOffset GrantedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record TakeoverAuthorizationResult(
    bool Authorized,
    IReadOnlyList<string> Errors);

public sealed record SuiteValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record OwnershipOverview(
    IReadOnlyList<OwnershipConsentV1> Consents,
    IReadOnlyList<OwnershipLeaseV1> Leases,
    IReadOnlyList<TakeoverTransactionV1>? Transactions = null,
    TakeoverExecutionStatusV1? ExecutorStatus = null);

/// <summary>
/// Describes whether this installed service is allowed to make the destructive
/// process/startup mutations used by ownership takeover. A development or
/// unsigned build must never enable the executor merely because a user has
/// supplied an otherwise valid consent record.
/// </summary>
public sealed record TakeoverExecutionStatusV1(
    bool CanExecute,
    string ServiceImagePath,
    string Message);

public sealed record TakeoverPreviewResultV1(
    TakeoverPlanV1 Plan,
    TakeoverExecutionStatusV1 ExecutorStatus);

public sealed record TakeoverExecutionResultV1(
    TakeoverTransactionV1 Transaction,
    OwnershipLeaseV1 Lease);

public sealed record StartupEntryBackupV1(
    string Id,
    string Scope,
    string Name,
    string Command,
    bool WasEnabled,
    string? ValueKind = null);

public sealed record TakeoverTransactionV1(
    int SchemaVersion,
    string Id,
    TakeoverPlanV1 Plan,
    TakeoverTransactionState State,
    IReadOnlyList<StartupEntryBackupV1> StartupBackups,
    IReadOnlyList<string> StoppedProcessPaths,
    IReadOnlyList<string> ResetControls,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? Error,
    string? LeaseId = null)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record FanControlImportRequest(
    string ConfigurationPath,
    IReadOnlyDictionary<string, string> SensorMappings,
    IReadOnlyDictionary<string, string> ControlMappings);

public sealed record AfterburnerImportRequest(
    string ProfilePath,
    string Section);

public sealed record OwnershipLeaseV1(
    int SchemaVersion,
    string Id,
    string Owner,
    IReadOnlyList<string> ResourceFamilies,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    OwnershipState State,
    string Reason)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record WorkflowTriggerV1(
    string Id,
    WorkflowTriggerKind Kind,
    string Value,
    IReadOnlyDictionary<string, double> NumericParameters);

public sealed record WorkflowActionV1(
    string Id,
    WorkflowActionKind Kind,
    string TargetId,
    bool Required,
    int Order);

public sealed record AutomationWorkflowV1(
    int SchemaVersion,
    string Id,
    string Name,
    bool Enabled,
    int Priority,
    IReadOnlyList<WorkflowTriggerV1> Triggers,
    IReadOnlyList<WorkflowActionV1> Actions)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record MacroStepV1(
    MacroStepKind Kind,
    int Code,
    int X,
    int Y,
    int Delta,
    TimeSpan Delay);

public sealed record MacroV1(
    int SchemaVersion,
    string Id,
    string Name,
    IReadOnlyList<MacroStepV1> Steps)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Recording metadata is persisted independently of the macro payload. Raw
/// input exists only while the user explicitly records and is discarded on
/// cancellation or failed validation.
/// </summary>
public sealed record MacroRecordingSessionV1(
    int SchemaVersion,
    string Id,
    string Name,
    MacroRecordingState State,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    TimeSpan MaximumDuration,
    int StepCount,
    string? MacroId,
    string? Error)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// The only completion payload emitted by the user-agent recorder. A cancelled
/// or recovered recording has no macro payload because raw input is never
/// retained outside an explicit completed session.
/// </summary>
public sealed record MacroRecordingResultV1(
    MacroRecordingSessionV1 Session,
    MacroV1? Macro);

public sealed record MacroRecordingStatusV1(
    MacroRecordingSessionV1? ActiveSession,
    bool InputCaptureActive,
    string Guidance);

public sealed record MacroExecutionResultV1(
    int SchemaVersion,
    string MacroId,
    bool Completed,
    int ExecutedSteps,
    TimeSpan Duration,
    string? Error)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ScriptActionV1(
    int SchemaVersion,
    string Id,
    string Name,
    string Interpreter,
    string ScriptPath,
    string Arguments,
    string Sha256,
    bool Trusted,
    TimeSpan Timeout,
    bool RequestElevation)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ScriptExecutionRequestV1(
    int SchemaVersion,
    ScriptActionV1 Action)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ScriptExecutionResultV1(
    int SchemaVersion,
    string ScriptId,
    bool Started,
    bool Completed,
    bool TimedOut,
    bool Elevated,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record EffectNodeV1(
    string Id,
    EffectNodeKind Kind,
    IReadOnlyList<string> InputNodeIds,
    IReadOnlyDictionary<string, double> NumericParameters,
    IReadOnlyDictionary<string, string> TextParameters);

public sealed record EffectGraphV1(
    int SchemaVersion,
    string Id,
    string Name,
    IReadOnlyList<EffectNodeV1> Nodes,
    string OutputNodeId,
    int FramesPerSecond)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record EffectScriptManifestV1(
    int SchemaVersion,
    string Id,
    string Name,
    string EntryPoint,
    string Sha256,
    bool Trusted,
    int MaximumFramesPerSecond,
    int MaximumLedCount)
{
    public const int CurrentSchemaVersion = 1;
}

public readonly record struct EffectColourV1(byte Red, byte Green, byte Blue);

public readonly record struct EffectLedCoordinateV1(int Index, double X, double Y);

public sealed record EffectRenderInputV1(
    double ElapsedMilliseconds,
    IReadOnlyList<EffectLedCoordinateV1> Leds,
    IReadOnlyDictionary<string, double> Sensors,
    IReadOnlyList<double> AudioBins,
    IReadOnlyDictionary<int, EffectColourV1> ScreenColours,
    IReadOnlyDictionary<string, EffectColourV1> Events);

public sealed record EffectRenderRequestV1(
    int SchemaVersion,
    EffectScriptManifestV1 Manifest,
    string PackageRoot,
    EffectRenderInputV1 Input,
    int WatchdogMilliseconds)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record EffectRenderResultV1(
    int SchemaVersion,
    string EffectId,
    bool Completed,
    bool TimedOut,
    IReadOnlyList<EffectColourV1> Colours,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record LightingZoneV1(
    string Id,
    string DeviceId,
    IReadOnlyList<int> LedIndices,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record LightingSceneV1(
    int SchemaVersion,
    string Id,
    string Name,
    string EffectGraphId,
    double BrightnessPercent,
    IReadOnlyList<LightingZoneV1> Zones,
    IReadOnlyList<string> DisabledDeviceIds)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record GameEntryV1(
    int SchemaVersion,
    string Id,
    string Name,
    string ExecutablePath,
    string? LaunchUri,
    string? IconPath,
    string? ProfileId,
    string? LightingSceneId,
    string? OsdLayoutId,
    string? CapturePresetId,
    IReadOnlyList<string> WorkflowIds,
    IReadOnlyList<string>? MacroIds = null)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record GameScanRoot(
    GameStoreKind Store,
    string Path);

public sealed record GameScanResult(
    IReadOnlyList<GameEntryV1> Games,
    IReadOnlyList<string> Warnings);

public sealed record OsdWidgetV1(
    string SensorId,
    string Label,
    string Format,
    int Row,
    int Column,
    string Colour);

public sealed record OsdLayoutV1(
    int SchemaVersion,
    string Id,
    string Name,
    IReadOnlyList<OsdWidgetV1> Widgets,
    double Opacity,
    double Scale,
    bool ShowGraph)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record OsdRenderedWidgetV1(
    string SensorId,
    string Label,
    string Text,
    int Row,
    int Column,
    string Colour,
    SensorQuality Quality);

public sealed record OsdFrameV1(
    int SchemaVersion,
    string LayoutId,
    DateTimeOffset Timestamp,
    IReadOnlyList<OsdRenderedWidgetV1> Widgets)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record RtssBridgeStatusV1(
    bool ProcessDetected,
    bool SharedMemoryDetected,
    string? ExecutablePath,
    string Message);

public sealed record OverlayBridgeStatusV1(
    RtssBridgeStatusV1 Rtss,
    bool GameBarInstalled,
    bool WindowsGraphicsCaptureSupported,
    string GameBarMessage,
    string CaptureMessage);

public sealed record CapturePresetV1(
    int SchemaVersion,
    string Id,
    string Name,
    CaptureTargetKind TargetKind,
    int FramesPerSecond,
    int VideoBitrateKbps,
    string VideoCodec,
    bool CaptureSystemAudio,
    bool CaptureMicrophone,
    bool IncludeTelemetryOverlay,
    string Container)
{
    public const int CurrentSchemaVersion = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<CaptureSessionState>))]
public enum CaptureSessionState
{
    Planned,
    Starting,
    Recording,
    Stopping,
    Completed,
    Failed
}

public sealed record CaptureTargetV1(
    CaptureTargetKind Kind,
    string StableId,
    string DisplayName);

/// <summary>
/// The local Windows transport used to query or set a display brightness. DDC/CI
/// is intended for external displays, while WMI is intended for Windows-managed
/// internal panels. A reported transport is not a blanket compatibility claim:
/// every write is confirmed by an immediate read-back.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MonitorBrightnessTransport>))]
public enum MonitorBrightnessTransport
{
    DdcCi,
    Wmi
}

/// <summary>
/// A display exposed by the signed-in user's Windows session. Unsupported and
/// read-only displays are deliberately retained so that every enumerated screen
/// has an explicit capability reason instead of silently disappearing.
/// </summary>
public sealed record MonitorBrightnessDeviceV1(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string? DisplayDeviceName,
    MonitorBrightnessTransport? Transport,
    CapabilityAccessState State,
    int? MinimumPercent,
    int? MaximumPercent,
    int? CurrentPercent,
    string Reason)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Presentation-only transport detail. This deliberately distinguishes an
    /// external DDC/CI monitor from a Windows-managed panel and from a logical
    /// display that Windows cannot control.
    /// </summary>
    public string TransportLabel => Transport switch
    {
        MonitorBrightnessTransport.DdcCi => "External monitor via DDC/CI",
        MonitorBrightnessTransport.Wmi => "Windows-managed panel via WMI",
        _ => "No supported brightness transport"
    };

    public string RangeLabel => MinimumPercent is int minimum && MaximumPercent is int maximum
        ? $"{minimum}% to {maximum}% reported range"
        : "No writable brightness range reported";
}

/// <summary>
/// A bounded, same-user display-brightness request. ConfirmDevice is required
/// because a physical DDC/CI implementation can be unreliable on an arbitrary
/// monitor and the selected screen may be in use while the UI is open.
/// </summary>
public sealed record SetMonitorBrightnessRequestV1(
    int SchemaVersion,
    string MonitorId,
    int BrightnessPercent,
    bool ConfirmDevice)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Returned only after the selected monitor was read back. A failed verification
/// retains the observed value and whether a best-effort rollback was attempted.
/// </summary>
public sealed record MonitorBrightnessApplyResultV1(
    int SchemaVersion,
    string MonitorId,
    int RequestedPercent,
    int? ObservedPercent,
    bool Applied,
    bool ReadBackVerified,
    bool RollbackAttempted,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// A same-user, explicitly confirmed still-image request. The output location is
/// chosen by the user-agent backend rather than supplied by the caller, so a
/// pipe client cannot use capture as an arbitrary-file writer.
/// </summary>
public sealed record CaptureSnapshotRequestV1(
    int SchemaVersion,
    CaptureTargetV1 Target,
    bool ConfirmedVisibleCapture)
{
    public const int CurrentSchemaVersion = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<CaptureSnapshotBackend>))]
public enum CaptureSnapshotBackend
{
    GdiDesktop,
    PrintWindow,
    PrintWindowWithDesktopFallback
}

/// <summary>
/// Metadata only. Snapshot pixels stay in the signed-in user's Pictures\RigPilot
/// directory and are never written to service state or compatibility reports.
/// </summary>
public sealed record CaptureSnapshotResultV1(
    int SchemaVersion,
    string Id,
    CaptureTargetV1 Target,
    string OutputPath,
    DateTimeOffset CapturedAt,
    int Width,
    int Height,
    long BytesWritten,
    CaptureSnapshotBackend Backend,
    string? Warning)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Explicit, same-user video recording request. Recording starts only after a
/// visible-session confirmation, targets only a currently discovered display or
/// window, is duration-bounded, and writes only below the user's Videos\RigPilot
/// directory. It never runs through the service.
/// </summary>
public sealed record VideoRecordingStartRequestV1(
    int SchemaVersion,
    CaptureTargetV1 Target,
    bool ConfirmedVisibleCapture,
    string IdempotencyKey,
    int MaxDurationSeconds,
    bool CaptureSystemAudio)
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumDurationSeconds = 5;
    public const int MaximumDurationSeconds = 600;
}

[JsonConverter(typeof(JsonStringEnumConverter<VideoRecordingState>))]
public enum VideoRecordingState
{
    Idle,
    Recording,
    Completed,
    Failed
}

/// <summary>
/// Metadata only. Recorded video stays in the signed-in user's Videos\RigPilot
/// directory and is never written to service state or compatibility reports.
/// </summary>
public sealed record VideoRecordingStatusV1(
    int SchemaVersion,
    VideoRecordingState State,
    CaptureTargetV1? Target,
    string? OutputPath,
    DateTimeOffset? StartedAt,
    double DurationSeconds,
    long BytesWritten,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// A request to publish (or refresh) RigPilot's own text line in the RTSS
/// on-screen display through the documented RTSS shared-memory contract. The
/// bridge writes only an OSD slot RigPilot owns (claimed by writing RigPilot's
/// owner name into an empty slot, the mechanism the RTSS SDK defines for
/// third-party clients); it never touches another application's slot, never
/// injects into any process, and refuses to write unless the mapped memory
/// carries the expected signature, a supported v2 ABI version, and
/// self-consistent bounds.
/// </summary>
public sealed record RtssOsdPublishRequestV1(
    int SchemaVersion,
    string Text,
    bool ConfirmedThirdPartyOsdWrite)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumTextLength = 255;
}

public sealed record RtssOsdBridgeStatusV1(
    int SchemaVersion,
    bool SharedMemoryDetected,
    bool AbiValidated,
    string? AbiVersion,
    bool Publishing,
    int SlotIndex,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record RtssAppFrameStatsV1(
    int ProcessId,
    string ProcessName,
    double FramesPerSecond,
    double FrameTimeMilliseconds);

public sealed record RtssFrameStatsV1(
    int SchemaVersion,
    bool SharedMemoryDetected,
    IReadOnlyList<RtssAppFrameStatsV1> Applications,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Starts an RTSS-sampled frame-rate benchmark for one application. Sampling is
/// entirely passive: RigPilot reads the frame counters RTSS already publishes in
/// shared memory (no injection, no OSD write). ProcessId 0 selects the
/// application RTSS reports with the highest frame rate at each sample.
/// </summary>
public sealed record FrametimeBenchmarkStartRequestV1(
    int SchemaVersion,
    int ProcessId,
    int MaxDurationSeconds)
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumDurationSeconds = 10;
    public const int MaximumDurationSeconds = 3600;
}

[JsonConverter(typeof(JsonStringEnumConverter<FrametimeBenchmarkState>))]
public enum FrametimeBenchmarkState
{
    Idle,
    Running,
    Completed,
    Failed
}

/// <summary>
/// Benchmark results computed from RTSS's ~one-second measurement windows. The
/// "low" figures are therefore the means of the worst 1% / 0.1% of one-second
/// windows — comparable across RigPilot runs, but deliberately not labelled as
/// per-frame 1% lows, which need per-frame data RigPilot does not collect.
/// </summary>
public sealed record FrametimeBenchmarkStatusV1(
    int SchemaVersion,
    FrametimeBenchmarkState State,
    string? ProcessName,
    DateTimeOffset? StartedAt,
    double DurationSeconds,
    int SampleCount,
    double? AverageFps,
    double? MinimumFps,
    double? MaximumFps,
    double? OnePercentLowFps,
    double? PointOnePercentLowFps,
    double? AverageFrameTimeMilliseconds,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static FrametimeBenchmarkStatusV1 Idle(string message) => new(
        CurrentSchemaVersion, FrametimeBenchmarkState.Idle, null, null, 0, 0,
        null, null, null, null, null, null, message);
}

public sealed record CaptureMetricsV1(
    long FramesEncoded,
    long FramesDropped,
    TimeSpan Duration,
    long BytesWritten);

public sealed record CaptureSessionV1(
    int SchemaVersion,
    string Id,
    CapturePresetV1 Preset,
    CaptureTargetV1 Target,
    string OutputPath,
    CaptureSessionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    CaptureMetricsV1 Metrics,
    string? Error)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record UpdateCandidateV1(
    int SchemaVersion,
    string Id,
    UpdateKind Kind,
    string DeviceId,
    string CurrentVersion,
    string TargetVersion,
    Uri DownloadUri,
    string Sha256,
    string ExpectedPublisher,
    bool RequiresReboot,
    bool RequiresBitLockerSuspension,
    string? RecoveryMethod)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record UpdatePlanV1(
    int SchemaVersion,
    string Id,
    UpdateCandidateV1 Candidate,
    string StagedPackagePath,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> RecoverySteps,
    bool UserConfirmed)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record UpdateTransactionV1(
    int SchemaVersion,
    string Id,
    UpdatePlanV1 Plan,
    UpdateTransactionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? PreviousVersion,
    string? Error)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// A non-mutating result for the exact staged update package. It deliberately
/// reports production execution readiness separately from package validity:
/// an unsigned development service may inspect a package but never install it.
/// </summary>
public sealed record UpdateValidationResultV1(
    int SchemaVersion,
    UpdatePlanV1 Plan,
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool ProductionExecutionReady,
    string ExecutionMessage)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record UpdateStatusV1(
    int SchemaVersion,
    bool ProductionExecutionReady,
    string ExecutionMessage,
    IReadOnlyList<UpdateTransactionV1> Transactions)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Result of a user-process update discovery. Discovery runs only in the network-
/// capable user agent (never the service). A build with no configured update feed
/// returns an empty candidate list and <see cref="SourceConfigured"/> = false rather
/// than contacting an endpoint, so this reflects reality instead of a dead route.
/// </summary>
public sealed record UpdateDiscoveryResultV1(
    int SchemaVersion,
    bool SourceConfigured,
    IReadOnlyList<UpdateCandidateV1> Candidates,
    string Message,
    DateTimeOffset CheckedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static UpdateDiscoveryResultV1 NoSourceConfigured() => new(
        CurrentSchemaVersion,
        false,
        [],
        "No update source is configured; this build does not auto-discover updates.",
        DateTimeOffset.UtcNow);
}

/// <summary>
/// A privacy-preserving, physical-system qualification record. SystemId must
/// be a random per-machine identifier, never a serial number, hostname, or
/// Windows account name. Records are evidence inputs to the release gate; a
/// successful record does not by itself certify a controller family.
/// </summary>
public sealed record HardwareQualificationRecordV1(
    int SchemaVersion,
    string ReportId,
    string SystemId,
    DateTimeOffset CapturedAt,
    ProcessorQualificationFamily ProcessorFamily,
    GraphicsQualificationFamily GraphicsFamily,
    PlatformQualificationFamily PlatformFamily,
    string MotherboardVendor,
    bool SignedProductionBuild,
    bool NoBsodOrUnexpectedReboot,
    bool NoStuckFan,
    bool NoUnauthorisedWrite,
    bool RollbackPassed,
    IReadOnlyList<ControllerQualificationEvidenceV1> ControllerEvidence,
    string? Notes)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One exact controller result within a hardware qualification record. A
/// controller becomes release-claimable only after two successful records on
/// different physical systems and the matrix validator has passed.
/// </summary>
public sealed record ControllerQualificationEvidenceV1(
    string ControllerFamily,
    string ExactDeviceId,
    string FirmwareVersion,
    string DriverVersion,
    bool ApplyReadBackResetPassed,
    bool ClaimedWriteCapability,
    string? Notes);

public sealed record QualificationRequirementStatusV1(
    string Requirement,
    bool Passed,
    int Observed,
    int Required,
    string Message);

public sealed record QualificationMatrixStatusV1(
    int SchemaVersion,
    int PhysicalSystemCount,
    bool CanReleaseV1,
    IReadOnlyList<QualificationRequirementStatusV1> Requirements,
    IReadOnlyList<string> BlockingDefects)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record AdapterTraceEvent(
    DateTimeOffset Timestamp,
    string AdapterId,
    string Operation,
    string CapabilityId,
    bool Success,
    string Message);
