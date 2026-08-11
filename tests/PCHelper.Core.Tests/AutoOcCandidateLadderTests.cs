using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The exact candidate ladders Auto OC generates, produced by the production generator rather
/// than by hand, because the shape of these ladders is a hardware-safety property.
///
/// <para>Auto OC has hard-crashed the reference machine. Candidate values are absolute and the
/// search starts from stock, so the cause is not compounding - it is how far apart the rungs
/// are. The clock capability reports <c>Step = 1</c> MHz, so the raw ladder is every whole
/// megahertz and <c>MaximumCandidates</c> sampling ALWAYS engages: the effective step is
/// therefore span/(candidates-1), not the reported step. That is the number that matters and
/// it is invisible from either constant on its own.</para>
/// </summary>
public sealed class AutoOcCandidateLadderTests
{
    // The reference RTX 3090's driver-reported delta ranges.
    private static readonly NumericRange CoreRange = new(-1000, 1000, 1);
    private static readonly NumericRange MemoryRange = new(-1000, 3000, 1);

    // CreateAutoOcTuneRequest's value.
    private const int AutoOcMaximumCandidates = 12;

    private static IReadOnlyList<double> Ladder(NumericRange range, string capabilityId) =>
        TuneCandidateGenerator.Generate(
            range,
            AutoOcSearchEnvelope.Constrain(capabilityId, range),
            TuneDirection.Maximize,
            AutoOcMaximumCandidates);

    [Fact]
    public void TheCoreLadderIsFixedAndItsFirstRungIsModest()
    {
        IReadOnlyList<double> ladder = Ladder(CoreRange, "gpuclock.core:0");

        Assert.Equal(
            [0, 18, 36, 55, 73, 91, 109, 127, 145, 164, 182, 200],
            ladder);
        Assert.Equal(18, ladder[1]);
        Assert.Equal(200, ladder[^1]);
    }

    /// <summary>
    /// The memory ladder, which is the one to look at hardest. GDDR6X does not fail cleanly:
    /// on-die error correction means an unstable module keeps "passing" while it corrects,
    /// and past that it hangs the machine outright rather than raising anything screening can
    /// observe. A first rung of +55 MHz and ~55 MHz thereafter is a coarse search in the least
    /// forgiving domain.
    /// </summary>
    [Fact]
    public void TheMemoryLadderStepsThreeTimesFurtherThanCore()
    {
        IReadOnlyList<double> ladder = Ladder(MemoryRange, "gpuclock.memory:0");

        Assert.Equal(
            [0, 55, 109, 164, 218, 273, 327, 382, 436, 491, 545, 600],
            ladder);
        Assert.Equal(55, ladder[1]);
        Assert.Equal(600, ladder[^1]);
    }

    /// <summary>
    /// The property that actually characterises risk: the largest jump between adjacent rungs.
    /// Memory's is roughly three times core's, in the domain far more likely to hard-hang.
    /// </summary>
    [Fact]
    public void TheLargestAdjacentJumpIsRecordedForBothDomains()
    {
        Assert.Equal(19, LargestJump(Ladder(CoreRange, "gpuclock.core:0")));
        Assert.Equal(55, LargestJump(Ladder(MemoryRange, "gpuclock.memory:0")));
    }

    /// <summary>
    /// How quickly each domain reaches territory a typical sample cannot survive. A good
    /// RTX 3090 core sample manages +100-150 MHz; memory varies far more widely.
    /// </summary>
    [Theory]
    [InlineData(100, 6)]
    [InlineData(150, 9)]
    [InlineData(200, 11)]
    public void CoreReachesTheseOffsetsAtTheseCandidateIndexes(double threshold, int expectedIndex)
    {
        IReadOnlyList<double> ladder = Ladder(CoreRange, "gpuclock.core:0");
        Assert.Equal(expectedIndex, FirstIndexAtOrAbove(ladder, threshold));
    }

    [Theory]
    [InlineData(100, 2)]
    [InlineData(200, 4)]
    [InlineData(300, 6)]
    [InlineData(500, 10)]
    public void MemoryReachesTheseOffsetsAtTheseCandidateIndexes(double threshold, int expectedIndex)
    {
        IReadOnlyList<double> ladder = Ladder(MemoryRange, "gpuclock.memory:0");
        Assert.Equal(expectedIndex, FirstIndexAtOrAbove(ladder, threshold));
    }

    /// <summary>No rung may fall outside the envelope, and none may repeat after rounding.</summary>
    [Fact]
    public void EveryRungIsInsideTheEnvelopeAndDistinct()
    {
        foreach ((NumericRange range, string id, double ceiling) in new[]
        {
            (CoreRange, "gpuclock.core:0", AutoOcSearchEnvelope.MaximumCoreOffsetMhz),
            (MemoryRange, "gpuclock.memory:0", AutoOcSearchEnvelope.MaximumMemoryOffsetMhz),
        })
        {
            IReadOnlyList<double> ladder = Ladder(range, id);
            Assert.All(ladder, value => Assert.InRange(value, 0, ceiling));
            Assert.Equal(ladder.Count, ladder.Distinct().Count());
            Assert.Equal(ladder.OrderBy(value => value), ladder);
        }
    }

    /// <summary>
    /// The reported <c>Step</c> is NOT the effective step. It is 1 MHz, so the raw ladder is
    /// always longer than the candidate cap and sampling always engages - which is why the
    /// real spacing is span/(candidates-1) and cannot be read off either constant.
    /// </summary>
    [Fact]
    public void TheReportedStepIsNotTheEffectiveStep()
    {
        Assert.Equal(1, MemoryRange.Step);
        IReadOnlyList<double> ladder = Ladder(MemoryRange, "gpuclock.memory:0");
        Assert.Equal(AutoOcMaximumCandidates, ladder.Count);
        Assert.True(LargestJump(ladder) > MemoryRange.Step * 50);
    }

    private static double LargestJump(IReadOnlyList<double> ladder)
    {
        double largest = 0;
        for (int index = 1; index < ladder.Count; index++)
        {
            largest = Math.Max(largest, ladder[index] - ladder[index - 1]);
        }

        return largest;
    }

    private static int FirstIndexAtOrAbove(IReadOnlyList<double> ladder, double threshold)
    {
        for (int index = 0; index < ladder.Count; index++)
        {
            if (ladder[index] >= threshold)
            {
                return index;
            }
        }

        return -1;
    }
}
