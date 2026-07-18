using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed record CoolingSafetyDecision(
    CoolingGraphEvaluation Evaluation,
    CoolingRuntimeState State,
    DateTimeOffset? EmergencySince,
    string Reason,
    bool RecoveredThisTick);

/// <summary>
/// Latches a cooling graph at its maximum outputs when telemetry is stale or
/// a bound temperature reaches the fixed safety ceiling. Normal output resumes
/// only after a bounded run of clean samples; one transient good sample cannot
/// drop fan duty immediately after an emergency.
/// </summary>
public sealed class CoolingSafetySupervisor
{
    private bool _emergencyLatched;
    private int _consecutiveHealthyPolls;
    private DateTimeOffset? _emergencySince;
    private string _emergencyReason = string.Empty;

    public DateTimeOffset? EmergencySince => _emergencySince;

    public CoolingSafetyDecision Evaluate(
        CoolingGraphV1 graph,
        CoolingGraphRuntimeTick tick,
        IReadOnlyList<SensorSample> samples,
        SafetyLimits limits,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(limits);

        string? emergency = tick.Evaluation.Emergency
            ? tick.Evaluation.Reason
            : FindCriticalTemperature(graph, samples, limits.FallbackCriticalTemperatureCelsius, timestamp);
        if (!string.IsNullOrWhiteSpace(emergency))
        {
            return EnterEmergency(graph, timestamp, emergency);
        }

        if (_emergencyLatched)
        {
            _consecutiveHealthyPolls++;
            int requiredHealthyPolls = Math.Max(2, limits.StalePollLimit);
            if (_consecutiveHealthyPolls < requiredHealthyPolls)
            {
                return Maximum(
                    graph,
                    timestamp,
                    $"Maximum cooling remains latched after '{_emergencyReason}'; {_consecutiveHealthyPolls}/{requiredHealthyPolls} clean recovery polls observed.");
            }

            _emergencyLatched = false;
            _consecutiveHealthyPolls = 0;
            DateTimeOffset? previousEmergency = _emergencySince;
            _emergencySince = null;
            _emergencyReason = string.Empty;
            return new CoolingSafetyDecision(
                tick.Evaluation,
                tick.HeldSensorIds.Count > 0 ? CoolingRuntimeState.SensorHold : CoolingRuntimeState.Normal,
                previousEmergency,
                "Cooling telemetry recovered across the required clean-poll window; the verified graph resumed.",
                RecoveredThisTick: true);
        }

        return new CoolingSafetyDecision(
            tick.Evaluation,
            tick.HeldSensorIds.Count > 0 ? CoolingRuntimeState.SensorHold : CoolingRuntimeState.Normal,
            null,
            tick.HeldSensorIds.Count > 0
                ? $"Holding the last good value for {tick.HeldSensorIds.Count} cooling sensor(s) within the stale-poll grace window."
                : tick.Evaluation.Reason,
            RecoveredThisTick: false);
    }

    public CoolingSafetyDecision ForceEmergency(
        CoolingGraphV1 graph,
        DateTimeOffset timestamp,
        string reason) => EnterEmergency(graph, timestamp, reason);

    private CoolingSafetyDecision EnterEmergency(
        CoolingGraphV1 graph,
        DateTimeOffset timestamp,
        string reason)
    {
        if (!_emergencyLatched)
        {
            _emergencySince = timestamp;
        }

        _emergencyLatched = true;
        _consecutiveHealthyPolls = 0;
        _emergencyReason = reason;
        return Maximum(graph, timestamp, reason);
    }

    private CoolingSafetyDecision Maximum(
        CoolingGraphV1 graph,
        DateTimeOffset timestamp,
        string reason)
    {
        Dictionary<string, double> outputs = graph.Outputs.ToDictionary(
            output => output.CapabilityId,
            output => output.Maximum,
            StringComparer.Ordinal);
        CoolingGraphEvaluation maximum = new(
            timestamp,
            new Dictionary<string, double>(),
            outputs,
            Emergency: true,
            reason);
        return new CoolingSafetyDecision(
            maximum,
            CoolingRuntimeState.EmergencyMaximum,
            _emergencySince,
            reason,
            RecoveredThisTick: false);
    }

    private static string? FindCriticalTemperature(
        CoolingGraphV1 graph,
        IReadOnlyList<SensorSample> samples,
        double ceilingCelsius,
        DateTimeOffset timestamp)
    {
        if (!double.IsFinite(ceilingCelsius) || ceilingCelsius is < 40 or > 120)
        {
            return "The active cooling profile supplied an invalid critical-temperature ceiling.";
        }

        HashSet<string> graphSensors = graph.Nodes
            .Where(node => node.Kind is CoolingNodeKind.Sensor or CoolingNodeKind.FileSensor)
            .Select(node => node.SensorId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        SensorSample? critical = samples
            .Where(sample => graphSensors.Contains(sample.SensorId)
                && sample.Quality == SensorQuality.Good
                && sample.Value is double value
                && double.IsFinite(value)
                && IsTemperatureUnit(sample.Unit)
                && timestamp - sample.Timestamp <= TimeSpan.FromSeconds(3)
                && sample.Timestamp - timestamp <= TimeSpan.FromSeconds(1)
                && value >= ceilingCelsius)
            .OrderByDescending(sample => sample.Value)
            .FirstOrDefault();
        return critical?.Value is double observed
            ? $"Critical temperature reached: {critical.Name} was {observed:0.0} °C (ceiling {ceilingCelsius:0.0} °C)."
            : null;
    }

    private static bool IsTemperatureUnit(string unit) =>
        string.Equals(unit, "°C", StringComparison.OrdinalIgnoreCase)
        || string.Equals(unit, "C", StringComparison.OrdinalIgnoreCase)
        || string.Equals(unit, "Celsius", StringComparison.OrdinalIgnoreCase);
}
