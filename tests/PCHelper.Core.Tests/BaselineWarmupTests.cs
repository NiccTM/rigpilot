using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Auto OC completed on the reference rig but shipped nothing, rejected for
/// baseline throughput variation of 4.03% against a 3% limit. The measurements
/// showed the cause was not instability but a cold card:
///
///   Baseline 1   throughput 302   76.0 °C   278 W
///   Baseline 2   throughput 290   80.3 °C   332 W
///   Baseline 3   throughput 290   83.2 °C   333 W
///
/// A GPU boosts highest when cold and settles as it heats, so sample 1 measured
/// the transient. Samples 2 and 3 — both taken warm — agree to 0.13%. The gate
/// was right; the measurement was taken too early.
/// </summary>
public sealed class BaselineWarmupTests
{
    [Fact]
    public void AWarmupWindowIsAppliedBeforeBaselineSampling()
    {
        Assert.True(AutoOcV3Policy.BaselineWarmupDuration > TimeSpan.Zero);
    }

    [Fact]
    public void TheWarmupOutlastsTheTransientItExistsToDiscard()
    {
        // The card settled within roughly one 10 s sample window on the reference
        // rig. A warmup shorter than that would leave the cold-boost transient in
        // the first measured sample, which is the whole defect.
        Assert.True(AutoOcV3Policy.BaselineWarmupDuration >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TheWarmupStaysNegligibleAgainstAScreeningRun()
    {
        // It is paid once per run against candidate screening and a 20-minute
        // final screen. If it ever grew to minutes it would be worth revisiting.
        Assert.True(AutoOcV3Policy.BaselineWarmupDuration <= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void TheObservedWarmSamplesWouldNowPassTheVariationGate()
    {
        // Replays the live numbers: discarding the cold sample leaves the two warm
        // ones, which are well inside the 3% limit that rejected the run.
        double[] warm = [290.1085228527147, 290.49416281139287];
        double spread = (warm.Max() - warm.Min()) / warm.Average() * 100;

        Assert.True(spread < 3, $"warm-sample spread was {spread:0.###}%");

        // And the cold sample is what pushed it over.
        double[] all = [301.97410730500235, .. warm];
        double allSpread = (all.Max() - all.Min()) / all.Average() * 100;
        Assert.True(allSpread > 3, $"cold-included spread was {allSpread:0.###}%");
    }
}
