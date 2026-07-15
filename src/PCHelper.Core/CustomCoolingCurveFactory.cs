using System.Text;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// The bounded, user-authored part of a temperature-to-duty cooling curve.
/// The output floor and maximum are deliberately not user configurable: they
/// come from the exact controller bounds and its completed calibration.
/// </summary>
public sealed record CustomCoolingCurveDefinition(
    string Name,
    IReadOnlyList<CurvePoint> Points,
    double HysteresisUp,
    double HysteresisDown,
    double ResponseUpSeconds,
    double ResponseDownSeconds);

/// <summary>
/// Builds a manual Fan Control-style graph only after the existing adaptive
/// factory has established the exact output's calibrated non-zero floor and
/// CPU/GPU temperature sources. The resulting graph/profile is inactive;
/// callers must use the normal profile transaction to apply it.
/// </summary>
public static class CustomCoolingCurveFactory
{
    public static AdaptiveCoolingProfileDraft Create(
        CapabilityDescriptor output,
        FanCalibrationResult calibration,
        string headerName,
        IReadOnlyList<SensorSample> samples,
        CustomCoolingCurveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Build(
            AdaptiveCoolingProfileFactory.Create(output, calibration, headerName, samples),
            headerName,
            definition);
    }

    public static AdaptiveCoolingProfileDraft Create(
        CapabilityDescriptor output,
        FanCalibrationV2 calibration,
        string headerName,
        IReadOnlyList<SensorSample> samples,
        CustomCoolingCurveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Build(
            AdaptiveCoolingProfileFactory.Create(output, calibration, headerName, samples),
            headerName,
            definition);
    }

    private static AdaptiveCoolingProfileDraft Build(
        AdaptiveCoolingProfileDraft baseline,
        string headerName,
        CustomCoolingCurveDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        CoolingGraphOutputV1 baselineOutput = baseline.Graph.Outputs.Single();
        Validate(definition, baselineOutput);

        string name = definition.Name.Trim();
        string outputToken = ToToken(baselineOutput.CapabilityId);
        string nameToken = ToToken(name);
        string graphId = $"custom.cooling.{outputToken}.{nameToken}";
        string profileId = $"custom.profile.{outputToken}.{nameToken}";
        const string curveNodeId = "custom-curve";
        CoolingGraphNodeV1 customCurve = new(
            curveNodeId,
            name,
            CoolingNodeKind.Graph,
            baseline.Graph.Nodes.Single(node => node.Id == "adaptive-curve").InputNodeIds,
            SensorId: null,
            definition.Points.ToArray(),
            new Dictionary<string, double>
            {
                ["hysteresisUp"] = definition.HysteresisUp,
                ["hysteresisDown"] = definition.HysteresisDown,
                ["responseUpSeconds"] = definition.ResponseUpSeconds,
                ["responseDownSeconds"] = definition.ResponseDownSeconds
            });
        CoolingGraphNodeV1[] nodes = baseline.Graph.Nodes
            .Where(node => node.Id != "adaptive-curve")
            .Append(customCurve)
            .ToArray();
        CoolingGraphV1 graph = baseline.Graph with
        {
            Id = graphId,
            Name = $"{headerName} {name}",
            Nodes = nodes,
            Outputs = baseline.Graph.Outputs
                .Select(output => output with { SourceNodeId = curveNodeId })
                .ToArray()
        };
        IReadOnlyList<string> graphErrors = CoolingGraphValidator.Validate(graph);
        if (graphErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", graphErrors));
        }

        ProfileV2 profile = baseline.Profile with
        {
            Id = profileId,
            Name = $"{headerName} {name}",
            Description = $"Manual calibration-bound cooling curve using the measured {baselineOutput.Minimum:0}% nonzero floor for this exact output.",
            CoolingGraphId = graphId
        };
        return new AdaptiveCoolingProfileDraft(graph, profile, baseline.SourceSensorIds);
    }

    private static void Validate(CustomCoolingCurveDefinition definition, CoolingGraphOutputV1 output)
    {
        string name = definition.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 48)
        {
            throw new InvalidOperationException("Curve name must contain 1 to 48 characters.");
        }
        if (definition.Points is null || definition.Points.Count is < 2 or > 8)
        {
            throw new InvalidOperationException("Provide between two and eight temperature:duty points.");
        }
        if (!InRange(definition.HysteresisUp, 0, 10)
            || !InRange(definition.HysteresisDown, 0, 10)
            || !InRange(definition.ResponseUpSeconds, 0, 60)
            || !InRange(definition.ResponseDownSeconds, 0, 60))
        {
            throw new InvalidOperationException("Hysteresis must be 0-10 °C and response time must be 0-60 seconds.");
        }

        CurvePoint? previous = null;
        foreach (CurvePoint point in definition.Points)
        {
            if (!double.IsFinite(point.Input) || !double.IsFinite(point.Output)
                || point.Input < 0 || point.Input > 110)
            {
                throw new InvalidOperationException("Curve temperatures must be finite values from 0 to 110 °C.");
            }
            if (point.Output < output.Minimum || point.Output > output.Maximum)
            {
                throw new InvalidOperationException(
                    $"Each duty value must remain within the calibrated {output.Minimum:0}-{output.Maximum:0}% controller range.");
            }
            if (previous is not null && point.Input <= previous.Input)
            {
                throw new InvalidOperationException("Curve temperatures must be strictly increasing.");
            }
            previous = point;
        }

        CurvePoint finalPoint = definition.Points[^1];
        if (Math.Abs(finalPoint.Output - output.Maximum) > 0.001)
        {
            throw new InvalidOperationException(
                $"The final curve point must reach the controller maximum of {output.Maximum:0}% for a safe thermal ceiling.");
        }
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static string ToToken(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        string token = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(token) ? "curve" : token[..Math.Min(40, token.Length)];
    }
}
