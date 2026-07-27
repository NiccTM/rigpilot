using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// A stage can end with no value for two opposite reasons, and until 2026-07-27 both were
/// reported with the same sentence.
///
/// Observed live: the core stage settled at +145 MHz leaving 5-6 °C of thermal margin, and
/// the memory stage then failed its STOCK rung at 96 °C memory junction. The owner was shown
/// "No memory candidate satisfied the selected objective and safety constraints" — which reads
/// as "your memory will not overclock" and sends them hunting for a tuning problem, when in
/// fact no overclock was ever applied and the card was simply too hot to screen.
/// </summary>
public sealed class AutoOcStageFailureMessageTests
{
    private static TuneScreeningResult Screening(bool passed, string message) =>
        new(passed, message, 0, 0, 0, 0, 0, 0);

    private static TuneResult Result(string statusLabel, params TuneCandidateResult[] candidates) =>
        new("gpuclock.memory:0", statusLabel, null, candidates, null);

    [Fact]
    public void AFailedStockRungIsReportedAsAnAbandonedStageAndCarriesTheRealReason()
    {
        const string reason = "Temperature ceiling exceeded on GPU Memory Junction: 96.0 °C observed, 95.0 °C allowed.";
        TuneResult result = Result(
            AutoOcV3Policy.BaselineFailedLabel,
            new TuneCandidateResult(0, false, reason, Screening(false, reason)));

        string message = AutoOcV3Policy.DescribeStageFailure("memory", result);

        // The operator needs three facts: nothing was overclocked, why it stopped, and that
        // this is not a statement about the card's tuning headroom.
        Assert.Contains("abandoned before any overclock was tested", message, StringComparison.Ordinal);
        Assert.Contains("GPU Memory Junction", message, StringComparison.Ordinal);
        Assert.Contains("Nothing was overclocked", message, StringComparison.Ordinal);
        Assert.Contains("not a limit of the card's tuning headroom", message, StringComparison.Ordinal);
        Assert.DoesNotContain("satisfied the selected objective", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenuinelyExhaustedSearchKeepsTheObjectiveWording()
    {
        // Here the stock rung passed and higher offsets failed, which IS a statement about
        // how far this card tunes. The original wording is correct and must not change.
        TuneResult result = Result(
            "No candidate passed screening",
            new TuneCandidateResult(0, true, "stable", Screening(true, "stable")),
            new TuneCandidateResult(100, false, "artifacts detected", Screening(false, "artifacts detected")));

        string message = AutoOcV3Policy.DescribeStageFailure("memory", result);

        Assert.Equal(
            "No memory candidate satisfied the selected objective and safety constraints.",
            message);
    }

    [Fact]
    public void TheLabelAloneDoesNotTriggerTheAbandonedWordingWhenTheOpeningRungPassed()
    {
        // Guards the flag against the data: the message is built from the opening candidate,
        // so a stale or wrong label cannot invent an abandonment that did not happen.
        TuneResult result = Result(
            AutoOcV3Policy.BaselineFailedLabel,
            new TuneCandidateResult(0, true, "stable", Screening(true, "stable")));

        Assert.Equal(
            "No core candidate satisfied the selected objective and safety constraints.",
            AutoOcV3Policy.DescribeStageFailure("core", result));
    }

    [Fact]
    public void AStageWithNoCandidatesAtAllStillProducesTheOrdinaryMessage()
    {
        Assert.Equal(
            "No core candidate satisfied the selected objective and safety constraints.",
            AutoOcV3Policy.DescribeStageFailure("core", Result(AutoOcV3Policy.BaselineFailedLabel)));
        Assert.Equal(
            "No core candidate satisfied the selected objective and safety constraints.",
            AutoOcV3Policy.DescribeStageFailure("core", null));
    }
}
