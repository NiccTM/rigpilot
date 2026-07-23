using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// A freshly replaced cooling graph started with an empty LastApplied, so its
/// first tick skipped the per-output slew limit and demanded an instant jump to
/// the raw curve value. Switching a live GPU-fan auto-mode (Cooling, 53%, to
/// Silent, 30% at the same temperature) reproduced this on the reference rig:
/// the fan could not settle to the new duty inside the verification window, the
/// write failed, and the resulting recovery attempt locked all hardware writes
/// (writesEnabled=false, RecoveryRequired, with no runtime clear short of a
/// service restart). These tests pin that a retained output carries its
/// previous duty and timestamp into the replacement graph, so the transition is
/// rate-limited exactly like any other tick instead of getting an unbounded
/// first jump.
/// </summary>
public sealed class CoolingGraphReplacementRateLimitTests
{
    private const string CapabilityId = "gpufan.duty:0";
    private const string OtherCapabilityId = "gpufan.duty:1";

    [Fact]
    public void RetainedOutputCarriesItsPriorDutyAndTimestampIntoTheReplacement()
    {
        DateTimeOffset appliedAt = DateTimeOffset.UtcNow;
        PCHelperRuntime.ActiveCoolingGraphRuntime previous = GraphRuntime("cooling", CapabilityId);
        previous.LastApplied[CapabilityId] = 53;
        previous.LastAppliedAt[CapabilityId] = appliedAt;
        PCHelperRuntime.ActiveCoolingGraphRuntime requested = GraphRuntime("silent", CapabilityId);

        PCHelperRuntime.SeedRetainedOutputRateLimits(previous, requested);

        Assert.Equal(53, requested.LastApplied[CapabilityId]);
        Assert.Equal(appliedAt, requested.LastAppliedAt[CapabilityId]);
    }

    [Fact]
    public void AnOutputDroppedFromTheNewGraphIsNotCarriedOver()
    {
        PCHelperRuntime.ActiveCoolingGraphRuntime previous = GraphRuntime("case-fans", OtherCapabilityId);
        previous.LastApplied[OtherCapabilityId] = 70;
        previous.LastAppliedAt[OtherCapabilityId] = DateTimeOffset.UtcNow;
        PCHelperRuntime.ActiveCoolingGraphRuntime requested = GraphRuntime("gpu-fan", CapabilityId);

        PCHelperRuntime.SeedRetainedOutputRateLimits(previous, requested);

        Assert.Empty(requested.LastApplied);
        Assert.Empty(requested.LastAppliedAt);
    }

    [Fact]
    public void ThereIsNoPreviousGraphOnFirstActivation()
    {
        PCHelperRuntime.ActiveCoolingGraphRuntime requested = GraphRuntime("gpu-fan", CapabilityId);

        PCHelperRuntime.SeedRetainedOutputRateLimits(null, requested);

        Assert.Empty(requested.LastApplied);
    }

    [Fact]
    public void ARetainedOutputNeverPreviouslyAppliedIsNotSeeded()
    {
        // The previous graph declared the output but never actually wrote it
        // (e.g. every tick was a no-op because the temperature never changed
        // enough to cross the apply threshold) — nothing to carry over.
        PCHelperRuntime.ActiveCoolingGraphRuntime previous = GraphRuntime("cooling", CapabilityId);
        PCHelperRuntime.ActiveCoolingGraphRuntime requested = GraphRuntime("silent", CapabilityId);

        PCHelperRuntime.SeedRetainedOutputRateLimits(previous, requested);

        Assert.Empty(requested.LastApplied);
    }

    private static PCHelperRuntime.ActiveCoolingGraphRuntime GraphRuntime(string profileId, params string[] capabilityIds) =>
        new(
            profileId,
            new CoolingGraphV1(
                CoolingGraphV1.CurrentSchemaVersion,
                $"graph.{profileId}",
                profileId,
                [],
                [.. capabilityIds.Select(id => new CoolingGraphOutputV1(id, "curve", FanOutputMode.DutyPercent, 30, 100, 0, 4, 4, []))]),
            new Dictionary<string, FanCalibrationV2>(),
            new SafetyLimits());
}
