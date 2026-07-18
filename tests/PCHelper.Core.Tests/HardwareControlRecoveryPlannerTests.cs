using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class HardwareControlRecoveryPlannerTests
{
    [Fact]
    public void LegacyCommittedProfileIsRecoveredWhenNoLeaseExists()
    {
        ProfileTransaction committed = Transaction(ProfileTransactionState.Committed);

        HardwareStartupRecoveryPlan plan = HardwareControlRecoveryPlanner.BuildStartupPlan(
            previousLease: null,
            pending: null,
            latestCommittedWithoutLease: committed);

        Assert.True(plan.RequiresRecovery);
        HardwareControlLeaseItemV1 control = Assert.Single(plan.Controls);
        Assert.Equal("test.adapter", control.AdapterId);
        Assert.Equal("test.control", control.CapabilityId);
        Assert.Equal(committed.Id, plan.LastTransactionId);
    }

    [Fact]
    public void CleanMarkerSuppressesHistoricalCommittedProfileRecovery()
    {
        HardwareControlLeaseV1 clean = Lease(clean: true, controls: []);

        HardwareStartupRecoveryPlan plan = HardwareControlRecoveryPlanner.BuildStartupPlan(
            clean,
            pending: null,
            latestCommittedWithoutLease: Transaction(ProfileTransactionState.Committed));

        Assert.False(plan.RequiresRecovery);
        Assert.Empty(plan.Controls);
    }

    [Fact]
    public void FailedStartupVerificationRetainsLeaseAndLocksRecovery()
    {
        HardwareControlLeaseV1 active = Lease(
            clean: false,
            controls: [new HardwareControlLeaseItemV1("test.adapter", "test.control")]);
        HardwareStartupRecoveryPlan plan = HardwareControlRecoveryPlanner.BuildStartupPlan(active, null, null);
        HardwareRecoveryResult failure = new(
            false,
            [new HardwareStateVerification("test.adapter", "test.control", false, null, "read-back failed")],
            ["test.control: read-back failed"]);

        HardwareControlLeaseV1 marker = HardwareControlRecoveryPlanner.CreateRunningMarker(
            "new-instance",
            active,
            plan,
            failure,
            DateTimeOffset.UtcNow);

        Assert.Equal(HardwareControlLeaseState.RecoveryRequired, marker.State);
        Assert.False(marker.CleanShutdown);
        Assert.False(marker.DefaultsVerified);
        Assert.Single(marker.Controls);
    }

    [Fact]
    public void CleanShutdownMarkerRequiresSuccessfulReadBack()
    {
        HardwareControlLeaseV1 active = Lease(
            clean: false,
            controls: [new HardwareControlLeaseItemV1("test.adapter", "test.control")]);
        HardwareRecoveryResult success = new(
            true,
            [new HardwareStateVerification("test.adapter", "test.control", true, ControlValue.FromNumeric(0), "default")],
            []);

        HardwareControlLeaseV1 marker = HardwareControlRecoveryPlanner.CreateShutdownMarker(
            "instance",
            active,
            success,
            DateTimeOffset.UtcNow);

        Assert.Equal(HardwareControlLeaseState.CleanShutdown, marker.State);
        Assert.True(marker.CleanShutdown);
        Assert.True(marker.DefaultsVerified);
        Assert.Empty(marker.Controls);
    }

    private static ProfileTransaction Transaction(ProfileTransactionState state)
    {
        ProfileAction action = new(
            "action",
            "test.adapter",
            "test.control",
            ControlValue.FromNumeric(50),
            Required: true,
            Order: 0);
        PreparedAction prepared = new(action, ControlValue.FromNumeric(0), DateTimeOffset.UtcNow, "token");
        return new ProfileTransaction(
            "transaction",
            1,
            "profile",
            state,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [prepared],
            [],
            null);
    }

    private static HardwareControlLeaseV1 Lease(
        bool clean,
        IReadOnlyList<HardwareControlLeaseItemV1> controls) => new(
            HardwareControlLeaseV1.CurrentSchemaVersion,
            HardwareControlLeaseV1.DefaultId,
            "old-instance",
            controls.Count == 0 ? null : "profile",
            controls.Count == 0 ? null : "transaction",
            controls,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            CleanShutdown: clean,
            DefaultsVerified: clean,
            clean ? HardwareControlLeaseState.CleanShutdown : HardwareControlLeaseState.Active,
            clean ? "clean" : "active");
}
