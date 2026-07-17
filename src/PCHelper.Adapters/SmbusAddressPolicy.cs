namespace PCHelper.Adapters;

/// <summary>
/// The safety keystone for every SMBus write RigPilot ever performs. The system
/// SMBus is a shared I2C bus: alongside the DIMM RGB controllers it carries the
/// SPD EEPROMs (corrupting one can make a stick unbootable), the on-DIMM thermal
/// sensors, DDR5 PMICs, and page/write-protect selectors. A single write to the
/// wrong 7-bit address is brick-class, so this policy is <b>default-deny</b>:
/// only the small, standardised RGB-controller range is writable, and every
/// known-dangerous range is named and blocked explicitly for defence in depth.
///
/// This is the invariant that must hold before any live SMBus transport is
/// wired in; it is pure and exhaustively unit-tested with no driver dependency.
/// </summary>
public static class SmbusAddressPolicy
{
    /// <summary>Inclusive 7-bit SMBus address range holding DDR RGB controllers (G.Skill Trident Z RGB, ASUS AURA DIMM).</summary>
    public const byte RgbControllerFirst = 0x70;
    public const byte RgbControllerLast = 0x77;

    private sealed record ForbiddenRange(byte First, byte Last, string Reason);

    // Every range on a DDR4/DDR5 SMBus that a write must never touch. These are
    // JEDEC-standardised, so the guard is exact rather than heuristic.
    private static readonly ForbiddenRange[] Forbidden =
    [
        new(0x18, 0x1F, "on-DIMM thermal sensor (TSOD) — writing corrupts temperature reporting"),
        new(0x30, 0x37, "SPD page-select / write-protect (SPA/SWP) — writing can unlock or brick the SPD"),
        new(0x48, 0x4F, "DDR5 PMIC / RCD — writing can change DIMM power rails"),
        new(0x50, 0x57, "SPD EEPROM — writing corrupts the memory descriptor and can make the stick unbootable"),
    ];

    /// <summary>True only for an address inside the RGB-controller range.</summary>
    public static bool IsRgbControllerAddress(byte address) =>
        address is >= RgbControllerFirst and <= RgbControllerLast;

    /// <summary>
    /// Returns null when the address may be written, otherwise the exact reason
    /// it is refused. Default-deny: anything outside the RGB range is refused,
    /// with a named reason for the known-dangerous ranges.
    /// </summary>
    public static string? DenyReason(byte address)
    {
        foreach (ForbiddenRange range in Forbidden)
        {
            if (address >= range.First && address <= range.Last)
            {
                return $"SMBus address 0x{address:X2} is {range.Reason}; writes are permanently blocked.";
            }
        }

        if (!IsRgbControllerAddress(address))
        {
            return $"SMBus address 0x{address:X2} is outside the RGB-controller range [0x{RgbControllerFirst:X2}, 0x{RgbControllerLast:X2}]; RigPilot writes only to RGB controllers.";
        }

        return null;
    }

    /// <summary>Throws <see cref="SmbusSafetyException"/> when the address may not be written.</summary>
    public static void EnsureWritable(byte address)
    {
        if (DenyReason(address) is string reason)
        {
            throw new SmbusSafetyException(reason);
        }
    }
}

/// <summary>Thrown when an SMBus request violates the address policy or an audit gate.</summary>
public sealed class SmbusSafetyException(string message) : Exception(message);
