using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed record FanControlState(double LastInput, double LastOutput, DateTimeOffset LastUpdated);

public sealed record FanControlOptions(double HysteresisCelsius, double MaximumChangePercentPerSecond);

public static class FanCurveEvaluator
{
    public static double Evaluate(IReadOnlyList<CurvePoint> points, double input)
    {
        Validate(points);
        if (input <= points[0].Input)
        {
            return ClampDuty(points[0].Output);
        }

        if (input >= points[^1].Input)
        {
            return ClampDuty(points[^1].Output);
        }

        for (int index = 1; index < points.Count; index++)
        {
            CurvePoint high = points[index];
            if (input > high.Input)
            {
                continue;
            }

            CurvePoint low = points[index - 1];
            double ratio = (input - low.Input) / (high.Input - low.Input);
            return ClampDuty(low.Output + ((high.Output - low.Output) * ratio));
        }

        return ClampDuty(points[^1].Output);
    }

    public static FanControlState EvaluateControlled(
        IReadOnlyList<CurvePoint> points,
        double input,
        DateTimeOffset now,
        FanControlState? previous,
        FanControlOptions options)
    {
        double requested = Evaluate(points, input);
        if (previous is null)
        {
            return new FanControlState(input, requested, now);
        }

        if (requested < previous.LastOutput && input > previous.LastInput - options.HysteresisCelsius)
        {
            requested = previous.LastOutput;
        }

        double elapsedSeconds = Math.Max(0, (now - previous.LastUpdated).TotalSeconds);
        double maximumDelta = options.MaximumChangePercentPerSecond * elapsedSeconds;
        double output = Math.Clamp(
            requested,
            previous.LastOutput - maximumDelta,
            previous.LastOutput + maximumDelta);
        return new FanControlState(input, ClampDuty(output), now);
    }

    public static double MaximumGoodSensorValue(IEnumerable<SensorSample> samples)
    {
        double[] values = samples
            .Where(sample => sample.Quality == SensorQuality.Good && sample.Value.HasValue)
            .Select(sample => sample.Value!.Value)
            .ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("No good sensor values are available.");
        }

        return values.Max();
    }

    public static void Validate(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("A fan curve requires at least two points.", nameof(points));
        }

        for (int index = 0; index < points.Count; index++)
        {
            CurvePoint point = points[index];
            if (!double.IsFinite(point.Input) || !double.IsFinite(point.Output) || point.Output is < 0 or > 100)
            {
                throw new ArgumentException("Fan curve points must be finite and duty must be between 0 and 100.", nameof(points));
            }

            if (index > 0 && point.Input <= points[index - 1].Input)
            {
                throw new ArgumentException("Fan curve inputs must be strictly increasing.", nameof(points));
            }
        }
    }

    private static double ClampDuty(double value) => Math.Clamp(value, 0, 100);
}

public sealed record SensorSafetyDecision(bool Emergency, string Reason, double FanDutyPercent, bool ReturnToFirmwareControl);

public static class SensorSafetyEvaluator
{
    public static SensorSafetyDecision Evaluate(
        IReadOnlyList<SensorSample> boundSensors,
        DateTimeOffset now,
        TimeSpan pollInterval,
        SafetyLimits limits,
        double? adapterCriticalTemperatureCelsius = null)
    {
        if (boundSensors.Count == 0)
        {
            return new SensorSafetyDecision(true, "No bound temperature source is available.", limits.EmergencyFanDutyPercent, true);
        }

        TimeSpan staleAfter = TimeSpan.FromTicks(pollInterval.Ticks * limits.StalePollLimit);
        if (boundSensors.Any(sample => sample.Quality != SensorQuality.Good || now - sample.Timestamp > staleAfter))
        {
            return new SensorSafetyDecision(true, "A bound temperature source is stale or invalid.", limits.EmergencyFanDutyPercent, true);
        }

        double threshold = adapterCriticalTemperatureCelsius ?? limits.FallbackCriticalTemperatureCelsius;
        SensorSample? critical = boundSensors.FirstOrDefault(sample => sample.Value >= threshold);
        if (critical is not null)
        {
            return new SensorSafetyDecision(
                true,
                $"{critical.Name} reached {critical.Value:0.0} {critical.Unit}; threshold is {threshold:0.0} °C.",
                limits.EmergencyFanDutyPercent,
                false);
        }

        return new SensorSafetyDecision(false, "Sensors are healthy.", 0, false);
    }
}
