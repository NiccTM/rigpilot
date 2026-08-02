using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// An armed-but-idle GPU session helper is dropped so it stops costing roughly 30 MB
/// private / 60 MB working set for nothing, and respawns transparently on the next write.
///
/// Two properties make that safe, and they are what these tests pin: an operation that is
/// in flight is never killed underneath, and a host that was given no idle timeout is never
/// released however long it sits. The second is the fan's protection — releasing the fan
/// helper hands the cooler back to firmware, so <c>RemoteGpuFanCoolerTransport</c>
/// deliberately passes no timeout while the power and clock transports do.
/// </summary>
public sealed class GpuSessionIdleReleaseTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public void AnIdleSessionPastItsTimeoutIsReleased()
    {
        Assert.True(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: IdleTimeout + TimeSpan.FromSeconds(1),
            IdleTimeout));
    }

    [Fact]
    public void ASessionExactlyAtItsTimeoutIsReleased()
    {
        Assert.True(GpuSessionHost.ShouldReleaseIdleSession(0, IdleTimeout, IdleTimeout));
    }

    [Fact]
    public void ASessionStillInsideItsTimeoutIsKept()
    {
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: IdleTimeout - TimeSpan.FromSeconds(1),
            IdleTimeout));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AnOperationInFlightIsNeverKilledUnderneath(int inFlight)
    {
        // The idle stamp is refreshed on entry and exit, so a long operation should not be
        // able to look idle — but a send that outlives the timeout must still be safe, and
        // the in-flight count is what guarantees that rather than the timing.
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight,
            idleFor: IdleTimeout * 10,
            IdleTimeout));
    }

    /// <summary>
    /// The clock helper's exemption, and the reason it exists.
    ///
    /// <para>An NVAPI pstates20 clock delta lives with the session that set it. Releasing an
    /// idle helper therefore reverted a verified overclock to stock: on an RTX 3090 a saved
    /// +49/+126 MHz applied and read-back verified at service start, then read back as 0/0
    /// once the two-minute timeout dropped the child, while the NVML power limit applied in
    /// the same transaction survived. Nothing was logged, because releasing an idle child is
    /// routine.</para>
    ///
    /// <para>Idle release still applies to a resting session; it is only suppressed while a
    /// non-stock offset is actually being held.</para>
    /// </summary>
    [Fact]
    public void ASessionHoldingLiveStateIsNeverReleasedHoweverLongItIsIdle()
    {
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: TimeSpan.FromHours(1),
            IdleTimeout,
            holdsLiveState: true));
    }

    [Fact]
    public void ASessionHoldingNothingIsStillReleasedOnTime()
    {
        // Stock is not "held" state, so an untouched service still ends up with no session.
        Assert.True(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: IdleTimeout + TimeSpan.FromSeconds(1),
            IdleTimeout,
            holdsLiveState: false));
    }

    /// <summary>Holding state must not override the in-flight protection either way.</summary>
    [Fact]
    public void HoldingLiveStateDoesNotWeakenTheInFlightGuard()
    {
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 1,
            idleFor: IdleTimeout * 10,
            IdleTimeout,
            holdsLiveState: true));
    }

    [Fact]
    public void AHostWithNoIdleTimeoutIsNeverReleased()
    {
        // This is the fan's exemption. The fan transport opts out by passing no timeout,
        // so however long its session sits unused it is kept: dropping it would return the
        // cooler to firmware and silently abandon an applied duty or a running graph.
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: TimeSpan.FromHours(1),
            idleTimeout: null));
    }
}
