using System.Globalization;
using HidSharp;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// RigPilot's own native lighting writer for the ASUS AURA addressable USB
/// controller (VID 0x0B05, PID 0x18F3 on the reference ROG STRIX X570-E). The
/// protocol is written clean-room from the community-documented AURA
/// addressable USB protocol (65-byte HID reports, opcode 0xEC): a direct-mode
/// frame is <c>[0xEC, 0x40, applyBit|channel, startLed, ledCount, RGB…]</c>
/// with up to 20 RGB triplets per report; the apply bit (0x80) on the final
/// frame latches the colours. This writer issues only full-channel static
/// colours (or off = black) on the addressable headers — no effects, no
/// EEPROM/save commands (colours revert with the controller), and nothing but
/// lighting registers. Intended for the crash-contained Adapter Host child
/// (<c>--set-aura-rgb</c>).
/// </summary>
public static class AuraUsbLightingWriter
{
    public const int VendorId = 0x0B05;
    public const int ProductId = 0x18F3;
    private const byte ReportId = 0xEC;
    private const byte DirectOpcode = 0x40;
    private const byte ApplyBit = 0x80;
    private const int MessageLength = 65;
    private const int LedsPerFrame = 20;
    private const int LedsPerChannel = 120;   // generous cover; extra addresses are ignored
    private const int ChannelCount = 2;       // the 18F3 exposes two addressable headers

    /// <summary>Builds one 65-byte direct-mode frame for a run of LEDs on a channel.</summary>
    public static byte[] BuildDirectFrame(byte channel, bool apply, byte startLed, byte red, byte green, byte blue, int ledCount)
    {
        int count = Math.Clamp(ledCount, 1, LedsPerFrame);
        byte[] message = new byte[MessageLength];
        message[0] = ReportId;
        message[1] = DirectOpcode;
        message[2] = (byte)((apply ? ApplyBit : 0) | channel);
        message[3] = startLed;
        message[4] = (byte)count;
        for (int led = 0; led < count; led++)
        {
            message[5 + (led * 3)] = red;
            message[6 + (led * 3)] = green;
            message[7 + (led * 3)] = blue;
        }

        return message;
    }

    /// <summary>
    /// Parses an adapter-host lighting target of the form <c>RRGGBB</c>,
    /// <c>off</c>, <c>RRGGBB@1</c>, or <c>off@2</c>, where the optional
    /// <c>@N</c> suffix names one addressable header (1-based, 1..2). Used to
    /// drive a single header carrying a passive ARGB device — e.g. the Cooler
    /// Master GPU sag bracket — without repainting the other header.
    /// </summary>
    public static bool TryParseTarget(string value, out string colourHex, out bool turnOff, out int? headerIndex)
    {
        colourHex = string.Empty;
        turnOff = false;
        headerIndex = null;
        string trimmed = value?.Trim() ?? string.Empty;
        int at = trimmed.IndexOf('@');
        if (at >= 0)
        {
            if (!int.TryParse(trimmed[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                || parsed < 1 || parsed > ChannelCount)
            {
                return false;
            }
            headerIndex = parsed;
            trimmed = trimmed[..at];
        }

        if (string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
        {
            turnOff = true;
            return true;
        }

        colourHex = trimmed;
        return trimmed.TrimStart('#').Length == 6;
    }

    /// <summary>
    /// Writes a static colour (or off) to the addressable channels of the
    /// first connected AURA USB controller — both channels by default, or a
    /// single 1-based header when <paramref name="headerIndex"/> is given (for
    /// a passive ARGB device on one header, like a GPU sag bracket). An
    /// exclusively-held device is a designed refusal, never forced.
    /// </summary>
    public static AuraLightingResultV1 Write(string colourHex, bool turnOff, int? headerIndex = null)
    {
        if (headerIndex is < 1 or > ChannelCount)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"Addressable header must be 1..{ChannelCount}.");
        }

        RgbColour parsed = RgbColour.Off;
        if (!turnOff && !RgbColour.TryParse(colourHex, out parsed))
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed, "Colour must use #RRGGBB format.");
        }
        byte red = parsed.Red, green = parsed.Green, blue = parsed.Blue;

        HidDevice? device;
        try
        {
            device = DeviceList.Local.GetHidDevices(VendorId, ProductId).FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed, $"HID enumeration failed: {exception.GetType().Name}.");
        }
        if (device is null)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.DeviceNotFound,
                "No AURA addressable USB controller (VID 0x0B05, PID 0x18F3) is connected.");
        }

        try
        {
            if (!device.TryOpen(out HidStream stream))
            {
                return AuraLightingResultV1.Unavailable(
                    KrakenLightingOutcome.AccessDenied,
                    "The AURA controller's HID stream is unavailable — another program (Armoury Crate or OpenRGB) may own it exclusively. RigPilot will not force access.");
            }

            using (stream)
            {
                byte firstChannel = (byte)(headerIndex is int index ? index - 1 : 0);
                byte lastChannel = (byte)(headerIndex is int only ? only - 1 : ChannelCount - 1);
                for (byte channel = firstChannel; channel <= lastChannel; channel++)
                {
                    for (int start = 0; start < LedsPerChannel; start += LedsPerFrame)
                    {
                        bool lastFrame = start + LedsPerFrame >= LedsPerChannel;
                        byte[] frame = BuildDirectFrame(
                            channel, apply: lastFrame, (byte)start, red, green, blue,
                            Math.Min(LedsPerFrame, LedsPerChannel - start));
                        stream.Write(frame, 0, frame.Length);
                    }
                }

                string scope = headerIndex is int named
                    ? $"addressable header {named}"
                    : "both addressable channels";
                return new AuraLightingResultV1(
                    AuraLightingResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    SafeProductName(device),
                    turnOff
                        ? $"Lighting-off frames written to {scope}. There is no firmware read-back; confirm visually."
                        : $"Static colour frames written to {scope}. There is no firmware read-back; confirm visually.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"The AURA lighting write failed: {exception.GetType().Name}.");
        }
    }

    private static string? SafeProductName(HidDevice device)
    {
        try
        {
            return device.GetProductName();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
