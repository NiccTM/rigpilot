using PCHelper.Adapters;
using Xunit.Abstractions;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the real NVAPI (NvAPIWrapper) fan transport. It reads bounds/state
/// and confirms the write gate refuses without an arm. It never commands a fan,
/// and tolerates NVAPI/NVIDIA being unavailable on the runner.
/// </summary>
public sealed class NvApiGpuFanCoolerTransportTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealNvApiTransportReadsBoundsAndRefusesWriteWithoutArm()
    {
        if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport transport, out string message))
        {
            output.WriteLine($"NVAPI unavailable on this runner: {message}");
            return;
        }

        using (transport)
        {
            output.WriteLine($"NVAPI load: {message}");
            Assert.True(transport.CanWrite);

            GpuFanBounds? bounds = await transport.ReadBoundsAsync("0", default);
            GpuFanChannelState state = await transport.ReadStateAsync("0", default);
            output.WriteLine($"bounds={(bounds is null ? "null" : $"[{bounds.FloorPercent},{bounds.CeilingPercent}]%")} policy={state.Policy} level={state.MeasuredDutyPercent}");

            if (bounds is not null)
            {
                Assert.InRange(bounds.FloorPercent, 0, 100);
                Assert.True(bounds.CeilingPercent >= bounds.FloorPercent);
            }

            // Write must refuse: not armed, no env opt-in.
            GpuFanSafetyException refused = await Assert.ThrowsAsync<GpuFanSafetyException>(
                () => transport.SetManualDutyAsync("0", 60, default));
            output.WriteLine($"write correctly refused: {refused.Message}");
        }
    }
}
