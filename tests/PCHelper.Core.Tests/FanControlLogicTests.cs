using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class FanControlLogicTests
{
    private static readonly CurvePoint[] Curve = [new(30, 20), new(60, 50), new(90, 100)];

    [Theory]
    [InlineData(10, 20)]
    [InlineData(30, 20)]
    [InlineData(45, 35)]
    [InlineData(60, 50)]
    [InlineData(90, 100)]
    [InlineData(100, 100)]
    public void CurveInterpolatesAndClamps(double input, double expected)
    {
        Assert.Equal(expected, FanCurveEvaluator.Evaluate(Curve, input), precision: 6);
    }

    [Fact]
    public void HysteresisPreventsImmediateRampDown()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FanControlState previous = new(60, 50, now);

        FanControlState result = FanCurveEvaluator.EvaluateControlled(
            Curve,
            58,
            now.AddSeconds(1),
            previous,
            new FanControlOptions(HysteresisCelsius: 3, MaximumChangePercentPerSecond: 100));

        Assert.Equal(50, result.LastOutput);
    }

    [Fact]
    public void SlewRateLimitsIncrease()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FanControlState previous = new(30, 20, now);

        FanControlState result = FanCurveEvaluator.EvaluateControlled(
            Curve,
            90,
            now.AddSeconds(2),
            previous,
            new FanControlOptions(HysteresisCelsius: 0, MaximumChangePercentPerSecond: 10));

        Assert.Equal(40, result.LastOutput);
    }

    [Fact]
    public void InvalidCurveIsRejected()
    {
        CurvePoint[] invalid = [new(50, 40), new(40, 30)];

        Assert.Throws<ArgumentException>(() => FanCurveEvaluator.Validate(invalid));
    }

    [Fact]
    public void StaleSensorTriggersEmergencyAndFirmwareReturn()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SensorSample stale = Sample(now.AddSeconds(-4), 50, SensorQuality.Good);

        SensorSafetyDecision result = SensorSafetyEvaluator.Evaluate(
            [stale],
            now,
            TimeSpan.FromSeconds(1),
            new SafetyLimits(StalePollLimit: 3));

        Assert.True(result.Emergency);
        Assert.True(result.ReturnToFirmwareControl);
        Assert.Equal(100, result.FanDutyPercent);
    }

    [Fact]
    public void CriticalTemperatureTriggersEmergency()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        SensorSafetyDecision result = SensorSafetyEvaluator.Evaluate(
            [Sample(now, 86, SensorQuality.Good)],
            now,
            TimeSpan.FromSeconds(1),
            new SafetyLimits(),
            adapterCriticalTemperatureCelsius: 85);

        Assert.True(result.Emergency);
        Assert.False(result.ReturnToFirmwareControl);
    }

    private static SensorSample Sample(DateTimeOffset timestamp, double value, SensorQuality quality) => new(
        "sensor",
        "adapter",
        "device",
        "CPU Package",
        timestamp,
        value,
        "°C",
        quality,
        DateTimeOffset.UtcNow - timestamp);
}
