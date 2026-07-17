using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Pins the fan-curve presets: every mode starts at the floor and reaches the
/// controller maximum (emergency headroom), Silent stays quieter than Balanced
/// which stays quieter than Cooling at the same temperature, and Cooling
/// reaches full speed at a lower temperature than Silent. Pure math — no
/// hardware or graph engine involved.
/// </summary>
public sealed class CoolingCurveShapeTests
{
    [Theory]
    [InlineData(CoolingCurveMode.Silent)]
    [InlineData(CoolingCurveMode.Balanced)]
    [InlineData(CoolingCurveMode.Cooling)]
    public void EveryModeStartsAtTheFloorAndReachesTheMaximum(CoolingCurveMode mode)
    {
        CoolingCurveShape.Shape shape = CoolingCurveShape.For(mode, floor: 50, maximum: 100);

        Assert.Equal(50, shape.Points[0].Output);
        Assert.Equal(100, shape.Points[^1].Output);
        // Monotonic non-decreasing duty as temperature rises.
        for (int index = 1; index < shape.Points.Count; index++)
        {
            Assert.True(shape.Points[index].Input > shape.Points[index - 1].Input);
            Assert.True(shape.Points[index].Output >= shape.Points[index - 1].Output);
        }
    }

    [Fact]
    public void SilentIsQuieterThanBalancedIsQuieterThanCoolingAtTheSameTemperature()
    {
        double DutyAt60(CoolingCurveMode mode)
        {
            CoolingCurveShape.Shape shape = CoolingCurveShape.For(mode, 50, 100);
            return InterpolateDuty(shape, 60);
        }

        Assert.True(DutyAt60(CoolingCurveMode.Silent) < DutyAt60(CoolingCurveMode.Balanced));
        Assert.True(DutyAt60(CoolingCurveMode.Balanced) < DutyAt60(CoolingCurveMode.Cooling));
    }

    [Fact]
    public void CoolingReachesFullSpeedAtALowerTemperatureThanSilent()
    {
        double coolingCritical = CoolingCurveShape.For(CoolingCurveMode.Cooling, 50, 100).Points[^1].Input;
        double silentCritical = CoolingCurveShape.For(CoolingCurveMode.Silent, 50, 100).Points[^1].Input;

        Assert.True(coolingCritical < silentCritical);
    }

    [Fact]
    public void CoolingRespondsFasterThanSilent()
    {
        CoolingCurveShape.Shape cooling = CoolingCurveShape.For(CoolingCurveMode.Cooling, 50, 100);
        CoolingCurveShape.Shape silent = CoolingCurveShape.For(CoolingCurveMode.Silent, 50, 100);

        Assert.True(cooling.StepUpPerSecond > silent.StepUpPerSecond);
        Assert.True(cooling.Tuning["responseUpSeconds"] <= silent.Tuning["responseUpSeconds"]);
    }

    [Fact]
    public void DutiesStayWithinTheFloorAndMaximumForAnyFloor()
    {
        CoolingCurveShape.Shape shape = CoolingCurveShape.For(CoolingCurveMode.Balanced, floor: 62, maximum: 100);

        Assert.All(shape.Points, point => Assert.InRange(point.Output, 62, 100));
    }

    [Fact]
    public void InvertedBoundsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoolingCurveShape.For(CoolingCurveMode.Balanced, 100, 50));
    }

    private static double InterpolateDuty(CoolingCurveShape.Shape shape, double temperature)
    {
        for (int index = 1; index < shape.Points.Count; index++)
        {
            CurvePoint low = shape.Points[index - 1];
            CurvePoint high = shape.Points[index];
            if (temperature <= high.Input)
            {
                double t = (temperature - low.Input) / (high.Input - low.Input);
                return low.Output + ((high.Output - low.Output) * Math.Clamp(t, 0, 1));
            }
        }

        return shape.Points[^1].Output;
    }
}
