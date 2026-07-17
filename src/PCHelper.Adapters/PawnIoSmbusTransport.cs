using System.Reflection;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// The production <see cref="ISmbusTransport"/>: SMBus byte/word transactions
/// through the signed PawnIO SmbusPIIX4 module (the AMD FCH SMBus controller on
/// the reference X570 board is PIIX4-compatible). The module blob is the one
/// LibreHardwareMonitorLib 0.9.6 already embeds and ships, extracted from the
/// referenced assembly at runtime, so RigPilot adds no new module distribution.
///
/// The PIIX4 module's <c>ioctl_smbus_xfer</c> has no address filtering of its
/// own, which makes this class a policy chokepoint: every write calls
/// <see cref="SmbusAddressPolicy.EnsureWritable"/> and reads are refused
/// outside the RGB-controller range. Nothing above this seam can reach an SPD,
/// thermal, or PMIC address through it.
/// </summary>
public sealed class PawnIoSmbusTransport(IPawnIoModuleSession session) : ISmbusTransport
{
    private const string ModuleResourceName = "LibreHardwareMonitor.Resources.PawnIo.SmbusPIIX4.bin";

    // ioctl_smbus_xfer input layout: [0]=address, [1]=read(1)/write(0),
    // [2]=command code, [3]=protocol, [4+]=data. Protocol numbers follow the
    // Linux i2c convention the module implements.
    private const ulong WriteFlag = 0;
    private const ulong ReadFlag = 1;
    private const ulong ProtocolByteData = 2;
    private const ulong ProtocolWordData = 3;

    /// <summary>
    /// Opens the production transport: signed PawnIO runtime detected, the
    /// embedded SmbusPIIX4 module loaded into an executor. Returns false with a
    /// reason when any link is missing (library absent, driver stopped, no
    /// administrator rights, module rejected, unsupported chipset).
    /// </summary>
    public static bool TryCreate(out PawnIoSmbusTransport transport, out string message)
    {
        transport = null!;
        PawnIoRuntimeStatus runtime = PawnIoRuntimeProbe.Detect();
        if (!runtime.Available || runtime.LibraryPath is null)
        {
            message = $"SMBus needs the signed PawnIO runtime: {runtime.Describe()}.";
            return false;
        }

        byte[] module;
        try
        {
            Assembly assembly = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(ModuleResourceName)
                ?? throw new InvalidOperationException($"Resource '{ModuleResourceName}' is missing from LibreHardwareMonitorLib.");
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            module = buffer.ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            message = $"The embedded SmbusPIIX4 module could not be extracted: {exception.GetType().Name}.";
            return false;
        }

        if (!PawnIoModuleSession.TryOpen(runtime.LibraryPath, module, out PawnIoModuleSession opened, out string openMessage))
        {
            message = openMessage;
            return false;
        }

        transport = new PawnIoSmbusTransport(opened);
        message = "PawnIO SmbusPIIX4 transport is open.";
        return true;
    }

    public void WriteByte(byte address, byte commandCode, byte value)
    {
        SmbusAddressPolicy.EnsureWritable(address);
        session.Execute("ioctl_smbus_xfer", [address, WriteFlag, commandCode, ProtocolByteData, value], 0);
    }

    public void WriteWord(byte address, byte commandCode, ushort value)
    {
        SmbusAddressPolicy.EnsureWritable(address);
        session.Execute("ioctl_smbus_xfer", [address, WriteFlag, commandCode, ProtocolWordData, value], 0);
    }

    public byte ReadByte(byte address, byte commandCode)
    {
        // Reads are allowed from RGB controllers and — read-only, for bus
        // location evidence — from the SPD EEPROMs (the same presence read
        // every monitoring tool performs). Writes to SPDs remain permanently
        // blocked by the address policy.
        if (!SmbusAddressPolicy.IsRgbControllerAddress(address) && address is not (>= 0x50 and <= 0x57))
        {
            throw new SmbusSafetyException(
                $"SMBus address 0x{address:X2} is outside the RGB-controller and SPD-read ranges; RigPilot reads nothing else.");
        }

        ulong[] output = session.Execute("ioctl_smbus_xfer", [address, ReadFlag, commandCode, ProtocolByteData], 1);
        if (output.Length < 1)
        {
            throw new PawnIoException($"SMBus read-byte at 0x{address:X2}/0x{commandCode:X2} returned no data.", unchecked((int)0x8000FFFF));
        }

        return (byte)output[0];
    }

    /// <summary>
    /// Selects the FCH SMBus port for subsequent transfers via the module's
    /// <c>ioctl_piix4_port_sel</c>. Returns the previously selected port so the
    /// caller can restore it. Port selection changes routing only — it is not a
    /// device write.
    /// </summary>
    public int SelectPort(int port)
    {
        ulong[] previous = session.Execute("ioctl_piix4_port_sel", [unchecked((ulong)port)], 1);
        return previous.Length > 0 ? (int)previous[0] : -1;
    }

    public void Dispose() => session.Dispose();
}

/// <summary>
/// Purely read-only survey of every FCH SMBus port: which ports carry DDR4
/// SPDs (DRAM-type presence byte) and which addresses answer the ENE
/// detection window. Locates the DIMM bus before any write is considered.
/// </summary>
public static class SmbusBusSurvey
{
    private const byte SpdFirst = 0x50;
    private const byte SpdLast = 0x57;
    private const byte SpdDramTypeOffset = 0x02;
    private const byte Ddr4DramType = 0x0C;
    private const int MaximumPort = 4;

    public static SmbusBusSurveyV1 Run()
    {
        if (!PawnIoSmbusTransport.TryCreate(out PawnIoSmbusTransport transport, out string message))
        {
            return new SmbusBusSurveyV1(SmbusBusSurveyV1.CurrentSchemaVersion, [], message);
        }

        using (transport)
        {
            List<SmbusPortSurveyV1> ports = [];
            int originalPort = transport.SelectPort(-1);
            try
            {
                for (int port = 0; port <= MaximumPort; port++)
                {
                    try
                    {
                        transport.SelectPort(port);
                    }
                    catch (PawnIoException)
                    {
                        continue; // the module rejected this port number
                    }

                    List<int> spdAddresses = [];
                    for (byte address = SpdFirst; address <= SpdLast; address++)
                    {
                        try
                        {
                            if (transport.ReadByte(address, SpdDramTypeOffset) == Ddr4DramType)
                            {
                                spdAddresses.Add(address);
                            }
                        }
                        catch (PawnIoException)
                        {
                            // No SPD acknowledged at this address on this port.
                        }
                    }

                    SmbusRgbProbeResultV1 detection = SmbusRgbDetection.ProbeWithTransport(transport);
                    ports.Add(new SmbusPortSurveyV1(port, spdAddresses, detection.Sightings));
                }
            }
            finally
            {
                if (originalPort >= 0)
                {
                    try
                    {
                        transport.SelectPort(originalPort);
                    }
                    catch (PawnIoException)
                    {
                        // Restoring the boot-time port is best-effort.
                    }
                }
            }

            return new SmbusBusSurveyV1(
                SmbusBusSurveyV1.CurrentSchemaVersion,
                ports,
                "Read-only port survey: SPD presence reads and ENE detection reads only; no write of any kind was issued.");
        }
    }
}

/// <summary>
/// Purely read-only presence detection for ENE DIMM RGB controllers: reads the
/// documented detection window (SMBus commands 0xA0.. return the repeating
/// pattern 0x10..) from each address in the RGB-controller range. No pointer
/// or data write of any kind is issued — an address with no device simply
/// fails to acknowledge and is reported absent.
/// </summary>
public static class SmbusRgbDetection
{
    private const int DetectionProbeLength = 4;

    public static SmbusRgbProbeResultV1 Probe()
    {
        if (!PawnIoSmbusTransport.TryCreate(out PawnIoSmbusTransport transport, out string message))
        {
            return SmbusRgbProbeResultV1.Unavailable(SmbusRgbProbeOutcome.TransportUnavailable, message);
        }

        using (transport)
        {
            return ProbeWithTransport(transport);
        }
    }

    /// <summary>
    /// Best-effort read-only evidence window for an address that already
    /// acknowledged: read-byte at consecutive command codes, 0xFF on any
    /// per-byte failure. Reads only — never a pointer or data write.
    /// </summary>
    private static byte[] ReadWindow(ISmbusTransport transport, byte address, byte firstCommand, int length)
    {
        byte[] window = new byte[length];
        for (int offset = 0; offset < length; offset++)
        {
            try
            {
                window[offset] = transport.ReadByte(address, (byte)(firstCommand + offset));
            }
            catch (PawnIoException)
            {
                window[offset] = 0xFF;
            }
        }

        return window;
    }

    /// <summary>Transport-driven core, separated so tests can supply a fake bus.</summary>
    public static SmbusRgbProbeResultV1 ProbeWithTransport(ISmbusTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        List<SmbusRgbControllerSightingV1> sightings = [];
        try
        {
            for (byte address = SmbusAddressPolicy.RgbControllerFirst; address <= SmbusAddressPolicy.RgbControllerLast; address++)
            {
                byte[] observed = new byte[DetectionProbeLength];
                bool acknowledged = true;
                for (int offset = 0; offset < DetectionProbeLength; offset++)
                {
                    try
                    {
                        observed[offset] = transport.ReadByte(address, (byte)(EneSmbusRgbProtocol.DetectionCommandFirst + offset));
                    }
                    catch (PawnIoException)
                    {
                        // No device acknowledged at this address; that is a
                        // normal probe result, not a failure.
                        acknowledged = false;
                        break;
                    }
                }

                if (!acknowledged)
                {
                    continue;
                }

                sightings.Add(new SmbusRgbControllerSightingV1(
                    address,
                    EneSmbusRgbProtocol.LooksLikeDetectionWindow(observed),
                    Convert.ToHexString([.. observed, .. ReadWindow(transport, address, 0xA4, 28), .. ReadWindow(transport, address, 0x00, 16)])));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return SmbusRgbProbeResultV1.Unavailable(
                SmbusRgbProbeOutcome.Failed,
                $"The read-only SMBus RGB probe failed: {exception.GetType().Name}.");
        }

        int matchedCount = sightings.Count(sighting => sighting.PatternMatched);
        return matchedCount > 0
            ? new SmbusRgbProbeResultV1(
                SmbusRgbProbeResultV1.CurrentSchemaVersion,
                SmbusRgbProbeOutcome.ControllersFound,
                sightings,
                $"{matchedCount} ENE DIMM RGB controller(s) answered the read-only detection pattern. No write of any kind was issued.")
            : new SmbusRgbProbeResultV1(
                SmbusRgbProbeResultV1.CurrentSchemaVersion,
                SmbusRgbProbeOutcome.NoControllers,
                sightings,
                "No ENE DIMM RGB controller answered the read-only detection pattern in the RGB address range.");
    }
}
