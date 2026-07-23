using PCHelper.Adapters;

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
        // One cooldown delay before the first attempt, plus one retry delay per failure.
        Assert.Equal(3, delays);
    }

    [Fact]
    public async Task ResetToDefaultStillPausesBeforeItsFirstAttemptWhenThatAttemptWorks()
    {
        // The cooldown runs before every restore, not just after a failure — it is
        // what gives the driver session a moment to settle after the preceding
        // settle-poll burst, so it cannot be skipped just because this call turns
        // out to succeed.
        FlakyRestoreTransport transport = new(failuresBeforeSuccess: 0);
        int delays = 0;
        NvidiaGpuFanAdapter adapter = new(
            transport, "nvidia:gpu-0", "0", () => true,
            settleDelay: (_, _) => { delays++; return Task.CompletedTask; });

        await adapter.ResetToDefaultAsync("gpufan.duty:0", CancellationToken.None);

        Assert.Equal(1, transport.Attempts);
        Assert.Equal(1, delays);
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
