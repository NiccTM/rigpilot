using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the native Kraken X3 lighting report construction (byte-exact
/// against the liquidctl-derived protocol), the request confirmation gate, and
/// the contained-process failure mapping. No real device is touched.
/// </summary>
public sealed class KrakenLightingTests
{
    [Fact]
    public void FixedColourReportMatchesTheDerivedProtocolExactly()
    {
        byte[] report = KrakenX3LightingWriter.BuildFixedColourReport(0x0A, 0x84, 0xFF);

        Assert.Equal(64, report.Length);
        Assert.Equal([0x2A, 0x04, 0x07, 0x07, 0x00, 0x32, 0x00], report[..7]);   // opcode, sync channel ×2, fixed mode, speed
        Assert.Equal([0x84, 0x0A, 0xFF], report[7..10]);                          // first colour triplet in GRB wire order (G, R, B)
        Assert.All(report[10..55], value => Assert.Equal(0, value));              // remaining 15 colour slots empty
        Assert.Equal([0x00, 0x01, 0x00, 40, 0x03], report[55..60]);               // direction, 1 colour, mode byte, static value, LED size
        Assert.All(report[60..], value => Assert.Equal(0, value));                // zero padding to 64
    }

    [Fact]
    public void OffReportCarriesZeroColoursAndTheSameFooterShape()
    {
        byte[] report = KrakenX3LightingWriter.BuildOffReport();

        Assert.Equal(64, report.Length);
        Assert.Equal([0x2A, 0x04, 0x07, 0x07, 0x00, 0x32, 0x00], report[..7]);
        Assert.All(report[7..55], value => Assert.Equal(0, value));
        Assert.Equal([0x00, 0x00, 0x00, 40, 0x03], report[55..60]);
    }

    [Theory]
    [InlineData(false, null, "requires explicit confirmation")]
    [InlineData(true, null, "exact-device confirmation")]
    [InlineData(true, "wrong:device", "exact-device confirmation")]
    public void RequestValidationRefusesMissingConfirmations(bool experimental, string? device, string expected)
    {
        KrakenLightingRequestV1 request = new(
            KrakenLightingRequestV1.CurrentSchemaVersion, "#FF0000", TurnOff: false, experimental, device);

        Assert.Contains(expected, request.Validate(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestValidationAcceptsAConfirmedColourOrOff()
    {
        KrakenLightingRequestV1 colour = new(
            KrakenLightingRequestV1.CurrentSchemaVersion, "#0A84FF", false, true, KrakenLightingRequestV1.ExactDeviceId);
        KrakenLightingRequestV1 off = new(
            KrakenLightingRequestV1.CurrentSchemaVersion, string.Empty, true, true, KrakenLightingRequestV1.ExactDeviceId);
        KrakenLightingRequestV1 badColour = new(
            KrakenLightingRequestV1.CurrentSchemaVersion, "red", false, true, KrakenLightingRequestV1.ExactDeviceId);

        Assert.Null(colour.Validate());
        Assert.Null(off.Validate());
        Assert.Contains("#RRGGBB", badColour.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainedWritePassesThroughACleanChildResult()
    {
        KrakenLightingResultV1 issued = new(
            KrakenLightingResultV1.CurrentSchemaVersion, KrakenLightingOutcome.WriteIssued, "NZXT Kraken X63", "written");
        ContainedKrakenLighting lighting = new(
            () => new FakeProcess(0, System.Text.Json.JsonSerializer.Serialize(issued, JsonDefaults.Options)));

        KrakenLightingResultV1 result = await lighting.WriteAsync(CancellationToken.None);

        Assert.Equal(KrakenLightingOutcome.WriteIssued, result.Outcome);
        Assert.Equal("NZXT Kraken X63", result.ProductName);
    }

    [Theory]
    [InlineData(1, "{}")]           // nonzero exit
    [InlineData(0, "not json")]     // garbage output
    [InlineData(0, "")]             // empty output
    public async Task ContainedWriteMapsEveryFailureToANonIssuedOutcome(int exitCode, string output)
    {
        ContainedKrakenLighting lighting = new(() => new FakeProcess(exitCode, output));

        KrakenLightingResultV1 result = await lighting.WriteAsync(CancellationToken.None);

        Assert.NotEqual(KrakenLightingOutcome.WriteIssued, result.Outcome);
    }

    [Fact]
    public async Task ContainedWriteMapsAStartFailureToANonIssuedOutcome()
    {
        ContainedKrakenLighting lighting = new(() => throw new InvalidOperationException("no host"));

        KrakenLightingResultV1 result = await lighting.WriteAsync(CancellationToken.None);

        Assert.Equal(KrakenLightingOutcome.Failed, result.Outcome);
    }

    private sealed class FakeProcess(int exitCode, string output) : IControllerDiscoveryProcess
    {
        public Task<ControllerDiscoveryProcessExit> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ControllerDiscoveryProcessExit(exitCode, output));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
