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
