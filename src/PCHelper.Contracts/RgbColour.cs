using System.Globalization;

namespace PCHelper.Contracts;

/// <summary>
/// A single 24-bit RGB colour: the one in-memory representation the RGB control stack uses.
///
/// Before consolidation this type existed in <c>PCHelper.Core</c> for the effect engine
/// (with <see cref="Blend"/>/<see cref="Scale"/>) while each device writer (AURA, Kraken,
/// Razer, DIMM/SMBus) carried its own copy of the same <c>#RRGGBB</c> parse/validate block,
/// so a colour crossed three or four ad-hoc forms on its way to hardware. This is now the
/// single parse, format, blend, and scale path. <see cref="EffectColourV1"/> remains the
/// serialised wire form; <see cref="ToEffectColour"/> / <see cref="FromEffectColour"/> bridge
/// it without duplicating the triplet.
/// </summary>
public readonly record struct RgbColour(byte Red, byte Green, byte Blue)
{
    /// <summary>Fully off / black. The canonical value written for a "turn off" request.</summary>
    public static RgbColour Off => new(0, 0, 0);

    /// <summary>
    /// Parses <c>#RRGGBB</c> or <c>RRGGBB</c> (case-insensitive, surrounding whitespace
    /// and a single leading <c>#</c> tolerated). Returns false for any other shape; never throws.
    /// </summary>
    public static bool TryParse(string? hex, out RgbColour colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string value = hex.Trim().TrimStart('#');
        if (value.Length != 6
            || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return false;
        }

        colour = new RgbColour((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>Parses <c>#RRGGBB</c>/<c>RRGGBB</c> or throws <see cref="FormatException"/>.</summary>
    public static RgbColour Parse(string hex) => TryParse(hex, out RgbColour colour)
        ? colour
        : throw new FormatException($"'{hex}' is not a #RRGGBB colour.");

    /// <summary>The six-digit uppercase hex body, no leading <c>#</c> (e.g. <c>4EA1FF</c>).</summary>
    public string ToHex() => $"{Red:X2}{Green:X2}{Blue:X2}";

    /// <summary>The canonical display form with a leading <c>#</c> (e.g. <c>#4EA1FF</c>).</summary>
    public override string ToString() => $"#{ToHex()}";

    /// <summary>Linear interpolation between two colours; <paramref name="amount"/> is clamped to [0,1].</summary>
    public static RgbColour Blend(RgbColour left, RgbColour right, double amount)
    {
        double value = Math.Clamp(amount, 0, 1);
        return new RgbColour(
            (byte)Math.Round(left.Red + ((right.Red - left.Red) * value)),
            (byte)Math.Round(left.Green + ((right.Green - left.Green) * value)),
            (byte)Math.Round(left.Blue + ((right.Blue - left.Blue) * value)));
    }

    /// <summary>Scales brightness uniformly; <paramref name="amount"/> is clamped to [0,1].</summary>
    public RgbColour Scale(double amount) => new(
        (byte)Math.Round(Red * Math.Clamp(amount, 0, 1)),
        (byte)Math.Round(Green * Math.Clamp(amount, 0, 1)),
        (byte)Math.Round(Blue * Math.Clamp(amount, 0, 1)));

    /// <summary>Bridges to the effect pipeline's serialised colour triplet.</summary>
    public EffectColourV1 ToEffectColour() => new(Red, Green, Blue);

    /// <summary>Bridges from the effect pipeline's serialised colour triplet.</summary>
    public static RgbColour FromEffectColour(EffectColourV1 colour) => new(colour.Red, colour.Green, colour.Blue);
}
