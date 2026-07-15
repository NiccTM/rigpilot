using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

/// <summary>
/// A local, privacy-minimised failure observation from the restartable Adapter
/// Host. Numeric error codes are retained because they are necessary to
/// diagnose driver/context failures without exposing raw native error text.
/// </summary>
public sealed record AdapterHostFailureV1(
    string Command,
    string Stage,
    string ExceptionType,
    int HResult,
    int? Win32Error,
    DateTimeOffset ObservedAt);

/// <summary>
/// Read-only Adapter Host execution context. Identity values are deliberately
/// classified rather than serialized as user names or SIDs.
/// </summary>
public sealed record AdapterHostDiagnosticsV1(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    int ProcessId,
    string ProcessIdentityKind,
    bool? IsElevated,
    string ThreadTokenState,
    string ClientAuthentication,
    string ClientIdentityEvaluation,
    AdapterHostFailureV1? LastFailure)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Requests a one-shot, explicitly UAC-approved user-session diagnostic. The
/// invoked process may only call adapter Prepare for this bounded controller;
/// it cannot apply, verify, roll back, reset, or alter service eligibility.
/// </summary>
public sealed record InteractiveFanPreflightRequestV1(
    int SchemaVersion,
    string CapabilityId)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Privacy-minimised outcome from the elevated diagnostic child. Every action
/// flag is structurally false because this protocol proves only the no-write
/// Prepare phase. The service must never consume this record as write evidence.
/// </summary>
public sealed record InteractiveFanPreflightResultV1(
    int SchemaVersion,
    string CapabilityId,
    bool Prepared,
    bool ApplyIssued,
    bool VerifyIssued,
    bool RollbackIssued,
    bool ResetIssued,
    bool IsElevated,
    string ExecutionContext,
    DateTimeOffset CheckedAt,
    string OutcomeCode,
    string Summary,
    AdapterHostFailureV1? Failure)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Outcome of an isolated USB/AIO controller-discovery probe. Controller
/// enumeration in LibreHardwareMonitor initialises native HidSharp code that can
/// terminate its host process; discovery therefore runs in a separate, killable
/// child, and every non-success outcome here represents a *contained* failure
/// that left the calling Adapter Host alive.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerDiscoveryOutcome>))]
public enum ControllerDiscoveryOutcome
{
    /// <summary>The probe process completed and returned a controller inventory.</summary>
    Succeeded,

    /// <summary>Discovery is intentionally not attempted (feature switched off).</summary>
    Skipped,

    /// <summary>The probe process exceeded its time budget and was killed.</summary>
    TimedOut,

    /// <summary>The probe process exited abnormally (native crash or nonzero exit).</summary>
    Crashed,

    /// <summary>The probe ran but reported a managed failure while enumerating.</summary>
    EnumerationFailed
}

/// <summary>
/// Read-only result of a contained controller-discovery probe. Discovered
/// controllers are inventory evidence only: this record never carries a writable
/// capability, and a non-<see cref="ControllerDiscoveryOutcome.Succeeded"/>
/// outcome must keep USB/AIO controllers unsupported rather than surfacing a
/// partial or unsafe device list.
/// </summary>
public sealed record ControllerDiscoveryResultV1(
    int SchemaVersion,
    ControllerDiscoveryOutcome Outcome,
    IReadOnlyList<HardwareDevice> Controllers,
    int? ExitCode,
    string Detail,
    DateTimeOffset CapturedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static ControllerDiscoveryResultV1 Contained(
        ControllerDiscoveryOutcome outcome,
        string detail,
        int? exitCode = null) => new(
            CurrentSchemaVersion,
            outcome,
            [],
            exitCode,
            detail,
            DateTimeOffset.UtcNow);
}

[JsonConverter(typeof(JsonStringEnumConverter<HealthRuleConditionKind>))]
public enum HealthRuleConditionKind
{
    SensorAbove,
    SensorBelow,
    SensorStale,
    FanBelow,
    WheaEvent,
    DisplayDriverReset
}

[JsonConverter(typeof(JsonStringEnumConverter<HealthRuleActionKind>))]
public enum HealthRuleActionKind
{
    NotifyOnly,
    RequestEmergencyProfile,
    EnterSafeMode
}

[JsonConverter(typeof(JsonStringEnumConverter<HealthAlertState>))]
public enum HealthAlertState
{
    Active,
    Acknowledged,
    Cleared
}

/// <summary>
/// A bounded, typed health rule. Rules never contain executable content. An
/// emergency profile action is only a request until the service verifies that
/// the selected profile is eligible for automatic use.
/// </summary>
public sealed record HealthRuleV1(
    int SchemaVersion,
    string Id,
    string Name,
    HealthRuleConditionKind Condition,
    string? SensorId,
    double? Threshold,
    int ConsecutiveObservations,
    TimeSpan Cooldown,
    HealthRuleActionKind Action,
    string? EmergencyProfileId,
    bool Enabled)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record HealthAlertEventV1(
    int SchemaVersion,
    string Id,
    string RuleId,
    string RuleName,
    HealthRuleConditionKind Condition,
    HealthRuleActionKind RequestedAction,
    HealthAlertState State,
    DateTimeOffset RaisedAt,
    DateTimeOffset UpdatedAt,
    string Message,
    string? SensorId,
    double? ObservedValue,
    string? Unit,
    string? EmergencyProfileId,
    bool ActionExecuted,
    string? ActionResult)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record DeleteHealthRuleRequestV1(int SchemaVersion, string RuleId)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record AcknowledgeHealthAlertRequestV1(int SchemaVersion, string AlertId)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record SensorAliasV1(string SensorId, string Alias);

/// <summary>
/// Per-user monitoring preferences. Alias and pinning are presentational and
/// cannot change sensor sampling, hardware ownership, or write eligibility.
/// </summary>
public sealed record MonitoringPreferencesV1(
    int SchemaVersion,
    string Id,
    IReadOnlyList<SensorAliasV1> Aliases,
    IReadOnlyList<string> PinnedSensorIds,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultId = "monitoring.default";
}

/// <summary>
/// Presentation-only filters for the live monitoring workspace. These values
/// never alter sampling, alerting, retention, or hardware control.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MonitoringTrendScope>))]
public enum MonitoringTrendScope
{
    All,
    Pinned,
    Temperature,
    Fan,
    Power
}

/// <summary>
/// Per-user selection for a small, normalized comparison chart. This is a
/// presentation-only record: it cannot alter collection rate, alerting, or
/// any hardware control. The selected series retain their native units in the
/// legend; normalization is used only to compare recent movement.
/// </summary>
public sealed record MonitoringComparisonLayoutV1(
    int SchemaVersion,
    string Id,
    IReadOnlyList<string> SensorIds,
    bool NormalizeEachSeries,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultId = "monitoring.comparison.default";
}

public sealed record SensorTrendPointV1(DateTimeOffset Timestamp, double Value);

public sealed record SensorTrendV1(
    string SensorId,
    string DisplayName,
    string Unit,
    IReadOnlyList<SensorTrendPointV1> Points,
    double? Minimum,
    double? Maximum,
    double? Latest,
    string Sparkline);

/// <summary>
/// Persisted safe-mode setting. It suppresses automation and alert-driven
/// profile requests; it never replaces the existing transactional rollback or
/// reset paths.
/// </summary>
public sealed record SafetyRecoveryStateV1(
    int SchemaVersion,
    string Id,
    bool SafeModeEnabled,
    bool AutomationSuspended,
    DateTimeOffset UpdatedAt,
    string Reason)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultId = "recovery.default";
}

public sealed record SafetyRecoveryStatusV1(
    int SchemaVersion,
    SafetyRecoveryStateV1 State,
    bool RollbackBlocked,
    HardwareOperationStatus? LatestOperation,
    IReadOnlyList<FanCommissioningSessionV1> RecoverySessions,
    string Guidance)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record SetSafeModeRequestV1(
    int SchemaVersion,
    bool Enabled,
    string Reason)
{
    public const int CurrentSchemaVersion = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<QualificationStepState>))]
public enum QualificationStepState
{
    NotRun,
    Ready,
    Passed,
    Failed,
    Blocked
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceQualificationKind>))]
public enum DeviceQualificationKind
{
    Cooling,
    CpuTuning,
    GpuTuning,
    Lighting
}

public sealed record DeviceQualificationStepV1(
    string Id,
    string Name,
    QualificationStepState State,
    string Evidence,
    string Guidance);

public sealed record DeviceQualificationPlanV1(
    int SchemaVersion,
    string Id,
    DeviceQualificationKind Kind,
    string DeviceId,
    string DeviceName,
    CapabilityAccessState CapabilityState,
    IReadOnlyList<DeviceQualificationStepV1> Steps,
    string Summary)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Presentation-only counts derived from the immutable steps. They are
    /// intentionally not a substitute for a capability state: a plan with
    /// several detected prerequisites can still be blocked from writes.
    /// </summary>
    public int PassedStepCount => Steps.Count(step => step.State == QualificationStepState.Passed);

    public int ReadyStepCount => Steps.Count(step => step.State == QualificationStepState.Ready);

    public int BlockingStepCount => Steps.Count(step => step.State is QualificationStepState.Blocked or QualificationStepState.Failed);

    public string ReadinessLabel => CapabilityState switch
    {
        CapabilityAccessState.Verified => "Verified control",
        CapabilityAccessState.Experimental => "Experimental control",
        CapabilityAccessState.ReadOnly => "Read-only evidence",
        CapabilityAccessState.Blocked => "Blocked pending evidence",
        CapabilityAccessState.Faulted => "Recovery required",
        _ => "Unsupported"
    };
}

public sealed record CoolingQualificationReportV1(
    int SchemaVersion,
    string SessionId,
    string CapabilityId,
    string HeaderName,
    FanCommissioningState CommissioningState,
    IReadOnlyList<DeviceQualificationStepV1> Steps,
    string Summary)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record EvidenceDeviceV1(
    string Id,
    string Kind,
    string Name,
    string? Manufacturer,
    string? Model,
    string? PnpId);

/// <summary>
/// Local evidence only. The UI must preview/redact this shape before any
/// compatibility upload; the service never uploads it.
/// </summary>
public sealed record HardwareEvidenceReportV1(
    int SchemaVersion,
    string Id,
    DateTimeOffset CapturedAt,
    IReadOnlyList<EvidenceDeviceV1> Devices,
    IReadOnlyList<AdapterHealth> AdapterHealth,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    IReadOnlyList<HealthAlertEventV1> Alerts,
    IReadOnlyList<AdapterTraceEvent> AdapterTrace,
    SafetyRecoveryStatusV1 Recovery,
    IReadOnlyList<DeviceQualificationPlanV1> DevicePlans,
    IReadOnlyList<CoolingQualificationReportV1> CoolingReports)
{
    public const int CurrentSchemaVersion = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<OsdScreenAnchor>))]
public enum OsdScreenAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Per-user presentation settings for the local OSD window. They do not alter
/// an OSD layout's sensor selection or render frames into third-party overlays.
/// </summary>
public sealed record OsdPresentationSettingsV1(
    int SchemaVersion,
    string Id,
    string? MonitorStableId,
    OsdScreenAnchor Anchor,
    double? OpacityOverride,
    double? ScaleOverride,
    string Hotkey,
    bool Enabled)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultId = "osd.presentation.default";
}

public sealed record WgcRecordingPreflightV1(
    int SchemaVersion,
    bool GraphicsCaptureSupported,
    bool SystemPickerRequired,
    bool EncoderConfigured,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}
