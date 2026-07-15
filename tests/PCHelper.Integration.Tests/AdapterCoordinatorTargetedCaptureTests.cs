using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Covers <see cref="AdapterCoordinator.CaptureAdapterCapabilitiesAsync"/>, the targeted
/// single-adapter re-probe used to refresh GPU-fan capability state synchronously right
/// after arming. The runtime relies on this reflecting the live armed flag so an
/// ApplyProfileV2 issued immediately after arming no longer races a backgrounded refresh.
/// </summary>
public sealed class AdapterCoordinatorTargetedCaptureTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private static string CapabilityId => $"{NvidiaGpuFanAdapter.CapabilityPrefix}{ChannelId}";

    [Fact]
    public async Task TargetedCaptureReflectsTheArmedFlagWithoutAFullCapture()
    {
        bool armed = false;
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(50, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, () => armed);

        CapabilityDescriptor disarmed = Assert.Single(
            await AdapterCoordinator.CaptureAdapterCapabilitiesAsync(adapter, default));
        Assert.Equal(CapabilityId, disarmed.Id);
        Assert.Equal(CapabilityAccessState.ReadOnly, disarmed.State);

        // Flip the live flag and re-capture only this adapter: the new state is visible
        // immediately, which is exactly what the arm handler depends on.
        armed = true;
        CapabilityDescriptor live = Assert.Single(
            await AdapterCoordinator.CaptureAdapterCapabilitiesAsync(adapter, default));
        Assert.Equal(CapabilityAccessState.Experimental, live.State);
    }

    private sealed class FakeGpuFanCoolerTransport(GpuFanBounds? bounds) : IGpuFanCoolerTransport
    {
        private GpuFanChannelState _state = new(GpuFanControlPolicy.Automatic, null, 30);

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(bounds);

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(_state);

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
        {
            _state = new GpuFanChannelState(GpuFanControlPolicy.Manual, dutyPercent, dutyPercent);
            return Task.CompletedTask;
        }

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
        {
            _state = new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, _state.MeasuredDutyPercent);
            return Task.CompletedTask;
        }
    }
}
