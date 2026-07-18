using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class CoolingSafetySupervisorTests
{
    [Fact]
    public void CriticalTemperatureImmediatelyLatchesMaximumCooling()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CoolingGraphV1 graph = Graph();
        CoolingGraphRuntimeTick tick = Tick(graph, now, 65);
        CoolingSafetyDecision decision = new CoolingSafetySupervisor().Evaluate(
            graph,
            tick,
            [Sample(now, 91)],
            new SafetyLimits(FallbackCriticalTemperatureCelsius: 90),
            now);

        Assert.Equal(CoolingRuntimeState.EmergencyMaximum, decision.State);
        Assert.True(decision.Evaluation.Emergency);
        Assert.Equal(100, decision.Evaluation.OutputValues["fan"]);
        Assert.Contains("Critical temperature", decision.Reason, StringComparison.Ordinal);
        Assert.Equal(now, decision.EmergencySince);
    }

    [Fact]
    public void EmergencyNeedsThreeCleanPollsBeforeGraphResumes()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CoolingGraphV1 graph = Graph();
        CoolingSafetySupervisor supervisor = new();
        SafetyLimits limits = new(StalePollLimit: 3);
        CoolingSafetyDecision emergency = supervisor.ForceEmergency(graph, now, "Sensor lost.");

        CoolingSafetyDecision first = supervisor.Evaluate(
            graph, Tick(graph, now.AddSeconds(1), 55), [Sample(now.AddSeconds(1), 55)], limits, now.AddSeconds(1));
        CoolingSafetyDecision second = supervisor.Evaluate(
            graph, Tick(graph, now.AddSeconds(2), 55), [Sample(now.AddSeconds(2), 55)], limits, now.AddSeconds(2));
        CoolingSafetyDecision third = supervisor.Evaluate(
            graph, Tick(graph, now.AddSeconds(3), 55), [Sample(now.AddSeconds(3), 55)], limits, now.AddSeconds(3));

        Assert.Equal(CoolingRuntimeState.EmergencyMaximum, emergency.State);
        Assert.Equal(CoolingRuntimeState.EmergencyMaximum, first.State);
        Assert.Equal(CoolingRuntimeState.EmergencyMaximum, second.State);
        Assert.Equal(CoolingRuntimeState.Normal, third.State);
        Assert.True(third.RecoveredThisTick);
        Assert.False(third.Evaluation.Emergency);
        Assert.True(third.Evaluation.OutputValues["fan"] < 100);
    }

    private static CoolingGraphRuntimeTick Tick(CoolingGraphV1 graph, DateTimeOffset now, double temperature) =>
        new CoolingGraphRuntime().Evaluate(
            graph,
            [Sample(now, temperature)],
            new Dictionary<string, FanCalibrationV2>(),
            stalePollLimit: 3,
            now);

    private static SensorSample Sample(DateTimeOffset now, double temperature) => new(
        "cpu",
        "adapter",
        "device",
        "CPU Package",
        now,
        temperature,
        "°C",
        SensorQuality.Good,
        TimeSpan.Zero);

    private static CoolingGraphV1 Graph() => new(
        CoolingGraphV1.CurrentSchemaVersion,
        "graph",
        "Graph",
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
                [new CurvePoint(30, 20), new CurvePoint(90, 100)],
                new Dictionary<string, double>())
        ],
        [new CoolingGraphOutputV1("fan", "curve", FanOutputMode.DutyPercent, 20, 100, 0, 100, 100, [])]);
}
