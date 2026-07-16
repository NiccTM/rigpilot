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
