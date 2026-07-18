using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Stateful sensor front-end for a cooling graph. It holds the last good
/// sensor value for a bounded number of missed polls, then deliberately lets
/// the graph enter its max-cooling emergency path. A missing first sample is
/// never treated as safe.
/// </summary>
public sealed class CoolingGraphRuntime
{
    private readonly CoolingGraphEngine _engine = new();
    private readonly Dictionary<string, double> _lastGoodValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stalePollCounts = new(StringComparer.Ordinal);

    public CoolingGraphRuntimeTick Evaluate(
        CoolingGraphV1 graph,
        IReadOnlyList<SensorSample> samples,
        IReadOnlyDictionary<string, FanCalibrationV2> calibrations,
        int stalePollLimit,
        DateTimeOffset timestamp,
        TimeSpan? maximumSampleAge = null)
    {
        if (stalePollLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stalePollLimit), "The stale poll limit must be at least one.");
        }

        TimeSpan sampleAgeLimit = maximumSampleAge ?? TimeSpan.FromSeconds(3);
        if (sampleAgeLimit <= TimeSpan.Zero || sampleAgeLimit > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleAge), "Cooling sensor age must be greater than zero and at most 30 seconds.");
        }

        Dictionary<string, SensorSample> latest = samples
            .GroupBy(sample => sample.SensorId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(sample => sample.Timestamp).First(),
                StringComparer.Ordinal);
        HashSet<string> sensorIds = graph.Nodes
            .Where(node => node.Kind is CoolingNodeKind.Sensor or CoolingNodeKind.FileSensor)
            .Select(node => node.SensorId)
            .Where(sensorId => !string.IsNullOrWhiteSpace(sensorId))
            .Select(sensorId => sensorId!)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        HashSet<string> stale = new(StringComparer.Ordinal);
        HashSet<string> held = new(StringComparer.Ordinal);

        foreach (string sensorId in sensorIds)
        {
            if (latest.TryGetValue(sensorId, out SensorSample? sample)
                && sample.Quality == SensorQuality.Good
                && sample.Value is double value
                && double.IsFinite(value)
                && timestamp - sample.Timestamp <= sampleAgeLimit
                && sample.Timestamp - timestamp <= TimeSpan.FromSeconds(1))
            {
                _lastGoodValues[sensorId] = value;
                _stalePollCounts.Remove(sensorId);
                values[sensorId] = value;
                continue;
            }

            int stalePolls = _stalePollCounts.GetValueOrDefault(sensorId) + 1;
            _stalePollCounts[sensorId] = stalePolls;
            if (stalePolls < stalePollLimit && _lastGoodValues.TryGetValue(sensorId, out double previous))
            {
                values[sensorId] = previous;
                held.Add(sensorId);
                continue;
            }

            // No historical value is a safety failure immediately. With a
            // history, the configured count has now elapsed.
            stale.Add(sensorId);
        }

        foreach (string staleId in _stalePollCounts.Keys.Except(sensorIds).ToArray())
        {
            _stalePollCounts.Remove(staleId);
            _lastGoodValues.Remove(staleId);
        }

        CoolingGraphEvaluation evaluation = _engine.EvaluateSafe(
            graph,
            new CoolingGraphInput(timestamp, values, stale, calibrations));
        return new CoolingGraphRuntimeTick(
            evaluation,
            held,
            new Dictionary<string, int>(_stalePollCounts, StringComparer.Ordinal));
    }
}

public sealed record CoolingGraphRuntimeTick(
    CoolingGraphEvaluation Evaluation,
    IReadOnlySet<string> HeldSensorIds,
    IReadOnlyDictionary<string, int> StalePollCounts);
