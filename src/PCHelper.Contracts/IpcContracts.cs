using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<IpcCommand>))]
public enum IpcCommand
{
    Handshake,
    GetInventory,
    SubscribeSensors,
    GetProfiles,
    GetAutomationRules,
    SaveAutomationRule,
    DeleteAutomationRule,
    ValidateProfile,
    PreviewProfileV2,
    ApplyProfile,
    ApplyProfileV2,
    ResetHardware,
    StartCalibration,
    GetCoolingOutputAssignments,
    SaveCoolingOutputAssignment,
    GetFanCommissioningSessions,
    GetFanCalibrations,
    BeginFanCommissioning,
    PreflightFanCommissioning,
    PulseFanCommissioning,
    ObserveFanCommissioning,
    ConfirmFanCommissioning,
    CompleteFanCommissioning,
    CancelFanCommissioning,
    RecoverFanCommissioning,
    StartTune,
    StartAutoOc,
    StartAutoOcV3,
    AbortOperation,
    GetOperationStatus,
    GetOperationById,
    AdapterProbe,
    AdapterReadSensors,
    AdapterPrepare,
    AdapterApply,
    AdapterVerify,
    AdapterRollback,
    AdapterReset,
    AdapterHealth,
    AdapterDiagnostics,
    GetAdapterTrace,
    GetHealthRules,
    SaveHealthRule,
    DeleteHealthRule,
    GetHealthAlerts,
    AcknowledgeHealthAlert,
    GetSafetyRecoveryStatus,
    SetSafeMode,
    GetHardwareEvidence,
    GetCoolingQualificationReports,
    GetDeviceQualificationPlans,
    AdapterShutdown,
    ExportReport,
    GetServiceStatus,
    GetCapabilitiesV2,
    GetProfilesV2,
    GetAutoOcProfileValidations,
    SaveProfileV2,
    GetCoolingGraphs,
    SaveCoolingGraph,
    PreviewAfterburnerImport,
    PreviewFanControlImport,
    GetAdapterPacks,
    InspectAdapterPack,
    InstallAdapterPack,
    RemoveAdapterPack,
    PreviewTakeover,
    GrantOwnershipConsent,
    ExecuteTakeover,
    ReleaseOwnership,
    GetOwnership,
    GetWorkflows,
    SaveWorkflow,
    DeleteWorkflow,
    GetLightingScenes,
    SaveLightingScene,
    GetEffectGraphs,
    SaveEffectGraph,
    RenderEffectFrame,
    GetGames,
    SaveGame,
    ScanGames,
    GetMacros,
    SaveMacro,
    ExecuteMacro,
    GetMacroRecordingSessions,
    GetMacroRecordingStatus,
    BeginMacroRecording,
    StopMacroRecording,
    CancelMacroRecording,
    RecoverMacroRecording,
    GetScripts,
    SaveScript,
    ExecuteScript,
    GetOsdLayouts,
    SaveOsdLayout,
    GetOsdPresentationSettings,
    SaveOsdPresentationSettings,
    GetMonitoringPreferences,
    SaveMonitoringPreferences,
    GetMonitoringComparisonLayout,
    SaveMonitoringComparisonLayout,
    GetOverlayStatus,
    GetWgcRecordingPreflight,
    GetCapturePresets,
    SaveCapturePreset,
    GetCaptureTargets,
    CaptureDesktopSnapshot,
    GetMonitorBrightnesses,
    SetMonitorBrightness,
    RunInteractiveFanPreflight,
    SubmitInteractiveFanPreflight,
    DiscoverUpdates,
    ValidateUpdate,
    ApplyUpdate,
    GetUpdateStatus,
    DiscoverControllers,
    DiscoverHidInventory,
    ReadKrakenTelemetry,
    ReadRyzenSmuFeasibility,
    SetGpuFanControlArmed,
    SetGpuPowerLimitArmed,
    SetGpuClockOffsetArmed,
    StartVideoRecording,
    StopVideoRecording,
    GetVideoRecordingStatus,
    GetRtssOsdBridgeStatus,
    GetRtssFrameStats,
    PublishRtssOsdText,
    ReleaseRtssOsd,
    StartFrametimeBenchmark,
    StopFrametimeBenchmark,
    GetFrametimeBenchmarkStatus,
    SetCpuTuningArmed,
    StartPresentMonBenchmark,
    StopPresentMonBenchmark,
    GetPresentMonBenchmarkStatus,
    SetKrakenLighting,
    SetKrakenPumpDuty,
    SetAuraLighting,
    StopConflictingProcesses,
    GetStorageHealth,
    SetDimmRgb,
    SetRazerRgb,
    SetHardwareControlArmed,
    AdapterVerifyDefault,
    AdapterVerifyRollback
}

public static class ProtocolConstants
{
    public const int LegacyReadOnlyVersion = 1;
    public const int Version = 2;
    public const string ServicePipeName = "pchelper.service.v1";
    public const string AdapterHostPipeName = "pchelper.adapterhost.v1";
    public const string UserAgentPipeName = "pchelper.useragent.v2";
    public const int MaximumMessageBytes = 2 * 1024 * 1024;
}

public static class IpcCommandPolicy
{
    public static bool IsReadOnly(IpcCommand command) => command is
        IpcCommand.Handshake or
        IpcCommand.GetInventory or
        IpcCommand.SubscribeSensors or
        IpcCommand.GetProfiles or
        IpcCommand.GetAutomationRules or
        IpcCommand.PreviewProfileV2 or
        IpcCommand.GetOperationStatus or
        IpcCommand.GetOperationById or
        IpcCommand.GetServiceStatus or
        IpcCommand.GetAdapterTrace or
        IpcCommand.GetHealthRules or
        IpcCommand.GetHealthAlerts or
        IpcCommand.GetSafetyRecoveryStatus or
        IpcCommand.GetHardwareEvidence or
        IpcCommand.GetCoolingQualificationReports or
        IpcCommand.GetDeviceQualificationPlans or
        IpcCommand.GetCapabilitiesV2 or
        IpcCommand.GetProfilesV2 or
        IpcCommand.GetAutoOcProfileValidations or
        IpcCommand.GetCoolingGraphs or
        IpcCommand.GetCoolingOutputAssignments or
        IpcCommand.GetFanCommissioningSessions or
        IpcCommand.GetFanCalibrations or
        IpcCommand.ObserveFanCommissioning or
        IpcCommand.PreviewAfterburnerImport or
        IpcCommand.PreviewFanControlImport or
        IpcCommand.GetAdapterPacks or
        IpcCommand.InspectAdapterPack or
        IpcCommand.GetOwnership or
        IpcCommand.GetWorkflows or
        IpcCommand.GetLightingScenes or
        IpcCommand.GetEffectGraphs or
        IpcCommand.GetGames or
        IpcCommand.GetMacros or
        IpcCommand.GetMacroRecordingSessions or
        IpcCommand.GetMacroRecordingStatus or
        IpcCommand.GetScripts or
        IpcCommand.GetOsdLayouts or
        IpcCommand.GetOsdPresentationSettings or
        IpcCommand.GetMonitoringPreferences or
        IpcCommand.GetMonitoringComparisonLayout or
        IpcCommand.GetOverlayStatus or
        IpcCommand.GetWgcRecordingPreflight or
        IpcCommand.GetVideoRecordingStatus or
        IpcCommand.GetRtssOsdBridgeStatus or
        IpcCommand.GetRtssFrameStats or
        IpcCommand.GetFrametimeBenchmarkStatus or
        IpcCommand.GetPresentMonBenchmarkStatus or
        IpcCommand.GetCapturePresets or
        IpcCommand.GetCaptureTargets or
        IpcCommand.GetMonitorBrightnesses or
        IpcCommand.GetUpdateStatus or
        IpcCommand.DiscoverUpdates or
        IpcCommand.DiscoverControllers or
        IpcCommand.ReadKrakenTelemetry or
        IpcCommand.ReadRyzenSmuFeasibility or
        IpcCommand.GetStorageHealth or
        IpcCommand.DiscoverHidInventory or
        IpcCommand.ValidateUpdate or
        IpcCommand.AdapterProbe or
        IpcCommand.AdapterReadSensors or
        IpcCommand.AdapterVerify or
        IpcCommand.AdapterVerifyDefault or
        IpcCommand.AdapterVerifyRollback or
        IpcCommand.AdapterHealth or
        IpcCommand.AdapterDiagnostics;

    public static bool IsMutation(IpcCommand command) => !IsReadOnly(command)
        && command is not IpcCommand.ExportReport;
}

public sealed record IpcRequest(
    int ProtocolVersion,
    string RequestId,
    IpcCommand Command,
    long? ExpectedStateRevision,
    string? IdempotencyKey,
    JsonElement? Payload);

public sealed record IpcResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    long StateRevision,
    string? ErrorCode,
    string? Error,
    JsonElement? Payload);

public sealed record ServiceStatus(
    string Version,
    DateTimeOffset StartedAt,
    long StateRevision,
    string? ActiveProfileId,
    bool WritesEnabled,
    bool EmergencyMode,
    string Message,
    bool RecoveryRequired = false,
    bool HardwareControlArmed = false,
    CoolingRuntimeStatusV1? Cooling = null,
    bool ReleaseWritesLocked = false,
    string? WriteLockReason = null);

[JsonConverter(typeof(JsonStringEnumConverter<CoolingRuntimeState>))]
public enum CoolingRuntimeState
{
    Inactive,
    Normal,
    SensorHold,
    EmergencyMaximum,
    RecoveryRequired
}

public sealed record CoolingRuntimeStatusV1(
    int SchemaVersion,
    string? ProfileId,
    string? GraphId,
    CoolingRuntimeState State,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? EmergencySince,
    IReadOnlyDictionary<string, double> OutputValues,
    IReadOnlyList<string> HeldSensorIds,
    IReadOnlyDictionary<string, int> StalePollCounts,
    string Reason)
{
    public const int CurrentSchemaVersion = 1;

    public static CoolingRuntimeStatusV1 Inactive(string reason) => new(
        CurrentSchemaVersion,
        null,
        null,
        CoolingRuntimeState.Inactive,
        DateTimeOffset.UtcNow,
        null,
        new Dictionary<string, double>(),
        [],
        new Dictionary<string, int>(),
        reason);
}

public sealed record ProfileValidationResult(
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> SkippedOptionalActions);

public sealed record ApplyProfileRequest(
    ProfileV1 Profile,
    bool ConfirmExperimental,
    bool ConfirmDevices = false);

public sealed record ApplyProfileResult(ProfileTransaction Transaction, string? ActiveProfileId);

public sealed record DeleteAutomationRuleRequest(string RuleId);

/// <summary>
/// Reads one durable operation record by its exact immutable identifier. This
/// is deliberately read-only so evidence export cannot reserve, update, or
/// recover an operation.
/// </summary>
public sealed record OperationLookupRequest(string OperationId);

public sealed record AdapterHostEnvelope<T>(string SessionToken, T Payload);

/// <summary>
/// Envelope used exclusively by the short-lived, explicitly elevated
/// user-session diagnostic child. The random launch token authenticates this
/// private pipe exchange; it does not grant hardware-service authority.
/// </summary>
public sealed record InteractivePreflightEnvelope<T>(string SessionToken, T Payload);

public sealed record AdapterResetRequest(string CapabilityId);

public sealed record AdapterRollbackVerificationRequest(PreparedAction Action);

public sealed record AdapterDefaultVerificationRequest(string CapabilityId);

public sealed record HandshakeRequest(string ClientName, string ClientVersion);

public sealed record HandshakeResponse(int ProtocolVersion, string ServiceVersion, long StateRevision);

public sealed record HandshakeRequestV2(
    string ClientName,
    string ClientVersion,
    int MinimumProtocolVersion,
    int MaximumProtocolVersion);

public sealed record HandshakeResponseV2(
    int SelectedProtocolVersion,
    int MinimumReadOnlyProtocolVersion,
    string ServiceVersion,
    long StateRevision,
    IReadOnlyList<string> Features);

public sealed record DeleteEntityRequest(string Id);

public sealed record InspectAdapterPackRequest(string PackagePath, bool AllowDevelopmentTrust);

public sealed record InstallAdapterPackRequest(string PackagePath, bool ConfirmDevelopmentTrust);

public sealed record RemoveAdapterPackRequest(string PackId, string Version);

public sealed record SetGpuFanControlArmedRequest(
    bool Armed,
    bool ConfirmExperimental,
    IReadOnlyList<string> ConfirmedDeviceIds);

public sealed record SetHardwareControlArmedRequest(
    bool Armed,
    bool ConfirmExperimental,
    IReadOnlyList<string> ConfirmedDeviceIds);

public sealed record HardwareControlFamilyResult(
    string Family,
    bool Available,
    bool RequestedStateApplied,
    bool ReadBackVerified,
    bool RolledBack,
    string Message);

public sealed record HardwareControlTransactionResult(
    bool Armed,
    bool AllRequestedFamiliesVerified,
    bool RecoveryRequired,
    IReadOnlyList<HardwareControlFamilyResult> Families,
    string Message);

public sealed record GpuFanControlStatus(
    bool Available,
    bool Armed,
    string DeviceId,
    string Message);

public sealed record SetGpuPowerLimitArmedRequest(
    bool Armed,
    bool ConfirmExperimental,
    IReadOnlyList<string> ConfirmedDeviceIds);

public sealed record GpuPowerLimitStatus(
    bool Available,
    bool Armed,
    string DeviceId,
    string Message);

public sealed record SetGpuClockOffsetArmedRequest(
    bool Armed,
    bool ConfirmExperimental,
    IReadOnlyList<string> ConfirmedDeviceIds);

public sealed record GpuClockOffsetStatus(
    bool Available,
    bool Armed,
    string DeviceId,
    string Message);

public sealed record SetCpuTuningArmedRequest(
    bool Armed,
    bool ConfirmExperimental,
    IReadOnlyList<string> ConfirmedDeviceIds);

/// <summary>
/// CPU PBO tuning arm status. <see cref="Qualified"/> reflects the CPU-tuning
/// qualification gate (docs/qualification/cpu-tuning-and-intel-arc.md); arming
/// is refused while it is false, which is every system today.
/// </summary>
public sealed record CpuTuningStatus(
    bool Available,
    bool Qualified,
    bool Armed,
    string DeviceId,
    string Message);

public sealed record GrantOwnershipConsentRequest(OwnershipConsentV1 Consent);

public sealed record ExecuteScriptRequest(string ScriptId);

public sealed record ExecuteMacroRequest(string MacroId, bool ConfirmedVisibleSession);

public sealed record BeginMacroRecordingRequest(string Name, TimeSpan MaximumDuration);

public sealed record StopMacroRecordingRequest(string SessionId);

public sealed record CancelMacroRecordingRequest(string SessionId);

public sealed record BeginFanCommissioningRequest(
    string CapabilityId,
    string RpmSensorId,
    string HeaderName,
    bool IsCpuOrPump,
    bool AllowFanStop,
    string? Notes);

/// <summary>
/// An explicit short, bounded physical-identification pulse. Both experimental
/// acknowledgements are required when the controller is not Verified.
/// </summary>
public sealed record PulseFanCommissioningRequest(
    string SessionId,
    bool ConfirmExperimental,
    bool ConfirmDevice,
    TimeSpan Duration);

/// <summary>
/// Runs only the adapter Prepare phase for a previously-created commissioning
/// session. It does not reserve an operation or call Apply, Verify, Rollback,
/// or Reset.
/// </summary>
public sealed record PreflightFanCommissioningRequest(
    string SessionId,
    bool ConfirmExperimental,
    bool ConfirmDevice);

public sealed record FanCommissioningSessionRequest(string SessionId);

public sealed record ConfirmFanCommissioningRequest(
    string SessionId,
    bool HeaderConfirmed,
    string HeaderName,
    string? Notes,
    bool PhysicalHeaderObserved = false);

public sealed record ExecuteTakeoverRequest(string PlanId, bool ConfirmExactProcesses);

public sealed record ValidateUpdateRequest(UpdatePlanV1 Plan);

public sealed record ApplyUpdateRequest(UpdatePlanV1 Plan);

/// <summary>
/// Releases an existing ownership lease. This recovery path remains available
/// even when an upgraded build is no longer eligible to start a new takeover.
/// </summary>
public sealed record ReleaseOwnershipRequest(string TransactionId);

public sealed record CompatibilityReportV1(
    int SchemaVersion,
    string ReportId,
    DateTimeOffset CreatedAt,
    string AppVersion,
    HardwareSnapshot Snapshot,
    IReadOnlyDictionary<string, string> Runtime,
    IReadOnlyList<string> SanitisedLogLines,
    bool UserApproved);
