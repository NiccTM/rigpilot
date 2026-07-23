using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.App;

/// <summary>A draggable point on the interactive fan-curve canvas.</summary>
public sealed record CurveHandleDisplay(double CenterX, double CenterY, double Left, double Top, int Index);

public sealed partial class MainViewModel
{
    // The interactive editor and the read-only preview share one coordinate
    // system so a handle sits exactly on the drawn line. These must match the
    // constants in CustomCoolingCurvePreviewGeometry.
    private const double CurveCanvasWidth = 320;
    private const double CurveCanvasTop = 12;
    private const double CurveCanvasHeight = 96;
    private const double CurveMaxTemp = 110;
    private const double CurveHandleRadius = 7;
    private const int CurveMaxPoints = 8;
    private const int CurveMinPoints = 2;

    /// <summary>
    /// Canvas-space handles for every curve point, so the editor can draw a
    /// grabbable dot on each and hit-test drags. Recomputed with the preview.
    /// </summary>
    public IReadOnlyList<CurveHandleDisplay> CustomCoolingCurveHandles
    {
        get
        {
            if (!TryReadCustomCoolingCurve(out CustomCoolingCurveDefinition? definition, out _))
            {
                return [];
            }

            (double minimumDuty, double maximumDuty) = GetCustomCoolingCurveDutyRange();
            List<CurveHandleDisplay> handles = [];
            int index = 0;
            foreach (CurvePoint point in definition!.Points)
            {
                double x = CurveTemperatureToCanvasX(point.Input);
                double y = CurveDutyToCanvasY(point.Output, minimumDuty, maximumDuty);
                handles.Add(new CurveHandleDisplay(x, y, x - CurveHandleRadius, y - CurveHandleRadius, index++));
            }

            return handles;
        }
    }

    private static double CurveTemperatureToCanvasX(double temperature) =>
        Math.Clamp(temperature / CurveMaxTemp, 0, 1) * CurveCanvasWidth;

    private static double CurveDutyToCanvasY(double duty, double minimumDuty, double maximumDuty)
    {
        double ratio = maximumDuty > minimumDuty
            ? Math.Clamp((duty - minimumDuty) / (maximumDuty - minimumDuty), 0, 1)
            : 0.5;
        return CurveCanvasTop + ((1 - ratio) * CurveCanvasHeight);
    }

    private static double CurveCanvasXToTemperature(double x) =>
        Math.Clamp(x / CurveCanvasWidth, 0, 1) * CurveMaxTemp;

    private static double CurveCanvasYToDuty(double y, double minimumDuty, double maximumDuty)
    {
        double ratio = 1 - Math.Clamp((y - CurveCanvasTop) / CurveCanvasHeight, 0, 1);
        return minimumDuty + (ratio * (maximumDuty - minimumDuty));
    }

    /// <summary>Nearest handle index within grab distance of the canvas point, or -1.</summary>
    public int HitTestCurveHandle(double canvasX, double canvasY)
    {
        const double grabRadiusSquared = 15 * 15;
        int best = -1;
        double bestDistance = grabRadiusSquared;
        foreach (CurveHandleDisplay handle in CustomCoolingCurveHandles)
        {
            double dx = handle.CenterX - canvasX;
            double dy = handle.CenterY - canvasY;
            double distance = (dx * dx) + (dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = handle.Index;
            }
        }

        return best;
    }

    /// <summary>Drags a point, constrained between its neighbours so the order and index stay stable.</summary>
    public void MoveCurvePoint(int index, double canvasX, double canvasY)
    {
        List<(double Temperature, double Duty)> points = ParseCurvePointsForEdit();
        if (index < 0 || index >= points.Count)
        {
            return;
        }

        (double minimumDuty, double maximumDuty) = GetCustomCoolingCurveDutyRange();
        double lower = index > 0 ? points[index - 1].Temperature + 1 : 0;
        double upper = index < points.Count - 1 ? points[index + 1].Temperature - 1 : CurveMaxTemp;
        if (upper < lower)
        {
            upper = lower;
        }

        double temperature = Math.Clamp(CurveCanvasXToTemperature(canvasX), lower, upper);
        double duty = CurveCanvasYToDuty(canvasY, minimumDuty, maximumDuty);
        points[index] = (temperature, duty);
        WriteCurvePoints(points);
    }

    /// <summary>Adds a point where the user clicked empty canvas, up to the eight-point cap.</summary>
    public void AddCurvePointAt(double canvasX, double canvasY)
    {
        List<(double Temperature, double Duty)> points = ParseCurvePointsForEdit();
        if (points.Count >= CurveMaxPoints)
        {
            ShowNotice($"A fan curve can have at most {CurveMaxPoints} points.", "Warning");
            return;
        }

        (double minimumDuty, double maximumDuty) = GetCustomCoolingCurveDutyRange();
        points.Add((CurveCanvasXToTemperature(canvasX), CurveCanvasYToDuty(canvasY, minimumDuty, maximumDuty)));
        WriteCurvePoints(points);
    }

    /// <summary>Removes a point on right-click, keeping at least two.</summary>
    public void RemoveCurvePoint(int index)
    {
        List<(double Temperature, double Duty)> points = ParseCurvePointsForEdit();
        if (index < 0 || index >= points.Count)
        {
            return;
        }

        if (points.Count <= CurveMinPoints)
        {
            ShowNotice($"A fan curve needs at least {CurveMinPoints} points.", "Warning");
            return;
        }

        points.RemoveAt(index);
        WriteCurvePoints(points);
    }

    private List<(double Temperature, double Duty)> ParseCurvePointsForEdit()
    {
        List<(double, double)> points = [];
        foreach (string line in CustomCoolingCurvePoints
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double duty))
            {
                points.Add((temperature, duty));
            }
        }

        return points;
    }

    private void WriteCurvePoints(List<(double Temperature, double Duty)> points)
    {
        (double minimumDuty, double maximumDuty) = GetCustomCoolingCurveDutyRange();
        // Setting the text property runs the existing validation/preview refresh,
        // so the drag editor and the text box stay in lock-step.
        CustomCoolingCurvePoints = string.Join(
            Environment.NewLine,
            points
                .OrderBy(point => point.Temperature)
                .Select(point => (
                    Temperature: Math.Clamp(Math.Round(point.Temperature), 0, CurveMaxTemp),
                    Duty: Math.Clamp(Math.Round(point.Duty), minimumDuty, maximumDuty)))
                .Select(point => $"{point.Temperature:0}:{point.Duty:0}"));
    }
}
