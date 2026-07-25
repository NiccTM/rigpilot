using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Auto OC must not start on a machine that is already reporting machine-check exceptions:
/// the faults are not caused by the candidate offset, so the screening verdict is
/// meaningless, and pushing an unstable machine harder risks a hard hang that no in-process
/// abort can catch. These tests pin exactly what blocks a run and what does not.
/// </summary>
public sealed class AutoOcPreflightPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 18, 0, 0, TimeSpan.Zero);

    private static HealthSystemSignal Whea(DateTimeOffset at) =>
        new(HealthSystemSignalKind.Whea, at, "A WHEA event (18) was observed in the Windows System log.");

    [Fact]
    public void ACleanMachineMayStartAutoOc()
    {
        Assert.Null(AutoOcPreflightPolicy.DescribeBlockingInstability([], Now));
    }

    [Fact]
    public void ARecentMachineCheckBlocksTheRun()
    {
        string? reason = AutoOcPreflightPolicy.DescribeBlockingInstability([Whea(Now.AddHours(-2))], Now);

        Assert.NotNull(reason);
        Assert.Contains("machine check", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRefusalReportsHowManyErrorsAndTheMostRecent()
    {
        string? reason = AutoOcPreflightPolicy.DescribeBlockingInstability(
            [Whea(Now.AddHours(-20)), Whea(Now.AddHours(-6)), Whea(Now.AddMinutes(-30))],
            Now);

        Assert.NotNull(reason);
        Assert.Contains("3 hardware errors", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMachineCheckOlderThanTheLookbackDoesNotBlock()
    {
        // A platform fixed days ago must not be blocked forever.
        DateTimeOffset stale = Now - AutoOcPreflightPolicy.InstabilityLookback - TimeSpan.FromMinutes(1);

        Assert.Null(AutoOcPreflightPolicy.DescribeBlockingInstability([Whea(stale)], Now));
    }

    [Fact]
    public void ADisplayDriverResetDoesNotBlockTheRun()
    {
        // A TDR is the fault class Auto OC screening is built to observe and recover from,
        // so it must not prevent a run from starting.
        HealthSystemSignal reset = new(
            HealthSystemSignalKind.DisplayDriverReset,
            Now.AddMinutes(-5),
            "A display-driver reset was observed.");

        Assert.Null(AutoOcPreflightPolicy.DescribeBlockingInstability([reset], Now));
    }

    [Fact]
    public void AnExplicitOverrideAllowsAKnowinglyUnstableMachine()
    {
        Assert.Null(AutoOcPreflightPolicy.DescribeBlockingInstability(
            [Whea(Now.AddMinutes(-1))],
            Now,
            overrideInstabilityBlock: true));
    }
}
