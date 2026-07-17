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
    /// Writes a static colour (or off) across both addressable channels of the
    /// first connected AURA USB controller. An exclusively-held device is a
    /// designed refusal, never forced.
    /// </summary>
    public static AuraLightingResultV1 Write(string colourHex, bool turnOff)
    {
        byte red = 0, green = 0, blue = 0;
        if (!turnOff)
        {
            string value = colourHex.Trim().TrimStart('#');
            if (value.Length != 6
                || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                return AuraLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed, "Colour must use #RRGGBB format.");
            }
            red = (byte)(rgb >> 16);
            green = (byte)(rgb >> 8);
            blue = (byte)rgb;
        }

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
                for (byte channel = 0; channel < ChannelCount; channel++)
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

                return new AuraLightingResultV1(
                    AuraLightingResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    SafeProductName(device),
                    turnOff
                        ? "Lighting-off frames written to both addressable channels. There is no firmware read-back; confirm visually."
                        : "Static colour frames written to both addressable channels. There is no firmware read-back; confirm visually.");
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
