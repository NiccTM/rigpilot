using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Proves start/stop shaping is actually reached by the graph pipeline, and —
/// more importantly — that it stays unreachable for any fan lacking physical
/// stop/restart evidence, whatever the configuration asks for.
/// </summary>
public sealed class CoolingGraphStartStopWiringTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AConfiguredStopIsIgnoredWithoutVerifiedRestartEvidence()
    {
        // Stop requested at 30% and the curve asks for 10%, but this fan has
        // never proven it restarts — so it must keep spinning.
        CoolingGraphEngine engine = new();

        CoolingGraphEvaluation evaluation = engine.Evaluate(
            Graph(stopBelow: 30, start: 45),
            Input(temperature: 20, calibration: UnprovenCalibration()));

        Assert.True(evaluation.OutputValues["fan.1"] > 0);
    }

    [Fact]
    public void AProvenFanReachesZeroAndRestartsWithTheCalibratedBoost()
    {
        CoolingGraphEngine engine = new();
        CoolingGraphV1 graph = Graph(stopBelow: 30, start: 45);
        FanCalibrationV2 proven = ProvenCalibration(restartDuty: 55);

        // Cold: the curve's low duty sits below the stop threshold.
        CoolingGraphEvaluation stopped = engine.Evaluate(graph, Input(20, proven, Origin));
        Assert.Equal(0, stopped.OutputValues["fan.1"]);

        // Hot: leaving rest commands at least the measured 55% restart duty,
        // not the curve's gentler value.
        CoolingGraphEvaluation restarted = engine.Evaluate(graph, Input(70, proven, Origin.AddSeconds(1)));
        Assert.True(restarted.OutputValues["fan.1"] >= 55);
    }

    [Fact]
    public void ValidationRejectsAStopThresholdThatWouldOscillate()
    {
        // A restart duty at or below the stop threshold makes the fan hunt.
        IReadOnlyList<string> errors = CoolingGraphValidator.Validate(Graph(stopBelow: 40, start: 20));

        Assert.Contains(errors, error => error.Contains("oscillate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultOutputsAreUnaffectedBecauseStoppingIsOptIn()
    {
        CoolingGraphEngine engine = new();

        CoolingGraphEvaluation evaluation = engine.Evaluate(
            Graph(stopBelow: 0, start: 0),
            Input(20, ProvenCalibration(restartDuty: 55)));

        Assert.True(evaluation.OutputValues["fan.1"] > 0);
    }

    private static CoolingGraphV1 Graph(double stopBelow, double start) => new(
        CoolingGraphV1.CurrentSchemaVersion,
        "graph.1",
        "Test graph",
        [
            new CoolingGraphNodeV1(
                "sensor.1", "Temp", CoolingNodeKind.Sensor, [], "temp.1", [],
                new Dictionary<string, double>(StringComparer.Ordinal)),
            new CoolingGraphNodeV1(
                "curve.1", "Curve", CoolingNodeKind.Graph, ["sensor.1"], null,
                [new CurvePoint(20, 10), new CurvePoint(80, 100)],
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["hysteresis"] = 0,
                    ["responseSeconds"] = 0
                })
        ],
        [
            new CoolingGraphOutputV1(
                "fan.1", "curve.1", FanOutputMode.DutyPercent,
                Minimum: 0, Maximum: 100, Offset: 0,
                StepUpPerSecond: 1000, StepDownPerSecond: 1000,
                AvoidBands: [],
                StopBelowPercent: stopBelow,
                StartPercent: start,
                StartHoldSeconds: 3)
        ]);

    private static CoolingGraphInput Input(
        double temperature,
        FanCalibrationV2 calibration,
        DateTimeOffset? timestamp = null) => new(
            timestamp ?? Origin,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["temp.1"] = temperature },
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, FanCalibrationV2>(StringComparer.Ordinal) { ["fan.1"] = calibration });

    private static FanCalibrationV2 UnprovenCalibration() =>
        Calibration(supportsStop: false, restartDuty: null, stallDuty: null);

    private static FanCalibrationV2 ProvenCalibration(double restartDuty) =>
        Calibration(supportsStop: true, restartDuty, stallDuty: 12);

    private static FanCalibrationV2 Calibration(bool supportsStop, double? restartDuty, double? stallDuty) => new(
        FanCalibrationV2.CurrentSchemaVersion,
        "fan.1",
        "rpm.1",
        [new FanCalibrationPoint(20, 800), new FanCalibrationPoint(100, 2000)],
        MaximumRpm: 2000,
        StallDutyPercent: stallDuty,
        RestartDutyPercent: restartDuty,
        MinimumDutyPercent: 20,
        KickStartDutyPercent: 60,
        AvoidBands: [],
        VerifiedAt: Origin,
        NonStopFloorObserved: true,
        SupportsVerifiedFanStop: supportsStop);
}
