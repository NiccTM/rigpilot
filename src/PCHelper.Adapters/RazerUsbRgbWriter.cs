using System.Globalization;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// RigPilot's own native lighting writer for Razer Chroma USB devices,
/// written clean-room from the community-documented Razer HID protocol
/// (openrazer project documentation): commands are 90-byte reports —
/// [status, transactionId, remainingPackets(2, big-endian), protocolType,
/// dataSize, commandClass, commandId, arguments(80), crc, reserved] — sent as
/// a HID feature report, where the CRC XORs bytes 2..87. The firmware answers
/// a readable status report (0x02 = success), so unlike the AURA/Kraken
/// lighting writers this one genuinely verifies the command was accepted.
///
/// Only the extended-matrix STATIC effect (class 0x0F, command 0x02) is ever
/// issued — no firmware/profile/EEPROM command class is referenced anywhere in
/// this type — and only devices on the audited allowlist are written to
/// (currently the Lian Li O11 Dynamic Razer Edition case on the reference
/// rig). Intended for the crash-contained Adapter Host child
/// (<c>--set-razer-usb-rgb</c>).
/// </summary>
public static class RazerUsbRgbWriter
{
    public const int VendorId = 0x1532;

    /// <summary>Razer PIDs whose static-effect handling is verified on real hardware.</summary>
    public static readonly IReadOnlyDictionary<int, string> AuditedProducts = new Dictionary<int, string>
    {
        [0x0F13] = "Lian Li O11 Dynamic Razer Edition",
    };

    private const int ReportLength = 90;
    private const byte StatusNewCommand = 0x00;
    private const byte StatusSuccess = 0x02;
    private const byte StatusBusy = 0x01;
    private const byte TransactionId = 0x1F; // modern Chroma accessories
    private const byte ExtendedMatrixClass = 0x0F;
    private const byte EffectCommand = 0x02;
    private const byte StaticEffectId = 0x01;
    private const byte VariableStore = 0x01;
    private const byte ZeroLed = 0x00;
    private const int AcknowledgeDelayMilliseconds = 60;

    /// <summary>
    /// Builds one 90-byte Razer command report with the CRC over bytes 2..87.
    /// </summary>
    public static byte[] BuildReport(byte commandClass, byte commandId, byte dataSize, ReadOnlySpan<byte> arguments)
    {
        if (arguments.Length > 80)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "A Razer report carries at most 80 argument bytes.");
        }

        byte[] report = new byte[ReportLength];
        report[0] = StatusNewCommand;
        report[1] = TransactionId;
        // remaining packets (2..3) stay 0; protocol type (4) stays 0.
        report[5] = dataSize;
        report[6] = commandClass;
        report[7] = commandId;
        arguments.CopyTo(report.AsSpan(8));
        byte crc = 0;
        for (int index = 2; index < 88; index++)
        {
            crc ^= report[index];
        }

        report[88] = crc;
        return report;
    }

    /// <summary>
    /// The extended-matrix static-colour command: variable store, LED id 0
    /// (whole device), static effect, one colour, RGB.
    /// </summary>
    public static byte[] BuildStaticColour(byte red, byte green, byte blue) =>
        BuildReport(
            ExtendedMatrixClass,
            EffectCommand,
            dataSize: 0x09,
            [VariableStore, ZeroLed, StaticEffectId, 0x00, 0x00, 0x01, red, green, blue]);

    private const byte CustomFrameCommand = 0x03;  // extended-matrix set-custom-frame
    private const byte CustomFrameEffectId = 0x08; // MATRIX_EFFECT_CUSTOMFRAME
    private const byte CustomFrameDataSize = 0x47; // openrazer's fixed packet length
    private const int MaxLedsPerFrame = 25;        // (80 argument bytes - 5 header) / 3

    /// <summary>
    /// The extended-matrix "set custom frame" command (class 0x0F, cmd 0x03) filling one
    /// row segment [<paramref name="startCol"/>..<paramref name="stopCol"/>] with one colour.
    /// Mirrors openrazer's <c>razer_chroma_extended_matrix_set_custom_frame</c>:
    /// arguments[2]=row, [3]=start col, [4]=stop col, [5..]=RGB triples; the data-size byte
    /// is the driver's fixed 0x47 regardless of segment length.
    /// </summary>
    public static byte[] BuildCustomFrame(byte rowIndex, byte startCol, byte stopCol, byte red, byte green, byte blue)
    {
        int ledCount = stopCol - startCol + 1;
        byte[] arguments = new byte[5 + (ledCount * 3)];
        arguments[2] = rowIndex;
        arguments[3] = startCol;
        arguments[4] = stopCol;
        for (int led = 0; led < ledCount; led++)
        {
            arguments[5 + (led * 3)] = red;
            arguments[6 + (led * 3)] = green;
            arguments[7 + (led * 3)] = blue;
        }

        return BuildReport(ExtendedMatrixClass, CustomFrameCommand, CustomFrameDataSize, arguments);
    }

    /// <summary>
    /// The extended-matrix "display the custom frame" effect (class 0x0F, cmd 0x02, effect
    /// id 0x08). Mirrors openrazer's <c>razer_chroma_extended_matrix_effect_custom_frame</c>
    /// (effect_base 0x0C / 0x00 / 0x00 / 0x08).
    /// </summary>
    public static byte[] BuildCustomFrameEffect() =>
        BuildReport(ExtendedMatrixClass, EffectCommand, dataSize: 0x0C, [ZeroLed, ZeroLed, CustomFrameEffectId]);

    private const byte ResizeCommand = 0x08;      // extended-matrix set addressable zone sizes
    private const byte BrightnessCommand = 0x04;  // extended-matrix set brightness
    private const byte FirstArgbChannelLedId = 0x1A; // RAZER_LED_ID_ARGB_CH_1..6 = 0x1A..0x1F

    /// <summary>
    /// The addressable-controller resize report (class 0x0F, cmd 0x08): tells the controller
    /// how many LEDs each of its six channels drives. Without this an addressable Razer
    /// controller ignores custom frames for LEDs it does not believe exist. Mirrors OpenRGB's
    /// <c>razer_create_addressable_size_report</c>: arg[0]=zone count, then per zone a marker
    /// (0x19 when populated, else the 1-based index) followed by the LED count.
    /// </summary>
    public static byte[] BuildAddressableSize(byte zone1, byte zone2, byte zone3, byte zone4, byte zone5, byte zone6)
    {
        byte[] arguments = new byte[13];
        arguments[0] = 0x06;
        arguments[1] = zone1 == 0 ? (byte)0x01 : (byte)0x19; arguments[2] = zone1;
        arguments[3] = zone2 == 0 ? (byte)0x02 : (byte)0x19; arguments[4] = zone2;
        arguments[5] = zone3 == 0 ? (byte)0x03 : (byte)0x19; arguments[6] = zone3;
        arguments[7] = zone4 == 0 ? (byte)0x04 : (byte)0x19; arguments[8] = zone4;
        arguments[9] = zone5 == 0 ? (byte)0x05 : (byte)0x19; arguments[10] = zone5;
        arguments[11] = zone6 == 0 ? (byte)0x06 : (byte)0x19; arguments[12] = zone6;
        return BuildReport(ExtendedMatrixClass, ResizeCommand, 0x0D, arguments);
    }

    /// <summary>
    /// The extended-matrix brightness report (class 0x0F, cmd 0x04) for one ARGB channel LED
    /// id. Mirrors OpenRGB's <c>razer_create_brightness_extended_matrix_report</c>. An
    /// addressable controller left at zero channel brightness acknowledges a custom frame but
    /// shows nothing, so brightness is set explicitly.
    /// </summary>
    public static byte[] BuildChannelBrightness(byte channelLedId, byte brightness) =>
        BuildReport(ExtendedMatrixClass, BrightnessCommand, 0x03, [ZeroLed, channelLedId, brightness]);

    // The EXTENDED_ARGB frame report is a DIFFERENT report from the 90-byte command
    // reports above: OpenRGB's razer_argb_report (RAZER_MATRIX_TYPE_EXTENDED_ARGB) sent via
    // razer_usb_send_argb -> hid_send_feature_report. It is a packed 321-byte feature report
    // whose layout is [0]=hid_id 0x00 (the HidD_SetFeature report-number prefix), [1]=report
    // id (0x04 for rows 0..4, 0x84 for rows 5+), [2]=channel_1=row, [3]=channel_2=row,
    // [4]=pad 0, [5]=last_idx=stop col, [6..]=RGB triples for cols 0..stopCol. The O11 detects
    // as EXTENDED_ARGB, which honours THIS report — not the extended-matrix custom-frame
    // command (0x0F/0x03) above, which it acknowledges (status 0x02) but never renders.
    private const int ArgbReportLength = 321;      // sizeof(razer_argb_report), pack(1)
    private const int ArgbColorDataCapacity = 315; // color_data[315] => 105 LEDs per row
    private const int ArgbMaxLedsPerRow = ArgbColorDataCapacity / 3;
    private const byte ArgbReportIdLow = 0x04;     // rows 0..4
    private const byte ArgbReportIdHigh = 0x84;    // rows 5+

    /// <summary>
    /// Builds one <see cref="ArgbReportLength"/>-byte razer_argb_report filling row
    /// <paramref name="rowIndex"/> columns 0..<paramref name="stopCol"/> with one colour.
    /// Mirrors OpenRGB's <c>razer_create_custom_frame_argb_report</c>. The returned buffer is
    /// sent to HidD_SetFeature as-is (byte 0 is the report-number prefix, already 0x00).
    /// </summary>
    public static byte[] BuildArgbFrame(byte rowIndex, byte stopCol, byte red, byte green, byte blue)
    {
        int ledCount = stopCol + 1;
        if (ledCount > ArgbMaxLedsPerRow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopCol), $"A razer_argb_report row carries at most {ArgbMaxLedsPerRow} LEDs.");
        }

        byte[] report = new byte[ArgbReportLength];
        report[0] = 0x00;                                            // hid_id / report-number prefix
        report[1] = rowIndex < 5 ? ArgbReportIdLow : ArgbReportIdHigh; // report_id
        report[2] = rowIndex;                                        // channel_1
        report[3] = rowIndex;                                        // channel_2
        report[4] = 0x00;                                           // pad
        report[5] = stopCol;                                        // last_idx
        for (int led = 0; led < ledCount; led++)
        {
            report[6 + (led * 3)] = red;
            report[7 + (led * 3)] = green;
            report[8 + (led * 3)] = blue;
        }

        return report;
    }

    /// <summary>
    /// Writes a static colour (off = black) to the first connected audited
    /// Razer device and reads the firmware's status reply. An
    /// exclusively-held device is a designed refusal, never forced.
    /// </summary>
    public static RazerRgbResultV1 Write(string colourHex, bool turnOff, (int Rows, int Cols)? customFrameMatrix = null)
    {
        if (!TryParseColour(colourHex, turnOff, out RgbColour parsed, out RazerRgbResultV1 parseFailure))
        {
            return parseFailure;
        }
        byte red = parsed.Red, green = parsed.Green, blue = parsed.Blue;

        // Addressable Razer matrix controllers (the Lian Li O11 Dynamic Razer Edition, a
        // 4-row x 16-col extended matrix per OpenRGB's RazerDevices) acknowledge the single
        // "static effect" command but do not render it; they light only when a per-LED custom
        // frame is written to EVERY row and the effect is then set to custom-frame. When a
        // matrix is supplied, use that path; otherwise the static effect (correct for the
        // non-addressable Chroma devices).
        return OpenAndRun((setFeature, getFeature, productName) =>
            customFrameMatrix is (int rows, int cols)
                ? TransmitCustomFrame(setFeature, getFeature, productName, red, green, blue, turnOff, rows, cols)
                : Transmit(setFeature, getFeature, productName, red, green, blue, turnOff));
    }

    /// <summary>
    /// Lights the first audited Razer device via the EXTENDED_ARGB report path (OpenRGB's
    /// <c>razer_create_custom_frame_argb_report</c>): six ARGB-channel brightness reports, then
    /// one 321-byte razer_argb_report per row. This is the path the O11 Dynamic actually
    /// renders — the extended-matrix custom-frame command (<see cref="Write"/> with a matrix)
    /// is acknowledged but never displayed on this controller.
    /// </summary>
    public static RazerRgbResultV1 WriteArgb(string colourHex, bool turnOff, int rows, int cols)
    {
        if (!TryParseColour(colourHex, turnOff, out RgbColour parsed, out RazerRgbResultV1 parseFailure))
        {
            return parseFailure;
        }
        byte red = parsed.Red, green = parsed.Green, blue = parsed.Blue;

        return OpenAndRun((setFeature, getFeature, productName) =>
            TransmitArgbFrame(setFeature, getFeature, productName, red, green, blue, turnOff, rows, cols));
    }

    /// <summary>
    /// Lead-(b) diagnostic: sends a faithful OpenRGB EXTENDED sequence — brightness on the
    /// likely device LED ids AND the ARGB channels, a per-row custom frame, then mode-custom —
    /// and then HOLDS the HID handle open for <paramref name="holdSeconds"/>, re-sending the
    /// frame every ~2s. If a one-shot open/write/close is acknowledged but dark because the
    /// controller reverts when the last handle closes (as a persistent OpenRGB session avoids),
    /// the case lights only while this call is holding the handle and goes dark when it returns.
    /// The caller (and a human watching the case) reads the difference.
    /// </summary>
    public static RazerRgbResultV1 WriteHold(string colourHex, bool turnOff, int holdSeconds, int rows, int cols)
    {
        if (!TryParseColour(colourHex, turnOff, out RgbColour parsed, out RazerRgbResultV1 parseFailure))
        {
            return parseFailure;
        }
        byte red = parsed.Red, green = parsed.Green, blue = parsed.Blue;

        return OpenAndRun((setFeature, getFeature, productName) =>
            TransmitHold(setFeature, getFeature, productName, red, green, blue, turnOff, holdSeconds, rows, cols));
    }

    private static bool TryParseColour(string colourHex, bool turnOff, out RgbColour colour, out RazerRgbResultV1 failure)
    {
        colour = RgbColour.Off;
        failure = null!;
        if (!turnOff && !RgbColour.TryParse(colourHex, out colour))
        {
            failure = RazerRgbResultV1.Unavailable(KrakenLightingOutcome.Failed, "Colour must use #RRGGBB format.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Enumerates the audited Razer devices, opens the first command-capable interface
    /// (HidSharp first, the zero-access raw channel as fallback) and hands the feature-report
    /// delegates to <paramref name="run"/>. Shared by every write path.
    /// </summary>
    private static RazerRgbResultV1 OpenAndRun(Func<Func<byte[], bool>, Func<byte[], bool>, string, RazerRgbResultV1> run)
    {
        // The device presents several HID interfaces; only the vendor control
        // interface accepts the 90-byte command feature report, and some of
        // the others are exclusively owned by the OS input stack. Try every
        // capable interface until one opens.
        HidDevice[] candidates;
        try
        {
            candidates = [.. DeviceList.Local.GetHidDevices(VendorId)
                .Where(candidate => AuditedProducts.ContainsKey(candidate.ProductID))
                .Where(candidate => SafeMaxFeatureLength(candidate) >= ReportLength + 1)
                .OrderByDescending(SafeMaxFeatureLength)];
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed, $"HID enumeration failed: {exception.GetType().Name}.");
        }
        if (candidates.Length == 0)
        {
            bool anyPresent = DeviceList.Local.GetHidDevices(VendorId)
                .Any(candidate => AuditedProducts.ContainsKey(candidate.ProductID));
            return anyPresent
                ? RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    "The Razer device exposes no interface accepting the 90-byte command feature report.")
                : RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.DeviceNotFound,
                    "No audited Razer Chroma USB device is connected.");
        }

        // The vendor control interface commonly refuses a read/write open even
        // with nothing else running (observed live on the reference O11 case).
        // Feature-report IOCTLs need no read/write access, so open the
        // interface with zero access rights — full sharing, no data stream —
        // exactly as vendor stacks do. HidSharp is tried first; the raw
        // zero-access channel is the fallback.
        foreach (HidDevice candidate in candidates)
        {
            string productName = AuditedProducts[candidate.ProductID];
            if (candidate.TryOpen(out HidStream stream))
            {
                using (stream)
                {
                    return run(
                        buffer => { stream.SetFeature(buffer); return true; },
                        buffer => { stream.GetFeature(buffer); return true; },
                        productName);
                }
            }

            using RawFeatureChannel? raw = RawFeatureChannel.TryOpen(candidate.DevicePath);
            if (raw is not null)
            {
                return run(raw.SetFeature, raw.GetFeature, productName);
            }
        }

        return RazerRgbResultV1.Unavailable(
            KrakenLightingOutcome.AccessDenied,
            "No command-capable Razer interface could be opened — Razer Synapse or OpenRGB may own the device exclusively. RigPilot will not force access.");
    }

    private static RazerRgbResultV1 Transmit(
        Func<byte[], bool> setFeature,
        Func<byte[], bool> getFeature,
        string productName,
        byte red,
        byte green,
        byte blue,
        bool turnOff)
    {
        try
        {
            byte[] command = BuildStaticColour(red, green, blue);
            byte[] buffer = new byte[ReportLength + 1]; // leading report id 0x00
            command.CopyTo(buffer, 1);
            if (!setFeature(buffer))
            {
                return RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    "The Razer command feature report was rejected by the interface.");
            }

            // The firmware needs a moment before the reply is readable.
            Thread.Sleep(AcknowledgeDelayMilliseconds);
            byte[] reply = new byte[ReportLength + 1];
            bool replyRead;
            try
            {
                replyRead = getFeature(reply);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                replyRead = false;
                _ = exception;
            }

            if (!replyRead)
            {
                return new RazerRgbResultV1(
                    RazerRgbResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    productName,
                    "Static command written, but the firmware status reply could not be read; confirm visually.");
            }

            return reply[1] switch
            {
                StatusSuccess => new RazerRgbResultV1(
                    RazerRgbResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    productName,
                    turnOff
                        ? "Lighting off acknowledged by the Razer firmware (status 0x02)."
                        : "Static colour acknowledged by the Razer firmware (status 0x02)."),
                StatusBusy => RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    "The Razer firmware reported busy (status 0x01); try again in a moment."),
                byte status => RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"The Razer firmware rejected the command (status 0x{status:X2})."),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"The Razer lighting write failed: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// Lights an addressable Razer matrix controller (the O11 Dynamic) by writing the colour
    /// into every LED of row 0 as one or more <see cref="MaxLedsPerFrame"/>-LED custom-frame
    /// segments, then issuing the custom-frame effect to display them. This is the path the
    /// O11 needs — it acknowledges the plain static effect but never renders it.
    /// </summary>
    private static RazerRgbResultV1 TransmitCustomFrame(
        Func<byte[], bool> setFeature,
        Func<byte[], bool> getFeature,
        string productName,
        byte red,
        byte green,
        byte blue,
        bool turnOff,
        int rows,
        int cols)
    {
        try
        {
            int rowCount = Math.Clamp(rows, 1, 24);
            int colCount = Math.Clamp(cols, 1, MaxLedsPerFrame); // one report covers a full row
            int leds = rowCount * colCount;

            bool Send(byte[] report)
            {
                byte[] buffer = new byte[ReportLength + 1]; // leading report id 0x00
                report.CopyTo(buffer, 1);
                bool accepted = setFeature(buffer);
                Thread.Sleep(AcknowledgeDelayMilliseconds);
                return accepted;
            }

            // 1. Brightness: raise the matrix brightness. The O11 is a plain EXTENDED matrix, so
            //    the brightness that matters is the single dev_led_id report (0x00/0x05) — NOT
            //    the ARGB-channel ids (0x1A..0x1F), which are the EXTENDED_ARGB style and leave
            //    an EXTENDED frame acknowledged-but-dark. Confirmed live 2026-07-21: adding the
            //    0x00/0x05 brightness (and dropping the addressable resize, which this matrix
            //    does not use and which mis-sized its zones) is what finally lit the case. All
            //    ids are written — the extras are harmless — so the correct one is always hit.
            byte brightness = turnOff ? (byte)0x00 : (byte)0xFF;
            foreach (byte ledId in HoldBrightnessLedIds)
            {
                Send(BuildChannelBrightness(ledId, brightness));
            }

            // 2. Custom frame: write the colour into every row.
            for (int row = 0; row < rowCount; row++)
            {
                if (!Send(BuildCustomFrame((byte)row, 0, (byte)(colCount - 1), red, green, blue)))
                {
                    return RazerRgbResultV1.Unavailable(
                        KrakenLightingOutcome.Failed,
                        "The Razer custom-frame feature report was rejected by the interface.");
                }
            }

            // 3. Display the frame just written (mode-custom, 0x0F/0x02 effect 0x08).
            if (!Send(BuildCustomFrameEffect()))
            {
                return RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    "The Razer custom-frame effect report was rejected by the interface.");
            }

            byte[] reply = new byte[ReportLength + 1];
            bool replyRead;
            try
            {
                replyRead = getFeature(reply);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                replyRead = false;
                _ = exception;
            }

            if (!replyRead)
            {
                return new RazerRgbResultV1(
                    RazerRgbResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    productName,
                    $"Custom frame ({leds} LEDs) written, but the firmware status reply could not be read; confirm visually.");
            }

            return reply[1] switch
            {
                StatusSuccess => new RazerRgbResultV1(
                    RazerRgbResultV1.CurrentSchemaVersion,
                    KrakenLightingOutcome.WriteIssued,
                    productName,
                    turnOff
                        ? $"Lighting off acknowledged (custom frame, {leds} LEDs, status 0x02)."
                        : $"Custom colour frame acknowledged by the Razer firmware ({leds} LEDs, status 0x02)."),
                StatusBusy => RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    "The Razer firmware reported busy (status 0x01); try again in a moment."),
                byte status => RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"The Razer firmware rejected the custom frame (status 0x{status:X2})."),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"The Razer custom-frame write failed: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// Lights an EXTENDED_ARGB Razer controller (the O11 Dynamic) exactly as OpenRGB does:
    /// raise the six ARGB-channel brightnesses, then write one 321-byte razer_argb_report
    /// (report id 0x04/0x84) per row. Unlike <see cref="TransmitCustomFrame"/> this issues the
    /// dedicated ARGB frame report the controller renders, and issues NO addressable resize,
    /// NO extended-matrix custom-frame command, and NO custom-frame effect — none of which the
    /// EXTENDED_ARGB path uses (OpenRGB's razer_set_mode_custom is a no-op for this type). The
    /// ARGB report carries no 90-byte firmware status reply, so acceptance by the interface —
    /// not a status byte — is what is reported; the result is worded for visual confirmation.
    /// </summary>
    private static RazerRgbResultV1 TransmitArgbFrame(
        Func<byte[], bool> setFeature,
        Func<byte[], bool> getFeature,
        string productName,
        byte red,
        byte green,
        byte blue,
        bool turnOff,
        int rows,
        int cols)
    {
        _ = getFeature;
        try
        {
            int rowCount = Math.Clamp(rows, 1, 24);
            int colCount = Math.Clamp(cols, 1, ArgbMaxLedsPerRow);
            int leds = rowCount * colCount;

            // 1. Brightness: raise every ARGB channel (0x0F/0x04, LED ids 0x1A..0x1F). An ARGB
            //    channel left at zero brightness renders a written frame as black. These ARE
            //    90-byte command reports, so they take the leading report-id-prefix buffer.
            byte brightness = turnOff ? (byte)0x00 : (byte)0xFF;
            for (byte channel = 0; channel < 6; channel++)
            {
                byte[] command = BuildChannelBrightness((byte)(FirstArgbChannelLedId + channel), brightness);
                byte[] buffer = new byte[ReportLength + 1];
                command.CopyTo(buffer, 1);
                if (!setFeature(buffer))
                {
                    return RazerRgbResultV1.Unavailable(
                        KrakenLightingOutcome.Failed,
                        "The Razer ARGB-channel brightness report was rejected by the interface.");
                }

                Thread.Sleep(AcknowledgeDelayMilliseconds);
            }

            // 2. Frame: one razer_argb_report per row. The 321-byte buffer already carries its
            //    own report-number prefix (byte 0), so it is sent verbatim — no wrapping.
            for (int row = 0; row < rowCount; row++)
            {
                byte[] frame = BuildArgbFrame((byte)row, (byte)(colCount - 1), red, green, blue);
                if (!setFeature(frame))
                {
                    return RazerRgbResultV1.Unavailable(
                        KrakenLightingOutcome.Failed,
                        $"The Razer ARGB frame report for row {row} was rejected by the interface.");
                }

                Thread.Sleep(AcknowledgeDelayMilliseconds);
            }

            return new RazerRgbResultV1(
                RazerRgbResultV1.CurrentSchemaVersion,
                KrakenLightingOutcome.WriteIssued,
                productName,
                turnOff
                    ? $"ARGB lighting-off frames accepted by the interface ({rowCount} rows x {colCount} LEDs); confirm the case is dark."
                    : $"ARGB colour frames accepted by the interface ({leds} LEDs across {rowCount} rows); the ARGB report carries no status reply, so confirm the case visually.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"The Razer ARGB frame write failed: {exception.GetType().Name}.");
        }
    }

    // Device LED ids to raise brightness on for the EXTENDED path. OpenRGB's EXTENDED
    // brightness targets a single dev_led_id (unknown for the O11 without RazerDevices.cpp), so
    // the hold diagnostic covers the common matrix ids plus the six ARGB channels (0x1A..0x1F).
    private static readonly byte[] HoldBrightnessLedIds =
        [0x00, 0x05, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F];

    /// <summary>
    /// Sends the faithful EXTENDED sequence then holds the handle open, re-sending the frame
    /// periodically. See <see cref="WriteHold"/>. Uses only the 90-byte command reports (the
    /// only ones this device's single feat=91 interface can carry), so nothing here depends on
    /// the undeliverable 321-byte ARGB report.
    /// </summary>
    private static RazerRgbResultV1 TransmitHold(
        Func<byte[], bool> setFeature,
        Func<byte[], bool> getFeature,
        string productName,
        byte red,
        byte green,
        byte blue,
        bool turnOff,
        int holdSeconds,
        int rows,
        int cols)
    {
        _ = getFeature;
        try
        {
            int rowCount = Math.Clamp(rows, 1, 24);
            int colCount = Math.Clamp(cols, 1, MaxLedsPerFrame);
            int holdWindow = Math.Clamp(holdSeconds, 1, 120);
            int leds = rowCount * colCount;

            bool Send(byte[] report)
            {
                byte[] buffer = new byte[ReportLength + 1]; // leading report-id prefix 0x00
                report.CopyTo(buffer, 1);
                bool accepted = setFeature(buffer);
                Thread.Sleep(AcknowledgeDelayMilliseconds);
                return accepted;
            }

            void SendFrame()
            {
                for (int row = 0; row < rowCount; row++)
                {
                    Send(BuildCustomFrame((byte)row, 0, (byte)(colCount - 1), red, green, blue));
                }

                Send(BuildCustomFrameEffect()); // mode-custom (0x0F/0x02, [0,0,8])
            }

            // 1. Brightness across every candidate LED id (single-report EXTENDED style, plus
            //    the ARGB channels) so the matrix brightness cannot be the thing left at zero.
            byte brightness = turnOff ? (byte)0x00 : (byte)0xFF;
            foreach (byte ledId in HoldBrightnessLedIds)
            {
                Send(BuildChannelBrightness(ledId, brightness));
            }

            // 2. Initial frame + mode-custom.
            SendFrame();

            // 3. Hold the handle open, refreshing the frame, so a close-reverts controller stays
            //    lit for the whole window. The handle closes only when this method returns.
            int refreshes = 0;
            for (int elapsedMs = 0; elapsedMs < holdWindow * 1000; elapsedMs += 2000)
            {
                Thread.Sleep(2000);
                SendFrame();
                refreshes++;
            }

            return new RazerRgbResultV1(
                RazerRgbResultV1.CurrentSchemaVersion,
                KrakenLightingOutcome.WriteIssued,
                productName,
                turnOff
                    ? $"Held the Razer handle open for {holdWindow}s sending off-frames ({refreshes} refreshes); note whether the case was dark only while held."
                    : $"Held the Razer handle open for {holdWindow}s ({leds} LEDs, {refreshes} refreshes); note whether the case lit ONLY while held (persistent-session revert) or stayed dark throughout.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"The Razer hold diagnostic failed: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// A zero-access-rights handle to one HID interface, capable of feature
    /// report IOCTLs only — it cannot read or write the input/output data
    /// stream at all, which is also why Windows grants it where a read/write
    /// open is refused.
    /// </summary>
    private sealed class RawFeatureChannel : IDisposable
    {
        private const uint FileShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;
        private readonly SafeFileHandle _handle;

        private RawFeatureChannel(SafeFileHandle handle) => _handle = handle;

        public static RawFeatureChannel? TryOpen(string devicePath)
        {
            SafeFileHandle handle = CreateFileW(
                devicePath, 0, FileShareReadWrite, nint.Zero, OpenExisting, 0, nint.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return new RawFeatureChannel(handle);
        }

        public bool SetFeature(byte[] buffer) => HidD_SetFeature(_handle, buffer, buffer.Length);

        public bool GetFeature(byte[] buffer) => HidD_GetFeature(_handle, buffer, buffer.Length);

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);
    }

    /// <summary>Read-only per-interface diagnostics: lengths and openability. Writes nothing.</summary>
    public static IReadOnlyList<string> ProbeInterfaces()
    {
        List<string> lines = [];
        foreach (HidDevice candidate in DeviceList.Local.GetHidDevices(VendorId))
        {
            string open;
            try
            {
                open = candidate.TryOpen(out HidStream stream) ? "opens" : "refused";
                if (open == "opens")
                {
                    stream.Dispose();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                open = $"throws {exception.GetType().Name}";
            }

            int input, output, feature;
            try { input = candidate.GetMaxInputReportLength(); } catch { input = -1; }
            try { output = candidate.GetMaxOutputReportLength(); } catch { output = -1; }
            try { feature = candidate.GetMaxFeatureReportLength(); } catch { feature = -1; }
            lines.Add($"pid=0x{candidate.ProductID:X4} in={input} out={output} feat={feature} {open} path={candidate.DevicePath}");
        }

        return lines;
    }

    private static int SafeMaxFeatureLength(HidDevice device)
    {
        try
        {
            return device.GetMaxFeatureReportLength();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return 0;
        }
    }
}
