using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ContainedHidInventoryTests
{
    [Fact]
    public async Task SuccessfulProbeReturnsHidInventory()
    {
        HidInventoryResultV1 payload = new(
            HidInventoryOutcome.Succeeded,
            [new HidDeviceInventoryItemV1(0x1E71, 0x2007, 0xFF00, 0x01, "VendorDefined", "NZXT USB Device", "NZXT")],
            "Enumerated 1 HID devices.");
        ContainedHidInventory inventory = new(() => new FakeProcess(0, Serialize(payload)));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.Succeeded, result.Outcome);
        HidDeviceInventoryItemV1 device = Assert.Single(result.Devices);
        Assert.Equal("VendorDefined", device.DeviceClass);
        Assert.Equal(0x1E71, device.VendorId);
    }

    [Fact]
    public async Task NativeCrashIsContainedAsFailedWithNoDevices()
    {
        ContainedHidInventory inventory = new(() => new FakeProcess(
            throwOnWait: new ControllerDiscoveryProcessException("Native access violation.", exitCode: -1073741819)));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task ProcessStartFailureIsContained()
    {
        ContainedHidInventory inventory = new(
            () => throw new ControllerDiscoveryProcessException("could not start"));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task TimeoutIsContained()
    {
        ContainedHidInventory inventory = new(
            () => new FakeProcess(throwOnWait: new TimeoutException("hung")));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task NonZeroExitCodeIsContained()
    {
        ContainedHidInventory inventory = new(() => new FakeProcess(3, "partial output with no json"));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task MalformedOutputIsContained()
    {
        ContainedHidInventory inventory = new(() => new FakeProcess(0, "{ this is not valid json"));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task NonSuccessOutcomeNeverLeaksDevices()
    {
        // A misbehaving child that reports a failure outcome but still attaches a device
        // list must not have that list surface as usable inventory.
        HidInventoryResultV1 payload = new(
            HidInventoryOutcome.EnumerationFailed,
            [new HidDeviceInventoryItemV1(0x1234, 0x5678, 0x01, 0x06, "Keyboard", "Leaked", null)],
            "Failed but attached a device anyway.");
        ContainedHidInventory inventory = new(() => new FakeProcess(0, Serialize(payload)));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.EnumerationFailed, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task BannerLinesBeforeJsonAreTolerated()
    {
        HidInventoryResultV1 payload = new(HidInventoryOutcome.Succeeded, [], "Enumerated 0 HID devices.");
        string output = "RigPilot adapter host banner\nanother diagnostic line\n" + Serialize(payload);
        ContainedHidInventory inventory = new(() => new FakeProcess(0, output));

        HidInventoryResultV1 result = await inventory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HidInventoryOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public async Task ProcessIsAlwaysDisposed()
    {
        FakeProcess process = new(0, "{ }");
        ContainedHidInventory inventory = new(() => process);

        await inventory.DiscoverAsync(CancellationToken.None);

        Assert.True(process.Disposed);
    }

    private static string Serialize(HidInventoryResultV1 payload) =>
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
