using System.Globalization;
using HidSharp;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// RigPilot's own native lighting writer for the ASUS AURA addressable USB
/// controller (VID 0x0B05, PID 0x18F3 on the reference ROG STRIX X570-E). The
/// protocol is written clean-room from the community-documented AURA
/// addressable USB protocol (65-byte HID reports, opcode 0xEC).
///
/// Four steps are needed to actually light the reference rig, mirroring the
/// order the OpenRGB mainboard controller performs at start-up:
/// <list type="number">
/// <item>Read the controller's config table (<c>[0xEC, 0xB0]</c>) to learn how
/// many addressable headers exist and how many LEDs the onboard (fixed) zone
/// has — the direct channels are not the raw 0/1 indices we used to assume.</item>
/// <item>Switch the controller into GEN1 addressable mode (<c>[0xEC, 0x52,
/// 0x53, 0x00, 0x01]</c>).</item>
/// <item>Switch each target zone into software/direct mode with a SendEffect
/// frame carrying <c>AURA_MODE_DIRECT</c> (<c>[0xEC, 0x35, effectChannel, 0x00,
/// 0x00, 0xFF]</c>). This is what stops the controller <em>acknowledging</em>
/// direct frames while it keeps running its stored hardware effect — the
/// "acknowledged but dark" symptom. The effect channel is a sequential index
/// (onboard zone 0, then each header), distinct from the direct channel.</item>
/// <item>Send direct-mode frames <c>[0xEC, 0x40, applyBit|directChannel,
/// startLed, ledCount, RGB…]</c> (up to 20 RGB triplets per report, apply bit
/// 0x80 on the final frame of each channel) to the onboard fixed zone
/// (<c>direct_channel 0x04</c>) and every addressable header
/// (<c>direct_channel i</c>).</item>
/// </list>
///
/// This writer issues only full-channel static colours (or off = black) — no
/// effects, no EEPROM/save commands (colours revert with the controller), and
/// nothing but lighting registers. Intended for the crash-contained Adapter
/// Host child (<c>--set-aura-rgb</c>).
/// </summary>
public static class AuraUsbLightingWriter
{
    public const int VendorId = 0x0B05;
    public const int ProductId = 0x18F3;
    private const byte ReportId = 0xEC;
    private const byte DirectOpcode = 0x40;            // AURA_CONTROL_MODE_DIRECT
    private const byte EffectOpcode = 0x35;            // AURA_MAINBOARD_CONTROL_MODE_EFFECT
    private const byte DirectModeValue = 0xFF;         // AURA_MODE_DIRECT (software/direct effect)
    private const byte ConfigTableOpcode = 0xB0;       // AURA_REQUEST_CONFIG_TABLE
    private const byte ConfigTableReply = 0x30;        // reply marker in response byte[1]
    private const byte ApplyBit = 0x80;
    private const int MessageLength = 65;
    private const int LedsPerFrame = 20;               // LEDS_PER_PACKET
    private const int LedsPerChannel = 120;            // generous cover for an addressable strip of unknown length; extra addresses are ignored
    private const int ChannelCount = 2;                // addressable headers exposed by the @N grammar on the reference board
    private const byte FixedMainboardChannel = 0x04;   // direct_channel of the onboard (fixed) zone per the config table
    private const int ConfigTableLength = 60;
    private const int ConfigAddressableHeaderCountOffset = 0x02; // config_table: number of addressable headers
    private const int ConfigMainboardLedCountOffset = 0x1B;      // config_table: onboard fixed-zone LED count
    private const int ConfigReadTimeoutMs = 500;
    private const int ConfigReadAttempts = 4;

    /// <summary>Builds one 65-byte direct-mode frame for a run of LEDs on a direct channel.</summary>
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
    /// Writes a static colour (or off) to the AURA USB controller. With no
    /// <paramref name="headerIndex"/> it drives every channel the controller
    /// reports — the onboard fixed zone plus all addressable headers. With a
    /// 1-based <paramref name="headerIndex"/> it drives only that addressable
    /// header (for a passive ARGB device on one header, like a GPU sag
    /// bracket). The controller is switched into GEN1 direct mode first, and
    /// channel/LED counts come from its config table rather than assumed
    /// indices. An exclusively-held device is a designed refusal, never forced.
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

            // One HID session covers the whole apply: config read, the GEN1
            // switch, and every direct frame. The session stays open until all
            // frames latch (apply bit) so the controller does not revert.
            using (stream)
            {
                byte[]? config = TryReadConfigTable(stream, device);

                // Switch into GEN1 addressable mode.
                stream.Write(BuildGen1Frame(), 0, MessageLength);

                List<ChannelPlan> plan = PlanChannels(config, headerIndex);

                // Put each target zone into software/direct mode (SendEffect with
                // AURA_MODE_DIRECT). This is the step that fixes "acknowledged but
                // dark": without it the controller keeps running its stored
                // hardware effect and silently ignores the direct colour frames.
                foreach (ChannelPlan target in plan)
                {
                    stream.Write(BuildEffectFrame(target.EffectChannel, DirectModeValue), 0, MessageLength);
                }

                foreach (ChannelPlan target in plan)
                {
                    for (int start = 0; start < target.LedCount; start += LedsPerFrame)
                    {
                        bool lastFrame = start + LedsPerFrame >= target.LedCount;
                        byte[] frame = BuildDirectFrame(
                            target.DirectChannel, apply: lastFrame, (byte)start, red, green, blue,
                            Math.Min(LedsPerFrame, target.LedCount - start));
                        stream.Write(frame, 0, frame.Length);
                    }
                }

                string scope = DescribeScope(plan, headerIndex);
                return new AuraLightingResultV1(
                    AuraLightingResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    SafeProductName(device),
                    turnOff
                        ? $"Lighting-off frames written to {scope} in GEN1 direct mode. There is no firmware read-back; confirm visually."
                        : $"Static colour frames written to {scope} in GEN1 direct mode. There is no firmware read-back; confirm visually.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"The AURA lighting write failed: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// One zone to paint: its effect channel (used to switch it into direct
    /// mode), its direct channel (used to stream colours), how many LEDs to
    /// cover, and whether it is the onboard fixed zone.
    /// </summary>
    private readonly record struct ChannelPlan(byte DirectChannel, byte EffectChannel, int LedCount, bool IsFixed);

    /// <summary>
    /// Turns the config table (or a safe fallback when it could not be read)
    /// into the set of direct channels to paint. A single <paramref
    /// name="headerIndex"/> targets only that addressable header; otherwise the
    /// onboard fixed zone (when present) and every addressable header are
    /// covered.
    /// </summary>
    private static List<ChannelPlan> PlanChannels(byte[]? config, int? headerIndex)
    {
        List<ChannelPlan> plan = new();

        // The effect channel is a sequential index: the onboard fixed zone (when
        // present) is 0, then each addressable header follows. The direct channel
        // is separate — 0x04 for the fixed zone, the header index for each header.
        bool fixedPresent = config is not null && config[ConfigMainboardLedCountOffset] > 0;

        if (headerIndex is int header)
        {
            // A single addressable header (1-based) — e.g. a passive ARGB bracket.
            byte directChannel = (byte)(header - 1);
            byte effectChannel = (byte)((header - 1) + (fixedPresent ? 1 : 0));
            plan.Add(new ChannelPlan(directChannel, effectChannel, LedsPerChannel, IsFixed: false));
            return plan;
        }

        byte effect = 0;

        // Drive the onboard fixed zone when the controller reports one, using
        // its real LED count — over-sending a fixed zone is unsafe, unlike an
        // addressable strip whose extra addresses are simply ignored.
        if (fixedPresent)
        {
            plan.Add(new ChannelPlan(FixedMainboardChannel, effect++, config![ConfigMainboardLedCountOffset], IsFixed: true));
        }

        int headers = config is not null ? config[ConfigAddressableHeaderCountOffset] : ChannelCount;
        if (headers <= 0)
        {
            headers = ChannelCount;
        }
        for (int i = 0; i < headers; i++)
        {
            plan.Add(new ChannelPlan((byte)i, effect++, LedsPerChannel, IsFixed: false));
        }

        return plan;
    }

    private static string DescribeScope(IReadOnlyList<ChannelPlan> plan, int? headerIndex)
    {
        if (headerIndex is int named)
        {
            return $"addressable header {named}";
        }

        int addressable = plan.Count(entry => !entry.IsFixed);
        bool fixedZone = plan.Any(entry => entry.IsFixed);
        string headers = $"{addressable} addressable header{(addressable == 1 ? string.Empty : "s")}";
        return fixedZone ? $"the onboard zone and {headers}" : headers;
    }

    /// <summary>
    /// Builds a SendEffect frame (<c>[0xEC, 0x35, effectChannel, 0x00, 0x00,
    /// modeValue]</c>). With <paramref name="modeValue"/> = 0xFF this switches a
    /// zone into software/direct mode so the direct colour frames render.
    /// </summary>
    private static byte[] BuildEffectFrame(byte effectChannel, byte modeValue)
    {
        byte[] message = new byte[MessageLength];
        message[0] = ReportId;
        message[1] = EffectOpcode;
        message[2] = effectChannel;
        message[3] = 0x00;
        message[4] = 0x00;            // shutdown_effect = false
        message[5] = modeValue;
        return message;
    }

    /// <summary>Builds the GEN1 mode-switch frame (<c>[0xEC, 0x52, 0x53, 0x00, 0x01]</c>).</summary>
    private static byte[] BuildGen1Frame()
    {
        byte[] message = new byte[MessageLength];
        message[0] = ReportId;
        message[1] = 0x52;
        message[2] = 0x53;
        message[3] = 0x00;
        message[4] = 0x01;
        return message;
    }

    /// <summary>
    /// Requests the config table (<c>[0xEC, 0xB0]</c>) and returns its 60 data
    /// bytes, or <c>null</c> if the controller did not answer in time. On
    /// <c>null</c> the caller falls back to the reference-board channel layout.
    /// </summary>
    private static byte[]? TryReadConfigTable(HidStream stream, HidDevice device)
    {
        try
        {
            byte[] request = new byte[MessageLength];
            request[0] = ReportId;
            request[1] = ConfigTableOpcode;
            stream.Write(request, 0, MessageLength);

            int length = Math.Max(MessageLength, device.GetMaxInputReportLength());
            byte[] response = new byte[length];
            int previousTimeout = stream.ReadTimeout;
            stream.ReadTimeout = ConfigReadTimeoutMs;
            try
            {
                for (int attempt = 0; attempt < ConfigReadAttempts; attempt++)
                {
                    int read = stream.Read(response, 0, response.Length);
                    if (read <= 0)
                    {
                        return null;
                    }
                    if (TryLocateConfigTable(response, read, out int dataStart))
                    {
                        byte[] table = new byte[ConfigTableLength];
                        Array.Copy(response, dataStart, table, 0, ConfigTableLength);
                        return table;
                    }
                }
                return null;
            }
            finally
            {
                stream.ReadTimeout = previousTimeout;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Locates the 60-byte config payload in a raw input report. The response
    /// header is <c>[0xEC, 0x30, …]</c> with the payload at offset 4; HidSharp
    /// may or may not include the 0xEC report id, so both alignments are
    /// accepted.
    /// </summary>
    private static bool TryLocateConfigTable(byte[] response, int read, out int dataStart)
    {
        if (read > 4 && response[0] == ReportId && response[1] == ConfigTableReply
            && response.Length >= 4 + ConfigTableLength)
        {
            dataStart = 4;
            return true;
        }
        if (read > 3 && response[0] == ConfigTableReply
            && response.Length >= 3 + ConfigTableLength)
        {
            dataStart = 3;
            return true;
        }
        dataStart = 0;
        return false;
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
