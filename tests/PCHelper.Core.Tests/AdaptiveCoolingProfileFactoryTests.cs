using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class AdaptiveCoolingProfileFactoryTests
{
    [Fact]
    public void CreatesNonzeroMixedTemperatureGraphForCommissionedOutput()
    {
        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.Create(
            Capability(),
            Calibration(),
            "CHA_FAN1",
            [
                Sample("cpu.package", "CPU Package", "cpu", 58),
                Sample("gpu.hotspot", "GPU Hot Spot", "gpu", 71)
            ]);

        CoolingGraphOutputV1 output = Assert.Single(draft.Graph.Outputs);
        CoolingGraphNodeV1 curve = Assert.Single(draft.Graph.Nodes, node => node.Id == "adaptive-curve");

        Assert.Equal("fan.case1", output.CapabilityId);
        Assert.Equal(10, output.Minimum);
        Assert.Equal(100, output.Maximum);
        Assert.DoesNotContain(curve.Points, point => point.Output <= 0);
        Assert.Contains(draft.Graph.Nodes, node => node.Kind == CoolingNodeKind.Mix && node.MixFunction == CoolingMixFunction.Maximum);
        Assert.Equal(draft.Graph.Id, draft.Profile.CoolingGraphId);
        Assert.True(draft.Profile.IsExperimental);
        Assert.Equal(["cpu.package", "gpu.hotspot"], draft.SourceSensorIds);
    }

    [Fact]
    public void UsesSingleDetectedCpuSourceWhenGpuTelemetryIsUnavailable()
    {
        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.Create(
            Capability(),
            Calibration(),
            "CHA_FAN1",
            [Sample("cpu.package", "CPU Package", "cpu", 58)]);

        Assert.Single(draft.SourceSensorIds);
        Assert.DoesNotContain(draft.Graph.Nodes, node => node.Kind == CoolingNodeKind.Mix);
        Assert.Equal("source-1", Assert.Single(draft.Graph.Nodes, node => node.Id == "adaptive-curve").InputNodeIds.Single());
    }

    [Fact]
    public void UsesTheExactControllerBoundsInsteadOfGenericDutyAssumptions()
    {
        CapabilityDescriptor limitedOutput = Capability() with { Range = new NumericRange(0, 60, 1) };

        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.Create(
            limitedOutput,
            Calibration(),
            "CHA_FAN1",
            [Sample("cpu.package", "CPU Package", "cpu", 58)]);

        CoolingGraphOutputV1 output = Assert.Single(draft.Graph.Outputs);
        CoolingGraphNodeV1 curve = Assert.Single(draft.Graph.Nodes, node => node.Id == "adaptive-curve");

        Assert.Equal(60, output.Maximum);
        Assert.All(curve.Points, point => Assert.InRange(point.Output, output.Minimum, output.Maximum));
    }

    [Fact]
    public void ReusesPersistedExactSessionCalibrationAfterRestart()
    {
        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.Create(
            Capability(),
            PersistedCalibration(),
            "CHA_FAN1",
            [Sample("cpu.package", "CPU Package", "cpu", 58)]);

        Assert.Equal("adaptive.cooling.fan-case1", draft.Graph.Id);
        Assert.Equal(10, Assert.Single(draft.Graph.Outputs).Minimum);
        Assert.Equal("CHA_FAN1 adaptive cooling", draft.Profile.Name);
    }

    [Fact]
    public void RefusesToInventAHeatingSource()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => AdaptiveCoolingProfileFactory.Create(
            Capability(),
            Calibration(),
            "CHA_FAN1",
            [Sample("board", "Motherboard", "ec", 40)]));

        Assert.Contains("CPU or GPU", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private static FanCalibrationResult Calibration() => new(
        "fan.case1",
        "fan.case1.rpm",
        1_980,
        StallDutyPercent: null,
        RestartDutyPercent: null,
        MinimumDutyPercent: 10,
        RestartVerified: false,
        [
            new FanCalibrationPoint(0, 540, Stable: true),
            new FanCalibrationPoint(20, 540, Stable: true),
            new FanCalibrationPoint(25, 630, Stable: true),
            new FanCalibrationPoint(100, 1_980, Stable: true)
        ],
        AllMeasurementsStable: true,
        EffectiveFloorDutyPercent: 20,
        EffectiveFloorRpm: 540,
        FirstResponsiveDutyPercent: 25,
        NonStopFloorObserved: true);

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

    private static FanCalibrationV2 PersistedCalibration() => new(
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
}
