using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class CustomCoolingCurveFactoryTests
{
    [Fact]
    public void CreatesCalibrationBoundInactiveProfileFromManualPoints()
    {
        AdaptiveCoolingProfileDraft draft = CustomCoolingCurveFactory.Create(
            Capability(),
            Calibration(),
            "CHA_FAN1",
            [
                Sample("cpu.package", "CPU Package", "cpu", 58),
                Sample("gpu.hotspot", "GPU Hot Spot", "gpu", 71)
            ],
            new CustomCoolingCurveDefinition(
                "Quiet gaming",
                [new CurvePoint(35, 10), new CurvePoint(55, 42), new CurvePoint(72, 75), new CurvePoint(88, 100)],
                HysteresisUp: 1,
                HysteresisDown: 2,
                ResponseUpSeconds: 1,
                ResponseDownSeconds: 5));

        CoolingGraphNodeV1 curve = Assert.Single(draft.Graph.Nodes, node => node.Id == "custom-curve");
        CoolingGraphOutputV1 output = Assert.Single(draft.Graph.Outputs);

        Assert.Equal("custom.cooling.fan-case1.quiet-gaming", draft.Graph.Id);
        Assert.Equal("custom.profile.fan-case1.quiet-gaming", draft.Profile.Id);
        Assert.Equal("CHA_FAN1 Quiet gaming", draft.Profile.Name);
        Assert.Equal("custom-curve", output.SourceNodeId);
        Assert.Equal(10, output.Minimum);
        Assert.Equal(100, output.Maximum);
        Assert.DoesNotContain(curve.Points, point => point.Output <= 0);
        Assert.Equal(100, curve.Points[^1].Output);
        Assert.Contains(draft.Graph.Nodes, node => node.Kind == CoolingNodeKind.Mix && node.MixFunction == CoolingMixFunction.Maximum);
        Assert.Empty(CoolingGraphValidator.Validate(draft.Graph));
    }

    [Fact]
    public void RejectsAValueBelowTheMeasuredNonzeroFloor()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => CustomCoolingCurveFactory.Create(
            Capability(),
            Calibration(),
            "CHA_FAN1",
            [Sample("cpu.package", "CPU Package", "cpu", 58)],
            new CustomCoolingCurveDefinition(
                "Too low",
                [new CurvePoint(35, 5), new CurvePoint(85, 100)],
                1,
                2,
                1,
                5)));

        Assert.Contains("calibrated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAManualCurveWithoutAFullSpeedThermalCeiling()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => CustomCoolingCurveFactory.Create(
            Capability(),
            Calibration(),
            "CHA_FAN1",
            [Sample("cpu.package", "CPU Package", "cpu", 58)],
            new CustomCoolingCurveDefinition(
                "No ceiling",
                [new CurvePoint(35, 10), new CurvePoint(85, 90)],
                1,
                2,
                1,
                5)));

        Assert.Contains("final curve point", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CapabilityDescriptor Capability() => new(
        "fan.case1",
        "adapter.board",
        "board.x570",
        "CHA_FAN1",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "%",
        RiskLevel.Experimental,
        EvidenceLevel.SingleSystem,
        null,
        "Exact output needs an explicit acknowledgement.",
        CanResetToDefault: true,
        ControlDomain.Cooling);

    private static FanCalibrationV2 Calibration() => new(
        FanCalibrationV2.CurrentSchemaVersion,
        "fan.case1",
        "fan.case1.rpm",
        [
            new FanCalibrationPoint(0, 540, Stable: true),
            new FanCalibrationPoint(20, 540, Stable: true),
            new FanCalibrationPoint(25, 630, Stable: true),
            new FanCalibrationPoint(100, 1_980, Stable: true)
        ],
        1_980,
        StallDutyPercent: null,
        RestartDutyPercent: null,
        MinimumDutyPercent: 10,
        KickStartDutyPercent: 10,
        AvoidBands: [],
        VerifiedAt: DateTimeOffset.UtcNow,
        CommissioningSessionId: "commission.case1",
        EffectiveFloorDutyPercent: 20,
        EffectiveFloorRpm: 540,
        FirstResponsiveDutyPercent: 25,
        NonStopFloorObserved: true,
        SupportsVerifiedFanStop: false);

    private static SensorSample Sample(string id, string name, string deviceId, double value) => new(
        id,
        "adapter",
        deviceId,
        name,
        DateTimeOffset.UtcNow,
        value,
        "C",
        SensorQuality.Good,
        TimeSpan.Zero);
}
