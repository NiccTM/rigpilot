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

    /// <summary>
    /// Writes a static colour (off = black) to the first connected audited
    /// Razer device and reads the firmware's status reply. An
    /// exclusively-held device is a designed refusal, never forced.
    /// </summary>
    public static RazerRgbResultV1 Write(string colourHex, bool turnOff)
    {
        RgbColour parsed = RgbColour.Off;
        if (!turnOff && !RgbColour.TryParse(colourHex, out parsed))
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed, "Colour must use #RRGGBB format.");
        }
        byte red = parsed.Red, green = parsed.Green, blue = parsed.Blue;

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
                    return Transmit(
                        buffer => { stream.SetFeature(buffer); return true; },
                        buffer => { stream.GetFeature(buffer); return true; },
                        productName, red, green, blue, turnOff);
                }
            }

            using RawFeatureChannel? raw = RawFeatureChannel.TryOpen(candidate.DevicePath);
            if (raw is not null)
            {
                return Transmit(raw.SetFeature, raw.GetFeature, productName, red, green, blue, turnOff);
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
