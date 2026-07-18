using System.Reflection;
using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

/// <summary>
/// The dashboard evaluates this before exposing a service-owned write workflow.
/// It is deliberately separate from capability evidence: a compatible runtime
/// does not make an individual hardware control qualified.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServiceCompatibilityState>))]
public enum ServiceCompatibilityState
{
    Ready,
    ReadOnly,
    UpgradeRequired,
    Unavailable
}

public sealed record ServiceRuntimeCompatibilityV1(
    int SchemaVersion,
    ServiceCompatibilityState State,
    string ClientVersion,
    string? ServiceVersion,
    int RequiredProtocolVersion,
    int? NegotiatedProtocolVersion,
    IReadOnlyList<string> MissingFeatures,
    string Summary)
{
    public const int CurrentSchemaVersion = 1;

    public bool CanUseServiceWrites => State == ServiceCompatibilityState.Ready;

    public bool IsServiceReachable => State is not ServiceCompatibilityState.Unavailable;
}

/// <summary>
/// Stable names advertised by the service handshake. New dashboard features
/// must be explicitly added here rather than inferred from an assembly version.
/// </summary>
public static class ServiceRuntimeFeatures
{
    public const string ServiceStatus = "service-status";
    public const string CapabilityV2 = "capability-v2";
    public const string FanCommissioning = "fan-commissioning";
    public const string FanCalibrations = "fan-calibrations";
    public const string Reliability = "reliability";
    public const string AdapterTrace = "adapter-trace";
    public const string CoolingGraphs = "cooling-graphs";
    public const string CoolingOutputRoles = "cooling-output-roles";
    public const string VerifiedHardwareControl = "verified-hardware-control";
    public const string ReleaseWritePolicy = "release-write-policy";
    public const string AutoOcV3 = "auto-oc-v3";
    public const string ProfileDryRunV1 = "profile-dry-run-v1";

    public static IReadOnlyList<string> RequiredByDashboard { get; } =
    [
        ServiceStatus,
        CapabilityV2,
        FanCommissioning,
        FanCalibrations,
        Reliability,
        AdapterTrace,
        CoolingOutputRoles,
        VerifiedHardwareControl
    ];

    public static IReadOnlyList<string> AdvertisedByCurrentService { get; } =
    [
        ServiceStatus,
        CapabilityV2,
        FanCommissioning,
        FanCalibrations,
        Reliability,
        AdapterTrace,
        CoolingGraphs,
        CoolingOutputRoles,
        VerifiedHardwareControl,
        ReleaseWritePolicy,
        AutoOcV3,
        ProfileDryRunV1
    ];
}

public static class RuntimeVersion
{
    public static string Get(Assembly assembly)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int metadata = informational.IndexOf('+');
            return metadata >= 0 ? informational[..metadata] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
