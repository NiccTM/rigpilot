using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// GDDR6X does not fail cleanly. On-die error correction means an over-clocked module keeps
/// "passing" screening while silently correcting errors — throughput falls and the display
/// corrupts long before anything hangs. Screening cannot see a visual artifact, so the
/// regression itself has to be treated as the instability signal, and the search ceiling has
/// to stay out of that range to begin with.
/// </summary>
public sealed class AutoOcMemoryRegressionTests
{
    private const double Baseline = 300;

    private static AutoOcObjectiveConstraintsV3 Performance => new(
        TuningObjective.Performance,
        MaximumBaselineVariationPercent: 3,
        MinimumEfficiencyPerformancePercent: 98,
        MinimumQuietPerformancePercent: 95,
        TemperatureCeilingCelsius: 83,
        PowerCeilingWatts: null,
        BaselineSampleDuration: TimeSpan.FromSeconds(10),
        CandidateScreeningDuration: TimeSpan.FromSeconds(30),
        FinalScreeningDuration: TimeSpan.FromMinutes(20),
        RequestPresentMonValidation: false);

    private static AutoOcCandidateScoreV3 Candidate(double value, double throughput) =>
        new("Memory", value, Passed: true, throughput, 300, 1500, 70, throughput, "screened");

    [Fact]
    public void ACandidateWhoseThroughputFellBelowStockIsRejected()
    {
        // The error-correction signature: screening passed, but the card is slower than it
        // was at stock because the memory is erroring and being corrected.
        AutoOcCandidateScoreV3? selected = AutoOcV3Policy.SelectBestCandidate(
            [Candidate(500, Baseline - 12)],
            Performance,
            Baseline);

        Assert.Null(selected);
    }

    [Fact]
    public void TheHighestCandidateThatStillBeatsStockIsSelected()
    {
        AutoOcCandidateScoreV3? selected = AutoOcV3Policy.SelectBestCandidate(
            [Candidate(200, Baseline + 6), Candidate(400, Baseline + 11), Candidate(600, Baseline - 20)],
            Performance,
            Baseline);

        Assert.NotNull(selected);
        Assert.Equal(400, selected!.Value);
    }

    [Fact]
    public void AllCandidatesRegressingLeavesNothingSelected()
    {
        // Better to ship no memory overclock than one that corrupts the screen for a loss.
        Assert.Null(AutoOcV3Policy.SelectBestCandidate(
            [Candidate(300, Baseline - 1), Candidate(450, Baseline - 30)],
            Performance,
            Baseline));
    }

    [Fact]
    public void TheMemoryCeilingStaysBelowTheCoreCeilingsRiskProfile()
    {
        // The memory ladder must not walk deep into the corrupting range before the
        // regression rule can act, so its envelope is tightened relative to the driver's
        // advertised +3000 MHz.
        Assert.True(AutoOcSearchEnvelope.MaximumMemoryOffsetMhz <= 600);

        TuneBounds bounds = AutoOcSearchEnvelope.Constrain("gpuclock.memory:0", new NumericRange(-1000, 3000, 1));
        Assert.Equal(AutoOcSearchEnvelope.MaximumMemoryOffsetMhz, bounds.Maximum);
    }
}
