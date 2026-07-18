using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

public sealed class VendorTelemetryAdapterTests
{
    [Fact]
    public async Task NvmlHealthRequiresSuccessfulInitialisation()
    {
        await using NvmlTelemetryAdapter adapter = new();

        AdapterHealth beforeProbe = await adapter.GetHealthAsync(CancellationToken.None);

        Assert.False(beforeProbe.Healthy);
        Assert.NotEmpty(beforeProbe.Errors);
    }

    [Fact]
    public async Task NvmlAdapterNeverAdvertisesAWriteCapability()
    {
        await using NvmlTelemetryAdapter adapter = new();

        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        IReadOnlyList<SensorSample> sensors = await adapter.ReadSensorsAsync(CancellationToken.None);

        Assert.All(probe.Capabilities, capability =>
        {
            Assert.Equal(CapabilityAccessState.ReadOnly, capability.State);
            Assert.False(capability.CanResetToDefault);
            Assert.Contains(capability.Domain, new[] { ControlDomain.Gpu, ControlDomain.Cooling });
            if (capability.Id.StartsWith("nvml.fan:", StringComparison.Ordinal))
            {
                Assert.NotNull(capability.Range);
                Assert.True(capability.Range!.Minimum >= AdaptiveCoolingProfileFactory.UncalibratedFloorDutyPercent);
            }
            if (capability.Id.StartsWith("nvml.fan-transport:", StringComparison.Ordinal))
            {
                // The transport-feasibility card is inventory evidence only: no range,
                // no reset, and it must never be anything but ReadOnly.
                Assert.Equal(CapabilityAccessState.ReadOnly, capability.State);
                Assert.Null(capability.Range);
                Assert.False(capability.CanResetToDefault);
            }
        });
        Assert.All(sensors, sample => Assert.Equal("nvidia.nvml", sample.AdapterId));
    }

    [Fact]
    public async Task NvmlAdapterWriteMethodsAreHardBlocked()
    {
        // A structural guard: the telemetry adapter must never gain a live write path,
        // even though its capability cards now report that a fan-write transport is
        // feasible on some drivers. Prepare/Apply/Verify/Reset must throw.
        await using NvmlTelemetryAdapter adapter = new();
        ProfileAction action = new(
            "action-1",
            "nvidia.nvml",
            "nvml.fan:test:0",
            ControlValue.FromNumeric(60),
            Required: true,
            Order: 0);
        PreparedAction prepared = new(action, null, DateTimeOffset.UtcNow, string.Empty);

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(action, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ApplyAsync(prepared, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.VerifyAsync(prepared, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ResetToDefaultAsync("nvml.fan:test:0", CancellationToken.None));
    }

    [Fact]
    public async Task IntelGraphicsControlAdapterIsReadOnlyAndFailsSafeWithoutIntel()
    {
        await using IntelGraphicsControlAdapter adapter = new();

        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        AdapterHealth health = await adapter.GetHealthAsync(CancellationToken.None);

        // On a system without the Intel driver, IGCL is absent: no capability is
        // surfaced and health is reported without error (nothing to control).
        Assert.All(probe.Capabilities, capability =>
        {
            Assert.Equal(CapabilityAccessState.ReadOnly, capability.State);
            Assert.False(capability.CanResetToDefault);
            Assert.Equal(ControlDomain.Gpu, capability.Domain);
        });
        Assert.True(health.Healthy);

        // The adapter never exposes a write, regardless of hardware.
        ProfileAction action = new("a", "intel.igcl", "igcl.feasibility:intel.igcl", ControlValue.FromNumeric(1), true, 0);
        PreparedAction prepared = new(action, null, DateTimeOffset.UtcNow, string.Empty);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(action, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ApplyAsync(prepared, CancellationToken.None));
    }

    [Fact]
    public async Task AmdGraphicsControlAdapterIsReadOnlyAndFailsSafeWithoutAmd()
    {
        await using AmdGraphicsControlAdapter adapter = new();

        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        AdapterHealth health = await adapter.GetHealthAsync(CancellationToken.None);

        // On a system without the AMD Radeon driver, ADLX is absent: no capability
        // is surfaced and health is reported without error (nothing to control).
        Assert.All(probe.Capabilities, capability =>
        {
            Assert.Equal(CapabilityAccessState.ReadOnly, capability.State);
            Assert.False(capability.CanResetToDefault);
            Assert.Equal(ControlDomain.Gpu, capability.Domain);
        });
        Assert.True(health.Healthy);

        // The adapter never exposes a write, regardless of hardware.
        ProfileAction action = new("a", "amd.adlx", "adlx.feasibility:amd.adlx", ControlValue.FromNumeric(1), true, 0);
        PreparedAction prepared = new(action, null, DateTimeOffset.UtcNow, string.Empty);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.PrepareAsync(action, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ApplyAsync(prepared, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ResetToDefaultAsync("adlx.feasibility:amd.adlx", CancellationToken.None));
    }

    [Fact]
    public async Task DetectedDirectUsbHidDevicesRemainReadOnly()
    {
        await using WindowsPeripheralInventoryAdapter adapter = new();

        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);

        Assert.All(probe.Capabilities, capability =>
        {
            Assert.Equal(CapabilityAccessState.ReadOnly, capability.State);
            Assert.False(capability.CanResetToDefault);
            Assert.Contains(capability.Domain, new[] { ControlDomain.Lighting, ControlDomain.Other });
        });
    }
}
