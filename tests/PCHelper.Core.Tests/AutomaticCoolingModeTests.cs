using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Exercises the one-click automatic cooling mode factory: configured 20%
/// floors on uncalibrated outputs, full maximum for emergency headroom, and
/// source selection. No hardware or service is involved.
/// </summary>
public sealed class AutomaticCoolingModeTests
{
    [Fact]
    public void AutomaticModeFloorsEveryOutputAtTheConservativeSafetyFloor()
    {
        CapabilityDescriptor fan = Output("lhm.control:/lpc/x/0/control/0", "Fan #1", minimum: 0, maximum: 100);
        CapabilityDescriptor gpuFan = Output("gpufan.duty:0", "GPU fan duty", minimum: 10, maximum: 100);

        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [fan, gpuFan], "Case fans", Samples());

        Assert.Equal(2, draft.Graph.Outputs.Count);
        Assert.All(draft.Graph.Outputs, output =>
        {
            Assert.Equal(AdaptiveCoolingProfileFactory.UncalibratedFloorDutyPercent, output.Minimum);
            Assert.Equal(100, output.Maximum);
        });
        // Every curve must reach the controller maximum for emergency headroom.
        Assert.All(
            draft.Graph.Nodes.Where(node => node.Kind == CoolingNodeKind.Graph),
            node => Assert.Equal(100, node.Points[^1].Output));
        Assert.True(draft.Profile.IsExperimental);
        Assert.Equal(draft.Graph.Id, draft.Profile.CoolingGraphId);
    }

    [Fact]
    public void AutomaticModePreservesAHigherControllerReportedMinimum()
    {
        CapabilityDescriptor fan = Output("gpufan.duty:0", "GPU fan duty", minimum: 30, maximum: 100);

        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [fan], "GPU fan", Samples(), preferGpuSourceOnly: true);

        Assert.Equal(30, Assert.Single(draft.Graph.Outputs).Minimum);
    }

    [Fact]
    public void GpuModePrefersTheGpuTemperatureSource()
    {
        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [Output("gpufan.duty:0", "GPU fan duty", 30, 100)], "GPU fan", Samples(), preferGpuSourceOnly: true);

        string sourceSensor = Assert.Single(draft.SourceSensorIds);
        Assert.Contains("gpu", sourceSensor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaseFanModeMixesCpuAndGpuTemperatures()
    {
        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [Output("lhm.control:/lpc/x/0/control/0", "Fan #1", 0, 100)], "Case fans", Samples());

        Assert.Equal(2, draft.SourceSensorIds.Count);
        Assert.Contains(draft.Graph.Nodes, node => node.Kind == CoolingNodeKind.Mix && node.MixFunction == CoolingMixFunction.Maximum);
    }

    [Fact]
    public void ModeChangesTheCurveShapeWhileKeepingTheFloorAndMaximum()
    {
        CapabilityDescriptor fan = Output("lhm.control:/lpc/x/0/control/0", "Fan #1", minimum: 0, maximum: 100);

        AdaptiveCoolingProfileDraft silent = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [fan], "Case fans", Samples(), mode: CoolingCurveMode.Silent);
        AdaptiveCoolingProfileDraft cooling = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [fan], "Case fans", Samples(), mode: CoolingCurveMode.Cooling);

        CurvePoint[] silentPoints = [.. silent.Graph.Nodes.First(node => node.Kind == CoolingNodeKind.Graph).Points];
        CurvePoint[] coolingPoints = [.. cooling.Graph.Nodes.First(node => node.Kind == CoolingNodeKind.Graph).Points];

        // Both still use the configured floor and top out at the controller maximum.
        Assert.Equal(AdaptiveCoolingProfileFactory.UncalibratedFloorDutyPercent, silentPoints[0].Output);
        Assert.Equal(100, silentPoints[^1].Output);
        Assert.Equal(100, coolingPoints[^1].Output);
        // Cooling reaches full speed at a lower temperature than Silent.
        Assert.True(coolingPoints[^1].Input < silentPoints[^1].Input);
        // The stable graph id is shared so switching modes replaces, not duplicates.
        Assert.Equal(silent.Graph.Id, cooling.Graph.Id);
    }

    [Fact]
    public void AutomaticModeRefusesOutputsWithNoRangeAboveTheFloor()
    {
        CapabilityDescriptor cramped = Output("lhm.control:/lpc/x/0/control/1", "Fan #2", minimum: 0, maximum: 20);

        Assert.Throws<InvalidOperationException>(() =>
            AdaptiveCoolingProfileFactory.CreateAutomaticMode([cramped], "Case fans", Samples()));
    }

    [Fact]
    public void AutomaticModeRefusesWithoutATemperatureSource()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdaptiveCoolingProfileFactory.CreateAutomaticMode(
                [Output("lhm.control:/lpc/x/0/control/0", "Fan #1", 0, 100)], "Case fans", []));
    }

    [Fact]
    public void ConservativeDutyGraphCanActivateWithoutCommissioningCalibration()
    {
        CapabilityDescriptor fan = Output("gpufan.duty:0", "GPU fan duty", 20, 100);
        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [fan], "GPU fan", Samples(), preferGpuSourceOnly: true);

        Assert.True(AdaptiveCoolingProfileFactory.CanActivateWithoutCalibration(
            fan,
            Assert.Single(draft.Graph.Outputs)));
    }

    [Theory]
    [InlineData(19, 100, FanOutputMode.DutyPercent)]
    [InlineData(20, 99, FanOutputMode.DutyPercent)]
    [InlineData(20, 100, FanOutputMode.Rpm)]
    public void UncalibratedActivationRejectsAnythingOutsideTheConservativeEnvelope(
        double minimum,
        double maximum,
        FanOutputMode mode)
    {
        CapabilityDescriptor fan = Output("lhm.control:/lpc/x/0/control/0", "Fan #1", 0, 100);
        CoolingGraphOutputV1 output = new(
            fan.Id, "curve", mode, minimum, maximum, 0, 10, 10, []);

        Assert.False(AdaptiveCoolingProfileFactory.CanActivateWithoutCalibration(fan, output));
    }

    private static CapabilityDescriptor Output(string id, string name, double minimum, double maximum) => new(
        id,
        "lhm",
        "device:x",
        name,
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(minimum, maximum, 1),
        "%",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "test",
        CanResetToDefault: true,
        Domain: ControlDomain.Cooling);

    private static SensorSample[] Samples() =>
    [
        new SensorSample("cpu.temp", "lhm", "cpu:0", "CPU Package", DateTimeOffset.UtcNow, 55, "°C", SensorQuality.Good, TimeSpan.Zero),
        new SensorSample("gpu.temp", "nvml", "gpu:0", "GPU Core", DateTimeOffset.UtcNow, 60, "°C", SensorQuality.Good, TimeSpan.Zero),
    ];
}
