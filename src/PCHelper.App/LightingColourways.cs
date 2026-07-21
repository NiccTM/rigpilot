using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>
/// Deterministic static colourways for the OpenRGB bridge and future native
/// lighting adapters. Each preset maps an LED index to a colour for a strip of
/// a given length; the whole frame is computed client-side and written once as
/// a static custom-mode update — no animation loop, no injection, and the same
/// input always produces the same frame. The preset family follows the common
/// community effect vocabulary (rainbow spread, linear gradients, multi-colour
/// puddles/sparkle patches).
/// </summary>
public static class LightingColourways
{
    public sealed record Colourway(string Id, string Name, string Description);

    public static IReadOnlyList<Colourway> All { get; } =
    [
        new("static", "Static colour", "One colour everywhere (uses the colour field)."),
        new("rainbow", "Rainbow", "Full hue sweep spread across each device's LEDs."),
        new("puddles", "Puddles", "Colour patches — pools of violet, cyan, and magenta."),
        new("sunset", "Sunset", "Warm gradient from amber through magenta."),
        new("ocean", "Ocean", "Cool gradient from deep blue to aqua."),
        new("lava", "Lava", "Deep red to bright amber gradient."),
    ];

    /// <summary>
    /// Computes the packed per-LED colours (0x00BBGGRR, the OpenRGB wire order)
    /// for one device. <paramref name="staticRgb"/> is used only by the
    /// "static" preset; brightness scales every channel linearly.
    /// </summary>
    public static uint[] Generate(string colourwayId, int ledCount, (byte R, byte G, byte B) staticRgb, int brightnessPercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ledCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(brightnessPercent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(brightnessPercent, 100);
        uint[] frame = new uint[ledCount];
        for (int index = 0; index < ledCount; index++)
        {
            double position = ledCount <= 1 ? 0 : (double)index / (ledCount - 1);
            (byte r, byte g, byte b) = colourwayId switch
            {
                "rainbow" => HsvToRgb(position * 300, 1, 1), // 0°–300° keeps the sweep from wrapping back to red
                "puddles" => Puddle(index),
                "sunset" => Lerp((255, 140, 0), (200, 30, 140), position),
                "ocean" => Lerp((0, 40, 160), (0, 210, 200), position),
                "lava" => Lerp((120, 0, 0), (255, 170, 20), position),
                _ => staticRgb,
            };
            frame[index] = Pack(r, g, b, brightnessPercent);
        }

        return frame;
    }

    /// <summary>
    /// A single colour that stands in for a whole colourway on a flat-colour device
    /// (native writers, Windows Dynamic Lighting) that cannot render the gradient.
    /// Derived from <see cref="Generate"/> itself — the middle LED of a three-LED
    /// reference strip is the gradient's midpoint (position 0.5) — so the stand-in
    /// always tracks the same gradient the OpenRGB bridge renders. "static" returns
    /// the supplied colour unchanged; brightness is left at full so callers can scale
    /// afterwards exactly as they do for the manual colour.
    /// </summary>
    public static RgbColour RepresentativeColour(string colourwayId, RgbColour staticColour)
    {
        uint packed = Generate(colourwayId, 3, (staticColour.Red, staticColour.Green, staticColour.Blue), 100)[1];
        return new RgbColour((byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF));
    }

    /// <summary>Deterministic pools of colour, roughly six LEDs per puddle.</summary>
    private static (byte, byte, byte) Puddle(int index)
    {
        (byte, byte, byte)[] palette =
        [
            (120, 40, 220),  // violet
            (0, 190, 220),   // cyan
            (230, 40, 160),  // magenta
            (30, 90, 235),   // blue
        ];
        return palette[(index / 6) % palette.Length];
    }

    private static (byte, byte, byte) Lerp((int R, int G, int B) from, (int R, int G, int B) to, double position) => (
        (byte)Math.Round(from.R + ((to.R - from.R) * position)),
        (byte)Math.Round(from.G + ((to.G - from.G) * position)),
        (byte)Math.Round(from.B + ((to.B - from.B) * position)));

    private static (byte, byte, byte) HsvToRgb(double hueDegrees, double saturation, double value)
    {
        double chroma = value * saturation;
        double huePrime = hueDegrees / 60;
        double x = chroma * (1 - Math.Abs((huePrime % 2) - 1));
        (double r, double g, double b) = huePrime switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        double m = value - chroma;
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Packs to the OpenRGB wire order (R | G&lt;&lt;8 | B&lt;&lt;16) with linear brightness scaling.</summary>
    private static uint Pack(byte r, byte g, byte b, int brightnessPercent)
    {
        double scale = brightnessPercent / 100d;
        return (uint)Math.Round(r * scale)
            | ((uint)Math.Round(g * scale) << 8)
            | ((uint)Math.Round(b * scale) << 16);
    }
}
