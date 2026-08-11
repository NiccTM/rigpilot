using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The GPU power helper is eligible for idle release while armed, because the driver keeps
/// the applied limit after the session exits. It nonetheless stayed resident for a full
/// 24-hour soak, and the cause was not the lifecycle code at all.
///
/// <para>The dashboard polled <c>GetGpuOcState</c> on its 5-second control-plane tick. That
/// read is not cached service-side, so it reached the power transport, which refreshed
/// <c>GpuSessionHost</c>'s last-activity stamp every 5 seconds against a 120-second timeout.
/// The idle deadline could never be reached. Proven live: the helper exited on its own within
/// two minutes of the dashboard closing.</para>
///
/// <para>These tests pin the arithmetic of that pinning, and the release the fix restores.
/// The page-visibility gate itself is a view-model concern; what matters here is the property
/// it exists to satisfy - a repeated read cadence below the idle timeout must not be able to
/// keep a session alive forever, and removing that cadence must let it go.</para>
/// </summary>
public sealed class GpuOcLiveStatePollGatingTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DashboardPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The defect, expressed as the rule it violated. Every poll resets the idle clock, so
    /// the largest idle interval ever observed is the poll interval - permanently below the
    /// timeout, no matter how long the service runs.
    /// </summary>
    [Fact]
    public void APollCadenceBelowTheIdleTimeoutCanNeverRelease()
    {
        // 24 hours of polling, evaluated at every tick.
        for (int tick = 1; tick <= 17_280; tick++)
        {
            Assert.False(
                GpuSessionHost.ShouldReleaseIdleSession(
                    inFlight: 0,
                    idleFor: DashboardPollInterval,
                    IdleTimeout),
                $"tick {tick}: a 5 s poll cadence left the session releasable, which is not the "
                    + "arithmetic that produced 24 h of residency.");
        }
    }

    /// <summary>
    /// And the fix: once the page that displays live values is not on screen, nothing refreshes
    /// the stamp, so the very next timer tick past the timeout releases it.
    /// </summary>
    [Fact]
    public void WithoutThePollTheSessionReleasesAtTheTimeout()
    {
        Assert.True(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: IdleTimeout,
            IdleTimeout));
    }

    /// <summary>
    /// The power family carries no hold predicate - unlike clock, whose applied offset dies
    /// with its session. Nothing about power was ever meant to pin it, which is why the cause
    /// had to be external activity.
    /// </summary>
    [Fact]
    public void PowerHasNoAppliedStateSuppressionUnlikeClock()
    {
        // holdsLiveState defaults to false: the power transport passes no predicate at all.
        Assert.True(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: IdleTimeout,
            IdleTimeout,
            holdsLiveState: false));

        // The clock transport does pass one, and it must still suppress release.
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: TimeSpan.FromHours(24),
            IdleTimeout,
            holdsLiveState: true));
    }

    /// <summary>
    /// A read in flight when the timer fires is still protected. The fix must not turn
    /// "stop extending the deadline" into "kill a session mid-read".
    /// </summary>
    [Fact]
    public void AReadInFlightIsNeverKilledEvenPastTheTimeout()
    {
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 1,
            idleFor: IdleTimeout * 100,
            IdleTimeout));
    }

    /// <summary>The fan family opts out by passing no timeout; that must be unaffected.</summary>
    [Fact]
    public void TheFanSessionStillNeverReleasesOnTimeAlone()
    {
        Assert.False(GpuSessionHost.ShouldReleaseIdleSession(
            inFlight: 0,
            idleFor: TimeSpan.FromHours(24),
            idleTimeout: null));
    }
}
