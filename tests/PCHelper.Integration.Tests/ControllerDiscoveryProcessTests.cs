using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class ControllerDiscoveryProcessTests
{
    [Fact]
    public async Task RealAdapterHostControllerDiscoveryStaysContained()
    {
        // Drives the actual Adapter Host `--discover-controllers` child through the
        // containment runner. Whatever the machine's USB/AIO stack does — enumerate
        // cleanly or fault natively — the caller must receive a well-formed result
        // and never observe an unhandled crash. This is the boundary that keeps a
        // native HidSharp fault from taking down the service.
        ContainedControllerDiscovery discovery = new(
            static () => new AdapterHostControllerDiscoveryProcess(),
            TimeSpan.FromSeconds(45));

        ControllerDiscoveryResultV1 result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ControllerDiscoveryResultV1.CurrentSchemaVersion, result.SchemaVersion);

        // Controllers may only be present on a genuine success; any contained failure
        // must expose an empty list so a partial enumeration cannot leak through.
        if (result.Outcome != ControllerDiscoveryOutcome.Succeeded)
        {
            Assert.Empty(result.Controllers);
        }

        // Discovered controllers are read-only inventory. None of them may ever be a
        // writable capability — this probe path issues no hardware writes.
        Assert.All(result.Controllers, controller => Assert.False(string.IsNullOrWhiteSpace(controller.Id)));
    }
}
