using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PCHelper.App;

/// <summary>
/// Turns PascalCase/camelCase enum and identifier names into human-readable
/// labels. A naive "insert a space before every capital" split renders hardware
/// acronyms as title case — <c>DeviceKind.Cpu</c> becomes "Cpu",
/// <c>HardwareOperationKind.AutoOc</c> becomes "Auto Oc". This restores the
/// well-known acronyms to all-caps after the split so labels read correctly.
/// </summary>
internal static class DisplayText
{
    private static readonly Regex WordBoundary = new("(?<!^)([A-Z])", RegexOptions.Compiled);

    // Whole words (after the camel-case split) that should read as all-caps
    // acronyms. Matched case-insensitively; only ever applied to a complete
    // split word, never a substring, so "Ram" -> "RAM" cannot touch "Ramp".
    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oc"] = "OC",
        ["cpu"] = "CPU",
        ["gpu"] = "GPU",
        ["bios"] = "BIOS",
        ["rgb"] = "RGB",
        ["argb"] = "ARGB",
        ["ram"] = "RAM",
        ["osd"] = "OSD",
        ["hid"] = "HID",
        ["aio"] = "AIO",
    };

    /// <summary>
    /// Splits <paramref name="value"/> on camel-case boundaries and upper-cases
    /// any resulting word that is a known hardware acronym.
    /// </summary>
    public static string Humanize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string[] words = WordBoundary.Replace(value, " $1").Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            if (Acronyms.TryGetValue(words[i], out string? acronym))
            {
                words[i] = acronym;
            }
        }

        return string.Join(' ', words);
    }
}
