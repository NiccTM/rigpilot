using System.Globalization;
using System.Text.RegularExpressions;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static partial class OsdFrameRenderer
{
    private const int MaximumWidgets = 64;

    public static SuiteValidationResult Validate(OsdLayoutV1 layout)
    {
        List<string> errors = [];
        if (layout.SchemaVersion != OsdLayoutV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(layout.Id)
            || string.IsNullOrWhiteSpace(layout.Name))
        {
            errors.Add("OSD layout schema, ID, and name are required.");
        }
        if (layout.Widgets.Count is 0 or > MaximumWidgets)
        {
            errors.Add($"OSD layouts require 1-{MaximumWidgets} widgets.");
        }
        if (layout.Opacity is < 0.1 or > 1 || layout.Scale is < 0.5 or > 3)
        {
            errors.Add("OSD opacity or scale is outside supported bounds.");
        }
        if (layout.Widgets.Any(widget =>
                string.IsNullOrWhiteSpace(widget.SensorId)
                || string.IsNullOrWhiteSpace(widget.Label)
                || widget.Row is < 0 or > 31
                || widget.Column is < 0 or > 7
                || !FormatPattern().IsMatch(widget.Format)
                || !ColourPattern().IsMatch(widget.Colour)))
        {
            errors.Add("OSD widget sensor, cell, format, or colour is invalid.");
        }
        if (layout.Widgets.Select(widget => (widget.Row, widget.Column)).Distinct().Count() != layout.Widgets.Count)
        {
            errors.Add("OSD widgets cannot occupy the same cell.");
        }
        return new SuiteValidationResult(errors.Count == 0, errors, []);
    }

    public static OsdFrameV1 Render(
        OsdLayoutV1 layout,
        IReadOnlyList<SensorSample> samples,
        DateTimeOffset timestamp)
    {
        SuiteValidationResult validation = Validate(layout);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        }
        Dictionary<string, SensorSample> byId = samples
            .GroupBy(sample => sample.SensorId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(sample => sample.Timestamp).First(), StringComparer.Ordinal);
        List<OsdRenderedWidgetV1> widgets = [];
        foreach (OsdWidgetV1 widget in layout.Widgets.OrderBy(item => item.Row).ThenBy(item => item.Column))
        {
            if (!byId.TryGetValue(widget.SensorId, out SensorSample? sample)
                || sample.Value is null
                || sample.Quality is SensorQuality.Unavailable or SensorQuality.Invalid)
            {
                widgets.Add(new OsdRenderedWidgetV1(
                    widget.SensorId,
                    widget.Label,
                    "--",
                    widget.Row,
                    widget.Column,
                    widget.Colour,
                    sample?.Quality ?? SensorQuality.Unavailable));
                continue;
            }
            string value = sample.Value.Value.ToString(widget.Format, CultureInfo.InvariantCulture);
            widgets.Add(new OsdRenderedWidgetV1(
                widget.SensorId,
                widget.Label,
                $"{value} {sample.Unit}".TrimEnd(),
                widget.Row,
                widget.Column,
                widget.Colour,
                sample.Quality));
        }
        return new OsdFrameV1(OsdFrameV1.CurrentSchemaVersion, layout.Id, timestamp, widgets);
    }

    [GeneratedRegex("^(?:F[0-3]|N[0-3]|0(?:\\.0{1,3})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex FormatPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColourPattern();
}
