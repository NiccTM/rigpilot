using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// A live fan test rejected a working write: commanding 50% read back 41% because
/// the fan was still ramping and the NVAPI cooler reports the live level, not the
/// setpoint. The manual policy flips immediately; only the RPM lags. Verification
/// now lets the level settle toward the target before deciding — while still
/// rejecting a fan that never gets there, and without waiting when the write did
/// not take at all.
/// </summary>
public sealed class GpuFanReadBackSettlingTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task VerifyPollsUntilARampingFanReachesTheCommandedDuty()
    {
        // The exact live sequence: 41% mid-ramp, then converging to the commanded 50%.
        RampingFanTransport transport = new(rampLevels: [41, 46, 50]);
        NvidiaGpuFanAdapter adapter = new(transport, "nvidia:gpu-0", "0", () => true, settleDelay: NoDelay);
        PreparedAction prepared = await PrepareAndApply(adapter, transport, 50);

        ActionVerification result = await adapter.VerifyAsync(prepared, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(transport.Reads > 1, "verification should have polled while the fan ramped");
    }

    [Fact]
    public async Task VerifyStillRejectsAFanThatNeverReachesTheCommandedDuty()
    {
        // A fan stuck far from the setpoint after the whole settle window is a real
        // failure — the transaction must still reject and roll back.
        RampingFanTransport transport = new(rampLevels: [20]);
        NvidiaGpuFanAdapter adapter = new(transport, "nvidia:gpu-0", "0", () => true, settleDelay: NoDelay);
        PreparedAction prepared = await PrepareAndApply(adapter, transport, 50);

        ActionVerification result = await adapter.VerifyAsync(prepared, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task VerifyDoesNotWaitWhenTheManualPolicyNeverTook()
    {
        // If the write did not flip the policy to Manual there is nothing to wait
        // for; the settle delay must never fire and the result must be a failure.
        RampingFanTransport transport = new(rampLevels: [0], policyTakes: false);
        int delays = 0;
        NvidiaGpuFanAdapter adapter = new(
            transport, "nvidia:gpu-0", "0", () => true,
            settleDelay: (_, _) => { delays++; return Task.CompletedTask; });
        PreparedAction prepared = await PrepareAndApply(adapter, transport, 50);

        ActionVerification result = await adapter.VerifyAsync(prepared, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task VerifyReturnsImmediatelyWhenTheFanIsAlreadyAtTarget()
    {
        // No ramp to wait out — a fan already at the commanded duty must verify on
        // the first read with no settle delay, so ordinary applies stay fast.
        RampingFanTransport transport = new(rampLevels: [50]);
        int delays = 0;
        NvidiaGpuFanAdapter adapter = new(
            transport, "nvidia:gpu-0", "0", () => true,
            settleDelay: (_, _) => { delays++; return Task.CompletedTask; });
        PreparedAction prepared = await PrepareAndApply(adapter, transport, 50);

        ActionVerification result = await adapter.VerifyAsync(prepared, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, delays);
    }

    private static async Task<PreparedAction> PrepareAndApply(
        NvidiaGpuFanAdapter adapter, RampingFanTransport transport, int duty)
    {
        PreparedAction prepared = await adapter.PrepareAsync(
            new ProfileAction("fan", NvidiaGpuFanAdapter.AdapterId, "gpufan.duty:0", ControlValue.FromNumeric(duty), Required: true, Order: 0),
            CancellationToken.None);
        await adapter.ApplyAsync(prepared, CancellationToken.None);
        transport.BeginRamp();
        return prepared;
    }

    /// <summary>
    /// Reports a fan that ramps: each read after a manual write returns the next
    /// level in the ramp sequence, and the last value sticks. Optionally the manual
    /// policy never takes, modelling a write that silently did nothing.
    /// </summary>
    private sealed class RampingFanTransport(int[] rampLevels, bool policyTakes = true) : IGpuFanCoolerTransport
    {
        private bool _manual;
        private bool _ramping;
        private int _rampIndex;

        public int Reads { get; private set; }

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        public void BeginRamp() => _ramping = true;

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult<GpuFanBounds?>(new GpuFanBounds(30, 100));

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
        {
            if (!_ramping)
            {
                // Pre-apply reads (Prepare) see the stock automatic curve.
                return Task.FromResult(new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, 0));
            }

            Reads++;
            int level = rampLevels[Math.Min(_rampIndex, rampLevels.Length - 1)];
            _rampIndex++;
            GpuFanControlPolicy policy = _manual && policyTakes ? GpuFanControlPolicy.Manual : GpuFanControlPolicy.Automatic;
            return Task.FromResult(new GpuFanChannelState(policy, policy == GpuFanControlPolicy.Manual ? level : null, level));
        }

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
        {
            _manual = true;
            return Task.CompletedTask;
        }

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
        {
            _manual = false;
            return Task.CompletedTask;
        }
    }
}
