using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Screening cannot observe the candidate that hard-hangs the machine — nothing in the
/// process runs again until reboot. The boot sentinel restores the card, but without a
/// memory of what was applied the next run climbs the same ladder into the same offset and
/// hangs again. These tests pin the memory that makes a crash inform the next search.
/// </summary>
public sealed class AutoOcCrashCeilingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 18, 0, 0, TimeSpan.Zero);
    private const string Core = "gpuclock.core:0";

    private static AutoOcCrashRecordV1 Crash(double value, DateTimeOffset at, string capability = Core) =>
        new(AutoOcCrashRecordV1.CurrentSchemaVersion, capability, value, at);

    [Fact]
    public void NoRememberedHangLeavesTheSearchUnconstrained()
    {
        Assert.Null(AutoOcCrashCeiling.CeilingFor(Core, [], Now));
    }

    [Fact]
    public void ARememberedHangCapsTheSearchBelowTheOffsetThatCrashed()
    {
        double? ceiling = AutoOcCrashCeiling.CeilingFor(Core, [Crash(200, Now.AddDays(-1))], Now);

        Assert.NotNull(ceiling);
        Assert.True(ceiling < 200, "the ceiling must sit below the value that hung the machine");
        Assert.Equal(200 * AutoOcCrashCeiling.SafeFractionOfCrashingValue, ceiling!.Value);
    }

    [Fact]
    public void TheMostConservativeRememberedHangWins()
    {
        // Two known crashes: the lower one is the better evidence about this card.
        double? ceiling = AutoOcCrashCeiling.CeilingFor(
            Core,
            [Crash(250, Now.AddDays(-2)), Crash(150, Now.AddDays(-1))],
            Now);

        Assert.Equal(150 * AutoOcCrashCeiling.SafeFractionOfCrashingValue, ceiling!.Value);
    }

    [Fact]
    public void ACrashOnADifferentControlDoesNotConstrainThisOne()
    {
        // A memory hang says nothing about the core ladder.
        Assert.Null(AutoOcCrashCeiling.CeilingFor(
            Core,
            [Crash(400, Now.AddDays(-1), "gpuclock.memory:0")],
            Now));
    }

    [Fact]
    public void ACrashOlderThanTheMemoryWindowStopsConstrainingTheSearch()
    {
        // A new driver, new BIOS, or a fixed platform must not be capped by an old result
        // forever.
        DateTimeOffset stale = Now - AutoOcCrashCeiling.CrashMemoryWindow - TimeSpan.FromDays(1);

        Assert.Null(AutoOcCrashCeiling.CeilingFor(Core, [Crash(200, stale)], Now));
    }

    [Fact]
    public void ACrashCeilingComposesWithTheStaticEnvelopeAndOnlyTightensIt()
    {
        // End-to-end shape of what the search does: take the envelope ceiling, then let a
        // remembered hang lower it. The +200 MHz core envelope becomes +160 after a hang at
        // +200, so the ladder cannot walk back into the offset that killed the machine.
        double envelope = AutoOcSearchEnvelope.MaximumCoreOffsetMhz;
        double? crash = AutoOcCrashCeiling.CeilingFor(Core, [Crash(200, Now.AddHours(-1))], Now);

        double effective = AutoOcCrashCeiling.ConstrainCeiling(envelope, crash);

        Assert.True(effective < envelope);
        Assert.Equal(160, effective);
    }

    [Fact]
    public void ConstrainCeilingOnlyEverLowersTheProposedCeiling()
    {
        // Applied on top of the static envelope, a remembered hang may tighten it but must
        // never raise it.
        Assert.Equal(160, AutoOcCrashCeiling.ConstrainCeiling(200, 160));
        Assert.Equal(200, AutoOcCrashCeiling.ConstrainCeiling(200, 400));
        Assert.Equal(200, AutoOcCrashCeiling.ConstrainCeiling(200, null));
    }
}
