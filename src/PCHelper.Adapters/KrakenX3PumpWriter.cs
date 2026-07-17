using HidSharp;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// RigPilot's native pump-duty writer for NZXT Kraken X3-family coolers
/// (X53/X63/X73). The protocol is derived from liquidctl's GPL-3.0
/// <c>kraken3</c> driver (reviewed 2026-07-16; attributed in
/// THIRD_PARTY_NOTICES; no code copied): a speed command is one 64-byte HID
/// output report <c>[0x72, channel, 0x00, 0x00]</c> followed by 40 duty bytes —
/// one per critical liquid temperature from 20 to 59 °C. A fixed duty is a
/// flat profile. The pump channel id is 0x01.
///
/// Safety invariants owned by this writer, on top of the transaction layers
/// above it: the commanded duty is HARD-CLAMPED to [60, 100] % — the pump can
/// be slowed for noise but never below RigPilot's conservative floor and never
/// stopped — and the result is read back from the firmware's unsolicited
/// status stream (byte 19 duty, bytes 17/18 RPM) before it is reported
/// verified. Lighting registers are untouched. Intended to run inside the
/// crash-contained Adapter Host child (<c>--set-kraken-pump</c>).
/// </summary>
public static class KrakenX3PumpWriter
{
    public const int MinimumDutyPercent = 60;
    public const int MaximumDutyPercent = 100;
    private const byte SpeedOpcode = 0x72;
    private const byte PumpChannelId = 0x01;
    private const int ProfilePoints = 40;   // critical temps 20..59 °C inclusive
    private const int MessageLength = 64;
    private const int ReadBackAttempts = 12;
    private const int ReadBackTolerancePercent = 3;

    /// <summary>Builds the 64-byte fixed-duty (flat profile) pump-speed report.</summary>
    public static byte[] BuildFixedPumpReport(int dutyPercent)
    {
        byte duty = (byte)Math.Clamp(dutyPercent, MinimumDutyPercent, MaximumDutyPercent);
        byte[] message = new byte[MessageLength];
        message[0] = SpeedOpcode;
        message[1] = PumpChannelId;
        message[2] = 0x00;
        message[3] = 0x00;
        for (int point = 0; point < ProfilePoints; point++)
        {
            message[4 + point] = duty;
        }

        return message;
    }

    private static int SafeMaxOutputReportLength(HidDevice device)
    {
        try
        {
            return device.GetMaxOutputReportLength();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return MessageLength;
        }
    }

    private static int SafeMaxInputReportLength(HidDevice device)
    {
        try
        {
            return device.GetMaxInputReportLength();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return MessageLength;
        }
    }

    /// <summary>
    /// Writes the clamped fixed pump duty to the first connected Kraken
    /// X3-family device and reads the firmware status stream back. An
    /// exclusively-held device is a designed refusal, never forced.
    /// </summary>
    public static KrakenPumpResultV1 Write(int dutyPercent)
    {
        int requested = Math.Clamp(dutyPercent, MinimumDutyPercent, MaximumDutyPercent);

        HidDevice? device;
        try
        {
            device = DeviceList.Local.GetHidDevices(KrakenX3TelemetryReader.VendorId)
                .FirstOrDefault(candidate => KrakenX3TelemetryReader.ProductIds.Contains(candidate.ProductID));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return KrakenPumpResultV1.Unavailable(
                KrakenPumpOutcome.Failed, $"HID enumeration failed: {exception.GetType().Name}.");
        }
        if (device is null)
        {
            return KrakenPumpResultV1.Unavailable(
                KrakenPumpOutcome.DeviceNotFound,
                "No Kraken X3-family cooler (VID 0x1E71, PID 0x2007/0x2014) is connected.");
        }

        string? productName = null;
        try
        {
            productName = device.GetProductName();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Optional metadata only.
        }

        try
        {
            if (!device.TryOpen(out HidStream stream))
            {
                return KrakenPumpResultV1.Unavailable(
                    KrakenPumpOutcome.AccessDenied,
                    "The Kraken's HID stream is unavailable — another program (for example NZXT CAM) may own it exclusively. RigPilot will not force access.");
            }

            using (stream)
            {
                byte[] message = BuildFixedPumpReport(requested);
                // Same buffer contract as the proven lighting writer: HidSharp
                // buffers start with the report ID; the Kraken uses unnumbered
                // reports, so only prepend the 0x00 report ID when the device
                // reports a >64-byte output length.
                int outputLength = SafeMaxOutputReportLength(device);
                byte[] buffer = new byte[outputLength > MessageLength ? outputLength : MessageLength];
                message.CopyTo(buffer, buffer.Length > MessageLength ? 1 : 0);
                stream.Write(buffer, 0, buffer.Length);

                // The firmware only streams unsolicited status after the
                // liquidctl-documented interval command; without it read-back
                // never arrives on a freshly powered device.
                byte[] interval = new byte[buffer.Length];
                new byte[] { 0x70, 0x02, 0x01, 0xB8, 0x01 }.CopyTo(interval, buffer.Length > MessageLength ? 1 : 0);
                stream.Write(interval, 0, interval.Length);

                // Read back from the firmware's unsolicited status stream. The
                // pump takes a moment to settle; accept the last observed duty.
                stream.ReadTimeout = 2000;
                int? observedDuty = null;
                int? observedRpm = null;
                byte[] report = new byte[Math.Max(SafeMaxInputReportLength(device), MessageLength)];
                for (int attempt = 0; attempt < ReadBackAttempts; attempt++)
                {
                    int read;
                    try
                    {
                        read = stream.Read(report, 0, report.Length);
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }

                    if (KrakenX3TelemetryReader.TryParseStatusReport(
                            report.AsSpan(0, read), out _, out int rpm, out int duty))
                    {
                        observedDuty = duty;
                        observedRpm = rpm;
                        if (Math.Abs(duty - requested) <= ReadBackTolerancePercent)
                        {
                            break;
                        }
                    }
                }

                bool verified = observedDuty is int settled
                    && Math.Abs(settled - requested) <= ReadBackTolerancePercent;
                return new KrakenPumpResultV1(
                    KrakenPumpResultV1.CurrentSchemaVersion,
                    verified ? KrakenPumpOutcome.ReadBackVerified : KrakenPumpOutcome.WriteIssued,
                    productName,
                    requested,
                    observedDuty,
                    observedRpm,
                    verified
                        ? $"Pump duty read back at {observedDuty}% ({observedRpm} RPM)."
                        : observedDuty is int reported
                            ? $"Pump duty written; firmware still reports {reported}% ({observedRpm} RPM) — it may settle within seconds."
                            : "Pump duty written; the status stream produced no read-back within the window.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return KrakenPumpResultV1.Unavailable(
                KrakenPumpOutcome.Failed,
                $"The pump write failed: {exception.GetType().Name}. The firmware fail-safe curve remains in charge if no command was accepted.");
        }
    }
}
