using PCHelper.App;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class LightingColourwaysTests
{
    [Fact]
    public void StaticColourwayReturnsTheSuppliedColour()
    {
        RgbColour blue = new(0x4E, 0xA1, 0xFF);
        Assert.Equal(blue, LightingColourways.RepresentativeColour("static", blue));
    }

    [Fact]
    public void UnknownColourwayFallsBackToTheSuppliedColour()
    {
        RgbColour green = new(0x10, 0xC0, 0x30);
        Assert.Equal(green, LightingColourways.RepresentativeColour("does-not-exist", green));
    }

    [Fact]
    public void SunsetRepresentativeIsTheGradientMidpointAndWarm()
    {
        // Sunset lerps (255,140,0) -> (200,30,140); the midpoint R is 227.5, which rounds
        // to even (228), giving (228,85,70) — warm, red-dominant, clearly not the manual
        // blue that native devices showed before.
        RgbColour ignoredManual = new(0x4E, 0xA1, 0xFF);
        RgbColour sunset = LightingColourways.RepresentativeColour("sunset", ignoredManual);

        Assert.Equal(new RgbColour(228, 85, 70), sunset);
        Assert.True(sunset.Red > sunset.Blue, "Sunset should read warm (more red than blue).");
        Assert.NotEqual(ignoredManual, sunset);
    }

    [Theory]
    [InlineData("ocean")]
    [InlineData("lava")]
    [InlineData("rainbow")]
    [InlineData("puddles")]
    public void EveryGradientColourwayProducesAColourIndependentOfTheManualColour(string colourwayId)
    {
        RgbColour manual = new(0x4E, 0xA1, 0xFF);
        RgbColour representative = LightingColourways.RepresentativeColour(colourwayId, manual);

        // The point of the fallback: gradient colourways no longer echo the stale manual
        // colour on flat-colour devices.
        Assert.NotEqual(manual, representative);
    }
}
