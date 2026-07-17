using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the native Kraken X3 pump-speed report construction (byte-exact
/// against the liquidctl-derived protocol), the hard 60% pump floor at every
/// layer, the request confirmation gate, and the contained-process failure
/// mapping. No real device is touched.
/// </summary>
public sealed class KrakenPumpTests
{
    [Fact]
    public void FixedPumpReportMatchesTheDerivedProtocolExactly()
    {
        byte[] report = KrakenX3PumpWriter.BuildFixedPumpReport(75);

        Assert.Equal(64, report.Length);
        Assert.Equal([0x72, 0x01, 0x00, 0x00], report[..4]);         // speed opcode, pump channel
        Assert.All(report[4..44], value => Assert.Equal(75, value)); // flat 40-point profile (20..59 °C)
        Assert.All(report[44..], value => Assert.Equal(0, value));   // zero padding to 64
    }

    [Theory]
    [InlineData(0, 60)]    // never stopped
    [InlineData(20, 60)]   // liquidctl's own minimum is still below RigPilot's floor
    [InlineData(59, 60)]
    [InlineData(101, 100)]
    public void ReportBuilderHardClampsTheDutyToTheSafetyFloor(int requested, int expected)
    {
        byte[] report = KrakenX3PumpWriter.BuildFixedPumpReport(requested);

        Assert.All(report[4..44], value => Assert.Equal(expected, value));
    }

    [Theory]
    [InlineData(80, false, null, "requires explicit confirmation")]
    [InlineData(80, true, "wrong:device", "exact-device confirmation")]
    [InlineData(59, true, KrakenPumpRequestV1.ExactDeviceId, "safety floor")]
    [InlineData(0, true, KrakenPumpRequestV1.ExactDeviceId, "safety floor")]
    [InlineData(101, true, KrakenPumpRequestV1.ExactDeviceId, "safety floor")]
    public void RequestValidationRefusesMissingConfirmationsAndOutOfFloorDuties(
        int duty, bool experimental, string? device, string expected)
    {
        KrakenPumpRequestV1 request = new(
            KrakenPumpRequestV1.CurrentSchemaVersion, duty, experimental, device);

        Assert.Contains(expected, request.Validate(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestValidationAcceptsAConfirmedInFloorDuty()
    {
        KrakenPumpRequestV1 request = new(
            KrakenPumpRequestV1.CurrentSchemaVersion, 80, true, KrakenPumpRequestV1.ExactDeviceId);

        Assert.Null(request.Validate());
    }

    [Fact]
    public async Task ContainedWritePassesThroughACleanChildResult()
    {
        KrakenPumpResultV1 verified = new(
            KrakenPumpResultV1.CurrentSchemaVersion, KrakenPumpOutcome.ReadBackVerified,
            "NZXT Kraken X63", 80, 80, 2100, "read back");
        ContainedKrakenPump pump = new(
            () => new FakeProcess(0, System.Text.Json.JsonSerializer.Serialize(verified, JsonDefaults.Options)));

        KrakenPumpResultV1 result = await pump.WriteAsync(CancellationToken.None);

        Assert.Equal(KrakenPumpOutcome.ReadBackVerified, result.Outcome);
        Assert.Equal(2100, result.ObservedPumpRpm);
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(0, "not json")]
    [InlineData(0, "")]
    public async Task ContainedWriteMapsEveryFailureToANonIssuedOutcome(int exitCode, string output)
    {
        ContainedKrakenPump pump = new(() => new FakeProcess(exitCode, output));

        KrakenPumpResultV1 result = await pump.WriteAsync(CancellationToken.None);

        Assert.NotEqual(KrakenPumpOutcome.ReadBackVerified, result.Outcome);
        Assert.NotEqual(KrakenPumpOutcome.WriteIssued, result.Outcome);
    }

    [Fact]
    public async Task ContainedWriteMapsAStartFailureToANonIssuedOutcome()
    {
        ContainedKrakenPump pump = new(() => throw new InvalidOperationException("no host"));

        KrakenPumpResultV1 result = await pump.WriteAsync(CancellationToken.None);

        Assert.Equal(KrakenPumpOutcome.Failed, result.Outcome);
    }

    private sealed class FakeProcess(int exitCode, string output) : IControllerDiscoveryProcess
    {
        public Task<ControllerDiscoveryProcessExit> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ControllerDiscoveryProcessExit(exitCode, output));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
