using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The automatic search must never climb into offsets where a failure is a hard hang rather
/// than something screening can observe. The driver's reported range is what NVAPI accepts
/// (±1000 MHz core, +3000 MHz memory on an RTX 3090), not what the silicon survives, and
/// searching it is what makes an auto-overclocker crash the machine. These tests pin the
/// envelope that bounds the ladder.
/// </summary>
public sealed class AutoOcSearchEnvelopeTests
{
    // The bounds this machine's RTX 3090 actually reports.
    private static NumericRange CoreRange => new(-1000, 1000, 1);
    private static NumericRange MemoryRange => new(-1000, 3000, 1);

    [Fact]
    public void TheCoreSearchStopsWellShortOfTheDriverMaximum()
    {
        TuneBounds bounds = AutoOcSearchEnvelope.Constrain("gpuclock.core:0", CoreRange);

        Assert.Equal(AutoOcSearchEnvelope.MaximumCoreOffsetMhz, bounds.Maximum);
        Assert.True(bounds.Maximum < CoreRange.Maximum);
    }

    [Fact]
    public void TheMemorySearchStopsWellShortOfTheDriverMaximum()
    {
        TuneBounds bounds = AutoOcSearchEnvelope.Constrain("gpuclock.memory:0", MemoryRange);

        Assert.Equal(AutoOcSearchEnvelope.MaximumMemoryOffsetMhz, bounds.Maximum);
        Assert.True(bounds.Maximum < MemoryRange.Maximum);
    }

    [Fact]
    public void TheSearchNeverGoesBelowStock()
    {
        // A performance search must climb from stock, never into negative offsets, even
        // though the controller reports a negative minimum.
        Assert.Equal(0, AutoOcSearchEnvelope.Constrain("gpuclock.core:0", CoreRange).Minimum);
    }

    [Fact]
    public void TheCandidateLadderStepsFarSmallerInsideTheEnvelope()
    {
        // The practical point of the envelope: with the fixed candidate count the ladder
        // steps a fraction of what the raw range produced, so the first failure is far more
        // likely to be observable rather than a dead machine.
        const int candidates = 12;
        double rawStep = (CoreRange.Maximum - Math.Max(0, CoreRange.Minimum)) / candidates;
        TuneBounds bounds = AutoOcSearchEnvelope.Constrain("gpuclock.core:0", CoreRange);
        double envelopeStep = (bounds.Maximum - bounds.Minimum) / candidates;

        Assert.True(envelopeStep < rawStep / 4, $"expected a much finer ladder, raw={rawStep} envelope={envelopeStep}");
    }

    [Fact]
    public void AControllerReportingLessThanTheEnvelopeKeepsItsOwnSmallerRange()
    {
        // The envelope is a ceiling, never a floor: a card that only offers +120 MHz must
        // not be searched to +200.
        TuneBounds bounds = AutoOcSearchEnvelope.Constrain("gpuclock.core:0", new NumericRange(0, 120, 1));

        Assert.Equal(120, bounds.Maximum);
    }

    [Fact]
    public void ANonClockCapabilityKeepsItsReportedRange()
    {
        // The power limit is bounded by the controller in watts and is not an offset ladder,
        // so the envelope must not touch it.
        TuneBounds bounds = AutoOcSearchEnvelope.Constrain("gpupower.limit:0", new NumericRange(100_000, 385_000, 1000));

        Assert.Equal(385_000, bounds.Maximum);
        Assert.Null(AutoOcSearchEnvelope.CeilingFor("gpupower.limit:0"));
    }
}
