using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// A live GPU-fan auto-mode switch reproduced the driver refusing the restore-to-
/// default call itself (NVAPI_INVALID_USER_PRIVILEGE) moments after the preceding
/// apply's settle-poll made a burst of rapid NVAPI calls — every caller of this
/// restore (rollback, explicit reset, cooling-graph recovery) escalates an
/// unrecovered failure straight to a full hardware write lock. These tests pin
/// that a transient restore failure is retried before giving up, and that a
/// persistent failure still fails closed rather than retrying forever.
/// </summary>
public sealed class GpuFanResetRetryTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task ResetToDefaultRetriesAfterATransientRestoreFailure()
    {
        FlakyRestoreTransport transport = new(failuresBeforeSuccess: 2);
        int delays = 0;
        NvidiaGpuFanAdapter adapter = new(
            transport, "nvidia:gpu-0", "0", () => true,
            settleDelay: (_, _) => { delays++; return Task.CompletedTask; });

        await adapter.ResetToDefaultAsync("gpufan.duty:0", CancellationToken.None);

        Assert.Equal(3, transport.Attempts);
        // No burst preceded this restore, so no cooldown: one retry delay per failure.
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task ResetToDefaultDoesNotPauseWhenNoNvApiBurstPrecededIt()
    {
        // The cooldown protects one specific precondition — a restore issued moments after a
        // settle-poll burst gets refused because the rapid NVAPI traffic destabilised the
        // driver session. Startup, shutdown, and recovery restores follow no such burst, and
        // paying it there cost seconds on every control: multiplied across fan channels,
        // power, and clock it was most of a ~90 s shutdown, which is what made runtime
        // deployments miss their handshake window.
        FlakyRestoreTransport transport = new(failuresBeforeSuccess: 0);
        int delays = 0;
        NvidiaGpuFanAdapter adapter = new(
            transport, "nvidia:gpu-0", "0", () => true,
            settleDelay: (_, _) => { delays++; return Task.CompletedTask; });

        await adapter.ResetToDefaultAsync("gpufan.duty:0", CancellationToken.None);

        Assert.Equal(1, transport.Attempts);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task ResetToDefaultStillFailsClosedAfterExhaustingRetries()
    {
        // A persistent driver refusal must not retry forever — it has to surface
        // as a real failure so the caller's own recovery/lock logic still applies.
        FlakyRestoreTransport transport = new(failuresBeforeSuccess: int.MaxValue);
        NvidiaGpuFanAdapter adapter = new(transport, "nvidia:gpu-0", "0", () => true, settleDelay: NoDelay);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ResetToDefaultAsync("gpufan.duty:0", CancellationToken.None));
        Assert.Equal(6, transport.Attempts);
    }

    [Fact]
    public async Task ResetToDefaultStillCoolsDownWhenASettlePollBurstPrecededIt()
    {
        // The other half of the contract, and the one that must not regress: after a real
        // settle-poll burst the driver session HAS been destabilised, and the restore still
        // waits before its first attempt. Skipping it there is what produced
        // NVAPI_INVALID_USER_PRIVILEGE on the safety restore in the first place.
        StubbornFanTransport transport = new();
        int delays = 0;
        NvidiaGpuFanAdapter adapter = new(
            transport, "nvidia:gpu-0", "0", () => true,
            settleDelay: (_, _) => { delays++; return Task.CompletedTask; });

        // A verify against a fan that never reaches target drives the settle-poll loop, which
        // is the burst the cooldown exists for.
        PreparedAction prepared = await adapter.PrepareAsync(
            new ProfileAction("burst", NvidiaGpuFanAdapter.AdapterId, "gpufan.duty:0", ControlValue.FromNumeric(80), true, 0),
            CancellationToken.None);
        await adapter.ApplyAsync(prepared, CancellationToken.None);
        await adapter.VerifyAsync(prepared, CancellationToken.None);
        int delaysAfterBurst = delays;
        Assert.True(delaysAfterBurst > 0, "the settle poll should have delayed at least once");

        await adapter.ResetToDefaultAsync("gpufan.duty:0", CancellationToken.None);

        // Exactly one extra delay: the cooldown before the (successful) first attempt.
        Assert.Equal(delaysAfterBurst + 1, delays);
        Assert.Equal(1, transport.RestoreAttempts);
    }

    /// <summary>A fan that accepts a manual duty but never reaches it, so the settle poll runs.</summary>
    private sealed class StubbornFanTransport : IGpuFanCoolerTransport
    {
        public int RestoreAttempts { get; private set; }

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult<GpuFanBounds?>(new GpuFanBounds(30, 100));

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken) =>
            // Manual policy, but parked far from any requested duty, so the poll keeps retrying.
            Task.FromResult(new GpuFanChannelState(GpuFanControlPolicy.Manual, 30, 30));

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
        {
            RestoreAttempts++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Reports automatic-restore failures for a configurable number of attempts, then succeeds.</summary>
    private sealed class FlakyRestoreTransport(int failuresBeforeSuccess) : IGpuFanCoolerTransport
    {
        public int Attempts { get; private set; }

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult<GpuFanBounds?>(new GpuFanBounds(30, 100));

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null));

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= failuresBeforeSuccess)
            {
                throw new InvalidOperationException("Driver refused the restore-to-default call.");
            }

            return Task.CompletedTask;
        }
    }
}
