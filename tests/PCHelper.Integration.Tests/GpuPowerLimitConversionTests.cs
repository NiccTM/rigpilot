using PCHelper.Adapters;

namespace PCHelper.Integration.Tests;

/// <summary>
/// NVAPI expresses the GPU power limit in PCM (per-cent-mille — percentage of the
/// default TDP × 1000), while the transport contract is milliwatts. The conversion
/// is anchored on the default TDP in milliwatts (read from NVML). It must round-trip
/// within the adapter's 1 W read-back tolerance, or a write that took would still be
/// rejected — so these use the reference RTX 3090 numbers (default 350 W = 100000
/// PCM, min ~100 W, max 385 W = 110000 PCM).
/// </summary>
public sealed class GpuPowerLimitConversionTests
{
    private const uint DefaultPcm = 100_000;
    private const uint DefaultTdpMilliwatts = 350_000;
    private const uint VerifyToleranceMilliwatts = 1_000;

    [Theory]
    [InlineData(100_000u, 350_000u)] // 100% -> default 350 W
    [InlineData(110_000u, 385_000u)] // 110% -> max 385 W
    public void PcmConvertsToTheExpectedMilliwatts(uint pcm, uint expectedMilliwatts)
    {
        uint milliwatts = NvApiGpuPowerLimitTransport.PcmToMilliwatts(pcm, DefaultPcm, DefaultTdpMilliwatts);
        Assert.InRange((int)milliwatts, (int)(expectedMilliwatts - VerifyToleranceMilliwatts), (int)(expectedMilliwatts + VerifyToleranceMilliwatts));
    }

    [Theory]
    [InlineData(350_000u, 100_000u)] // default 350 W -> 100%
    [InlineData(385_000u, 110_000u)] // max 385 W -> 110%
    public void MilliwattsConvertToTheExpectedPcm(uint milliwatts, uint expectedPcm)
    {
        Assert.Equal(expectedPcm, NvApiGpuPowerLimitTransport.MilliwattsToPcm(milliwatts, DefaultPcm, DefaultTdpMilliwatts));
    }

    [Theory]
    [InlineData(100_000u)] // 100 W
    [InlineData(200_000u)]
    [InlineData(300_000u)]
    [InlineData(350_000u)] // default
    [InlineData(385_000u)] // max
    public void MilliwattRoundTripStaysWithinTheReadBackTolerance(uint milliwatts)
    {
        // The apply commands milliwatts -> PCM (written), and verification reads PCM
        // -> milliwatts. If that round trip drifts past 1 W a valid write is rejected.
        uint pcm = NvApiGpuPowerLimitTransport.MilliwattsToPcm(milliwatts, DefaultPcm, DefaultTdpMilliwatts);
        uint readBack = NvApiGpuPowerLimitTransport.PcmToMilliwatts(pcm, DefaultPcm, DefaultTdpMilliwatts);

        Assert.True(
            Math.Abs((long)readBack - milliwatts) <= VerifyToleranceMilliwatts,
            $"{milliwatts} mW round-tripped to {readBack} mW, beyond the {VerifyToleranceMilliwatts} mW tolerance");
    }

    [Fact]
    public void TheDefaultPointMapsToTheAnchorRegardlessOfItsPcmValue()
    {
        // The default TDP in milliwatts is by definition the milliwatt value of the
        // DefaultPowerInPCM point, even on a card whose default is not exactly 100%.
        Assert.Equal(DefaultTdpMilliwatts, NvApiGpuPowerLimitTransport.PcmToMilliwatts(50_000, 50_000, DefaultTdpMilliwatts));
        Assert.Equal(50_000u, NvApiGpuPowerLimitTransport.MilliwattsToPcm(DefaultTdpMilliwatts, 50_000, DefaultTdpMilliwatts));
    }

    [Fact]
    public void DegenerateAnchorsConvertToZeroRatherThanDivideByZero()
    {
        Assert.Equal(0u, NvApiGpuPowerLimitTransport.PcmToMilliwatts(100_000, defaultPcm: 0, DefaultTdpMilliwatts));
        Assert.Equal(0u, NvApiGpuPowerLimitTransport.MilliwattsToPcm(350_000, DefaultPcm, defaultTdpMilliwatts: 0));
    }
}
