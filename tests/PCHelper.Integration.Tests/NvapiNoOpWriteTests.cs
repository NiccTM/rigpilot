using PCHelper.Adapters;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Auto OC failed at its first candidate on every run with
/// NVAPI_INVALID_USER_PRIVILEGE for a Core clock write of 0 kHz — while the card
/// was already at 0, and while the identical 0 kHz value succeeded moments
/// earlier as part of arming, and manual writes of +15, +30 and +100 MHz all
/// applied and read-back verified from the same LocalSystem service.
///
/// The engine screens the stock value as its first candidate, and rollback
/// restores stock when the control is already stock, so both paths asked the
/// driver to move the hardware to where it already was. Such a write is skipped:
/// the requested state is the current state, and callers prove that by read-back
/// rather than by trusting the write returned.
/// </summary>
public sealed class NvapiNoOpWriteTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(15_000, 15_000)]
    [InlineData(-45_000, -45_000)]
    public void AWriteMatchingTheCurrentDeltaIsSkipped(int current, int requested)
    {
        // The live failure: stock control, stock value requested.
        Assert.True(NvapiGpuClockOffsetTransport.IsNoOpWrite(current, requested));
    }

    [Theory]
    [InlineData(0, 15_000)]
    [InlineData(15_000, 0)]
    [InlineData(15_000, 15_001)]
    [InlineData(-1, 1)]
    public void AnyRealChangeStillReachesTheDriver(int current, int requested)
    {
        // The skip must be exactly a no-op guard. If it ever swallowed a genuine
        // change the control would silently not move while read-back verification
        // compared against a value that was never applied.
        Assert.False(NvapiGpuClockOffsetTransport.IsNoOpWrite(current, requested));
    }

    [Fact]
    public void RestoringStockFromAnAppliedOffsetIsNotSkipped()
    {
        // The safety-critical direction: rolling back a live +120 MHz overclock is
        // a real state change and must always be submitted.
        Assert.False(NvapiGpuClockOffsetTransport.IsNoOpWrite(120_000, 0));
    }
}
