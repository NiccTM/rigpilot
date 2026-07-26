using System.Globalization;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The curve editor supports dragging, adding, and removing points, but every custom curve
/// started from whatever happened to be there — so "something quieter than balanced" meant
/// hand-placing points from scratch. These templates make the shapes the automatic modes are
/// already generated from reachable as a starting point.
/// </summary>
public sealed class CurveTemplateLibraryTests
{
    private const double Floor = 20;
    private const double Maximum = 100;

    [Fact]
    public void TheLibraryOffersTheQuietBalancedAndCoolingShapes()
    {
        IReadOnlyList<CurveTemplate> templates = CurveTemplateLibrary.For(Floor, Maximum);

        Assert.Equal(["silent", "balanced", "cooling"], templates.Select(template => template.Key));
        Assert.All(templates, template =>
        {
            Assert.NotEmpty(template.Points);
            Assert.False(string.IsNullOrWhiteSpace(template.Description));
        });
    }

    [Fact]
    public void EveryTemplateStaysInsideTheOutputsOwnDutyRange()
    {
        // A template is dropped into an editor bound to one output; a point outside that
        // output's range would be clamped or rejected the moment it was applied.
        foreach (CurveTemplate template in CurveTemplateLibrary.For(Floor, Maximum))
        {
            Assert.All(template.Points, point => Assert.InRange(point.Output, Floor, Maximum));
        }
    }

    [Fact]
    public void TheQuietTemplateStaysBelowTheCoolingOneThroughEverydayTemperatures()
    {
        // The names have to mean something: at a mid-range temperature the silent shape must
        // actually be quieter than the cooling shape, or the library is decorative.
        IReadOnlyList<CurveTemplate> templates = CurveTemplateLibrary.For(Floor, Maximum);
        double silent = DutyAt(templates.Single(template => template.Key == "silent"), 65);
        double cooling = DutyAt(templates.Single(template => template.Key == "cooling"), 65);

        Assert.True(silent < cooling, $"silent {silent} should sit below cooling {cooling} at 65 °C");
    }

    [Fact]
    public void TemplatesScaleToAnOutputWithAHigherFloor()
    {
        // A controller that cannot run below 40% must never be handed a 20% point.
        foreach (CurveTemplate template in CurveTemplateLibrary.For(floor: 40, maximum: 100))
        {
            Assert.All(template.Points, point => Assert.True(point.Output >= 40));
        }
    }

    [Fact]
    public void ADegenerateRangeYieldsNoTemplatesRatherThanThrowing()
    {
        // The editor can be open before an output is selected; that must not fault.
        Assert.Empty(CurveTemplateLibrary.For(50, 50));
        Assert.Empty(CurveTemplateLibrary.For(double.NaN, 100));
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("SILENT")]
    [InlineData("  Silent  ")]
    public void FindResolvesAKeyRegardlessOfCaseOrSurroundingSpace(string key)
    {
        Assert.Equal("silent", CurveTemplateLibrary.Find(key, Floor, Maximum)?.Key);
    }

    [Fact]
    public void FindReturnsNullForAnUnknownOrEmptyKey()
    {
        Assert.Null(CurveTemplateLibrary.Find("aggressive", Floor, Maximum));
        Assert.Null(CurveTemplateLibrary.Find("  ", Floor, Maximum));
    }

    [Fact]
    public void TemplatesRenderInTheEditorsOwnTextFormat()
    {
        // Applying a template must go through the same parse and validation as a hand-typed
        // curve, so it is rendered as the editor's "temperature:duty" lines with an invariant
        // decimal separator rather than being injected through a second path.
        CurveTemplate template = CurveTemplateLibrary.Find("balanced", Floor, Maximum)!;

        string text = CurveTemplateLibrary.ToEditorText(template);

        string[] lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(template.Points.Count, lines.Length);
        Assert.All(lines, line =>
        {
            string[] parts = line.Split(':');
            Assert.Equal(2, parts.Length);
            Assert.True(double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            Assert.True(double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        });
    }

    /// <summary>Linear interpolation across the template, matching how a curve is evaluated.</summary>
    private static double DutyAt(CurveTemplate template, double temperature)
    {
        IReadOnlyList<CurvePoint> points = template.Points;
        if (temperature <= points[0].Input) return points[0].Output;
        for (int index = 1; index < points.Count; index++)
        {
            if (temperature > points[index].Input) continue;
            CurvePoint low = points[index - 1];
            CurvePoint high = points[index];
            double span = high.Input - low.Input;
            double ratio = span <= 0 ? 0 : (temperature - low.Input) / span;
            return low.Output + ((high.Output - low.Output) * ratio);
        }

        return points[^1].Output;
    }
}
