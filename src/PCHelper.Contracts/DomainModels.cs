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
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<TuningObjective>))]
public enum TuningObjective
{
    Quiet,
    Efficiency,
    Performance
}

public sealed record HardwareDevice(
    string Id,
    string Name,
    DeviceKind Kind,
    string? Manufacturer,
    string? Model,
    string? PnpId,
    IReadOnlyDictionary<string, string> Properties);

public sealed record NumericRange(double Minimum, double Maximum, double Step);

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
