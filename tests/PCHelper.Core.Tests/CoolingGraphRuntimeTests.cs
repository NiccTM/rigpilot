using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class CoolingGraphRuntimeTests
{
    [Fact]
    public void HoldsLastGoodSensorForTwoPollsThenEntersEmergencyOnThird()
    {
        CoolingGraphV1 graph = Graph();
        CoolingGraphRuntime runtime = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        CoolingGraphRuntimeTick good = runtime.Evaluate(
            graph,
            [Sample(now, 60, SensorQuality.Good)],
            new Dictionary<string, FanCalibrationV2>(),
            stalePollLimit: 3,
            now);
        CoolingGraphRuntimeTick heldFirst = runtime.Evaluate(
            graph,
            [Sample(now.AddSeconds(1), null, SensorQuality.Unavailable)],
            new Dictionary<string, FanCalibrationV2>(),
            stalePollLimit: 3,
            now.AddSeconds(1));
        CoolingGraphRuntimeTick heldSecond = runtime.Evaluate(
            graph,
            [Sample(now.AddSeconds(2), null, SensorQuality.Unavailable)],
            new Dictionary<string, FanCalibrationV2>(),
            stalePollLimit: 3,
            now.AddSeconds(2));
        CoolingGraphRuntimeTick emergency = runtime.Evaluate(
            graph,
            [Sample(now.AddSeconds(3), null, SensorQuality.Unavailable)],
            new Dictionary<string, FanCalibrationV2>(),
            stalePollLimit: 3,
            now.AddSeconds(3));

        Assert.False(good.Evaluation.Emergency);
        Assert.False(heldFirst.Evaluation.Emergency);
        Assert.False(heldSecond.Evaluation.Emergency);
        Assert.Contains("cpu", heldFirst.HeldSensorIds);
        Assert.Equal(2, heldSecond.StalePollCounts["cpu"]);
        Assert.True(emergency.Evaluation.Emergency);
        Assert.Equal(100, emergency.Evaluation.OutputValues["fan"]);
        Assert.Contains("stale", emergency.Evaluation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingInitialSensorEntersEmergencyWithoutUsingInventedValue()
    {
        CoolingGraphRuntimeTick tick = new CoolingGraphRuntime().Evaluate(
            Graph(),
            [Sample(DateTimeOffset.UtcNow, null, SensorQuality.Unavailable)],
            new Dictionary<string, FanCalibrationV2>(),
            stalePollLimit: 3,
            DateTimeOffset.UtcNow);

        Assert.True(tick.Evaluation.Emergency);
        Assert.Empty(tick.HeldSensorIds);
        Assert.Equal(100, tick.Evaluation.OutputValues["fan"]);
    }

    [Fact]
    public void OldGoodSampleCountsAsStaleTelemetry()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CoolingGraphRuntimeTick tick = new CoolingGraphRuntime().Evaluate(
            Graph(),
            [Sample(now.AddSeconds(-10), 60, SensorQuality.Good)],
            new Dictionary<string, FanCalibrationV2>(),
            stalePollLimit: 3,
            now);

        Assert.True(tick.Evaluation.Emergency);
        Assert.Equal(1, tick.StalePollCounts["cpu"]);
    }

    private static CoolingGraphV1 Graph() => new(
        CoolingGraphV1.CurrentSchemaVersion,
        "graph.case1",
        "Case fan",
        [
            new CoolingGraphNodeV1(
                "cpu",
                "CPU",
                CoolingNodeKind.Sensor,
                [],
                "cpu",
                [],
                new Dictionary<string, double>()),
            new CoolingGraphNodeV1(
                "curve",
                "Curve",
                CoolingNodeKind.Graph,
                ["cpu"],
                null,
                [new CurvePoint(30, 25), new CurvePoint(90, 100)],
                new Dictionary<string, double>())
        ],
        [new CoolingGraphOutputV1("fan", "curve", FanOutputMode.DutyPercent, 0, 100, 0, 100, 100, [])]);

    private static SensorSample Sample(DateTimeOffset timestamp, double? value, SensorQuality quality) => new(
        "cpu",
        "adapter",
        "device",
        "CPU",
        timestamp,
        value,
        "C",
        quality,
        TimeSpan.Zero);
}
