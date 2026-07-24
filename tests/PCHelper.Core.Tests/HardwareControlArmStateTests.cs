using PCHelper.Contracts;

namespace PCHelper.Core.Tests;

/// <summary>
/// Arming one GPU family and reading the composite <c>HardwareControlArmed</c> back reports
/// false, which reads as a failed arm and cost a live verification pass before it was
/// understood. The flag is not wrong — it demands every available family — but it is not
/// the answer to "did my family arm?", so the per-family breakdown now travels with it and
/// the composite is derived from that breakdown rather than computed separately.
///
/// These tests pin both halves: what the breakdown reports, and that the composite still
/// means exactly what it always did.
/// </summary>
public sealed class HardwareControlArmStateTests
{
    [Fact]
    public void ArmingOneFamilyLeavesTheCompositeFalseButNamesTheArmedFamily()
    {
        // The exact live situation: power armed, fan and clock not.
        HardwareControlArmStateV1 state = new(GpuFan: false, GpuPower: true, GpuClock: false);

        Assert.False(state.FullyArmed);
        Assert.True(state.GpuPower);
        Assert.False(state.GpuFan);
    }

    [Fact]
    public void EveryAvailableFamilyArmedIsFullyArmed()
    {
        Assert.True(new HardwareControlArmStateV1(true, true, true).FullyArmed);
    }

    [Fact]
    public void AnAbsentFamilyDoesNotBlockFullyArmed()
    {
        // A machine with no clock transport must still be able to report fully armed;
        // null means "no such family here", which is not the same as disarmed.
        HardwareControlArmStateV1 state = new(GpuFan: true, GpuPower: true, GpuClock: null);

        Assert.True(state.FullyArmed);
        Assert.Null(state.GpuClock);
    }

    [Fact]
    public void AMachineWithNoGpuControlAtAllIsNotFullyArmed()
    {
        // Vacuous truth would report a machine that can control nothing as fully armed.
        HardwareControlArmStateV1 state = new(null, null, null);

        Assert.False(state.AnyAvailable);
        Assert.False(state.FullyArmed);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void AnySingleDisarmedFamilyClearsTheComposite(bool fan, bool power, bool clock)
    {
        Assert.False(new HardwareControlArmStateV1(fan, power, clock).FullyArmed);
    }
}
