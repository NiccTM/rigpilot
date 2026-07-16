using PCHelper.Adapters;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Validates the Kraken X3 status-report parser against synthetic reports built
/// from the liquidctl kraken3 protocol (marker 0x75 0x02; temperature at bytes
/// 15/16, pump rpm little-endian at 17/18, pump duty at 19). No hardware is
/// touched — the reader's HID path only runs inside the Adapter Host child.
/// </summary>
public sealed class KrakenX3TelemetryParserTests
{
    [Fact]
    public void ParsesALiquidctlAlignedStatusReport()
    {
        byte[] report = BuildReport(anchor: 0, tempWhole: 33, tempTenths: 4, rpm: 2130, duty: 60);

        bool parsed = KrakenX3TelemetryReader.TryParseStatusReport(report, out double temperature, out int rpm, out int duty);

        Assert.True(parsed);
        Assert.Equal(33.4, temperature);
        Assert.Equal(2130, rpm);
        Assert.Equal(60, duty);
    }

    [Fact]
    public void ParsesAReportCarryingALeadingReportIdByte()
    {
        // HidSharp can deliver the same report shifted by one leading byte; the
        // parser must anchor on the 0x75 0x02 marker instead of a fixed offset.
        byte[] report = BuildReport(anchor: 1, tempWhole: 28, tempTenths: 9, rpm: 800, duty: 40);

        bool parsed = KrakenX3TelemetryReader.TryParseStatusReport(report, out double temperature, out int rpm, out int duty);

        Assert.True(parsed);
        Assert.Equal(28.9, temperature);
        Assert.Equal(800, rpm);
        Assert.Equal(40, duty);
    }

    [Fact]
    public void RejectsReportsWithoutTheStatusMarker()
    {
        byte[] report = BuildReport(anchor: 0, tempWhole: 33, tempTenths: 4, rpm: 2130, duty: 60);
        report[1] = 0x01; // right report id, wrong kind

        Assert.False(KrakenX3TelemetryReader.TryParseStatusReport(report, out _, out _, out _));
    }

    [Theory]
    [InlineData(0xFF, 0xFF, 2000, 50)] // the firmware's broken-sensor sentinel
    [InlineData(0, 0, 2000, 50)]       // temperature below the plausible floor
    [InlineData(33, 4, 6500, 50)]      // rpm above any real pump
    public void RejectsImplausibleReadings(int tempWhole, int tempTenths, int rpm, int duty)
    {
        byte[] report = BuildReport(0, (byte)tempWhole, (byte)tempTenths, rpm, (byte)duty);

        Assert.False(KrakenX3TelemetryReader.TryParseStatusReport(report, out _, out _, out _));
    }

    [Fact]
    public void RejectsAReportTooShortToCarryTheStatusFields()
    {
        byte[] truncated = [0x75, 0x02, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.False(KrakenX3TelemetryReader.TryParseStatusReport(truncated, out _, out _, out _));
    }

    private static byte[] BuildReport(int anchor, byte tempWhole, byte tempTenths, int rpm, byte duty)
    {
        byte[] report = new byte[64];
        report[anchor] = 0x75;
        report[anchor + 1] = 0x02;
        report[anchor + 15] = tempWhole;
        report[anchor + 16] = tempTenths;
        report[anchor + 17] = (byte)(rpm & 0xFF);
        report[anchor + 18] = (byte)(rpm >> 8);
        report[anchor + 19] = duty;
        return report;
    }
}
