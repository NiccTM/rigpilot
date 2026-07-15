using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ContainedControllerDiscoveryTests
{
    [Fact]
    public async Task SuccessfulProbeReturnsControllerInventory()
    {
        HardwareDevice controller = new(
            "lhm.device:/hid/0",
            "Test AIO Controller",
            DeviceKind.Controller,
            null,
            "Test AIO Controller",
            null,
            new Dictionary<string, string>());
        ControllerDiscoveryResultV1 payload = new(
            ControllerDiscoveryResultV1.CurrentSchemaVersion,
            ControllerDiscoveryOutcome.Succeeded,
            [controller],
            0,
            "Enumerated 1 controller device(s).",
            DateTimeOffset.UtcNow);
        ContainedControllerDiscovery discovery = new(() => new FakeProcess(0, Serialize(payload)));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.Succeeded, result.Outcome);
        Assert.Single(result.Controllers);
        Assert.Equal("lhm.device:/hid/0", result.Controllers[0].Id);
    }

    [Fact]
    public async Task NativeCrashIsContainedAsCrashedOutcome()
    {
        ContainedControllerDiscovery discovery = new(() => new FakeProcess(
            throwOnWait: new ControllerDiscoveryProcessException("Native access violation.", exitCode: -1073741819)));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.Crashed, result.Outcome);
        Assert.Empty(result.Controllers);
        Assert.Equal(-1073741819, result.ExitCode);
    }

    [Fact]
    public async Task ProcessStartFailureIsContained()
    {
        ContainedControllerDiscovery discovery = new(
            () => throw new ControllerDiscoveryProcessException("could not start"));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.Crashed, result.Outcome);
        Assert.Empty(result.Controllers);
    }

    [Fact]
    public async Task TimeoutIsContainedAsTimedOutOutcome()
    {
        ContainedControllerDiscovery discovery = new(
            () => new FakeProcess(throwOnWait: new TimeoutException("hung")));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.TimedOut, result.Outcome);
        Assert.Empty(result.Controllers);
    }

    [Fact]
    public async Task NonZeroExitCodeIsContainedAsCrashed()
    {
        ContainedControllerDiscovery discovery = new(() => new FakeProcess(3, "partial output with no json"));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.Crashed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Empty(result.Controllers);
    }

    [Fact]
    public async Task MalformedOutputIsContainedAsEnumerationFailed()
    {
        ContainedControllerDiscovery discovery = new(() => new FakeProcess(0, "{ this is not valid json"));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Controllers);
    }

    [Fact]
    public async Task EmptyOutputIsContainedAsEnumerationFailed()
    {
        ContainedControllerDiscovery discovery = new(() => new FakeProcess(0, "RigPilot adapter host banner\n"));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Controllers);
    }

    [Fact]
    public async Task NonSuccessOutcomeNeverLeaksControllers()
    {
        // A misbehaving child that reports a failure outcome but still attaches a
        // controller list must not have that list surface as usable inventory.
        HardwareDevice controller = new(
            "lhm.device:/hid/leak",
            "Leaked",
            DeviceKind.Controller,
            null,
            null,
            null,
            new Dictionary<string, string>());
        ControllerDiscoveryResultV1 payload = new(
            ControllerDiscoveryResultV1.CurrentSchemaVersion,
            ControllerDiscoveryOutcome.EnumerationFailed,
            [controller],
            0,
            "Enumeration failed but attached a device anyway.",
            DateTimeOffset.UtcNow);
        ContainedControllerDiscovery discovery = new(() => new FakeProcess(0, Serialize(payload)));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Controllers);
    }

    [Fact]
    public async Task BannerLinesBeforeJsonAreTolerated()
    {
        ControllerDiscoveryResultV1 payload = new(
            ControllerDiscoveryResultV1.CurrentSchemaVersion,
            ControllerDiscoveryOutcome.Succeeded,
            [],
            0,
            "Enumerated 0 controller device(s).",
            DateTimeOffset.UtcNow);
        string output = "some startup banner\nanother diagnostic line\n" + Serialize(payload);
        ContainedControllerDiscovery discovery = new(() => new FakeProcess(0, output));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Controllers);
    }

    [Fact]
    public async Task ProcessIsAlwaysDisposed()
    {
        FakeProcess process = new(0, "{ }");
        ContainedControllerDiscovery discovery = new(() => process);

        await discovery.DiscoverAsync(CancellationToken.None);

        Assert.True(process.Disposed);
    }

    private static string Serialize(ControllerDiscoveryResultV1 payload) =>
        JsonSerializer.Serialize(payload, JsonDefaults.Options);

    private sealed class FakeProcess(
        int exitCode = 0,
        string output = "",
        Exception? throwOnWait = null) : IControllerDiscoveryProcess
    {
        public bool Disposed { get; private set; }

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
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
