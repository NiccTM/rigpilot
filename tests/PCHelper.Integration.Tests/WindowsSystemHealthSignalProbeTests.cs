using PCHelper.Core;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class WindowsSystemHealthSignalProbeTests
{
    [Theory]
    [InlineData("Microsoft-Windows-WHEA-Logger", 18, HealthSystemSignalKind.Whea)]
    [InlineData("Display", 4101, HealthSystemSignalKind.DisplayDriverReset)]
    [InlineData("nvlddmkm", 4101, HealthSystemSignalKind.DisplayDriverReset)]
    public void SupportedSystemEventsAreClassifiedWithoutRetainingRawEventData(
        string provider,
        int eventId,
        HealthSystemSignalKind expected)
    {
        bool matched = WindowsSystemHealthSignalProbe.TryClassify(
            provider,
            eventId,
            DateTimeOffset.UtcNow,
            out HealthSystemSignal? signal);

        Assert.True(matched);
        Assert.NotNull(signal);
        Assert.Equal(expected, signal!.Kind);
        Assert.DoesNotContain("\\", signal.Message);
    }

    [Fact]
    public void UnrelatedSystemEventsRemainIgnored()
    {
        bool matched = WindowsSystemHealthSignalProbe.TryClassify(
            "Kernel-General",
            1,
            DateTimeOffset.UtcNow,
            out HealthSystemSignal? signal);

        Assert.False(matched);
        Assert.Null(signal);
    }
}
