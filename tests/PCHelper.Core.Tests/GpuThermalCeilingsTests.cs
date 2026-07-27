using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The screening monitor binds hot spot and memory junction alongside the edge
/// sensor, then judged the maximum of all of them against one operator ceiling.
/// On the reference 3090 that made Auto OC impossible as soon as the workload
/// host was fixed to saturate the card: GDDR6X memory junction reached the 83 °C
/// core ceiling on baseline sample 2 while the core was in the 70s, and the run
/// was rejected. The weak workload had been hiding it.
/// </summary>
public sealed class GpuThermalCeilingsTests
{
    private const double EdgeCeiling = 83;

    [Theory]
    [InlineData("GPU temperature", GpuTemperatureClass.Edge)]
    [InlineData("GPU Core", GpuTemperatureClass.Edge)]
    [InlineData("GPU Hot Spot", GpuTemperatureClass.HotSpot)]
    [InlineData("GPU Hotspot", GpuTemperatureClass.HotSpot)]
    [InlineData("GPU Memory Junction", GpuTemperatureClass.MemoryJunction)]
    [InlineData("VRAM Temp", GpuTemperatureClass.MemoryJunction)]
    public void SensorsAreClassifiedByTheirOwnName(string name, GpuTemperatureClass expected)
    {
        // The specific sensors are themselves named "GPU ...", so a naive edge
        // match would swallow them and reimpose the core ceiling.
        Assert.Equal(expected, GpuThermalCeilings.Classify(name));
    }

    [Fact]
    public void TheOperatorCeilingStillGovernsTheCoreSensor()
    {
        // This must never be relaxed — it is the limit the operator actually chose.
        Assert.Equal(EdgeCeiling, GpuThermalCeilings.CeilingForSensor("GPU temperature", EdgeCeiling));
    }

    [Fact]
    public void MemoryJunctionIsJudgedAgainstItsOwnCeiling()
    {
        double ceiling = GpuThermalCeilings.CeilingForSensor("GPU Memory Junction", EdgeCeiling);

        Assert.Equal(GpuThermalCeilings.MemoryJunctionCeilingCelsius, ceiling);
        // The exact reading that rejected the live run must now pass.
        Assert.True(83.0 < ceiling);
    }

    [Fact]
    public void HotSpotIsJudgedAgainstItsOwnCeiling()
    {
        Assert.Equal(
            GpuThermalCeilings.HotSpotCeilingCelsius,
            GpuThermalCeilings.CeilingForSensor("GPU Hot Spot", EdgeCeiling));
    }

    [Fact]
    public void EveryCeilingStaysBelowTheVendorThrottlePoints()
    {
        // Screening must abort before the hardware protects itself, or the run is
        // measuring throttled clocks rather than stability.
        Assert.True(GpuThermalCeilings.HotSpotCeilingCelsius < 105);
        Assert.True(GpuThermalCeilings.MemoryJunctionCeilingCelsius < 110);
    }

    [Fact]
    public void ARaisedOperatorCeilingIsNeverSilentlyLowered()
    {
        // An operator who deliberately allows a high limit must not have it quietly
        // clamped back to a class default behind their back. The value has to sit ABOVE
        // every class default or the test stops exercising its own property — it was 100,
        // which silently became meaningless the moment the memory-junction default moved
        // to 102 and is what caught this.
        const double raised = 106;

        Assert.True(raised > GpuThermalCeilings.MemoryJunctionCeilingCelsius);
        Assert.True(raised > GpuThermalCeilings.HotSpotCeilingCelsius);
        Assert.Equal(raised, GpuThermalCeilings.CeilingForSensor("GPU Memory Junction", raised));
        Assert.Equal(raised, GpuThermalCeilings.CeilingForSensor("GPU Hot Spot", raised));
    }

    [Fact]
    public void TheMemoryJunctionCeilingClearsTheBandAHealthyCardOccupiesAtStock()
    {
        // The defect this pins: at 95 °C the reference RTX 3090 failed the memory stage's
        // STOCK rung at 96.0 °C, with no offset applied. A ceiling inside the normal
        // operating band rejects the configuration the card ships in, which protects
        // nothing and makes the stage unreachable. Double-sided GDDR6X routinely runs
        // 95-105 °C under load, so the ceiling must clear that band while still aborting
        // before the ~110 °C throttle point.
        Assert.True(
            GpuThermalCeilings.MemoryJunctionCeilingCelsius > 100,
            "The ceiling must sit above the band a healthy GDDR6X card occupies under load.");
        Assert.True(
            GpuThermalCeilings.MemoryJunctionCeilingCelsius <= 105,
            "The ceiling must still abort before GDDR6X throttles, or the run measures throttled clocks.");
    }

    [Fact]
    public void AnUnknownSensorFallsBackToTheStrictestCeiling()
    {
        // Failing closed: an unrecognised sensor must not inherit a relaxed limit.
        Assert.Equal(EdgeCeiling, GpuThermalCeilings.CeilingForSensor("Board Temperature", EdgeCeiling));
    }
}
