using System.Globalization;
using PCHelper.Contracts;

namespace PCHelper.App;

internal enum OnboardingModeChoice
{
    Quiet,
    Efficiency,
    Performance
}

internal sealed record OnboardingCapabilitySummary(
    int DeviceCount,
    int VerifiedCount,
    int ExperimentalCount,
    int ReadOnlyCount,
    int BlockedCount,
    int BridgedCount,
    int ActiveConflictCount)
{
    public string ToDisplayText()
    {
        string conflict = ActiveConflictCount == 0
            ? "No competing hardware owner is running."
            : $"{ActiveConflictCount} competing owner{(ActiveConflictCount == 1 ? " is" : "s are")} running; affected writes remain blocked.";
        return $"{DeviceCount} devices found. {VerifiedCount} Verified controls, {ExperimentalCount} Experimental controls, "
            + $"{ReadOnlyCount} read-only controls, and {BlockedCount} blocked or unavailable controls. "
            + $"{BridgedCount} user-session bridge endpoint{(BridgedCount == 1 ? string.Empty : "s")} detected. {conflict}";
    }
}

internal sealed record OnboardingMetricSummary(
    string SensorId,
    string Name,
    string Unit,
    double Average,
    double Minimum,
    double Maximum,
    double VariationPercent);

internal sealed record OnboardingBaselineSummary(
    int SnapshotCount,
    IReadOnlyList<OnboardingMetricSummary> Metrics)
{
    public string ToDisplayText()
    {
        if (Metrics.Count == 0)
        {
            return $"Captured {SnapshotCount} telemetry samples. No fresh temperature, power, or fan-speed sensors were available.";
        }

        string values = string.Join("; ", Metrics.Select(metric =>
            $"{metric.Name}: {metric.Average.ToString("0.#", CultureInfo.CurrentCulture)} {metric.Unit} "
            + $"(range {metric.Minimum.ToString("0.#", CultureInfo.CurrentCulture)}-{metric.Maximum.ToString("0.#", CultureInfo.CurrentCulture)})"));
        return $"Captured {SnapshotCount} telemetry samples. {values}.";
    }
}

internal static class OnboardingWorkflow
{
    private static readonly HashSet<string> BaselineUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "°C",
        "C",
        "W",
        "RPM"
    };

    public static OnboardingCapabilitySummary SummarizeCapabilities(HardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<CapabilityDescriptor> capabilities = snapshot.Capabilities;
        return new OnboardingCapabilitySummary(
            snapshot.Devices.Count,
            capabilities.Count(capability => capability.State == CapabilityAccessState.Verified),
            capabilities.Count(capability => capability.State == CapabilityAccessState.Experimental),
            capabilities.Count(capability => capability.State == CapabilityAccessState.ReadOnly),
            capabilities.Count(capability => capability.State is CapabilityAccessState.Blocked
                or CapabilityAccessState.Unsupported
                or CapabilityAccessState.Faulted),
            capabilities.Count(capability => capability.ExecutionContext == AdapterExecutionContext.UserSession),
            snapshot.Conflicts.Count(conflict => conflict.IsRunning));
    }

    public static OnboardingBaselineSummary SummarizeBaseline(IReadOnlyList<HardwareSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        OnboardingMetricSummary[] metrics = snapshots
            .SelectMany(snapshot => snapshot.Sensors)
            .Where(sensor => sensor.Quality == SensorQuality.Good
                && sensor.Value is double value
                && double.IsFinite(value)
                && BaselineUnits.Contains(sensor.Unit))
            .GroupBy(sensor => sensor.SensorId, StringComparer.Ordinal)
            .Select(group =>
            {
                SensorSample latest = group.OrderByDescending(sample => sample.Timestamp).First();
                double[] values = group.Select(sample => sample.Value!.Value).ToArray();
                double average = values.Average();
                double minimum = values.Min();
                double maximum = values.Max();
                double variation = Math.Abs(average) < 0.001
                    ? 0
                    : (maximum - minimum) / Math.Abs(average) * 100;
                return new OnboardingMetricSummary(
                    group.Key,
                    latest.Name,
                    latest.Unit,
                    average,
                    minimum,
                    maximum,
                    variation);
            })
            .OrderBy(MetricPriority)
            .ThenBy(metric => metric.Name, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        return new OnboardingBaselineSummary(snapshots.Count, metrics);
    }

    public static string ProfileId(OnboardingModeChoice mode) => mode switch
    {
        OnboardingModeChoice.Quiet => "quiet",
        OnboardingModeChoice.Efficiency => "efficiency",
        OnboardingModeChoice.Performance => "performance",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static int MetricPriority(OnboardingMetricSummary metric) => metric.Unit.ToUpperInvariant() switch
    {
        "°C" or "C" => 0,
        "W" => 1,
        "RPM" => 2,
        _ => 3
    };
}
