using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class AdvancedCoolingGraphTests
{
    [Fact]
    public void EvaluatesMixedGraphAndAppliesSeparateSlewRate()
    {
        CoolingGraphV1 graph = Graph(
            [
                Node("cpu", CoolingNodeKind.Sensor, sensorId: "cpu"),
                Node("gpu", CoolingNodeKind.Sensor, sensorId: "gpu"),
                Node("hot", CoolingNodeKind.Mix, inputs: ["cpu", "gpu"]),
                Node("curve", CoolingNodeKind.Graph, inputs: ["hot"], points: [new(40, 30), new(80, 90)])
            ],
            [Output("fan", "curve", stepUp: 5, stepDown: 20)]);
        CoolingGraphEngine engine = new();
        DateTimeOffset start = DateTimeOffset.UtcNow;

        CoolingGraphEvaluation first = engine.Evaluate(graph, Input(start, ("cpu", 50), ("gpu", 70)));
        CoolingGraphEvaluation second = engine.Evaluate(graph, Input(start.AddSeconds(1), ("cpu", 80), ("gpu", 80)));

        Assert.Equal(75, first.OutputValues["fan"], 6);
        Assert.Equal(80, second.OutputValues["fan"], 6);
    }

    [Fact]
    public void RpmOutputUsesCalibrationAndAvoidsResonanceBand()
    {
        CoolingGraphV1 graph = Graph(
            [Node("rpm", CoolingNodeKind.Flat, parameters: new Dictionary<string, double> { ["value"] = 1500 })],
            [Output("fan", "rpm", mode: FanOutputMode.Rpm, avoidBands: [new CurvePoint(45, 55)])]);
        FanCalibrationV2 calibration = new(
            FanCalibrationV2.CurrentSchemaVersion,
            "fan",
            "fan.rpm",
            [new FanCalibrationPoint(30, 900), new FanCalibrationPoint(60, 1800)],
            1800,
            null,
            30,
            35,
            60,
            [],
            DateTimeOffset.UtcNow);
        CoolingGraphInput input = new(
            DateTimeOffset.UtcNow,
            new Dictionary<string, double>(),
            new HashSet<string>(),
            new Dictionary<string, FanCalibrationV2> { ["fan"] = calibration });

        CoolingGraphEvaluation result = new CoolingGraphEngine().Evaluate(graph, input);

        Assert.Equal(45, result.OutputValues["fan"], 6);
    }

    [Fact]
    public void StaleSensorFailsToMaximumCooling()
    {
        CoolingGraphV1 graph = Graph(
            [
                Node("cpu", CoolingNodeKind.Sensor, sensorId: "cpu"),
                Node("curve", CoolingNodeKind.Graph, inputs: ["cpu"], points: [new(30, 20), new(90, 100)])
            ],
            [Output("fan", "curve")]);
        CoolingGraphInput input = new(
            DateTimeOffset.UtcNow,
            new Dictionary<string, double> { ["cpu"] = 40 },
            new HashSet<string> { "cpu" },
            new Dictionary<string, FanCalibrationV2>());

        CoolingGraphEvaluation result = new CoolingGraphEngine().EvaluateSafe(graph, input);

        Assert.True(result.Emergency);
        Assert.Equal(100, result.OutputValues["fan"]);
        Assert.Contains("stale", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalibratedNonStoppingFanNeverSlewsBelowMeasuredFloor()
    {
        CoolingGraphV1 graph = Graph(
            [
                Node("cpu", CoolingNodeKind.Sensor, sensorId: "cpu"),
                Node("curve", CoolingNodeKind.Graph, inputs: ["cpu"], points: [new(0, 0), new(100, 20)])
            ],
            [Output("fan", "curve", stepUp: 5, stepDown: 5)]);
        FanCalibrationV2 calibration = new(
            FanCalibrationV2.CurrentSchemaVersion,
            "fan",
            "fan.rpm",
            [new FanCalibrationPoint(0, 540), new FanCalibrationPoint(20, 540), new FanCalibrationPoint(25, 630), new FanCalibrationPoint(100, 1_980)],
            1_980,
            null,
            null,
            10,
            10,
            [],
            DateTimeOffset.UtcNow,
            NonStopFloorObserved: true,
            EffectiveFloorDutyPercent: 20,
            EffectiveFloorRpm: 540,
            FirstResponsiveDutyPercent: 25);
        CoolingGraphEngine engine = new();
        DateTimeOffset start = DateTimeOffset.UtcNow;

        CoolingGraphEvaluation uncalibrated = engine.Evaluate(graph, Input(start, ("cpu", 0)));
        CoolingGraphEvaluation calibrated = engine.Evaluate(
            graph,
            new CoolingGraphInput(
                start.AddSeconds(1),
                new Dictionary<string, double> { ["cpu"] = 50 },
                new HashSet<string>(),
                new Dictionary<string, FanCalibrationV2> { ["fan"] = calibration }));

        Assert.Equal(0, uncalibrated.OutputValues["fan"]);
        Assert.Equal(10, calibrated.OutputValues["fan"]);
    }

    [Fact]
    public void FeedbackNodeAdjustsOnlyAfterResponsePeriod()
    {
        CoolingGraphV1 graph = Graph(
            [
                Node("cpu", CoolingNodeKind.Sensor, sensorId: "cpu"),
                Node(
                    "auto",
                    CoolingNodeKind.FeedbackAuto,
                    inputs: ["cpu"],
                    parameters: new Dictionary<string, double>
                    {
                        ["idleTemperature"] = 40,
                        ["loadTemperature"] = 70,
                        ["minimum"] = 30,
                        ["maximum"] = 100,
                        ["step"] = 5,
                        ["deadband"] = 2,
                        ["responseSeconds"] = 1
                    })
            ],
            [Output("fan", "auto")]);
        CoolingGraphEngine engine = new();
        DateTimeOffset start = DateTimeOffset.UtcNow;

        CoolingGraphEvaluation first = engine.Evaluate(graph, Input(start, ("cpu", 80)));
        CoolingGraphEvaluation second = engine.Evaluate(graph, Input(start.AddSeconds(2), ("cpu", 82)));

        Assert.Equal(30, first.OutputValues["fan"]);
        Assert.Equal(35, second.OutputValues["fan"]);
    }

    [Fact]
    public void ValidatorRejectsCycles()
    {
        CoolingGraphV1 graph = Graph(
            [
                Node("a", CoolingNodeKind.Offset, inputs: ["b"], parameters: new Dictionary<string, double> { ["offset"] = 1 }),
                Node("b", CoolingNodeKind.Offset, inputs: ["a"], parameters: new Dictionary<string, double> { ["offset"] = 1 })
            ],
            [Output("fan", "a")]);

        IReadOnlyList<string> errors = CoolingGraphValidator.Validate(graph);

        Assert.Contains(errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    private static CoolingGraphInput Input(DateTimeOffset timestamp, params (string Id, double Value)[] sensors) => new(
        timestamp,
        sensors.ToDictionary(item => item.Id, item => item.Value, StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, FanCalibrationV2>(StringComparer.Ordinal));

    private static CoolingGraphV1 Graph(
        IReadOnlyList<CoolingGraphNodeV1> nodes,
        IReadOnlyList<CoolingGraphOutputV1> outputs) => new(
            CoolingGraphV1.CurrentSchemaVersion,
            "graph",
            "Graph",
            nodes,
            outputs);

    private static CoolingGraphNodeV1 Node(
        string id,
        CoolingNodeKind kind,
        IReadOnlyList<string>? inputs = null,
        string? sensorId = null,
        IReadOnlyList<CurvePoint>? points = null,
        IReadOnlyDictionary<string, double>? parameters = null) => new(
            id,
            id,
            kind,
            inputs ?? [],
            sensorId,
            points ?? [],
            parameters ?? new Dictionary<string, double>(),
            null,
            CoolingMixFunction.Maximum);

    private static CoolingGraphOutputV1 Output(
        string capabilityId,
        string source,
        FanOutputMode mode = FanOutputMode.DutyPercent,
        double stepUp = 100,
        double stepDown = 100,
        IReadOnlyList<CurvePoint>? avoidBands = null) => new(
            capabilityId,
            source,
            mode,
            0,
            100,
            0,
            stepUp,
            stepDown,
            avoidBands ?? []);
}
