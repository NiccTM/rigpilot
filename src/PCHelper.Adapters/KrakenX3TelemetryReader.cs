using HidSharp;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only telemetry for NZXT Kraken X3-family coolers (X53/X63/X73). The
/// protocol is derived from liquidctl's GPL-3.0 <c>kraken3</c> driver
/// (attributed in THIRD_PARTY_NOTICES): the firmware streams unsolicited
/// 64-byte status reports beginning <c>0x75 0x02</c> that carry liquid
/// temperature, pump speed, and pump duty. This reader therefore performs no
/// HID writes of any kind — it opens the device stream, reads a bounded number
/// of input reports, and parses the first plausible status report. Like the
/// HID inventory, it is intended to run inside the crash-isolated Adapter Host
/// child so a native HID fault cannot take down the service.
/// </summary>
public static class KrakenX3TelemetryReader
{
    internal const int VendorId = 0x1E71;
    internal static readonly int[] ProductIds = [0x2007, 0x2014];

    private const byte StatusReportId = 0x75;
    private const byte StatusReportKind = 0x02;
    private const int MaximumReadAttempts = 12;
    private const int ReadTimeoutMilliseconds = 2000;

    public static KrakenTelemetryV1 Read()
    {
        HidDevice? device;
        try
        {
            device = DeviceList.Local.GetHidDevices(VendorId)
                .Where(candidate => ProductIds.Contains(candidate.ProductID))
                .OrderByDescending(candidate => SafeMaxInputReportLength(candidate))
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return KrakenTelemetryV1.Unavailable(
                KrakenTelemetryOutcome.Failed,
                $"HID enumeration failed: {exception.GetType().Name}.");
        }
        if (device is null)
        {
            return KrakenTelemetryV1.Unavailable(
                KrakenTelemetryOutcome.DeviceNotFound,
                "No Kraken X3-family cooler (VID 0x1E71, PID 0x2007/0x2014) is connected.");
        }

        string? productName = TryReadProductName(device);
        try
        {
            if (!device.TryOpen(out HidStream stream))
            {
                return KrakenTelemetryV1.Unavailable(
                    KrakenTelemetryOutcome.AccessDenied,
                    "The Kraken's HID stream is unavailable — another program (for example NZXT CAM) may own it exclusively. RigPilot will not force access.");
            }
            using (stream)
            {
                stream.ReadTimeout = ReadTimeoutMilliseconds;
                int reportLength = Math.Max(64, SafeMaxInputReportLength(device));
                byte[] buffer = new byte[reportLength];
                for (int attempt = 0; attempt < MaximumReadAttempts; attempt++)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        return KrakenTelemetryV1.Unavailable(
                            KrakenTelemetryOutcome.NoStatusReport,
                            "The Kraken produced no status report within the read budget. Telemetry streaming may be paused; no request was written to the device.");
                    }
                    if (TryParseStatusReport(buffer.AsSpan(0, read), out double temperature, out int rpm, out int duty))
                    {
                        return new KrakenTelemetryV1(
                            KrakenTelemetryV1.CurrentSchemaVersion,
                            KrakenTelemetryOutcome.Succeeded,
                            productName,
                            temperature,
                            rpm,
                            duty,
                            $"Read a streamed status report from {productName ?? "the Kraken"} without writing to the device.");
                    }
                }
                return KrakenTelemetryV1.Unavailable(
                    KrakenTelemetryOutcome.NoStatusReport,
                    $"No plausible Kraken status report arrived within {MaximumReadAttempts} reads; the device stream stays untouched by writes.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return KrakenTelemetryV1.Unavailable(
                KrakenTelemetryOutcome.Failed,
                $"Kraken telemetry read failed: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// Parses one HID input report. liquidctl's offsets assume the report-ID
    /// byte (0x75) is the first byte of the buffer; HidSharp may deliver the
    /// same report with an extra leading byte depending on report numbering,
    /// so the parser anchors on the documented <c>0x75 0x02</c> status marker
    /// at index 0 or 1 instead of trusting a fixed base, then applies the
    /// liquidctl arithmetic relative to that anchor and rejects readings
    /// outside physically plausible bounds.
    /// </summary>
    public static bool TryParseStatusReport(ReadOnlySpan<byte> report, out double liquidTemperatureCelsius, out int pumpSpeedRpm, out int pumpDutyPercent)
    {
        liquidTemperatureCelsius = 0;
        pumpSpeedRpm = 0;
        pumpDutyPercent = 0;
        int anchor = -1;
        for (int candidate = 0; candidate <= 1; candidate++)
        {
            if (report.Length > candidate + 19
                && report[candidate] == StatusReportId
                && report[candidate + 1] == StatusReportKind)
            {
                anchor = candidate;
                break;
            }
        }
        if (anchor < 0)
        {
            return false;
        }

        // liquidctl kraken3: temperature = msg[15] + msg[16] / 10,
        // pump rpm = msg[18] << 8 | msg[17], pump duty = msg[19].
        double temperature = report[anchor + 15] + report[anchor + 16] / 10.0;
        int rpm = report[anchor + 18] << 8 | report[anchor + 17];
        int duty = report[anchor + 19];
        bool plausible =
            report[anchor + 15] != 0xFF && report[anchor + 16] != 0xFF
            && temperature is >= 1.0 and <= 90.0
            && rpm is >= 0 and <= 6000
            && duty is >= 0 and <= 100;
        if (!plausible)
        {
            return false;
        }
        liquidTemperatureCelsius = temperature;
        pumpSpeedRpm = rpm;
        pumpDutyPercent = duty;
        return true;
    }

    private static int SafeMaxInputReportLength(HidDevice device)
    {
        try { return device.GetMaxInputReportLength(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return 0; }
    }

    private static string? TryReadProductName(HidDevice device)
    {
        try
        {
            string name = device.GetProductName();
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
