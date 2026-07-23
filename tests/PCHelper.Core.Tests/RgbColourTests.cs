using PCHelper.Contracts;

namespace PCHelper.Core.Tests;

public sealed class RgbColourTests
{
    [Theory]
    [InlineData("#4EA1FF", 0x4E, 0xA1, 0xFF)]
    [InlineData("4EA1FF", 0x4E, 0xA1, 0xFF)]
    [InlineData("  #4ea1ff  ", 0x4E, 0xA1, 0xFF)]
    [InlineData("000000", 0, 0, 0)]
    [InlineData("FFFFFF", 255, 255, 255)]
    public void TryParseAcceptsValidHex(string hex, int r, int g, int b)
    {
        Assert.True(RgbColour.TryParse(hex, out RgbColour colour));
        Assert.Equal((byte)r, colour.Red);
        Assert.Equal((byte)g, colour.Green);
        Assert.Equal((byte)b, colour.Blue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#FFF")]
    [InlineData("4EA1F")]
    [InlineData("4EA1FFF")]
    [InlineData("GGGGGG")]
    [InlineData("#12 34 56")]
    public void TryParseRejectsInvalidHex(string? hex)
    {
        Assert.False(RgbColour.TryParse(hex, out RgbColour colour));
        Assert.Equal(default, colour);
    }

    [Fact]
    public void ParseThrowsOnInvalid()
    {
        Assert.Throws<FormatException>(() => RgbColour.Parse("nope"));
    }

    [Fact]
    public void ToHexRoundTripsAndIsUppercaseWithoutHash()
    {
        RgbColour colour = RgbColour.Parse("#4ea1ff");
        Assert.Equal("4EA1FF", colour.ToHex());
        Assert.Equal("#4EA1FF", colour.ToString());
        Assert.Equal(colour, RgbColour.Parse(colour.ToHex()));
    }

    [Fact]
    public void OffIsBlack()
    {
        Assert.Equal(new RgbColour(0, 0, 0), RgbColour.Off);
        Assert.Equal("000000", RgbColour.Off.ToHex());
    }

    [Fact]
    public void EffectColourBridgeRoundTrips()
    {
        RgbColour colour = new(0x4E, 0xA1, 0xFF);
        EffectColourV1 effect = colour.ToEffectColour();
        Assert.Equal(0x4E, effect.Red);
        Assert.Equal(0xA1, effect.Green);
        Assert.Equal(0xFF, effect.Blue);
        Assert.Equal(colour, RgbColour.FromEffectColour(effect));
    }

    [Fact]
    public void BlendAndScaleBehaveLinearly()
    {
        RgbColour black = RgbColour.Off;
        RgbColour white = new(255, 255, 255);
        Assert.Equal(new RgbColour(128, 128, 128), RgbColour.Blend(black, white, 0.5));
        Assert.Equal(black, RgbColour.Blend(black, white, 0));
        Assert.Equal(white, RgbColour.Blend(black, white, 1));
        Assert.Equal(new RgbColour(128, 64, 32), new RgbColour(255, 128, 64).Scale(0.5));
        Assert.Equal(white, white.Scale(1));
        Assert.Equal(RgbColour.Off, white.Scale(0));
    }
}
