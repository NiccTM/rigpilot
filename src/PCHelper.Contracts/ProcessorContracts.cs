namespace PCHelper.Contracts;

/// <summary>Outcome of a contained, read-only Ryzen SMU feasibility pass.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RyzenSmuFeasibilityOutcome>))]
public enum RyzenSmuFeasibilityOutcome
{
    /// <summary>The SMU answered and the PM table version is a known Zen 2/3 layout; limits are populated.</summary>
    Succeeded,

    /// <summary>The signed PawnIO runtime is not installed, not running, or refused the executor handle.</summary>
    PawnIoUnavailable,

    /// <summary>PawnIO works but the SMU/PM-table version is not in the audited layout allowlist; raw evidence only.</summary>
    UnrecognisedPmTable,

    /// <summary>The pass failed for another contained reason.</summary>
    Failed,
}

/// <summary>
/// Read-only Ryzen SMU evidence for the PBO qualification gate
/// (docs/qualification/cpu-tuning-and-intel-arc.md step 1). Values come from
/// the SMU's own PM table (the same data HWiNFO and ryzen_monitor read):
/// PPT in watts, TDC/EDC in amperes, THM in °C, each as a limit/actual pair.
/// This pass issues no tuning command of any kind — the only SMU mailbox
/// commands used are the documented read-class version query and PM-table
/// refresh that LibreHardwareMonitor already issues on every poll. CPU/SMU
/// writes remain Blocked regardless of this evidence.
/// </summary>
public sealed record RyzenSmuFeasibilityV1(
    int SchemaVersion,
    RyzenSmuFeasibilityOutcome Outcome,
    long CodeNameId,
    string? SmuFirmwareVersion,
    string? PmTableVersion,
    double? PptLimitWatts,
    double? PptValueWatts,
    double? TdcLimitAmperes,
    double? TdcValueAmperes,
    double? ThmLimitCelsius,
    double? ThmValueCelsius,
    double? EdcLimitAmperes,
    double? EdcValueAmperes,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static RyzenSmuFeasibilityV1 Unavailable(RyzenSmuFeasibilityOutcome outcome, string message) =>
        new(CurrentSchemaVersion, outcome, 0, null, null, null, null, null, null, null, null, null, null, message);
}
