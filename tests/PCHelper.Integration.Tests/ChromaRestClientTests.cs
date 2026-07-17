using System.Text.Json;
using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the Razer Chroma REST protocol construction (BGR packing, brightness
/// scaling, app-info payload shape). The live SDK server needs Razer Synapse,
/// so these verify the wire format without a server.
/// </summary>
public sealed class ChromaRestClientTests
{
    [Theory]
    [InlineData("#FF0000", 100, 0x0000FF)] // pure red  -> BGR 0x0000FF
    [InlineData("#00FF00", 100, 0x00FF00)] // pure green -> BGR 0x00FF00
    [InlineData("#0000FF", 100, 0xFF0000)] // pure blue  -> BGR 0xFF0000
    [InlineData("#0A84FF", 100, 0xFF840A)] // RigPilot blue
    public void ToBgrPacksColoursInTheSdksBlueGreenRedOrder(string hex, int brightness, int expected)
    {
        Assert.Equal(expected, ChromaRestClient.ToBgr(hex, brightness));
    }

    [Fact]
    public void ToBgrScalesEveryChannelByBrightness()
    {
        int half = ChromaRestClient.ToBgr("#FFFFFF", 50);

        Assert.Equal(0x7F, half & 0xFF);          // blue channel
        Assert.Equal(0x7F, half >> 8 & 0xFF);     // green channel
        Assert.Equal(0x7F, half >> 16 & 0xFF);    // red channel
    }

    [Fact]
    public void ToBgrRejectsMalformedColours()
    {
        Assert.Throws<FormatException>(() => ChromaRestClient.ToBgr("red", 100));
        Assert.Throws<FormatException>(() => ChromaRestClient.ToBgr("#12345", 100));
    }

    [Fact]
    public void AppInfoSerialisesWithTheSnakeCaseFieldsTheSdkRequires()
    {
        string json = JsonSerializer.Serialize(ChromaRestClient.AppInfo);

        Assert.Contains("\"title\":\"RigPilot\"", json, StringComparison.Ordinal);
        Assert.Contains("\"device_supported\":[", json, StringComparison.Ordinal);
        Assert.Contains("chromalink", json, StringComparison.Ordinal); // the Lian Li O11 Razer Edition category
        Assert.Contains("\"category\":\"application\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetStaticColourFailsCleanlyWhenNoChromaServerIsListening()
    {
        // Port 1 never hosts the SDK, so this exercises the no-server path.
        ChromaRestClient client = new("http://127.0.0.1:1/razer/chromasdk", TimeSpan.FromMilliseconds(400));

        ChromaConnectionResult result = await client.SetStaticColourAsync("#0A84FF", 100, CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Contains("Synapse", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
