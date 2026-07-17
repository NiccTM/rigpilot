namespace PCHelper.Contracts;

/// <summary>Outcome of a contained HID peripheral enumeration.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HidInventoryOutcome>))]
public enum HidInventoryOutcome
{
    /// <summary>Enumeration completed cleanly; <see cref="HidInventoryResultV1.Devices"/> is authoritative.</summary>
    Succeeded,

    /// <summary>The enumeration process could not complete; the device list is empty.</summary>
    EnumerationFailed,
}

/// <summary>
/// One read-only HID device inventory record. Contains only classification-relevant
/// identity — never a serial number, usage data, or any writable capability.
/// </summary>
public sealed record HidDeviceInventoryItemV1(
    int VendorId,
    int ProductId,
    int UsagePage,
    int Usage,
    string DeviceClass,
    string? ProductName,
    string? Manufacturer);

/// <summary>
/// Result of a contained, read-only HID peripheral enumeration. Devices are reported only
/// on <see cref="HidInventoryOutcome.Succeeded"/>; no entry implies any write capability.
/// </summary>
public sealed record HidInventoryResultV1(
    HidInventoryOutcome Outcome,
    IReadOnlyList<HidDeviceInventoryItemV1> Devices,
    string? Detail)
{
    public static HidInventoryResultV1 Failed(string detail) =>
        new(HidInventoryOutcome.EnumerationFailed, [], detail);
}

/// <summary>Outcome of a contained, read-only Kraken X3 telemetry pass.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<KrakenTelemetryOutcome>))]
public enum KrakenTelemetryOutcome
{
    /// <summary>A plausible status report was read; the telemetry fields are populated.</summary>
    Succeeded,

    /// <summary>No supported Kraken X3-family device (VID 0x1E71, PID 0x2007/0x2014) is present.</summary>
    DeviceNotFound,

    /// <summary>The device exists but its HID stream could not be opened (for example CAM owns it exclusively).</summary>
    AccessDenied,

    /// <summary>The stream opened but no plausible status report arrived within the read budget.</summary>
    NoStatusReport,

    /// <summary>The telemetry pass failed for another contained reason.</summary>
    Failed,
}

/// <summary>
/// Read-only liquid-cooler telemetry from an NZXT Kraken X3-family device
/// (protocol derived from liquidctl's GPL-3.0 kraken3 driver; see
/// THIRD_PARTY_NOTICES). RigPilot never writes a HID report to the device on
/// this path — the firmware streams status reports unsolicited — so this
/// carries no pump or lighting write capability of any kind.
/// </summary>
public sealed record KrakenTelemetryV1(
    int SchemaVersion,
    KrakenTelemetryOutcome Outcome,
    string? ProductName,
    double? LiquidTemperatureCelsius,
    int? PumpSpeedRpm,
    int? PumpDutyPercent,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static KrakenTelemetryV1 Unavailable(KrakenTelemetryOutcome outcome, string message) =>
        new(CurrentSchemaVersion, outcome, null, null, null, null, message);
}

/// <summary>
/// Requests a native Kraken X3 lighting write (RigPilot's own adapter; protocol
/// derived from liquidctl's GPL-3.0 kraken3 driver, see THIRD_PARTY_NOTICES).
/// The write is Experimental: it requires the explicit confirmation flag plus
/// the exact device identity, runs inside the crash-contained Adapter Host
/// child, and has no firmware read-back — the result reports that the write
/// was issued and visually confirmable, never that it was "verified".
/// </summary>
public sealed record KrakenLightingRequestV1(
    int SchemaVersion,
    string Colour,
    bool TurnOff,
    bool ConfirmExperimental,
    string? ConfirmDeviceId)
{
    public const int CurrentSchemaVersion = 1;
    public const string ExactDeviceId = "nzxt:kraken-x3";

    /// <summary>Returns null when the request is valid, otherwise the exact refusal.</summary>
    public string? Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return $"Unsupported Kraken lighting request schema {SchemaVersion}.";
        }
        if (!ConfirmExperimental)
        {
            return "Native Kraken lighting is Experimental and requires explicit confirmation.";
        }
        if (!string.Equals(ConfirmDeviceId, ExactDeviceId, StringComparison.Ordinal))
        {
            return $"Native Kraken lighting requires exact-device confirmation '{ExactDeviceId}'.";
        }
        if (!TurnOff)
        {
            string value = Colour.Trim().TrimStart('#');
            if (value.Length != 6 || !value.All(Uri.IsHexDigit))
            {
                return "Colour must use #RRGGBB format.";
            }
        }

        return null;
    }
}

/// <summary>
/// Requests a fixed Kraken X3 pump duty. Experimental and exact-device
/// confirmed like lighting, but pump speed is safety-critical: the duty is
/// additionally hard-clamped to [60, 100] % at every layer — the pump can be
/// slowed for noise, never below the conservative floor, never stopped — and
/// the write is read back from the firmware status stream.
/// </summary>
public sealed record KrakenPumpRequestV1(
    int SchemaVersion,
    int DutyPercent,
    bool ConfirmExperimental,
    string? ConfirmDeviceId)
{
    public const int CurrentSchemaVersion = 1;
    public const string ExactDeviceId = "nzxt:kraken-x3";
    public const int MinimumDutyPercent = 60;
    public const int MaximumDutyPercent = 100;

    /// <summary>Returns null when the request is valid, otherwise the exact refusal.</summary>
    public string? Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return $"Unsupported Kraken pump request schema {SchemaVersion}.";
        }
        if (!ConfirmExperimental)
        {
            return "Native Kraken pump control is Experimental and requires explicit confirmation.";
        }
        if (!string.Equals(ConfirmDeviceId, ExactDeviceId, StringComparison.Ordinal))
        {
            return $"Kraken pump control requires exact-device confirmation for '{ExactDeviceId}'.";
        }
        if (DutyPercent is < MinimumDutyPercent or > MaximumDutyPercent)
        {
            return $"Pump duty must stay within [{MinimumDutyPercent}, {MaximumDutyPercent}] %; the pump is never slowed below the safety floor or stopped.";
        }

        return null;
    }
}

public enum KrakenPumpOutcome
{
    /// <summary>The duty was written and the firmware status stream confirmed it.</summary>
    ReadBackVerified,

    /// <summary>The duty was written but the status stream did not confirm it within the window.</summary>
    WriteIssued,

    /// <summary>No supported Kraken X3-family device is present.</summary>
    DeviceNotFound,

    /// <summary>The device exists but its HID stream could not be opened (a competing writer may own it).</summary>
    AccessDenied,

    /// <summary>The pump pass failed for another contained reason.</summary>
    Failed,
}

public sealed record KrakenPumpResultV1(
    int SchemaVersion,
    KrakenPumpOutcome Outcome,
    string? ProductName,
    int RequestedDutyPercent,
    int? ObservedDutyPercent,
    int? ObservedPumpRpm,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static KrakenPumpResultV1 Unavailable(KrakenPumpOutcome outcome, string message) =>
        new(CurrentSchemaVersion, outcome, null, 0, null, null, message);
}

public enum KrakenLightingOutcome
{
    /// <summary>The lighting reports were written. There is no firmware read-back; confirmation is visual.</summary>
    WriteIssued,

    /// <summary>No supported Kraken X3-family device is present.</summary>
    DeviceNotFound,

    /// <summary>The device exists but its HID stream could not be opened (a competing writer may own it).</summary>
    AccessDenied,

    /// <summary>The lighting pass failed for another contained reason.</summary>
    Failed,
}

public sealed record KrakenLightingResultV1(
    int SchemaVersion,
    KrakenLightingOutcome Outcome,
    string? ProductName,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static KrakenLightingResultV1 Unavailable(KrakenLightingOutcome outcome, string message) =>
        new(CurrentSchemaVersion, outcome, null, message);
}

public enum SmbusRgbProbeOutcome
{
    /// <summary>At least one address answered the read-only ENE detection pattern.</summary>
    ControllersFound,

    /// <summary>The bus was probed but no address answered the detection pattern.</summary>
    NoControllers,

    /// <summary>The signed PawnIO SMBus transport could not be opened.</summary>
    TransportUnavailable,

    /// <summary>The probe failed for another contained reason.</summary>
    Failed,
}

/// <summary>One SMBus address that acknowledged the read-only detection reads.</summary>
public sealed record SmbusRgbControllerSightingV1(
    int Address,
    bool PatternMatched,
    string ObservedBytes);

/// <summary>
/// Result of the purely read-only ENE DIMM RGB controller detection probe. The
/// probe issues SMBus read-byte transactions only, restricted to the
/// RGB-controller address range; it never writes a pointer, colour, or any
/// other byte to the bus.
/// </summary>
public sealed record SmbusRgbProbeResultV1(
    int SchemaVersion,
    SmbusRgbProbeOutcome Outcome,
    IReadOnlyList<SmbusRgbControllerSightingV1> Sightings,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static SmbusRgbProbeResultV1 Unavailable(SmbusRgbProbeOutcome outcome, string message) =>
        new(CurrentSchemaVersion, outcome, [], message);
}

/// <summary>One FCH SMBus port's read-only survey: DDR4 SPD presence and ENE detection sightings.</summary>
public sealed record SmbusPortSurveyV1(
    int Port,
    IReadOnlyList<int> SpdAddresses,
    IReadOnlyList<SmbusRgbControllerSightingV1> Sightings);

/// <summary>Read-only survey of every FCH SMBus port; empty Ports means the transport was unavailable.</summary>
public sealed record SmbusBusSurveyV1(
    int SchemaVersion,
    IReadOnlyList<SmbusPortSurveyV1> Ports,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Requests termination of the running processes for one or more detected
/// conflicting controllers (for example NZXT CAM, MSI Afterburner, Fan Control,
/// Armoury Crate). This frees the device handles those apps hold so RigPilot's
/// own gated write paths can operate; it takes over no hardware control and is
/// distinct from the identity-verified hardware-takeover executor. The service
/// only ever terminates processes on its curated known-controller allowlist,
/// never an arbitrary name, and only with <see cref="Confirm"/> set. An empty
/// <see cref="ConflictIds"/> means every detected running conflict.
/// </summary>
public sealed record StopConflictingProcessesRequestV1(
    int SchemaVersion,
    IReadOnlyList<string> ConflictIds,
    bool Confirm)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record TerminatedProcessV1(int ProcessId, string ProcessName, bool Terminated, string? Error);

/// <summary>
/// Requests a native AURA addressable USB lighting write. Experimental and
/// exact-device confirmed like the Kraken lighting request; lighting registers
/// only, no EEPROM/save, no read-back.
/// </summary>
public sealed record AuraLightingRequestV1(
    int SchemaVersion,
    string Colour,
    bool TurnOff,
    bool ConfirmExperimental,
    string? ConfirmDeviceId)
{
    public const int CurrentSchemaVersion = 1;
    public const string ExactDeviceId = "asus:aura-usb";

    public string? Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return $"Unsupported AURA lighting request schema {SchemaVersion}.";
        }
        if (!ConfirmExperimental)
        {
            return "Native AURA lighting is Experimental and requires explicit confirmation.";
        }
        if (!string.Equals(ConfirmDeviceId, ExactDeviceId, StringComparison.Ordinal))
        {
            return $"AURA lighting requires exact-device confirmation for '{ExactDeviceId}'.";
        }

        return null;
    }
}

/// <summary>
/// Result of RigPilot's in-house AURA addressable USB lighting write. Shares
/// the Kraken lighting outcome vocabulary (issued / not-found / access-denied /
/// failed); like all native lighting there is no firmware read-back, so a
/// result never claims verification.
/// </summary>
public sealed record AuraLightingResultV1(
    int SchemaVersion,
    KrakenLightingOutcome Outcome,
    string? ProductName,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static AuraLightingResultV1 Unavailable(KrakenLightingOutcome outcome, string message) =>
        new(CurrentSchemaVersion, outcome, null, message);
}

public sealed record StopConflictingProcessesResultV1(
    int SchemaVersion,
    IReadOnlyList<TerminatedProcessV1> Results,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public int TerminatedCount => Results.Count(result => result.Terminated);

    public static StopConflictingProcessesResultV1 Empty(string message) =>
        new(CurrentSchemaVersion, [], message);
}

/// <summary>
/// One physical drive's health snapshot from the Windows Storage provider:
/// identity, media/bus class, the OS-reported health status, and the optional
/// reliability counters (temperature, wear, power-on hours) where the drive
/// exposes them. Read-only evidence — RigPilot has no storage write path of
/// any kind. Absent counters stay null; they are never invented.
/// </summary>
public sealed record StorageDeviceHealthV1(
    string FriendlyName,
    string MediaType,
    string BusType,
    string HealthStatus,
    double SizeGigabytes,
    double? TemperatureCelsius,
    int? WearPercent,
    double? PowerOnHours);

/// <summary>Read-only drive-health report for the Devices page and CLI.</summary>
public sealed record StorageHealthReportV1(
    int SchemaVersion,
    IReadOnlyList<StorageDeviceHealthV1> Devices,
    string Message)
{
    public const int CurrentSchemaVersion = 1;

    public static StorageHealthReportV1 Unavailable(string message) =>
        new(CurrentSchemaVersion, [], message);
}
