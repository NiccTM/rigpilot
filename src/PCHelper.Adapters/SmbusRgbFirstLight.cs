using System.Globalization;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Serializable outcome of the operator-run witnessed first-light: the
/// read-only detection evidence plus what (if anything) was transmitted.
/// </summary>
public sealed record SmbusRgbFirstLightResultV1(
    int SchemaVersion,
    SmbusRgbProbeResultV1 Probe,
    string WriteOutcome,
    int TransactionsIssued,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Serializable outcome of the operator-run ENE identity read.</summary>
public sealed record SmbusRgbIdentityResultV1(
    int SchemaVersion,
    int Address,
    bool Succeeded,
    string DeviceName,
    string RawBytes,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Operator-run ENE identity read: latches the device-name register (0x1000)
/// with a single pointer write, then reads the 16-byte name through the
/// auto-incrementing data command. The pointer write is the only write issued
/// — no colour, mode, apply, or configuration register is touched — but it IS
/// a bus write, so this runs only when the operator asks, never automatically.
/// </summary>
public static class SmbusRgbIdentify
{
    public const ushort DeviceNameRegister = 0x1000;
    private const int DeviceNameLength = 16;

    /// <summary>
    /// Deep identity pass for one operator-named address: tries the documented
    /// read-command/pointer variants in turn (ENE reads use 0x81; older
    /// firmware may answer 0x01 or an unswapped pointer). Each variant issues
    /// exactly one pointer write; nothing else is written.
    /// </summary>
    public static IReadOnlyList<SmbusRgbIdentityResultV1> DeepRun(byte address)
    {
        if (!SmbusAddressPolicy.IsRgbControllerAddress(address))
        {
            return [new SmbusRgbIdentityResultV1(SmbusRgbIdentityResultV1.CurrentSchemaVersion, address, false, string.Empty, string.Empty,
                $"0x{address:X2} is not an RGB-controller address; the deep identity pass refuses it.")];
        }

        if (!PawnIoSmbusTransport.TryCreate(out PawnIoSmbusTransport transport, out string message))
        {
            return [new SmbusRgbIdentityResultV1(SmbusRgbIdentityResultV1.CurrentSchemaVersion, address, false, string.Empty, string.Empty, message)];
        }

        using (transport)
        {
            List<SmbusRgbIdentityResultV1> results = [];
            foreach ((byte readCommand, bool swapped) in (ReadOnlySpan<(byte, bool)>)
                     [(EneSmbusRgbProtocol.DataReadCommand, true), (EneSmbusRgbProtocol.DataCommand, true),
                      (EneSmbusRgbProtocol.DataReadCommand, false), (EneSmbusRgbProtocol.DataCommand, false)])
            {
                SmbusRgbIdentityResultV1 attempt = ReadIdentity(transport, address, readCommand, swapped);
                results.Add(attempt with
                {
                    Message = $"{attempt.Message} (read command 0x{readCommand:X2}, {(swapped ? "swapped" : "unswapped")} pointer)",
                });
            }

            return results;
        }
    }

    public static IReadOnlyList<SmbusRgbIdentityResultV1> Run()
    {
        if (!PawnIoSmbusTransport.TryCreate(out PawnIoSmbusTransport transport, out string message))
        {
            return [new SmbusRgbIdentityResultV1(SmbusRgbIdentityResultV1.CurrentSchemaVersion, 0, false, string.Empty, string.Empty, message)];
        }

        using (transport)
        {
            // Only addresses that answered the ENE detection pattern are ever
            // queried — an unknown device that merely acknowledges reads is
            // never written to, not even a pointer.
            SmbusRgbProbeResultV1 probe = SmbusRgbDetection.ProbeWithTransport(transport);
            return [.. probe.Sightings
                .Where(sighting => sighting.PatternMatched)
                .Select(sighting => ReadIdentity(transport, (byte)sighting.Address))];
        }
    }

    /// <summary>Transport-driven core, separated so tests can supply a fake bus.</summary>
    public static SmbusRgbIdentityResultV1 ReadIdentity(ISmbusTransport transport, byte address) =>
        ReadIdentity(transport, address, EneSmbusRgbProtocol.DataReadCommand, swappedPointer: true);

    /// <summary>
    /// One identity-read attempt with an explicit data-read command code and
    /// pointer byte order. The only write issued is the single pointer word.
    /// </summary>
    public static SmbusRgbIdentityResultV1 ReadIdentity(ISmbusTransport transport, byte address, byte readCommand, bool swappedPointer)
    {
        ArgumentNullException.ThrowIfNull(transport);
        try
        {
            ushort pointer = swappedPointer ? EneSmbusRgbProtocol.PointerWord(DeviceNameRegister) : DeviceNameRegister;
            transport.WriteWord(address, EneSmbusRgbProtocol.PointerCommand, pointer);
            byte[] raw = new byte[DeviceNameLength];
            for (int offset = 0; offset < DeviceNameLength; offset++)
            {
                raw[offset] = transport.ReadByte(address, readCommand);
            }

            string name = new([.. raw.TakeWhile(value => value != 0).Select(value => value is >= 0x20 and < 0x7F ? (char)value : '.')]);
            return new SmbusRgbIdentityResultV1(
                SmbusRgbIdentityResultV1.CurrentSchemaVersion,
                address,
                true,
                name,
                Convert.ToHexString(raw),
                $"Device name read from 0x{address:X2} with one pointer write and {DeviceNameLength} byte reads.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SmbusRgbIdentityResultV1(
                SmbusRgbIdentityResultV1.CurrentSchemaVersion,
                address,
                false,
                string.Empty,
                string.Empty,
                $"The identity read at 0x{address:X2} failed: {exception.GetType().Name}.");
        }
    }
}

/// <summary>
/// The single sanctioned path that may transmit the ENE RGB plan to an
/// unaudited kit. It is run manually by the operator while watching the DIMMs
/// (never by the service, a schedule, or any automatic caller), and it only
/// ever transmits to addresses that first answered the read-only ENE detection
/// pattern in the same run. The audit gate is bypassed; the
/// <see cref="SmbusAddressPolicy"/> is not and cannot be.
/// </summary>
public static class SmbusRgbFirstLight
{
    public static SmbusRgbFirstLightResultV1 Run(string colourHex) =>
        Transmit(colourHex, witnessedFirstLight: true);

    internal static SmbusRgbFirstLightResultV1 Transmit(string colourHex, bool witnessedFirstLight)
    {
        if (!TryParseColour(colourHex, out byte red, out byte green, out byte blue))
        {
            return Failure(
                SmbusRgbProbeResultV1.Unavailable(SmbusRgbProbeOutcome.Failed, "The probe was not run."),
                $"'{colourHex}' is not an RRGGBB colour; nothing was transmitted.");
        }

        if (!PawnIoSmbusTransport.TryCreate(out PawnIoSmbusTransport transport, out string message))
        {
            return Failure(
                SmbusRgbProbeResultV1.Unavailable(SmbusRgbProbeOutcome.TransportUnavailable, message),
                message);
        }

        using (transport)
        {
            SmbusRgbProbeResultV1 probe = SmbusRgbDetection.ProbeWithTransport(transport);
            byte[] patternMatched = [.. probe.Sightings
                .Where(sighting => sighting.PatternMatched)
                .Select(sighting => (byte)sighting.Address)];
            if (patternMatched.Length == 0)
            {
                return Failure(probe,
                    "No ENE controller answered the read-only detection pattern; nothing was transmitted.");
            }

            // Identity gate: colour is transmitted only to a controller whose
            // device name matches a known ENE DRAM identity. A device that
            // merely acknowledges reads is never sent a colour byte.
            List<string> identities = [];
            List<byte> confirmed = [];
            foreach (byte candidate in patternMatched)
            {
                SmbusRgbIdentityResultV1 identity = SmbusRgbIdentify.ReadIdentity(transport, candidate);
                identities.Add($"0x{candidate:X2}='{identity.DeviceName}'");
                if (identity.Succeeded && EneSmbusRgbProtocol.IsKnownDeviceName(identity.DeviceName))
                {
                    confirmed.Add(candidate);
                }
            }

            if (confirmed.Count == 0)
            {
                return Failure(probe,
                    $"No pattern-matched controller returned a known ENE DRAM device name ({string.Join(", ", identities)}); nothing was transmitted.");
            }

            SmbusRgbResult write = new SmbusRgbWriter(transport).WriteStaticColour(
                confirmed,
                EneSmbusRgbProtocol.ReferenceKitPartNumber,
                red,
                green,
                blue,
                witnessedFirstLight);
            return new SmbusRgbFirstLightResultV1(
                SmbusRgbFirstLightResultV1.CurrentSchemaVersion,
                probe,
                write.Outcome.ToString(),
                write.WritesIssued,
                $"{write.Message} Identities: {string.Join(", ", identities)}.");
        }
    }

    /// <summary>
    /// The production DIMM RGB path: identical detection + identity gating to
    /// the first-light, but the audit gate is enforced — only kits verified at
    /// a witnessed first-light are ever transmitted to.
    /// </summary>
    public static SmbusRgbFirstLightResultV1 Apply(string colourHex) =>
        Transmit(colourHex, witnessedFirstLight: false);

    private static SmbusRgbFirstLightResultV1 Failure(SmbusRgbProbeResultV1 probe, string message) =>
        new(SmbusRgbFirstLightResultV1.CurrentSchemaVersion, probe, SmbusRgbOutcome.Failed.ToString(), 0, message);

    private static bool TryParseColour(string value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        string trimmed = value?.TrimStart('#') ?? string.Empty;
        return trimmed.Length == 6
            && byte.TryParse(trimmed[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red)
            && byte.TryParse(trimmed[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green)
            && byte.TryParse(trimmed[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue);
    }
}
