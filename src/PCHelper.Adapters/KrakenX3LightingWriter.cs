using System.Globalization;
using HidSharp;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// RigPilot's own native lighting writer for NZXT Kraken X3-family coolers
/// (X53/X63/X73). The protocol is derived from liquidctl's GPL-3.0
/// <c>kraken3</c> driver (reviewed 2026-07-16; attributed in
/// THIRD_PARTY_NOTICES; no code copied): a lighting command is one 64-byte HID
/// output report <c>[0x2A, 0x04, cid, cid, mode, speedLo, speedHi]</c> followed
/// by 16 RGB triplets (48 bytes) and the footer
/// <c>[direction, colourCount, modeRelated, staticValue, ledSize]</c>. This
/// writer issues only the "fixed" (one static colour) and "off" variants on the
/// sync channel (0b111 = ring + logo together) — no animation, no per-LED
/// addressing, and never anything but lighting. Cooling/pump behaviour is
/// untouched by these registers. There is no firmware read-back for lighting,
/// so the result reports the write as issued, never as verified. Intended to
/// run inside the crash-contained Adapter Host child (<c>--set-kraken-rgb</c>).
/// </summary>
public static class KrakenX3LightingWriter
{
    private const byte SyncChannelId = 0b111;
    private const byte SyncChannelStaticValue = 40; // liquidctl _STATIC_VALUE[0b111]
    private const int MessageLength = 64;

    /// <summary>Builds the 64-byte fixed-colour report for the sync (ring + logo) channel.</summary>
    public static byte[] BuildFixedColourReport(byte red, byte green, byte blue)
    {
        byte[] message = new byte[MessageLength];
        WriteHeader(message);
        message[7] = red;
        message[8] = green;
        message[9] = blue;
        WriteFooter(message, colourCount: 1);
        return message;
    }

    /// <summary>Builds the 64-byte lighting-off report for the sync channel.</summary>
    public static byte[] BuildOffReport()
    {
        byte[] message = new byte[MessageLength];
        WriteHeader(message);
        WriteFooter(message, colourCount: 0);
        return message;
    }

    private static void WriteHeader(byte[] message)
    {
        message[0] = 0x2A;                 // lighting opcode
        message[1] = 0x04;
        message[2] = SyncChannelId;        // channel address, repeated
        message[3] = SyncChannelId;
        message[4] = 0x00;                 // mode value: fixed/off
        message[5] = 0x32;                 // speed scale 0 is constant [0x32, 0x00]
        message[6] = 0x00;
    }

    private static void WriteFooter(byte[] message, byte colourCount)
    {
        // 7 header bytes + 48 colour bytes = offset 55.
        message[55] = 0x00;                       // direction/backward byte: none
        message[56] = colourCount;                // colours actually present
        message[57] = 0x00;                       // mode-related byte: none for fixed/off
        message[58] = SyncChannelStaticValue;
        message[59] = 0x03;                       // LED size for non-variant modes
    }

    /// <summary>
    /// Writes the requested lighting state to the first connected Kraken
    /// X3-family device. Every failure degrades to a non-issued outcome; an
    /// exclusively-held device is a designed refusal, never forced.
    /// </summary>
    public static KrakenLightingResultV1 Write(string colourHex, bool turnOff)
    {
        byte red = 0, green = 0, blue = 0;
        if (!turnOff)
        {
            string value = colourHex.Trim().TrimStart('#');
            if (value.Length != 6
                || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                return KrakenLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed, "Colour must use #RRGGBB format.");
            }
            red = (byte)(rgb >> 16);
            green = (byte)(rgb >> 8);
            blue = (byte)rgb;
        }

        HidDevice? device;
        try
        {
            device = DeviceList.Local.GetHidDevices(KrakenX3TelemetryReader.VendorId)
                .FirstOrDefault(candidate => KrakenX3TelemetryReader.ProductIds.Contains(candidate.ProductID));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return KrakenLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed, $"HID enumeration failed: {exception.GetType().Name}.");
        }
        if (device is null)
        {
            return KrakenLightingResultV1.Unavailable(
                KrakenLightingOutcome.DeviceNotFound,
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
                return KrakenLightingResultV1.Unavailable(
                    KrakenLightingOutcome.AccessDenied,
                    "The Kraken's HID stream is unavailable — another program (for example NZXT CAM or OpenRGB) may own it exclusively. RigPilot will not force access.");
            }
            using (stream)
            {
                byte[] message = turnOff ? BuildOffReport() : BuildFixedColourReport(red, green, blue);
                // HidSharp buffers start with the report ID; the Kraken uses
                // unnumbered reports, so a 65-byte output buffer carries report
                // ID 0x00 followed by the 64-byte message.
                int outputLength = Math.Max(MessageLength, SafeMaxOutputReportLength(device));
                byte[] buffer = new byte[outputLength == MessageLength ? MessageLength : outputLength];
                int offset = buffer.Length > MessageLength ? 1 : 0;
                message.CopyTo(buffer, offset);
                stream.Write(buffer, 0, buffer.Length);
                return new KrakenLightingResultV1(
                    KrakenLightingResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    productName,
                    turnOff
                        ? "Lighting-off written to ring and logo. There is no firmware read-back; confirm visually."
                        : $"Fixed colour written to ring and logo. There is no firmware read-back; confirm visually.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return KrakenLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"The lighting write failed: {exception.GetType().Name}. The cooling function of the device is unaffected.");
        }
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
}
