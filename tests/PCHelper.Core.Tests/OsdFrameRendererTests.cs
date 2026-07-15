using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class OsdFrameRendererTests
{
    [Fact]
    public void RendersLatestGoodSamplesAndMarksUnavailableValues()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OsdLayoutV1 layout = new(
            OsdLayoutV1.CurrentSchemaVersion,
            "osd.game",
            "Game",
            [
                new OsdWidgetV1("cpu.temp", "CPU", "F1", 0, 0, "#00FF00"),
                new OsdWidgetV1("gpu.power", "GPU", "0", 1, 0, "#FFFFFF")
            ],
            0.9,
            1,
            ShowGraph: false);
        SensorSample older = Sample("cpu.temp", 50, "°C", SensorQuality.Good, now.AddSeconds(-1));
        SensorSample latest = Sample("cpu.temp", 62.34, "°C", SensorQuality.Good, now);

        OsdFrameV1 frame = OsdFrameRenderer.Render(layout, [older, latest], now);

        Assert.Equal("62.3 °C", frame.Widgets[0].Text);
        Assert.Equal("--", frame.Widgets[1].Text);
        Assert.Equal(SensorQuality.Unavailable, frame.Widgets[1].Quality);
    }

    [Fact]
    public void RejectsOverlappingCellsAndArbitraryFormatStrings()
    {
        OsdLayoutV1 layout = new(
            OsdLayoutV1.CurrentSchemaVersion,
            "osd.bad",
            "Bad",
            [
                new OsdWidgetV1("a", "A", "{0:pwn}", 0, 0, "#FFFFFF"),
                new OsdWidgetV1("b", "B", "F1", 0, 0, "#FFFFFF")
            ],
            1,
            1,
            false);

        SuiteValidationResult result = OsdFrameRenderer.Validate(layout);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("format", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("same cell", StringComparison.OrdinalIgnoreCase));
    }

    private static SensorSample Sample(string id, double value, string unit, SensorQuality quality, DateTimeOffset time) =>
        new(id, "adapter", "device", id, time, value, unit, quality, TimeSpan.Zero);
}
