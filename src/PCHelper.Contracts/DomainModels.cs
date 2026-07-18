using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityAccessState>))]
public enum CapabilityAccessState
{
    Verified,
    Experimental,
    ReadOnly,
    Blocked,
    Unsupported,
    Faulted
}

[JsonConverter(typeof(JsonStringEnumConverter<AdapterExecutionContext>))]
public enum AdapterExecutionContext
{
    SystemService,
    UserSession,
    AdapterHost
}

[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    Safe,
    Guarded,
    Experimental,
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceLevel>))]
public enum EvidenceLevel
{
    None,
    Detected,
    ReadBackVerified,
    SingleSystem,
    TwoSystemCertified
}

[JsonConverter(typeof(JsonStringEnumConverter<SensorQuality>))]
public enum SensorQuality
{
    Good,
    Stale,
    Unavailable,
    Invalid
}

[JsonConverter(typeof(JsonStringEnumConverter<DeviceKind>))]
public enum DeviceKind
{
    OperatingSystem,
    Motherboard,
    Bios,
    Cpu,
    Gpu,
    Memory,
    Storage,
    Network,
    Cooling,
    Lighting,
    Controller,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<ControlDomain>))]
public enum ControlDomain
{
    CoolingSafety,
    Power,
    Cpu,
    Gpu,
    Cooling,
    Lighting,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter<ControlValueKind>))]
public enum ControlValueKind
{
    Numeric,
    Boolean,
    Text,
    Curve,
    Colour
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileTransactionState>))]
public enum ProfileTransactionState
{
    Pending,
    Prepared,
    Applying,
    Verifying,
    Committed,
    RollingBack,
    RolledBack,
    Failed,
    RecoveryRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<TuningObjective>))]
public enum TuningObjective
{
    Quiet,
    Efficiency,
    Performance
}

[JsonConverter(typeof(JsonStringEnumConverter<CoolingCurveMode>))]
public enum CoolingCurveMode
{
    /// <summary>Quiet-biased: stays near the floor longer, reaches full speed only near critical.</summary>
    Silent,
    /// <summary>The default balance of noise and temperature.</summary>
    Balanced,
    /// <summary>Temperature-biased: ramps early and steeply, reaches full speed at a lower temperature.</summary>
    Cooling
}

[JsonConverter(typeof(JsonStringEnumConverter<HardwareOperationKind>))]
public enum HardwareOperationKind
{
    Calibration,
    CommissioningPulse,
    Tuning,
    AutoOc
}

[JsonConverter(typeof(JsonStringEnumConverter<HardwareOperationState>))]
public enum HardwareOperationState
{
    Pending,
    Running,
    Screening,
    Completed,
    Aborted,
    Failed,
    RecoveryRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<TuneDirection>))]
public enum TuneDirection
{
    Minimize,
    Maximize
}

[JsonConverter(typeof(JsonStringEnumConverter<AutomationTriggerKind>))]
public enum AutomationTriggerKind
{
    Process,
    ForegroundApplication,
    Schedule,
    SessionLock,
    Idle,
    Hotkey
}

public sealed record HardwareDevice(
    string Id,
    string Name,
    DeviceKind Kind,
    string? Manufacturer,
    string? Model,
    string? PnpId,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Numeric control bounds. <see cref="Default"/> is the vendor default value
/// when the adapter can discover one (e.g. the NVML default power limit) — it
/// lets UIs show Afterburner-style percentages where 100% is stock, not max.
/// Optional and additive: absent in JSON means unknown.
/// </summary>
public sealed record NumericRange(double Minimum, double Maximum, double Step, double? Default = null);

public sealed record CapabilityDescriptor(
    string Id,
    string AdapterId,
    string DeviceId,
    string Name,
    CapabilityAccessState State,
    AdapterExecutionContext ExecutionContext,
    ControlValueKind ValueKind,
    NumericRange? Range,
    string? Unit,
    RiskLevel Risk,
    EvidenceLevel Evidence,
    string? ConflictOwner,
    string Reason,
    bool CanResetToDefault,
    ControlDomain Domain = ControlDomain.Other);

public sealed record SensorSample(
    string SensorId,
    string AdapterId,
    string DeviceId,
    string Name,
    DateTimeOffset Timestamp,
    double? Value,
    string Unit,
    SensorQuality Quality,
    TimeSpan Freshness);

public sealed record CurvePoint(double Input, double Output);

public sealed record ControlValue(
    ControlValueKind Kind,
    double? Numeric = null,
    bool? Boolean = null,
    string? Text = null,
    IReadOnlyList<CurvePoint>? Curve = null)
{
    public static ControlValue FromNumeric(double value) => new(ControlValueKind.Numeric, Numeric: value);

    public static ControlValue FromBoolean(bool value) => new(ControlValueKind.Boolean, Boolean: value);

    public static ControlValue FromText(string value) => new(ControlValueKind.Text, Text: value);

    public static ControlValue FromCurve(IReadOnlyList<CurvePoint> value) => new(ControlValueKind.Curve, Curve: value);

    public static ControlValue FromColour(string rgbHex) => new(ControlValueKind.Colour, Text: rgbHex);
}

public sealed record ProfileAction(
    string Id,
    string AdapterId,
    string CapabilityId,
    ControlValue Value,
    bool Required,
    int Order);

public sealed record SafetyLimits(
    double FallbackCriticalTemperatureCelsius = 90,
    int StalePollLimit = 3,
    double EmergencyFanDutyPercent = 100,
    bool AllowAutomaticVoltageIncrease = false,
    bool AllowCpuFanStop = false,
    bool AllowPumpStop = false);

public sealed record ProfileV1(
    int SchemaVersion,
    string Id,
    string Name,
    string Description,
    IReadOnlyList<ProfileAction> Actions,
    SafetyLimits SafetyLimits,
    IReadOnlyList<string> AutomationReferences,
    bool IsBuiltIn,
    bool IsExperimental)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record PreparedAction(
    ProfileAction Action,
    ControlValue? PreviousValue,
    DateTimeOffset PreparedAt,
    string AdapterToken);

public sealed record ActionVerification(
    string ActionId,
    bool Success,
    ControlValue? ObservedValue,
    string Message);

public sealed record ProfileTransaction(
    string Id,
    long Revision,
    string ProfileId,
    ProfileTransactionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PreparedAction> PreparedActions,
    IReadOnlyList<ActionVerification> Verifications,
    string? Error);

public sealed record TuneBounds(double Minimum, double Maximum, double Step);

public sealed record TunePlan(
    string Id,
    string DeviceId,
    TuningObjective Objective,
    IReadOnlyDictionary<string, TuneBounds> Bounds,
    TimeSpan ScreeningDuration,
    double TemperatureCeilingCelsius,
    double? PowerCeilingWatts,
    bool Provisional,
    DateTimeOffset? SoakStartedAt,
    TimeSpan ActiveUseRequired,
    int ColdBootsRequired);

public sealed record StartCalibrationRequest(
    string CapabilityId,
    string RpmSensorId,
    bool ConfirmExperimental,
    bool ConfirmDevice,
    bool AllowFanStop = false,
    TimeSpan? SettlingTime = null,
    int StableSampleCount = 3,
    int MaximumSampleCount = 15,
    TimeSpan? SampleInterval = null,
    double StabilityTolerancePercent = 10,
    int RestartVerificationCycles = 2,
    IReadOnlyList<FanCalibrationTemperatureLimit>? TemperatureLimits = null,
    string? CommissioningSessionId = null);

public sealed record FanCalibrationTemperatureLimit(string SensorId, double MaximumCelsius);

public sealed record FanCalibrationPoint(
    double DutyPercent,
    double Rpm,
    int SampleCount = 1,
    double RpmSpread = 0,
    bool Stable = true);

public sealed record FanCalibrationResult(
    string CapabilityId,
    string RpmSensorId,
    double MaximumRpm,
    double? StallDutyPercent,
    double? RestartDutyPercent,
    double MinimumDutyPercent,
    bool RestartVerified,
    IReadOnlyList<FanCalibrationPoint> Measurements,
    int RestartVerificationCyclesCompleted = 0,
    int StableSampleCount = 1,
    TimeSpan? SampleInterval = null,
    double StabilityTolerancePercent = 0,
    bool AllMeasurementsStable = true,
    double? VerifiedStopDutyPercent = null,
    IReadOnlyDictionary<string, double>? MaximumTemperaturesCelsius = null,
    double? EffectiveFloorDutyPercent = null,
    double? EffectiveFloorRpm = null,
    double? FirstResponsiveDutyPercent = null,
    bool NonStopFloorObserved = false);

public sealed record StartTuneRequest(
    TunePlan Plan,
    string CapabilityId,
    TuneDirection Direction,
    bool ConfirmExperimental,
    bool ConfirmDevice,
    TimeSpan? CandidateScreeningTime = null,
    int MaximumCandidates = 12,
    // Auto-OC refinement: after the coarse scan finds the last stable candidate
    // and the first failing one, screen this many evenly-spaced values in that
    // gap to locate the stability edge precisely. 0 keeps the plain coarse scan.
    int RefinementCandidates = 0,
    // Back off this many units (the capability's unit) from the best stable
    // value before the final long screening, so the shipped result carries
    // headroom instead of sitting on the edge of stability. 0 = no margin.
    double SafetyMargin = 0,
    // Stop climbing once a passing candidate's peak temperature reaches within
    // this many degrees of the ceiling, so the result keeps thermal headroom
    // instead of only stability headroom. 0 = climb purely to the stability edge.
    double ThermalHeadroomCelsius = 0);

[JsonConverter(typeof(JsonStringEnumConverter<AutoOcWorkloadMode>))]
public enum AutoOcWorkloadMode
{
    Stopped,
    Core,
    Memory,
    Combined
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkloadHostCommand>))]
public enum WorkloadHostCommand
{
    Ping,
    SetMode,
    Stop
}

/// <summary>
/// Private per-operation endpoint created by the signed-in dashboard. The
/// service never launches a GPU workload from session zero; it authenticates
/// this random pipe and refuses an ambiguous adapter mapping.
/// </summary>
public sealed record WorkloadHostDescriptorV1(
    int SchemaVersion,
    string SessionId,
    string PipeName,
    string AuthenticationToken,
    string TargetDeviceId,
    int VendorId,
    uint AdapterIndex,
    int HostProcessId)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record WorkloadHostRequestV1(
    int SchemaVersion,
    string SessionId,
    string AuthenticationToken,
    WorkloadHostCommand Command,
    AutoOcWorkloadMode Mode)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record WorkloadHostStatusV1(
    int SchemaVersion,
    string SessionId,
    bool Authenticated,
    bool Ready,
    bool Running,
    AutoOcWorkloadMode Mode,
    string AdapterDescription,
    int VendorId,
    int DeviceId,
    long AdapterLuid,
    uint AdapterIndex,
    int MatchingHardwareAdapterCount,
    long DispatchCount,
    DateTimeOffset HeartbeatAt,
    string? Error)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Exact telemetry binding used by a tune; no same-kind sensor fallback is permitted.</summary>
public sealed record TuneSensorBindingV2(
    int SchemaVersion,
    string TargetDeviceId,
    IReadOnlyList<string> BoundDeviceIds,
    IReadOnlyList<string> TemperatureSensorIds,
    string UtilizationSensorId,
    string CoreClockSensorId,
    string MemoryClockSensorId,
    string? PowerSensorId)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record StartAutoOcV2Request(
    int SchemaVersion,
    string DeviceId,
    string CoreCapabilityId,
    string MemoryCapabilityId,
    WorkloadHostDescriptorV1 WorkloadHost,
    bool ConfirmExperimental,
    bool ConfirmDevice)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record TuneScreeningResult(
    bool Passed,
    string Message,
    double? MaximumTemperatureCelsius,
    double? AveragePowerWatts,
    double? AverageClockMegahertz);

public sealed record TuneCandidateResult(
    double Value,
    bool Passed,
    string Message,
    TuneScreeningResult Screening);

public sealed record TuneResult(
    string CapabilityId,
    string StatusLabel,
    double? SelectedValue,
    IReadOnlyList<TuneCandidateResult> Candidates,
    ProfileV1? GeneratedProfile);

public sealed record AutoOcResultV2(
    int SchemaVersion,
    string DeviceId,
    TuneResult? CoreResult,
    TuneResult? MemoryResult,
    TuneScreeningResult? CombinedScreening,
    double? CoreOffsetMegahertz,
    double? MemoryOffsetMegahertz,
    bool AllRequestedFamiliesVerified,
    bool PriorStateRestored,
    bool HardwareStateKnown,
    ProfileV2? GeneratedProfile,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Message)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record HardwareOperationStatus(
    string Id,
    HardwareOperationKind Kind,
    HardwareOperationState State,
    string CapabilityId,
    string CapabilityName,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    double ProgressPercent,
    string Message,
    FanCalibrationResult? CalibrationResult,
    TuneResult? TuneResult,
    string? Error,
    AutoOcResultV2? AutoOcResult = null);

public sealed record AutomationRuleV1(
    int SchemaVersion,
    string Id,
    string Name,
    bool Enabled,
    AutomationTriggerKind TriggerKind,
    string TriggerValue,
    string ProfileId,
    int Priority)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record AutomationObservation(
    DateTimeOffset Timestamp,
    IReadOnlySet<string> RunningProcesses,
    string? ForegroundProcess,
    bool SessionLocked,
    TimeSpan IdleTime,
    string? Hotkey);

public sealed record AutomationDecision(
    string? ProfileId,
    bool ShouldSwitch,
    string Reason,
    IReadOnlyList<string> ActiveRuleIds);

public sealed record AdapterManifest(
    string Id,
    string Name,
    string Version,
    string Licence,
    string? RequiredDriver,
    AdapterExecutionContext ExecutionContext,
    IReadOnlyList<string> SupportedDevicePatterns,
    IReadOnlyList<string> ControlFamilies);

public sealed record AdapterHealth(
    string AdapterId,
    bool Healthy,
    DateTimeOffset CheckedAt,
    string Message,
    IReadOnlyList<string> Errors);

public sealed record ConflictDescriptor(
    string Id,
    string DisplayName,
    string ProcessName,
    IReadOnlyList<string> ResourceFamilies,
    bool IsRunning,
    string Guidance);

public sealed record DiagnosticWarning(
    string Code,
    string Severity,
    string Message,
    string? Remediation);

public sealed record HardwareSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<HardwareDevice> Devices,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    IReadOnlyList<SensorSample> Sensors,
    IReadOnlyList<ConflictDescriptor> Conflicts,
    IReadOnlyList<DiagnosticWarning> Warnings,
    IReadOnlyList<AdapterHealth> AdapterHealth);

public sealed record AdapterProbeResult(
    AdapterManifest Manifest,
    IReadOnlyList<HardwareDevice> Devices,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    IReadOnlyList<DiagnosticWarning> Warnings);
