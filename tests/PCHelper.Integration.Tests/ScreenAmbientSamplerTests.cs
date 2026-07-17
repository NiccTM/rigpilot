using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the screen-ambient zone math on synthetic frames: clockwise zone
/// ordering, per-edge averaging, brightness scaling, LED mapping across
/// different strip lengths, and input validation. No screen capture or OpenRGB
/// socket is involved — the capture and transport layers are exercised live.
/// </summary>
public sealed class ScreenAmbientSamplerTests
{
    private const int Width = ScreenAmbientSampler.SampleWidth;
    private const int Height = ScreenAmbientSampler.SampleHeight;

    private static byte[] SolidFrame(byte blue, byte green, byte red)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 0xFF;
        }

        return pixels;
    }

    [Fact]
    public void SolidRedScreenYieldsRedInEveryZone()
    {
        uint[] zones = ScreenAmbientSampler.ComputeEdgeZones(SolidFrame(0, 0, 0xFF), Width, Height, 100);

        Assert.Equal(ScreenAmbientSampler.ZoneCount, zones.Length);
        Assert.All(zones, zone => Assert.Equal(0x0000FFu, zone)); // R in the low byte (OpenRGB wire order)
    }

    [Fact]
    public void BrightnessScalesEveryChannel()
    {
        uint[] zones = ScreenAmbientSampler.ComputeEdgeZones(SolidFrame(0xFF, 0xFF, 0xFF), Width, Height, 50);

        uint expected = 0x80u | (0x80u << 8) | (0x80u << 16); // 255 * 0.5 rounded = 128
        Assert.All(zones, zone => Assert.Equal(expected, zone));
    }

    [Fact]
    public void TopAndBottomEdgesReadDifferentScreenRegions()
    {
        // Top half green, bottom half blue.
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = ((y * Width) + x) * 4;
                if (y < Height / 2)
                {
                    pixels[offset + 1] = 0xFF; // green
                }
                else
                {
                    pixels[offset] = 0xFF; // blue
                }
            }
        }

        uint[] zones = ScreenAmbientSampler.ComputeEdgeZones(pixels, Width, Height, 100);

        Assert.Equal(0x00FF00u, zones[0]);  // first top zone → green
        Assert.Equal(0xFF0000u, zones[12]); // a bottom zone (index 8+4=12 starts the bottom run) → blue
    }

    [Theory]
    [InlineData(9)]   // Kraken ring
    [InlineData(64)]  // case strip
    [InlineData(126)] // keyboard
    [InlineData(1)]
    public void ZoneRingMapsOntoAnyLedCount(int ledCount)
    {
        uint[] zones = new uint[ScreenAmbientSampler.ZoneCount];
        for (int index = 0; index < zones.Length; index++)
        {
            zones[index] = (uint)index;
        }

        uint[] frame = ScreenAmbientSampler.MapZonesToLeds(zones, ledCount);

        Assert.Equal(ledCount, frame.Length);
        Assert.Equal(0u, frame[0]);
        Assert.All(frame, colour => Assert.InRange(colour, 0u, (uint)(zones.Length - 1)));
        if (ledCount >= zones.Length)
        {
            // With more LEDs than zones every zone must appear.
            Assert.Equal(zones.Length, frame.Distinct().Count());
        }
    }

    [Fact]
    public void UndersizedBufferIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ScreenAmbientSampler.ComputeEdgeZones(new byte[16], Width, Height, 100));
    }
}
