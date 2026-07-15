using PCHelper.Adapters;
using Xunit.Abstractions;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Verifies the triple gate on the real NVML fan transport. These tests exercise
/// the gate itself; they never actually command a fan, because the operator opt-in
/// environment variable is never set here. If NVML is unavailable on the runner the
/// transport simply fails to load and the gate tests are skipped for that reason.
/// </summary>
public sealed class NvmlGpuFanCoolerTransportTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealTransportReadOnlyPreflightReportsBoundsAndRefusesWrite()
    {
        // Read-only bench preflight against the real driver when NVML is present.
        // It reads bounds/state and confirms the write path refuses. It never writes.
        if (!NvmlGpuFanCoolerTransport.TryCreate(enableWrites: true, out NvmlGpuFanCoolerTransport transport, out string message))
        {
            output.WriteLine($"NVML unavailable on this runner: {message}");
            return;
        }

        using (transport)
        {
            output.WriteLine($"NVML load: {message}");
            GpuFanBounds? bounds = await transport.ReadBoundsAsync("0", default);
            GpuFanChannelState state = await transport.ReadStateAsync("0", default);
            output.WriteLine($"bounds={(bounds is null ? "null" : $"[{bounds.FloorPercent},{bounds.CeilingPercent}]%")} measuredDuty={state.MeasuredDutyPercent}");

            GpuFanSafetyException refused = await Assert.ThrowsAsync<GpuFanSafetyException>(
                () => transport.SetManualDutyAsync("0", 60, default));
            output.WriteLine($"write correctly refused: {refused.Message}");
        }
    }

    [Fact]
    public async Task WriteMethodsRefuseWithoutOperatorOptIn()
    {
        // Guard: make sure the opt-in is not set in this test's environment.
        Assert.NotEqual("1", Environment.GetEnvironmentVariable(NvmlGpuFanCoolerTransport.WriteOptInEnvironmentVariable));

        if (!NvmlGpuFanCoolerTransport.TryCreate(enableWrites: true, out NvmlGpuFanCoolerTransport transport, out _))
        {
            // No NVML runtime on this machine; nothing to arm. The gate cannot be
            // bypassed because there is no transport at all.
            return;
        }

        using (transport)
        {
            // Even though writes were enabled at construction, the operator opt-in is
            // absent, so every write path must refuse.
            await Assert.ThrowsAsync<GpuFanSafetyException>(() => transport.SetManualDutyAsync("0", 60, default));
            await Assert.ThrowsAsync<GpuFanSafetyException>(() => transport.RestoreAutomaticAsync("0", default));
        }
    }

    [Fact]
    public async Task ReadOnlyTransportNeverExposesSetters()
    {
        // With writes disabled, the setter delegates are never marshalled, and the
        // write methods refuse regardless of the environment.
        if (!NvmlGpuFanCoolerTransport.TryCreate(enableWrites: false, out NvmlGpuFanCoolerTransport transport, out _))
        {
            return;
        }

        using (transport)
        {
            await Assert.ThrowsAsync<GpuFanSafetyException>(() => transport.SetManualDutyAsync("0", 60, default));
        }
    }

    [Fact]
    public async Task ReadOnlyPreflightBoundsAndStateAreAllowedWhenNvmlIsPresent()
    {
        // Reads are always permitted (Prepare-only bench preflight). This asserts the
        // read path does not throw the write gate; it tolerates NVML being absent.
        if (!NvmlGpuFanCoolerTransport.TryCreate(enableWrites: false, out NvmlGpuFanCoolerTransport transport, out _))
        {
            return;
        }

        using (transport)
        {
            Exception? readError = await Record.ExceptionAsync(async () =>
            {
                await transport.ReadBoundsAsync("0", default);
                await transport.ReadStateAsync("0", default);
            });

            Assert.IsNotType<GpuFanSafetyException>(readError);
        }
    }
}
