using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the single-header AURA targeting added for passive ARGB devices on one
/// addressable header (the Cooler Master GPU sag bracket): the adapter-host
/// target grammar, the direct-frame channel encoding, and the request
/// contract's header validation. No real HID device is touched.
/// </summary>
public sealed class AuraHeaderTargetingTests
{
    [Theory]
    [InlineData("FF0000", "FF0000", false, null)]
    [InlineData("#FF0000", "#FF0000", false, null)]
    [InlineData("off", "", true, null)]
    [InlineData("FF0000@1", "FF0000", false, 1)]
    [InlineData("00FF7F@2", "00FF7F", false, 2)]
    [InlineData("off@2", "", true, 2)]
    public void TargetGrammarParsesColourOffAndHeaderSuffix(string value, string colour, bool off, int? header)
    {
        Assert.True(AuraUsbLightingWriter.TryParseTarget(value, out string parsedColour, out bool parsedOff, out int? parsedHeader));
        Assert.Equal(colour, parsedColour);
        Assert.Equal(off, parsedOff);
        Assert.Equal(header, parsedHeader);
    }

    [Theory]
    [InlineData("FF0000@0")]  // headers are 1-based
    [InlineData("FF0000@3")]  // only two addressable headers exist
    [InlineData("FF0000@x")]
    [InlineData("FF00@1")]    // short colour
    [InlineData("")]
    public void TargetGrammarRejectsInvalidForms(string value)
    {
        Assert.False(AuraUsbLightingWriter.TryParseTarget(value, out _, out _, out _));
    }

    [Fact]
    public void DirectFrameEncodesChannelAndApplyBit()
    {
        byte[] plain = AuraUsbLightingWriter.BuildDirectFrame(1, apply: false, 0, 0x11, 0x22, 0x33, 20);
        byte[] applied = AuraUsbLightingWriter.BuildDirectFrame(1, apply: true, 100, 0x11, 0x22, 0x33, 20);

        Assert.Equal(0xEC, plain[0]);
        Assert.Equal(0x40, plain[1]);
        Assert.Equal(0x01, plain[2]);        // channel 1, no apply bit
        Assert.Equal(0x81, applied[2]);      // channel 1 | apply bit 0x80
        Assert.Equal(100, applied[3]);       // start LED
        Assert.Equal(0x11, plain[5]);        // first RGB triplet
        Assert.Equal(0x22, plain[6]);
        Assert.Equal(0x33, plain[7]);
    }

    [Fact]
    public void WriterRefusesAnOutOfRangeHeaderBeforeTouchingHid()
    {
        AuraLightingResultV1 result = AuraUsbLightingWriter.Write("FF0000", turnOff: false, headerIndex: 3);

        Assert.Equal(KrakenLightingOutcome.Failed, result.Outcome);
        Assert.Contains("1..2", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(0, "1..2")]
    [InlineData(3, "1..2")]
    public void RequestContractValidatesTheHeaderIndex(int? header, string? expectedRefusal)
    {
        AuraLightingRequestV1 request = new(
            AuraLightingRequestV1.CurrentSchemaVersion,
            "FF0000",
            TurnOff: false,
            ConfirmExperimental: true,
            AuraLightingRequestV1.ExactDeviceId,
            header);

        string? refusal = request.Validate();
        if (expectedRefusal is null)
        {
            Assert.Null(refusal);
        }
        else
        {
            Assert.NotNull(refusal);
            Assert.Contains(expectedRefusal, refusal, StringComparison.Ordinal);
        }
    }
}
