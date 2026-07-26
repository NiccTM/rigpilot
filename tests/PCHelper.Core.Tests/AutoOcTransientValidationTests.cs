using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The reason an auto-overclocker "passes the test and crashes in the game": a sustained
/// synthetic screen pins the GPU to one power-limited operating point — a low boost bin at
/// high voltage, the most forgiving part of the V/F curve. Games swing between load levels,
/// so the card spends much of its time in high boost bins at low voltage and repeatedly
/// transitions between them, which is where a marginal offset actually fails.
///
/// These tests pin the shape of the transient phase that exercises those transitions, and
/// the evidence its failure message must carry.
/// </summary>
public sealed class AutoOcTransientValidationTests
{
    [Fact]
    public void TheTransientPhaseRunsEnoughCyclesToBeMoreThanASingleTransition()
    {
        // One load/idle edge could pass by luck; a marginal candidate needs repeated
        // transitions before it is trusted.
        Assert.True(AutoOcV3Policy.TransientValidationCycles >= 3);
    }

    [Fact]
    public void EachLoadBurstIsLongEnoughToBoostButShortEnoughToStayTransient()
    {
        // Long enough for the card to climb into a boost state, short enough that the phase
        // stays a series of transitions rather than becoming another sustained screen.
        Assert.InRange(AutoOcV3Policy.TransientLoadDuration.TotalSeconds, 5, 30);
    }

    [Fact]
    public void TheIdleGapIsLongEnoughForClocksToActuallyFallBack()
    {
        // Without a real idle gap the next burst continues the previous load instead of
        // being a fresh transition, which would defeat the whole phase.
        Assert.InRange(AutoOcV3Policy.TransientIdleDuration.TotalSeconds, 3, 30);
    }

    [Fact]
    public void TheWholePhaseStaysAShortAdditionToTheRun()
    {
        // The value here is catching a game-crashing candidate, not adding many minutes to
        // an already long run.
        TimeSpan total = (AutoOcV3Policy.TransientLoadDuration + AutoOcV3Policy.TransientIdleDuration)
            * AutoOcV3Policy.TransientValidationCycles;

        Assert.InRange(total.TotalMinutes, 0.5, 5);
    }

    [Fact]
    public void AFailureNamesTheCycleSoAMarginalCandidateIsDistinguishableFromAnUnstableOne()
    {
        string message = AutoOcV3Policy.DescribeTransientFailure(5, 6, "display driver reset observed");

        Assert.Contains("cycle 5 of 6", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display driver reset observed", message, StringComparison.Ordinal);
        // The message must explain WHY a steady pass is not enough, or the result reads as a
        // spurious rejection of an overclock the user just watched succeed.
        Assert.Contains("games swing", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No profile was generated", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFailureWithoutDetailStillReadsAsACompleteSentence()
    {
        string message = AutoOcV3Policy.DescribeTransientFailure(1, 6, null);

        Assert.Contains("the screening monitor reported a failure", message, StringComparison.OrdinalIgnoreCase);
    }
}
