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
    [InlineData("spectrum")]
    [InlineData("aurora")]
    [InlineData("neon")]
    [InlineData("gold")]
    [InlineData("ice")]
    [InlineData("forest")]
    [InlineData("stripes")]
    [InlineData("alert")]
    public void EveryGradientColourwayProducesAColourIndependentOfTheManualColour(string colourwayId)
    {
        RgbColour manual = new(0x4E, 0xA1, 0xFF);
        RgbColour representative = LightingColourways.RepresentativeColour(colourwayId, manual);

        // The point of the fallback: self-contained colourways no longer echo the stale
        // manual colour on flat-colour devices. (The colour-field-driven presets — static,
        // sparkle, comet — intentionally track the manual colour and are excluded.)
        Assert.NotEqual(manual, representative);
    }

    [Fact]
    public void EveryColourwayIdIsUniqueAndGeneratesALitFrame()
    {
        RgbColour manual = new(0x4E, 0xA1, 0xFF);
        HashSet<string> ids = [];
        foreach (LightingColourways.Colourway colourway in LightingColourways.All)
        {
            Assert.True(ids.Add(colourway.Id), $"Duplicate colourway id: {colourway.Id}");

            uint[] frame = LightingColourways.Generate(colourway.Id, 30, (manual.Red, manual.Green, manual.Blue), 100);

            Assert.Equal(30, frame.Length);
            Assert.Contains(frame, packed => packed != 0); // at least one lit LED — guards every pattern
        }
    }

    [Fact]
    public void ColourFieldDrivenPatternsTrackTheManualColour()
    {
        // sparkle places a bright dot of the manual colour at index 1; comet's head starts
        // near full manual colour. Both must reflect the chosen colour, not a fixed palette.
        RgbColour manual = new(0xFF, 0x40, 0x10);
        uint[] sparkle = LightingColourways.Generate("sparkle", 8, (manual.Red, manual.Green, manual.Blue), 100);

        Assert.Equal(manual.Red, (byte)(sparkle[1] & 0xFF));
        Assert.Equal(manual.Green, (byte)((sparkle[1] >> 8) & 0xFF));
        Assert.Equal(manual.Blue, (byte)((sparkle[1] >> 16) & 0xFF));
    }
}
