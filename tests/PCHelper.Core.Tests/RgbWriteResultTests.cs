using PCHelper.Contracts;

namespace PCHelper.Core.Tests;

public sealed class RgbWriteResultTests
{
    [Theory]
    [InlineData(KrakenLightingOutcome.WriteIssued, RgbWriteOutcome.WriteIssued, true)]
    [InlineData(KrakenLightingOutcome.DeviceNotFound, RgbWriteOutcome.DeviceNotFound, false)]
    [InlineData(KrakenLightingOutcome.AccessDenied, RgbWriteOutcome.AccessDenied, false)]
    [InlineData(KrakenLightingOutcome.Failed, RgbWriteOutcome.Failed, false)]
    public void KrakenAuraRazerResultsMapUniformly(KrakenLightingOutcome wire, RgbWriteOutcome expected, bool issued)
    {
        // The three families share the wire enum, so one mapping covers all of them.
        KrakenLightingResultV1 kraken = new(1, wire, "NZXT Kraken", "m");
        AuraLightingResultV1 aura = new(1, wire, "ASUS Aura", "m");
        RazerRgbResultV1 razer = new(1, wire, "Razer", "m");

        foreach (RgbWriteResult mapped in new[] { kraken.ToRgbWriteResult(), aura.ToRgbWriteResult(), razer.ToRgbWriteResult() })
        {
            Assert.Equal(expected, mapped.Outcome);
            Assert.Equal(issued, mapped.WriteIssued);
            Assert.Equal("m", mapped.Message);
        }

        Assert.Equal("NZXT Kraken", kraken.ToRgbWriteResult().ProductName);
    }

    [Fact]
    public void DimmStringOutcomeNormalisesToTheSamePredicate()
    {
        // DIMM diverges: string outcome + computed WriteIssued. The mapper reconciles it
        // so callers no longer special-case 'WriteIssued == true' against the others'
        // 'Outcome == WriteIssued'.
        DimmRgbResultV1 issued = new(1, "WriteIssued", 20, "done");
        DimmRgbResultV1 failed = new(1, "AccessDenied", 0, "blocked");

        Assert.True(issued.ToRgbWriteResult().WriteIssued);
        Assert.Equal(RgbWriteOutcome.WriteIssued, issued.ToRgbWriteResult().Outcome);
        Assert.False(failed.ToRgbWriteResult().WriteIssued);
        Assert.Equal(RgbWriteOutcome.Failed, failed.ToRgbWriteResult().Outcome);
        Assert.Equal("blocked", failed.ToRgbWriteResult().Message);
    }
}
