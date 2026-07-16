using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Exercises the one-click automatic cooling mode factory: conservative 50%
/// floors on uncalibrated outputs, full maximum for emergency headroom, and
/// source selection. No hardware or service is involved.
/// </summary>
public sealed class AutomaticCoolingModeTests
{
    [Fact]
    public void AutomaticModeFloorsEveryOutputAtTheConservativeSafetyFloor()
    {
        CapabilityDescriptor fan = Output("lhm.control:/lpc/x/0/control/0", "Fan #1", minimum: 0, maximum: 100);
        CapabilityDescriptor gpuFan = Output("gpufan.duty:0", "GPU fan duty", minimum: 30, maximum: 100);

        AdaptiveCoolingProfileDraft draft = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
            [fan, gpuFan], "Case fans", Samples());

        Assert.Equal(2, draft.Graph.Outputs.Count);
        Assert.All(draft.Graph.Outputs, output =>
        {
            Assert.Equal(AdaptiveCoolingProfileFactory.ConservativeFloorDutyPercent, output.Minimum);
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
    public void AutomaticModeRefusesOutputsWithNoRangeAboveTheFloor()
    {
        CapabilityDescriptor cramped = Output("lhm.control:/lpc/x/0/control/1", "Fan #2", minimum: 0, maximum: 45);

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
