using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ContainedKrakenTelemetryTests
{
    [Fact]
    public async Task SuccessfulReadReturnsTelemetry()
    {
        KrakenTelemetryV1 payload = new(
            KrakenTelemetryV1.CurrentSchemaVersion,
            KrakenTelemetryOutcome.Succeeded,
            "NZXT Kraken X63",
            33.4,
            2130,
            60,
            "Read a streamed status report.");
        ContainedKrakenTelemetry telemetry = new(() => new FakeProcess(0, Serialize(payload)));

        KrakenTelemetryV1 result = await telemetry.ReadAsync(CancellationToken.None);

        Assert.Equal(KrakenTelemetryOutcome.Succeeded, result.Outcome);
        Assert.Equal(33.4, result.LiquidTemperatureCelsius);
        Assert.Equal(2130, result.PumpSpeedRpm);
        Assert.Equal(60, result.PumpDutyPercent);
    }

    [Fact]
    public async Task NonSuccessOutcomeNeverLeaksReadings()
    {
        KrakenTelemetryV1 payload = new(
            KrakenTelemetryV1.CurrentSchemaVersion,
            KrakenTelemetryOutcome.NoStatusReport,
            "NZXT Kraken X63",
            99.9,
            9999,
            100,
            "Failed but attached readings anyway.");
        ContainedKrakenTelemetry telemetry = new(() => new FakeProcess(0, Serialize(payload)));

        KrakenTelemetryV1 result = await telemetry.ReadAsync(CancellationToken.None);

        Assert.Equal(KrakenTelemetryOutcome.NoStatusReport, result.Outcome);
        Assert.Null(result.LiquidTemperatureCelsius);
        Assert.Null(result.PumpSpeedRpm);
        Assert.Null(result.PumpDutyPercent);
    }

    [Fact]
    public async Task NativeCrashTimeoutStartFailureAndMalformedOutputAreContained()
    {
        ContainedKrakenTelemetry crashed = new(() => new FakeProcess(
            throwOnWait: new ControllerDiscoveryProcessException("Native access violation.", exitCode: -1073741819)));
        ContainedKrakenTelemetry hung = new(() => new FakeProcess(throwOnWait: new TimeoutException("hung")));
        ContainedKrakenTelemetry unstartable = new(() => throw new ControllerDiscoveryProcessException("could not start"));
        ContainedKrakenTelemetry garbled = new(() => new FakeProcess(0, "{ this is not valid json"));
        ContainedKrakenTelemetry failedExit = new(() => new FakeProcess(3, "no json"));

        foreach (ContainedKrakenTelemetry telemetry in new[] { crashed, hung, unstartable, garbled, failedExit })
        {
            KrakenTelemetryV1 result = await telemetry.ReadAsync(CancellationToken.None);
            Assert.Equal(KrakenTelemetryOutcome.Failed, result.Outcome);
            Assert.Null(result.LiquidTemperatureCelsius);
        }
    }

    private static string Serialize(KrakenTelemetryV1 payload) =>
        JsonSerializer.Serialize(payload, JsonDefaults.Options);

    private sealed class FakeProcess(
        int exitCode = 0,
        string output = "",
        Exception? throwOnWait = null) : IControllerDiscoveryProcess
    {
        public Task<ControllerDiscoveryProcessExit> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (throwOnWait is not null)
            {
                throw throwOnWait;
            }

            return Task.FromResult(new ControllerDiscoveryProcessExit(exitCode, output));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
