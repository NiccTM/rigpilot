using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>One named starting shape a user can drop into the custom curve editor.</summary>
public sealed record CurveTemplate(string Key, string Name, string Description, IReadOnlyList<CurvePoint> Points);

/// <summary>
/// Named starting points for the custom fan-curve editor.
///
/// The editor already supports dragging, adding, and removing points, but every custom curve
/// began from whatever happened to be there — so "I want something quieter than balanced" meant
/// hand-placing points from scratch. The suite already knows what a silent, balanced, and
/// cooling-biased shape look like, because the automatic modes are generated from exactly those
/// shapes; the templates simply make them reachable as a starting point rather than as an
/// all-or-nothing mode you either accept or replace.
///
/// A template is a STARTING POINT, deliberately not a live binding: once applied, the points
/// belong to the user and dragging one does not silently re-shape the rest. That is the whole
/// difference between this and just selecting the automatic mode.
/// </summary>
public static class CurveTemplateLibrary
{
    /// <summary>
    /// Builds the templates for an output whose duty runs from <paramref name="floor"/> to
    /// <paramref name="maximum"/>. Shapes are taken from the same generator the automatic
    /// modes use, so a template and its matching mode cannot drift apart.
    /// </summary>
    public static IReadOnlyList<CurveTemplate> For(double floor, double maximum)
    {
        if (!double.IsFinite(floor) || !double.IsFinite(maximum) || maximum <= floor)
        {
            return [];
        }

        return
        [
            new CurveTemplate(
                "silent",
                "Silent",
                "Holds the floor through everyday temperatures and only ramps near critical. Quietest, warmest.",
                CoolingCurveShape.For(CoolingCurveMode.Silent, floor, maximum).Points),
            new CurveTemplate(
                "balanced",
                "Balanced",
                "A steady ramp across the usable range. The default trade between noise and temperature.",
                CoolingCurveShape.For(CoolingCurveMode.Balanced, floor, maximum).Points),
            new CurveTemplate(
                "cooling",
                "Cooling",
                "Ramps early and reaches full speed sooner. Coolest, loudest.",
                CoolingCurveShape.For(CoolingCurveMode.Cooling, floor, maximum).Points),
        ];
    }

    /// <summary>
    /// Finds a template by key, or null. Case-insensitive so a persisted or hand-typed key
    /// still resolves.
    /// </summary>
    public static CurveTemplate? Find(string? key, double floor, double maximum) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : For(floor, maximum).FirstOrDefault(template =>
                string.Equals(template.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Renders a template's points in the editor's text form, so applying one goes through
    /// exactly the same parse, validation, and clamping as a hand-typed curve rather than a
    /// second path that could accept something the editor would reject.
    /// </summary>
    public static string ToEditorText(CurveTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return string.Join(
            Environment.NewLine,
            template.Points.Select(point =>
                $"{point.Input.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}:"
                + $"{point.Output.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}"));
    }
}
