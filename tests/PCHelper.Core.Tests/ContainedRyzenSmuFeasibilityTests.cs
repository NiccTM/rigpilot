using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ContainedRyzenSmuFeasibilityTests
{
    [Fact]
    public async Task SuccessfulReadReturnsLimits()
    {
        RyzenSmuFeasibilityV1 payload = new(
            RyzenSmuFeasibilityV1.CurrentSchemaVersion,
            RyzenSmuFeasibilityOutcome.Succeeded,
            4,
            "56.53.0",
            "0x380805",
            142, 54.5, 95, 33.2, 90, 62.25, 140, 41.7,
            "Decoded.");
        ContainedRyzenSmuFeasibility feasibility = new(() => new FakeProcess(0, Serialize(payload)));

        RyzenSmuFeasibilityV1 result = await feasibility.ReadAsync(CancellationToken.None);

        Assert.Equal(RyzenSmuFeasibilityOutcome.Succeeded, result.Outcome);
        Assert.Equal(142, result.PptLimitWatts);
        Assert.Equal(140, result.EdcLimitAmperes);
    }

    [Fact]
    public async Task NonSuccessOutcomeNeverLeaksLimits()
    {
        RyzenSmuFeasibilityV1 payload = new(
            RyzenSmuFeasibilityV1.CurrentSchemaVersion,
            RyzenSmuFeasibilityOutcome.UnrecognisedPmTable,
            4,
            "56.53.0",
            "0x540104",
            999, 999, 999, 999, 999, 999, 999, 999,
            "Failed but attached limits anyway.");
        ContainedRyzenSmuFeasibility feasibility = new(() => new FakeProcess(0, Serialize(payload)));

        RyzenSmuFeasibilityV1 result = await feasibility.ReadAsync(CancellationToken.None);

        Assert.Equal(RyzenSmuFeasibilityOutcome.UnrecognisedPmTable, result.Outcome);
        Assert.Null(result.PptLimitWatts);
        Assert.Null(result.TdcLimitAmperes);
        Assert.Null(result.ThmLimitCelsius);
        Assert.Null(result.EdcLimitAmperes);
    }

    [Fact]
    public async Task CrashTimeoutStartFailureGarbageAndExitCodeAreContained()
    {
        ContainedRyzenSmuFeasibility crashed = new(() => new FakeProcess(
            throwOnWait: new ControllerDiscoveryProcessException("Native access violation.", exitCode: -1073741819)));
        ContainedRyzenSmuFeasibility hung = new(() => new FakeProcess(throwOnWait: new TimeoutException("hung")));
        ContainedRyzenSmuFeasibility unstartable = new(() => throw new ControllerDiscoveryProcessException("could not start"));
        ContainedRyzenSmuFeasibility garbled = new(() => new FakeProcess(0, "{ not json"));
        ContainedRyzenSmuFeasibility failedExit = new(() => new FakeProcess(5, "no json"));

        foreach (ContainedRyzenSmuFeasibility feasibility in new[] { crashed, hung, unstartable, garbled, failedExit })
        {
            RyzenSmuFeasibilityV1 result = await feasibility.ReadAsync(CancellationToken.None);
            Assert.Equal(RyzenSmuFeasibilityOutcome.Failed, result.Outcome);
            Assert.Null(result.PptLimitWatts);
        }
    }

    private static string Serialize(RyzenSmuFeasibilityV1 payload) =>
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
