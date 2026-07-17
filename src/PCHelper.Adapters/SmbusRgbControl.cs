namespace PCHelper.Adapters;

public enum SmbusTransactionKind
{
    /// <summary>SMBus write-byte-data: one data byte to a command code.</summary>
    WriteByte,

    /// <summary>SMBus write-word-data: one 16-bit word to a command code.</summary>
    WriteWord,
}

/// <summary>One SMBus transaction in a transmit plan: a value for a command code on a device address.</summary>
public sealed record SmbusTransaction(SmbusTransactionKind Kind, byte Address, byte Command, ushort Value);

/// <summary>
/// The seam between the SMBus RGB logic and a physical SMBus controller. The
/// only rule-compliant production implementation is
/// <see cref="PawnIoSmbusTransport"/>, backed by the signed PawnIO driver
/// (never WinRing0/inpout). Implementations MUST call
/// <see cref="SmbusAddressPolicy.EnsureWritable"/> before every write and must
/// refuse reads outside the RGB-controller range; the writer enforces both
/// again as defence in depth.
/// </summary>
public interface ISmbusTransport : IDisposable
{
    void WriteByte(byte address, byte commandCode, byte value);
    void WriteWord(byte address, byte commandCode, ushort value);
    byte ReadByte(byte address, byte commandCode);
}

/// <summary>
/// The ENE (ex-ASMedia) SMBus RGB controller protocol used by ASUS Aura DRAM
/// and G.Skill Trident Z RGB DIMMs, per the OpenRGB project's protocol
/// documentation (Aura-Controller-Registers): the chip exposes a 16-bit
/// register space through the 8-bit SMBus by writing the byte-swapped target
/// register as a word to command 0x00, then moving data bytes through command
/// 0x01 with address auto-increment. Direct colours live at 0x8000 as
/// R,B,G triplets for five LEDs; 0x8020=1 enables direct mode; 0x80A0=1
/// applies. Register facts only — no vendor code was copied.
///
/// The exact map is still gated per-kit: values are confirmed against the
/// physical DIMMs at a witnessed first-light before a kit joins
/// <see cref="AuditedKits"/>, so an unaudited kit yields a plan for inspection
/// but is never transmitted through the normal path.
/// </summary>
public static class EneSmbusRgbProtocol
{
    // The reference kit this register map is being qualified against.
    public const string ReferenceKitPartNumber = "F4-4000C15-8GTZR";

    // Kits whose register map has been verified at a witnessed first-light.
    // Empty until that evidence exists — no kit is transmitted to on a guess.
    private static readonly HashSet<string> AuditedKits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SMBus command that latches the 16-bit ENE register pointer (byte-swapped word).</summary>
    public const byte PointerCommand = 0x00;

    /// <summary>SMBus command that writes one data byte at the pointer, auto-incrementing.</summary>
    public const byte DataCommand = 0x01;

    /// <summary>SMBus command that reads one data byte at the pointer, auto-incrementing (write command | 0x80).</summary>
    public const byte DataReadCommand = 0x81;

    // ENE 16-bit register addresses (OpenRGB Aura-Controller-Registers).
    public const ushort DirectColoursRegister = 0x8000; // 15 bytes, R,B,G per LED
    public const ushort DirectModeRegister = 0x8020;    // 0 = effects, 1 = direct
    public const ushort ModeRegister = 0x8021;
    public const ushort ApplyRegister = 0x80A0;         // write 1 to apply
    public const byte DirectModeOn = 0x01;
    public const byte ApplyValue = 0x01;

    /// <summary>Trident Z RGB exposes five addressable LEDs per stick.</summary>
    public const int LedCount = 5;

    /// <summary>
    /// Detection command-code window: reading SMBus commands 0xA0..0xBF from an
    /// ENE controller returns an incrementing byte sequence (documented as
    /// 0x10..0x1F; the DIMM_LED-0103 firmware on the reference kit answers
    /// 0x00..0x1F). Reading these needs no pointer write, so presence
    /// detection is purely read-only.
    /// </summary>
    public const byte DetectionCommandFirst = 0xA0;

    /// <summary>
    /// Device-name prefixes of known ENE/AsMedia DRAM RGB controllers. The
    /// first-light and every future transmit path require a matching identity
    /// read before any colour byte is sent — a device that merely acknowledges
    /// reads is never written to.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownDeviceNamePrefixes = ["DIMM_LED-", "AUDA"];

    public static bool IsKnownDeviceName(string? deviceName) =>
        deviceName is not null
        && KnownDeviceNamePrefixes.Any(prefix => deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a read-only detection window looks like an ENE controller: an
    /// incrementing byte sequence from whatever base the firmware uses, with
    /// one glitched byte tolerated (live buses shared with a polling service
    /// showed single-byte read glitches), and never a flat constant window.
    /// </summary>
    public static bool LooksLikeDetectionWindow(ReadOnlySpan<byte> window)
    {
        if (window.Length < 4)
        {
            return false;
        }

        int increments = 0;
        for (int offset = 1; offset < window.Length; offset++)
        {
            if (window[offset] == (byte)(window[0] + offset))
            {
                increments++;
            }
        }

        return increments >= window.Length - 2;
    }

    public static bool IsKitAudited(string? partNumber) =>
        partNumber is not null && AuditedKits.Contains(partNumber.Trim());

    /// <summary>The byte-swapped pointer word for an ENE register (high byte transmits first).</summary>
    public static ushort PointerWord(ushort eneRegister) =>
        (ushort)((eneRegister << 8) | (eneRegister >> 8));

    /// <summary>
    /// Produces the ordered SMBus transaction plan for a static colour on one
    /// controller: enable direct mode, stream R,B,G for all five LEDs through
    /// the auto-incrementing data command, then apply. Every transaction
    /// targets <paramref name="controllerAddress"/> only.
    /// </summary>
    public static IReadOnlyList<SmbusTransaction> BuildStaticColour(byte controllerAddress, byte red, byte green, byte blue)
    {
        SmbusAddressPolicy.EnsureWritable(controllerAddress);
        List<SmbusTransaction> plan =
        [
            new(SmbusTransactionKind.WriteWord, controllerAddress, PointerCommand, PointerWord(DirectModeRegister)),
            new(SmbusTransactionKind.WriteByte, controllerAddress, DataCommand, DirectModeOn),
            new(SmbusTransactionKind.WriteWord, controllerAddress, PointerCommand, PointerWord(DirectColoursRegister)),
        ];
        for (int led = 0; led < LedCount; led++)
        {
            // ENE colour order is R, B, G; the data command auto-increments.
            plan.Add(new SmbusTransaction(SmbusTransactionKind.WriteByte, controllerAddress, DataCommand, red));
            plan.Add(new SmbusTransaction(SmbusTransactionKind.WriteByte, controllerAddress, DataCommand, blue));
            plan.Add(new SmbusTransaction(SmbusTransactionKind.WriteByte, controllerAddress, DataCommand, green));
        }

        plan.Add(new SmbusTransaction(SmbusTransactionKind.WriteWord, controllerAddress, PointerCommand, PointerWord(ApplyRegister)));
        plan.Add(new SmbusTransaction(SmbusTransactionKind.WriteByte, controllerAddress, DataCommand, ApplyValue));
        return plan;
    }
}

public enum SmbusRgbOutcome
{
    /// <summary>The plan was transmitted to the SMBus controllers.</summary>
    WriteIssued,

    /// <summary>No signed PawnIO SMBus transport is available.</summary>
    NoTransport,

    /// <summary>The kit's register map is not yet audited at a witnessed first-light.</summary>
    ProtocolNotAudited,

    /// <summary>A target address failed the SMBus address policy.</summary>
    AddressRefused,

    /// <summary>No RGB-capable DIMM controller was found.</summary>
    DeviceNotFound,

    /// <summary>The write failed for another reason.</summary>
    Failed,
}

public sealed record SmbusRgbResult(SmbusRgbOutcome Outcome, int WritesIssued, string Message);

/// <summary>
/// Writes a static colour to ENE SMBus DIMM RGB controllers, behind two
/// independent gates that must both hold before any byte reaches the bus: a
/// live transport must exist, and the exact kit must be audited. The single
/// sanctioned exception is the witnessed first-light — the operator runs the
/// dedicated first-light entry point while watching the DIMMs, which bypasses
/// only the audit gate, never the address policy. Every transaction is
/// re-checked against <see cref="SmbusAddressPolicy"/> so a mis-built plan can
/// never write to an SPD, thermal, or PMIC address.
/// </summary>
public sealed class SmbusRgbWriter(ISmbusTransport? transport)
{
    public SmbusRgbResult WriteStaticColour(
        IReadOnlyList<byte> controllerAddresses,
        string? kitPartNumber,
        byte red,
        byte green,
        byte blue,
        bool witnessedFirstLight = false)
    {
        ArgumentNullException.ThrowIfNull(controllerAddresses);
        if (controllerAddresses.Count == 0)
        {
            return new SmbusRgbResult(SmbusRgbOutcome.DeviceNotFound, 0,
                "No RGB DIMM controller address was supplied.");
        }

        if (transport is null)
        {
            return new SmbusRgbResult(SmbusRgbOutcome.NoTransport, 0,
                "SMBus RGB is feasible but no signed PawnIO SMBus transport is available. A PawnIO SMBus module and a witnessed first-light are required before any DIMM write.");
        }

        if (!EneSmbusRgbProtocol.IsKitAudited(kitPartNumber) && !witnessedFirstLight)
        {
            return new SmbusRgbResult(SmbusRgbOutcome.ProtocolNotAudited, 0,
                $"The register map for '{kitPartNumber}' has not been verified at a witnessed first-light. No SMBus write is issued on an unaudited kit outside the operator-run first-light entry point.");
        }

        int issued = 0;
        try
        {
            foreach (byte address in controllerAddresses)
            {
                if (SmbusAddressPolicy.DenyReason(address) is string reason)
                {
                    return new SmbusRgbResult(SmbusRgbOutcome.AddressRefused, issued, reason);
                }

                foreach (SmbusTransaction transaction in EneSmbusRgbProtocol.BuildStaticColour(address, red, green, blue))
                {
                    // Defence in depth: never let a mis-built plan escape the policy.
                    SmbusAddressPolicy.EnsureWritable(transaction.Address);
                    switch (transaction.Kind)
                    {
                        case SmbusTransactionKind.WriteWord:
                            transport.WriteWord(transaction.Address, transaction.Command, transaction.Value);
                            break;
                        default:
                            transport.WriteByte(transaction.Address, transaction.Command, (byte)transaction.Value);
                            break;
                    }
                    issued++;
                }
            }
        }
        catch (SmbusSafetyException exception)
        {
            return new SmbusRgbResult(SmbusRgbOutcome.AddressRefused, issued, exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new SmbusRgbResult(SmbusRgbOutcome.Failed, issued,
                $"The SMBus write failed after {issued} transaction(s): {exception.GetType().Name}.");
        }

        return new SmbusRgbResult(SmbusRgbOutcome.WriteIssued, issued,
            $"Static colour transmitted to {controllerAddresses.Count} DIMM controller(s) in {issued} SMBus transactions. There is no controller read-back; confirm visually.");
    }
}
