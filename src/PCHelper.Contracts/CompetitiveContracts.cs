using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<AutoOcValidationState>))]
public enum AutoOcValidationState
{
    Rejected,
    Screened,
    Provisional,
    Validated,
    Invalidated,
    RecoveryRequired
}

public sealed record AutoOcObjectiveConstraintsV3(
    TuningObjective Objective,
    double MaximumBaselineVariationPercent = 3,
    double MinimumEfficiencyPerformancePercent = 98,
    double MinimumQuietPerformancePercent = 95,
    double TemperatureCeilingCelsius = 83,
    double? PowerCeilingWatts = null,
    TimeSpan? BaselineSampleDuration = null,
    TimeSpan? CandidateScreeningDuration = null,
    TimeSpan? FinalScreeningDuration = null,
    bool RequestPresentMonValidation = false);

public sealed record HardwareFingerprintV1(
    int SchemaVersion,
    string DeviceId,
    string DeviceIdentity,
    string? PnpId,
    string VbiosVersion,
    string DriverVersion,
    string FingerprintSha256)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record AutoOcMeasurementV3(
    string Label,
    TimeSpan Duration,
    bool Passed,
    double? ThroughputScore,
    double? AveragePowerWatts,
    double? MaximumTemperatureCelsius,
    double? AverageFanRpm,
    double? AverageClockMegahertz,
    string Message);

public sealed record AutoOcCandidateScoreV3(
    string Stage,
    double Value,
    bool Passed,
    double? ThroughputScore,
    double? AveragePowerWatts,
    double? AverageFanRpm,
    double? MaximumTemperatureCelsius,
    double? ObjectiveScore,
    string Message);

public sealed record RestorationProofV1(
    bool PriorStateRestored,
    bool HardwareStateKnown,
    DateTimeOffset VerifiedAt,
    IReadOnlyList<HardwareStateVerification> Verifications,
    string Message);

public sealed record StartAutoOcV3Request(
    int SchemaVersion,
    string DeviceId,
    string CoreCapabilityId,
    string MemoryCapabilityId,
    string? PowerLimitCapabilityId,
    WorkloadHostDescriptorV1 WorkloadHost,
    AutoOcObjectiveConstraintsV3 Constraints,
    bool ConfirmExperimental,
    bool ConfirmDevice)
{
    public const int CurrentSchemaVersion = 3;
}

public sealed record AutoOcResultV3(
    int SchemaVersion,
    string DeviceId,
    TuneResult? CoreResult,
    TuneResult? MemoryResult,
    TuneResult? PowerLimitResult,
    TuneScreeningResult? CombinedScreening,
    double? CoreOffsetMegahertz,
    double? MemoryOffsetMegahertz,
    double? PowerLimitWatts,
    IReadOnlyList<AutoOcMeasurementV3> BaselineMeasurements,
    AutoOcMeasurementV3? FinalMeasurement,
    IReadOnlyList<AutoOcCandidateScoreV3> CandidateScores,
    double? BaselineVariationPercent,
    HardwareFingerprintV1 HardwareFingerprint,
    AutoOcValidationState ValidationState,
    bool AllRequestedFamiliesVerified,
    RestorationProofV1 RestorationProof,
    ProfileV2? GeneratedProfile,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Message)
{
    public const int CurrentSchemaVersion = 3;
}

[JsonConverter(typeof(JsonStringEnumConverter<AutoOcStabilityEventKind>))]
public enum AutoOcStabilityEventKind
{
    Whea,
    DisplayDriverReset,
    UncleanShutdown,
    HardwareFingerprintChanged,
    RestorationFailed
}

public sealed record AutoOcStabilityEventV1(
    AutoOcStabilityEventKind Kind,
    DateTimeOffset ObservedAt,
    string Message);

public sealed record AutoOcProfileValidationV1(
    int SchemaVersion,
    string Id,
    string ProfileId,
    HardwareFingerprintV1 HardwareFingerprint,
    AutoOcValidationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TimeSpan ActiveUse,
    int SuccessfulColdBoots,
    int SuccessfulManualApplications,
    DateTimeOffset? ActiveSessionStartedAt,
    string? ActiveServiceInstanceId,
    IReadOnlyList<AutoOcStabilityEventV1> RelevantEvents,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ControlSessionMetricsV1(
    double? AverageFramesPerSecond,
    double? OnePercentLowFramesPerSecond,
    double? PointOnePercentLowFramesPerSecond,
    double? AveragePowerWatts,
    double? PeakTemperatureCelsius,
    double? AverageCoreClockMegahertz,
    double? AverageMemoryClockMegahertz,
    double? AverageFanRpm);

public sealed record ControlSessionSummaryV1(
    int SchemaVersion,
    string Id,
    string? ProfileId,
    string? GameId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    ControlSessionMetricsV1 Baseline,
    ControlSessionMetricsV1 Result,
    IReadOnlyList<string> AppliedDomains,
    string VerificationResult,
    string RollbackPath,
    bool LocalOnly)
{
    public const int CurrentSchemaVersion = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileDryRunActionState>))]
public enum ProfileDryRunActionState
{
    Ready,
    RequiresConfirmation,
    OmittedOptional,
    Conflict,
    Blocked,
    IndependentCompanion
}

public sealed record ProfileDryRunActionV1(
    string ActionId,
    string Domain,
    string Description,
    ProfileDryRunActionState State,
    bool Required,
    string? CapabilityId,
    string Message);

public sealed record PreviewProfileV2Request(
    ProfileV2 Profile,
    ProfileActivationSource Source,
    bool ConfirmExperimental,
    IReadOnlyList<string> ConfirmedDeviceIds,
    bool ConfirmManualVoltage,
    IReadOnlyList<string> KnownLightingSceneIds,
    IReadOnlyList<string> KnownOsdLayoutIds);

public sealed record ProfileDryRunResultV1(
    int SchemaVersion,
    string ProfileId,
    bool CanApply,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<ProfileDryRunActionV1> Actions,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> OmittedOptionalActions,
    IReadOnlyList<string> AtomicDomains,
    IReadOnlyList<string> IndependentDomains,
    string ExpectedRollback,
    DateTimeOffset GeneratedAt)
{
    public const int CurrentSchemaVersion = 1;
}
