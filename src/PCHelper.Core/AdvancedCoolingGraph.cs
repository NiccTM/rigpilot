using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed record CoolingGraphInput(
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, double> SensorValues,
    IReadOnlySet<string> StaleSensorIds,
    IReadOnlyDictionary<string, FanCalibrationV2> Calibrations);

public sealed record CoolingGraphEvaluation(
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, double> NodeValues,
    IReadOnlyDictionary<string, double> OutputValues,
    bool Emergency,
    string Reason);

public sealed class CoolingGraphEvaluationException(string message) : InvalidOperationException(message);

public static class CoolingGraphValidator
{
    public static IReadOnlyList<string> Validate(CoolingGraphV1 graph)
    {
        List<string> errors = [];
        if (graph.SchemaVersion != CoolingGraphV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported cooling-graph schema {graph.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(graph.Id) || string.IsNullOrWhiteSpace(graph.Name))
        {
            errors.Add("Cooling graph ID and name are required.");
        }

        Dictionary<string, CoolingGraphNodeV1> nodes = new(StringComparer.Ordinal);
        foreach (CoolingGraphNodeV1 node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node))
            {
                errors.Add($"Cooling node ID '{node.Id}' is empty or duplicated.");
                continue;
            }

            ValidateNode(node, errors);
        }

        foreach (CoolingGraphNodeV1 node in graph.Nodes)
        {
            foreach (string input in node.InputNodeIds)
            {
                if (!nodes.ContainsKey(input))
                {
                    errors.Add($"Cooling node '{node.Id}' references missing input '{input}'.");
                }
            }
        }

        DetectCycles(nodes, errors);

        HashSet<string> outputs = new(StringComparer.Ordinal);
        foreach (CoolingGraphOutputV1 output in graph.Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.CapabilityId) || !outputs.Add(output.CapabilityId))
            {
                errors.Add($"Cooling output '{output.CapabilityId}' is empty or duplicated.");
            }

            if (!nodes.ContainsKey(output.SourceNodeId))
            {
                errors.Add($"Cooling output '{output.CapabilityId}' references missing node '{output.SourceNodeId}'.");
            }

            if (!AreFinite(output.Minimum, output.Maximum, output.Offset, output.StepUpPerSecond, output.StepDownPerSecond)
                || output.Minimum < 0
                || output.Maximum <= output.Minimum
                || output.StepUpPerSecond <= 0
                || output.StepDownPerSecond <= 0)
            {
                errors.Add($"Cooling output '{output.CapabilityId}' has invalid limits or slew rates.");
            }

            foreach (CurvePoint band in output.AvoidBands)
            {
                if (!AreFinite(band.Input, band.Output) || band.Input < output.Minimum || band.Output > output.Maximum || band.Input >= band.Output)
                {
                    errors.Add($"Cooling output '{output.CapabilityId}' has invalid avoid band {band.Input}-{band.Output}.");
                }
            }

            if (!AreFinite(output.StopBelowPercent, output.StartPercent, output.StartHoldSeconds)
                || output.StopBelowPercent < 0
                || output.StartPercent < 0
                || output.StartHoldSeconds < 0)
            {
                errors.Add($"Cooling output '{output.CapabilityId}' has invalid start/stop shaping values.");
            }
            else if (output.StopBelowPercent > 0
                && !FanStartStopPolicy.IsStableConfiguration(
                    CoolingGraphEngine.BuildStartStopOptions(output, calibratedRestartFloorPercent: 0, stoppingPermitted: true),
                    out string startStopReason))
            {
                errors.Add($"Cooling output '{output.CapabilityId}': {startStopReason}");
            }
        }

        if (graph.Outputs.Count == 0)
        {
            errors.Add("Cooling graph must define at least one output.");
        }

        return errors;
    }

    private static void ValidateNode(CoolingGraphNodeV1 node, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
        {
            errors.Add($"Cooling node '{node.Id}' requires a name.");
        }

        if (node.Parameters.Values.Any(value => !double.IsFinite(value)))
        {
            errors.Add($"Cooling node '{node.Id}' has a non-finite parameter.");
        }

        int inputs = node.InputNodeIds.Count;
        switch (node.Kind)
        {
            case CoolingNodeKind.Sensor:
            case CoolingNodeKind.FileSensor:
                if (string.IsNullOrWhiteSpace(node.SensorId) || inputs != 0)
                {
                    errors.Add($"{node.Kind} node '{node.Id}' requires one sensor ID and no node inputs.");
                }
                break;
            case CoolingNodeKind.Offset:
            case CoolingNodeKind.TimeAverage:
            case CoolingNodeKind.Linear:
            case CoolingNodeKind.Graph:
            case CoolingNodeKind.Trigger:
            case CoolingNodeKind.Sync:
            case CoolingNodeKind.FeedbackAuto:
                if (inputs != 1)
                {
                    errors.Add($"{node.Kind} node '{node.Id}' requires exactly one input.");
                }
                break;
            case CoolingNodeKind.Mix:
                if (inputs == 0)
                {
                    errors.Add($"Mix node '{node.Id}' requires at least one input.");
                }
                break;
            case CoolingNodeKind.Flat:
                if (inputs != 0 || !Has(node, "value"))
                {
                    errors.Add($"Flat node '{node.Id}' requires a value and no input.");
                }
                break;
            default:
                errors.Add($"Cooling node '{node.Id}' uses an unsupported kind.");
                break;
        }

        if (node.Kind is CoolingNodeKind.Linear or CoolingNodeKind.Graph)
        {
            ValidatePoints(node, errors);
        }

        if (node.Kind == CoolingNodeKind.TimeAverage && (!Has(node, "seconds") || Get(node, "seconds") <= 0))
        {
            errors.Add($"Time-average node '{node.Id}' requires a positive seconds parameter.");
        }

        if (node.Kind == CoolingNodeKind.Trigger
            && (!Has(node, "idleTemperature", "loadTemperature", "idleOutput", "loadOutput", "responseSeconds")
                || Get(node, "idleTemperature") >= Get(node, "loadTemperature")
                || Get(node, "responseSeconds") < 0))
        {
            errors.Add($"Trigger node '{node.Id}' has invalid thresholds or response time.");
        }

        if (node.Kind == CoolingNodeKind.FeedbackAuto
            && (!Has(node, "idleTemperature", "loadTemperature", "minimum", "maximum", "step", "deadband", "responseSeconds")
                || Get(node, "idleTemperature") >= Get(node, "loadTemperature")
                || Get(node, "minimum") < 0
                || Get(node, "maximum") <= Get(node, "minimum")
                || Get(node, "step") <= 0
                || Get(node, "deadband") < 0
                || Get(node, "responseSeconds") <= 0))
        {
            errors.Add($"Feedback-auto node '{node.Id}' has invalid parameters.");
        }
    }

    private static void ValidatePoints(CoolingGraphNodeV1 node, List<string> errors)
    {
        if (node.Points.Count < 2)
        {
            errors.Add($"{node.Kind} node '{node.Id}' requires at least two points.");
            return;
        }

        for (int index = 0; index < node.Points.Count; index++)
        {
            CurvePoint point = node.Points[index];
            if (!AreFinite(point.Input, point.Output)
                || index > 0 && point.Input <= node.Points[index - 1].Input)
            {
                errors.Add($"{node.Kind} node '{node.Id}' points must be finite and strictly increasing.");
                return;
            }
        }
    }

    private static void DetectCycles(Dictionary<string, CoolingGraphNodeV1> nodes, List<string> errors)
    {
        Dictionary<string, int> state = new(StringComparer.Ordinal);
        foreach (string nodeId in nodes.Keys)
        {
            Visit(nodeId);
        }

        void Visit(string nodeId)
        {
            if (state.TryGetValue(nodeId, out int value))
            {
                if (value == 1)
                {
                    errors.Add($"Cooling graph contains a cycle at '{nodeId}'.");
                }
                return;
            }

            state[nodeId] = 1;
            foreach (string input in nodes[nodeId].InputNodeIds.Where(nodes.ContainsKey))
            {
                Visit(input);
            }
            state[nodeId] = 2;
        }
    }

    internal static bool Has(CoolingGraphNodeV1 node, params string[] names) =>
        names.All(node.Parameters.ContainsKey);

    internal static double Get(CoolingGraphNodeV1 node, string name) => node.Parameters[name];

    private static bool AreFinite(params double[] values) => values.All(double.IsFinite);
}

public sealed class CoolingGraphEngine
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FanStartStopState> _startStopStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<TimedValue>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeRuntimeState> _nodeStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimedValue> _outputStates = new(StringComparer.Ordinal);

    public CoolingGraphEvaluation Evaluate(CoolingGraphV1 graph, CoolingGraphInput input)
    {
        lock (_sync)
        {
            IReadOnlyList<string> errors = CoolingGraphValidator.Validate(graph);
            if (errors.Count > 0)
            {
                throw new CoolingGraphEvaluationException(string.Join(" ", errors));
            }

            Dictionary<string, CoolingGraphNodeV1> nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            Dictionary<string, double> values = new(StringComparer.Ordinal);
            foreach (CoolingGraphNodeV1 node in graph.Nodes)
            {
                EvaluateNode(node.Id, nodes, values, input);
            }

            Dictionary<string, double> outputs = new(StringComparer.Ordinal);
            foreach (CoolingGraphOutputV1 output in graph.Outputs)
            {
                double requested = values[output.SourceNodeId];
                input.Calibrations.TryGetValue(output.CapabilityId, out FanCalibrationV2? calibration);
                if (output.Mode == FanOutputMode.Rpm)
                {
                    if (calibration is null)
                    {
                        throw new CoolingGraphEvaluationException($"RPM output '{output.CapabilityId}' has no verified calibration.");
                    }
                    requested = ConvertRpmToDuty(requested, calibration);
                }

                requested = Math.Clamp(requested + output.Offset, output.Minimum, output.Maximum);
                requested = EnforceCalibrationFloor(output, calibration, requested);
                double? previousOutput = _outputStates.TryGetValue(output.CapabilityId, out TimedValue previous)
                    ? previous.Value
                    : null;
                requested = Avoid(requested, output.AvoidBands, previousOutput);
                requested = EnforceCalibrationFloor(output, calibration, requested);
                double slewed = Slew(output, requested, input.Timestamp);
                slewed = EnforceCalibrationFloor(output, calibration, slewed);
                // Start/stop shaping runs last, deliberately. A stop must reach
                // a true 0% — below the calibration floor the earlier stages
                // enforce — and both stopping and the restart boost must be
                // immediate rather than slew-limited, or the boost is defeated
                // by the ramp. Stopping is permitted only where calibration
                // proved this exact fan restarts, so a protected output can
                // never reach it.
                slewed = ApplyStartStop(output, calibration, slewed, input.Timestamp);
                outputs[output.CapabilityId] = slewed;
                _outputStates[output.CapabilityId] = new TimedValue(input.Timestamp, slewed);
            }

            return new CoolingGraphEvaluation(input.Timestamp, values, outputs, false, "Cooling graph evaluated successfully.");
        }
    }

    public CoolingGraphEvaluation EvaluateSafe(CoolingGraphV1 graph, CoolingGraphInput input)
    {
        try
        {
            return Evaluate(graph, input);
        }
        catch (Exception exception) when (exception is CoolingGraphEvaluationException or InvalidOperationException or ArgumentException)
        {
            Dictionary<string, double> emergency = graph.Outputs.ToDictionary(
                output => output.CapabilityId,
                output => output.Maximum,
                StringComparer.Ordinal);
            return new CoolingGraphEvaluation(input.Timestamp, new Dictionary<string, double>(), emergency, true, exception.Message);
        }
    }

    private double EvaluateNode(
        string nodeId,
        IReadOnlyDictionary<string, CoolingGraphNodeV1> nodes,
        Dictionary<string, double> values,
        CoolingGraphInput input)
    {
        if (values.TryGetValue(nodeId, out double cached))
        {
            return cached;
        }

        CoolingGraphNodeV1 node = nodes[nodeId];
        double[] dependencies = node.InputNodeIds.Select(id => EvaluateNode(id, nodes, values, input)).ToArray();
        double value = node.Kind switch
        {
            CoolingNodeKind.Sensor or CoolingNodeKind.FileSensor => ReadSensor(node, input),
            CoolingNodeKind.Offset => dependencies[0] + CoolingGraphValidator.Get(node, "offset"),
            CoolingNodeKind.TimeAverage => Average(node, dependencies[0], input.Timestamp),
            CoolingNodeKind.Mix => Mix(node.MixFunction, dependencies),
            CoolingNodeKind.Linear or CoolingNodeKind.Graph => Curve(node, dependencies[0], input.Timestamp),
            CoolingNodeKind.Trigger => Trigger(node, dependencies[0], input.Timestamp),
            CoolingNodeKind.Flat => CoolingGraphValidator.Get(node, "value"),
            CoolingNodeKind.Sync => Sync(node, dependencies[0]),
            CoolingNodeKind.FeedbackAuto => Feedback(node, dependencies[0], input.Timestamp),
            _ => throw new CoolingGraphEvaluationException($"Cooling node '{node.Id}' is unsupported.")
        };

        if (!double.IsFinite(value))
        {
            throw new CoolingGraphEvaluationException($"Cooling node '{node.Id}' produced a non-finite value.");
        }

        values[nodeId] = value;
        return value;
    }

    private static double ReadSensor(CoolingGraphNodeV1 node, CoolingGraphInput input)
    {
        string sensorId = node.SensorId!;
        if (input.StaleSensorIds.Contains(sensorId))
        {
            throw new CoolingGraphEvaluationException($"Sensor '{sensorId}' is stale.");
        }

        if (!input.SensorValues.TryGetValue(sensorId, out double value) || !double.IsFinite(value))
        {
            throw new CoolingGraphEvaluationException($"Sensor '{sensorId}' is unavailable.");
        }

        return value;
    }

    internal static FanStartStopOptions BuildStartStopOptions(
        CoolingGraphOutputV1 output,
        double calibratedRestartFloorPercent,
        bool stoppingPermitted) => new(
            output.StartPercent,
            TimeSpan.FromSeconds(output.StartHoldSeconds),
            output.StopBelowPercent,
            stoppingPermitted,
            calibratedRestartFloorPercent);

    /// <summary>
    /// Applies zero-RPM idle and kickstart restart. Stopping requires both an
    /// opt-in threshold and physical evidence that this exact fan restarts
    /// (<see cref="FanCalibrationPolicy.SupportsVerifiedFanStop(FanCalibrationV2)"/>);
    /// an output with no such calibration is never stopped, which is what keeps
    /// pumps and CPU fans spinning regardless of configuration.
    /// </summary>
    private double ApplyStartStop(
        CoolingGraphOutputV1 output,
        FanCalibrationV2? calibration,
        double duty,
        DateTimeOffset timestamp)
    {
        bool stoppingPermitted = output.StopBelowPercent > 0
            && calibration is not null
            && FanCalibrationPolicy.SupportsVerifiedFanStop(calibration);
        if (!stoppingPermitted)
        {
            _startStopStates.Remove(output.CapabilityId);
            return duty;
        }

        FanStartStopOptions options = BuildStartStopOptions(
            output,
            calibration!.RestartDutyPercent ?? 0,
            stoppingPermitted: true);
        FanStartStopState state = _startStopStates.TryGetValue(output.CapabilityId, out FanStartStopState? existing)
            ? existing
            : FanStartStopState.Running;
        FanStartStopDecision decision = FanStartStopPolicy.Evaluate(duty, state, options, timestamp);
        _startStopStates[output.CapabilityId] = decision.State;
        return decision.DutyPercent;
    }

    private double Average(CoolingGraphNodeV1 node, double value, DateTimeOffset timestamp)
    {
        double seconds = CoolingGraphValidator.Get(node, "seconds");
        if (!_history.TryGetValue(node.Id, out Queue<TimedValue>? samples))
        {
            samples = new Queue<TimedValue>();
            _history[node.Id] = samples;
        }

        samples.Enqueue(new TimedValue(timestamp, value));
        DateTimeOffset boundary = timestamp - TimeSpan.FromSeconds(seconds);
        while (samples.Count > 1 && samples.Peek().Timestamp < boundary)
        {
            samples.Dequeue();
        }

        return samples.Average(sample => sample.Value);
    }

    private static double Mix(CoolingMixFunction function, double[] values) => function switch
    {
        CoolingMixFunction.Maximum => values.Max(),
        CoolingMixFunction.Minimum => values.Min(),
        CoolingMixFunction.Average => values.Average(),
        CoolingMixFunction.Sum => values.Sum(),
        CoolingMixFunction.Subtract => values.Skip(1).Aggregate(values[0], (current, value) => current - value),
        _ => throw new CoolingGraphEvaluationException("Unknown cooling mix function.")
    };

    private double Trigger(CoolingGraphNodeV1 node, double input, DateTimeOffset timestamp)
    {
        double idleTemperature = CoolingGraphValidator.Get(node, "idleTemperature");
        double loadTemperature = CoolingGraphValidator.Get(node, "loadTemperature");
        double idleOutput = CoolingGraphValidator.Get(node, "idleOutput");
        double loadOutput = CoolingGraphValidator.Get(node, "loadOutput");
        TimeSpan response = TimeSpan.FromSeconds(CoolingGraphValidator.Get(node, "responseSeconds"));
        NodeRuntimeState previous = _nodeStates.GetValueOrDefault(node.Id)
            ?? new NodeRuntimeState(input, idleOutput, timestamp);
        double requested = input >= loadTemperature
            ? loadOutput
            : input <= idleTemperature
                ? idleOutput
                : previous.Output;
        if (requested != previous.Output && timestamp - previous.Timestamp < response)
        {
            requested = previous.Output;
        }

        _nodeStates[node.Id] = new NodeRuntimeState(input, requested, timestamp);
        return requested;
    }

    private double Curve(CoolingGraphNodeV1 node, double input, DateTimeOffset timestamp)
    {
        double requested = Interpolate(node.Points, input);
        if (!_nodeStates.TryGetValue(node.Id, out NodeRuntimeState? previous))
        {
            _nodeStates[node.Id] = new NodeRuntimeState(input, requested, timestamp);
            return requested;
        }

        bool increasing = requested > previous.Output;
        double hysteresis = node.Parameters.GetValueOrDefault(
            increasing ? "hysteresisUp" : "hysteresisDown",
            node.Parameters.GetValueOrDefault("hysteresis", 0));
        double responseSeconds = node.Parameters.GetValueOrDefault(
            increasing ? "responseUpSeconds" : "responseDownSeconds",
            node.Parameters.GetValueOrDefault("responseSeconds", 0));
        bool withinHysteresis = increasing
            ? input < previous.Input + hysteresis
            : input > previous.Input - hysteresis;
        if (withinHysteresis || timestamp - previous.Timestamp < TimeSpan.FromSeconds(responseSeconds))
        {
            requested = previous.Output;
        }
        else
        {
            _nodeStates[node.Id] = new NodeRuntimeState(input, requested, timestamp);
        }

        return requested;
    }

    private static double Sync(CoolingGraphNodeV1 node, double input)
    {
        double offset = node.Parameters.GetValueOrDefault("offset", 0);
        bool proportional = node.Parameters.GetValueOrDefault("proportional", 0) >= 0.5;
        return proportional ? input * (1 + (offset / 100)) : input + offset;
    }

    private double Feedback(CoolingGraphNodeV1 node, double temperature, DateTimeOffset timestamp)
    {
        double idle = CoolingGraphValidator.Get(node, "idleTemperature");
        double load = CoolingGraphValidator.Get(node, "loadTemperature");
        double minimum = CoolingGraphValidator.Get(node, "minimum");
        double maximum = CoolingGraphValidator.Get(node, "maximum");
        double step = CoolingGraphValidator.Get(node, "step");
        double deadband = CoolingGraphValidator.Get(node, "deadband");
        TimeSpan response = TimeSpan.FromSeconds(CoolingGraphValidator.Get(node, "responseSeconds"));
        NodeRuntimeState previous = _nodeStates.GetValueOrDefault(node.Id)
            ?? new NodeRuntimeState(temperature, minimum, timestamp);

        double output;
        if (temperature <= idle)
        {
            output = minimum;
        }
        else if (temperature < load - deadband)
        {
            output = minimum + ((maximum - minimum) * ((temperature - idle) / (load - deadband - idle)));
        }
        else if (timestamp - previous.Timestamp < response)
        {
            output = previous.Output;
        }
        else if (temperature > load + deadband || temperature > previous.Input)
        {
            output = previous.Output + step;
        }
        else if (temperature < previous.Input && timestamp - previous.Timestamp >= response + response)
        {
            output = previous.Output - (step / 2);
        }
        else
        {
            output = previous.Output;
        }

        output = Math.Clamp(output, minimum, maximum);
        _nodeStates[node.Id] = new NodeRuntimeState(temperature, output, timestamp);
        return output;
    }

    private double Slew(CoolingGraphOutputV1 output, double requested, DateTimeOffset timestamp)
    {
        if (!_outputStates.TryGetValue(output.CapabilityId, out TimedValue previous))
        {
            return requested;
        }

        double seconds = Math.Max(0, (timestamp - previous.Timestamp).TotalSeconds);
        double maximumDelta = requested >= previous.Value
            ? output.StepUpPerSecond * seconds
            : output.StepDownPerSecond * seconds;
        return Math.Clamp(requested, previous.Value - maximumDelta, previous.Value + maximumDelta);
    }

    private static double EnforceCalibrationFloor(
        CoolingGraphOutputV1 output,
        FanCalibrationV2? calibration,
        double requested)
    {
        if (calibration is null)
        {
            return requested;
        }

        if (output.Maximum + 1e-6 < calibration.MinimumDutyPercent)
        {
            throw new CoolingGraphEvaluationException(
                $"Cooling output '{output.CapabilityId}' caps below the measured safe floor of {calibration.MinimumDutyPercent:0.#}%.");
        }

        double safe = FanCalibrationPolicy.EnforceSafeDuty(requested, calibration);
        return Math.Clamp(safe, output.Minimum, output.Maximum);
    }

    private static double Avoid(double requested, IReadOnlyList<CurvePoint> bands, double? previous)
    {
        foreach (CurvePoint band in bands.OrderBy(item => item.Input))
        {
            if (requested <= band.Input || requested >= band.Output)
            {
                continue;
            }

            if (previous.HasValue)
            {
                return previous.Value <= band.Input ? band.Input : band.Output;
            }

            return requested - band.Input <= band.Output - requested ? band.Input : band.Output;
        }

        return requested;
    }

    private static double ConvertRpmToDuty(double requestedRpm, FanCalibrationV2 calibration)
    {
        CurvePoint[] points = calibration.Measurements
            .Where(point => point.Rpm > 0 && double.IsFinite(point.Rpm) && double.IsFinite(point.DutyPercent))
            .Select(point => new CurvePoint(point.Rpm, point.DutyPercent))
            .GroupBy(point => point.Input)
            .Select(group => group.OrderBy(point => point.Output).First())
            .OrderBy(point => point.Input)
            .ToArray();
        if (points.Length < 2)
        {
            throw new CoolingGraphEvaluationException($"Calibration for '{calibration.CapabilityId}' has insufficient RPM points.");
        }

        return Interpolate(points, requestedRpm);
    }

    private static double Interpolate(IReadOnlyList<CurvePoint> points, double input)
    {
        if (input <= points[0].Input)
        {
            return points[0].Output;
        }

        if (input >= points[^1].Input)
        {
            return points[^1].Output;
        }

        for (int index = 1; index < points.Count; index++)
        {
            CurvePoint upper = points[index];
            if (input > upper.Input)
            {
                continue;
            }

            CurvePoint lower = points[index - 1];
            double ratio = (input - lower.Input) / (upper.Input - lower.Input);
            return lower.Output + ((upper.Output - lower.Output) * ratio);
        }

        return points[^1].Output;
    }

    private sealed record NodeRuntimeState(double Input, double Output, DateTimeOffset Timestamp);
    private readonly record struct TimedValue(DateTimeOffset Timestamp, double Value);
}
