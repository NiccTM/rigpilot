using System.Text;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Creates the conservative first curve after one exact output has completed
/// commissioning. It intentionally does not create a zero-RPM point: even a
/// stop-qualified fan should require an explicit user edit before fan stop is
/// part of a normal profile.
/// </summary>
public static class AdaptiveCoolingProfileFactory
{
    public static AdaptiveCoolingProfileDraft Create(
        CapabilityDescriptor output,
        FanCalibrationResult calibration,
        string headerName,
        IReadOnlyList<SensorSample> samples)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(calibration);
        return CreateCore(
            output,
            calibration.MinimumDutyPercent,
            FanCalibrationPolicy.SupportsNonZeroCurve(calibration),
            headerName,
            samples);
    }

    /// <summary>
    /// Reuses the persisted, exact-session calibration after an app or service
    /// restart. The caller must still bind it to the completed commissioning
    /// session before requesting a profile.
    /// </summary>
    public static AdaptiveCoolingProfileDraft Create(
        CapabilityDescriptor output,
        FanCalibrationV2 calibration,
        string headerName,
        IReadOnlyList<SensorSample> samples)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(calibration);
        return CreateCore(
            output,
            calibration.MinimumDutyPercent,
            FanCalibrationPolicy.SupportsNonZeroCurve(calibration),
            headerName,
            samples);
    }

    /// <summary>
    /// One-click "Automatic mode": a conservative temperature→duty curve over
    /// one or more writable cooling outputs that have NOT been calibrated.
    /// Safety comes from construction instead of measurement: every output's
    /// floor is the deterministic 50% safety floor (the established policy for
    /// unverified fans — no fan stalls at half duty), the curve always reaches
    /// the controller maximum for emergency headroom, and the graph runtime's
    /// stale-sensor maximum-cooling behaviour applies unchanged. Pump and
    /// CPU-fan role protections are enforced by the service on save and apply.
    /// </summary>
    public static AdaptiveCoolingProfileDraft CreateAutomaticMode(
        IReadOnlyList<CapabilityDescriptor> outputs,
        string name,
        IReadOnlyList<SensorSample> samples,
        bool preferGpuSourceOnly = false)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(samples);
        if (outputs.Count == 0)
        {
            throw new InvalidOperationException("Automatic mode needs at least one writable cooling output.");
        }

        SensorSample[] temperatureSources = SelectTemperatureSources(samples);
        if (preferGpuSourceOnly)
        {
            SensorSample[] gpuOnly = [.. temperatureSources.Where(sample =>
                ContainsAny(Identity(sample), "gpu", "hot spot", "hotspot", "junction", "geforce", "radeon", "arc", "nvidia"))];
            if (gpuOnly.Length > 0)
            {
                temperatureSources = gpuOnly;
            }
        }
        if (temperatureSources.Length == 0)
        {
            throw new InvalidOperationException("No current CPU or GPU temperature source is available for automatic mode.");
        }

        string token = ToToken(name);
        List<CoolingGraphNodeV1> nodes = [];
        List<string> sourceNodeIds = [];
        foreach ((SensorSample sample, int index) in temperatureSources.Select((sample, index) => (sample, index)))
        {
            string nodeId = $"source-{index + 1}";
            sourceNodeIds.Add(nodeId);
            nodes.Add(new CoolingGraphNodeV1(
                nodeId, sample.Name, CoolingNodeKind.Sensor, [], sample.SensorId, [],
                new Dictionary<string, double>(), null, CoolingMixFunction.Maximum));
        }

        string mixedSource = sourceNodeIds[0];
        if (sourceNodeIds.Count > 1)
        {
            mixedSource = "mixed-temperature";
            nodes.Add(new CoolingGraphNodeV1(
                mixedSource, "Maximum temperature", CoolingNodeKind.Mix, sourceNodeIds, null, [],
                new Dictionary<string, double>(), null, CoolingMixFunction.Maximum));
        }

        List<CoolingGraphOutputV1> graphOutputs = [];
        bool anyExperimental = false;
        foreach (CapabilityDescriptor output in outputs)
        {
            if (output.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety)
                || output.ValueKind != ControlValueKind.Numeric
                || output.Range is not NumericRange range)
            {
                throw new InvalidOperationException($"'{output.Name}' is not a numeric cooling control with bounds.");
            }

            double floor = Math.Max(range.Minimum, ConservativeFloorDutyPercent);
            if (floor >= range.Maximum)
            {
                throw new InvalidOperationException($"'{output.Name}' leaves no dynamic range above the {ConservativeFloorDutyPercent:0}% safety floor.");
            }

            string curveNodeId = $"auto-curve-{graphOutputs.Count + 1}";
            double quietDuty = Math.Max(floor, Math.Min(range.Maximum, 55));
            double loadDuty = Math.Max(floor, Math.Min(range.Maximum, 75));
            nodes.Add(new CoolingGraphNodeV1(
                curveNodeId, $"{output.Name} automatic curve", CoolingNodeKind.Graph, [mixedSource], null,
                [
                    new CurvePoint(40, floor),
                    new CurvePoint(60, quietDuty),
                    new CurvePoint(75, loadDuty),
                    new CurvePoint(85, range.Maximum)
                ],
                new Dictionary<string, double>
                {
                    ["hysteresisUp"] = 1,
                    ["hysteresisDown"] = 2,
                    ["responseUpSeconds"] = 1,
                    ["responseDownSeconds"] = 5
                }));
            graphOutputs.Add(new CoolingGraphOutputV1(
                output.Id, curveNodeId, FanOutputMode.DutyPercent,
                floor, range.Maximum, 0, StepUpPerSecond: 20, StepDownPerSecond: 8, AvoidBands: []));
            anyExperimental |= output.State == CapabilityAccessState.Experimental;
        }

        CoolingGraphV1 graph = new(
            CoolingGraphV1.CurrentSchemaVersion,
            $"auto.cooling.{token}",
            $"{name} automatic mode",
            nodes,
            graphOutputs);
        ProfileV2 profile = new(
            ProfileV2.CurrentSchemaVersion,
            $"auto.profile.{token}",
            $"{name} automatic mode",
            $"Conservative temperature curve with a {ConservativeFloorDutyPercent:0}% duty floor on every output; stale sensors command maximum cooling.",
            [],
            new SafetyLimits(),
            graph.Id,
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: [],
            AutomationReferences: [],
            IsBuiltIn: false,
            IsExperimental: anyExperimental);
        return new AdaptiveCoolingProfileDraft(graph, profile, [.. temperatureSources.Select(sample => sample.SensorId)]);
    }

    /// <summary>The deterministic duty floor applied to uncalibrated automatic-mode outputs.</summary>
    public const double ConservativeFloorDutyPercent = 50;

    private static AdaptiveCoolingProfileDraft CreateCore(
        CapabilityDescriptor output,
        double calibratedMinimumDutyPercent,
        bool supportsNonZeroCurve,
        string headerName,
        IReadOnlyList<SensorSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        ArgumentNullException.ThrowIfNull(samples);
        if (output.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety)
            || output.ValueKind != ControlValueKind.Numeric)
        {
            throw new InvalidOperationException("The selected output is not a numeric cooling control.");
        }
        if (!supportsNonZeroCurve)
        {
            throw new InvalidOperationException("A stable measured non-zero operating floor is required before creating an adaptive curve.");
        }
        if (output.Range is not NumericRange range)
        {
            throw new InvalidOperationException("The selected output no longer exposes numeric bounds.");
        }

        double floor = Math.Clamp(calibratedMinimumDutyPercent, range.Minimum, range.Maximum);
        if (floor <= 0 || floor >= range.Maximum)
        {
            throw new InvalidOperationException("The measured operating floor leaves no usable dynamic range in the current controller bounds.");
        }

        SensorSample[] temperatureSources = SelectTemperatureSources(samples);
        if (temperatureSources.Length == 0)
        {
            throw new InvalidOperationException("No current CPU or GPU temperature source is available for an adaptive cooling curve.");
        }

        string token = ToToken(output.Id);
        string graphId = $"adaptive.cooling.{token}";
        string profileId = $"adaptive.profile.{token}";
        List<CoolingGraphNodeV1> nodes = [];
        List<string> sourceNodeIds = [];
        foreach ((SensorSample sample, int index) in temperatureSources.Select((sample, index) => (sample, index)))
        {
            string nodeId = $"source-{index + 1}";
            sourceNodeIds.Add(nodeId);
            nodes.Add(new CoolingGraphNodeV1(
                nodeId,
                sample.Name,
                CoolingNodeKind.Sensor,
                [],
                sample.SensorId,
                [],
                new Dictionary<string, double>(),
                null,
                CoolingMixFunction.Maximum));
        }

        string mixedSource = sourceNodeIds[0];
        if (sourceNodeIds.Count > 1)
        {
            mixedSource = "mixed-temperature";
            nodes.Add(new CoolingGraphNodeV1(
                mixedSource,
                "Maximum CPU/GPU temperature",
                CoolingNodeKind.Mix,
                sourceNodeIds,
                null,
                [],
                new Dictionary<string, double>(),
                null,
                CoolingMixFunction.Maximum));
        }

        double quietDuty = Math.Max(floor, Math.Min(range.Maximum, 40));
        double loadDuty = Math.Max(floor, Math.Min(range.Maximum, 70));
        nodes.Add(new CoolingGraphNodeV1(
            "adaptive-curve",
            "Adaptive curve",
            CoolingNodeKind.Graph,
            [mixedSource],
            null,
            [
                new CurvePoint(35, floor),
                new CurvePoint(50, quietDuty),
                new CurvePoint(70, loadDuty),
                new CurvePoint(85, range.Maximum)
            ],
            new Dictionary<string, double>
            {
                ["hysteresisUp"] = 1,
                ["hysteresisDown"] = 2,
                ["responseUpSeconds"] = 1,
                ["responseDownSeconds"] = 5
            }));

        CoolingGraphV1 graph = new(
            CoolingGraphV1.CurrentSchemaVersion,
            graphId,
            $"{headerName} adaptive cooling",
            nodes,
            [new CoolingGraphOutputV1(
                output.Id,
                "adaptive-curve",
                FanOutputMode.DutyPercent,
                floor,
                range.Maximum,
                0,
                StepUpPerSecond: 20,
                StepDownPerSecond: 8,
                AvoidBands: [])]);
        ProfileV2 profile = new(
            ProfileV2.CurrentSchemaVersion,
            profileId,
            $"{headerName} adaptive cooling",
            $"Conservative CPU/GPU mixed curve using the measured {floor:0}% nonzero floor for this exact output.",
            [],
            new SafetyLimits(),
            graph.Id,
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: [],
            AutomationReferences: [],
            IsBuiltIn: false,
            IsExperimental: output.State == CapabilityAccessState.Experimental);
        return new AdaptiveCoolingProfileDraft(graph, profile, temperatureSources.Select(sample => sample.SensorId).ToArray());
    }

    private static SensorSample[] SelectTemperatureSources(IReadOnlyList<SensorSample> samples)
    {
        SensorSample[] temperatures = samples
            .Where(sample => sample.Quality == SensorQuality.Good
                && sample.Value is double value
                && double.IsFinite(value)
                && IsTemperatureUnit(sample.Unit))
            .ToArray();
        SensorSample? cpu = temperatures
            .Where(sample => ContainsAny(Identity(sample), "cpu", "core", "package", "tctl", "tdie", "zen", "intel"))
            .OrderByDescending(sample => Score(Identity(sample), "cpu", "package", "tctl", "tdie", "zen", "intel"))
            .FirstOrDefault();
        SensorSample? gpu = temperatures
            .Where(sample => ContainsAny(Identity(sample), "gpu", "hot spot", "hotspot", "junction", "geforce", "radeon", "arc", "nvidia", "amd"))
            .OrderByDescending(sample => Score(Identity(sample), "gpu", "hot spot", "hotspot", "junction", "geforce", "radeon", "arc", "nvidia", "amd"))
            .FirstOrDefault();
        return new SensorSample?[] { cpu, gpu }
            .OfType<SensorSample>()
            .DistinctBy(sample => sample.SensorId, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
    }

    private static bool IsTemperatureUnit(string unit) =>
        unit.Contains('C')
        || unit.Contains('c')
        || unit.Contains('\u00B0');

    private static string Identity(SensorSample sample) => $"{sample.Name} {sample.DeviceId} {sample.AdapterId}";

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static int Score(string value, params string[] tokens) =>
        tokens.Count(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string ToToken(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        string token = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(token) ? "fan" : token;
    }
}

public sealed record AdaptiveCoolingProfileDraft(
    CoolingGraphV1 Graph,
    ProfileV2 Profile,
    IReadOnlyList<string> SourceSensorIds);
