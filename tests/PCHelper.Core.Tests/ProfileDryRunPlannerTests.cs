using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ProfileDryRunPlannerTests
{
    [Fact]
    public void ReadyReadBackCapabilityProducesApplicableAtomicPlan()
    {
        CapabilityDescriptorV2 capability = Capability();
        ProfileDryRunResultV1 result = ProfileDryRunPlanner.Build(
            Request(Profile()),
            new Dictionary<string, CapabilityDescriptorV2> { [capability.Capability.Id] = capability });

        Assert.True(result.CanApply);
        Assert.Equal(ProfileDryRunActionState.Ready, Assert.Single(result.Actions).State);
        Assert.Contains(ControlDomain.Gpu.ToString(), result.AtomicDomains);
        Assert.Contains(capability.Capability.Id, result.RequiredCapabilities);
        Assert.Contains("Recovery Required", result.ExpectedRollback, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnershipConflictAndMissingReadBackBlockBeforeMutation()
    {
        CapabilityDescriptorV2 owned = Capability() with
        {
            OwnershipState = OwnershipState.OwnedByAnotherApplication
        };
        ProfileDryRunResultV1 conflict = ProfileDryRunPlanner.Build(
            Request(Profile()),
            new Dictionary<string, CapabilityDescriptorV2> { [owned.Capability.Id] = owned });
        CapabilityDescriptorV2 writeOnly = Capability() with { SupportsReadBack = false };
        ProfileDryRunResultV1 noReadBack = ProfileDryRunPlanner.Build(
            Request(Profile()),
            new Dictionary<string, CapabilityDescriptorV2> { [writeOnly.Capability.Id] = writeOnly });

        Assert.False(conflict.CanApply);
        Assert.Equal(ProfileDryRunActionState.Conflict, Assert.Single(conflict.Actions).State);
        Assert.Single(conflict.Conflicts);
        Assert.False(noReadBack.CanApply);
        Assert.Equal(ProfileDryRunActionState.Blocked, Assert.Single(noReadBack.Actions).State);
    }

    [Fact]
    public void UserSessionCompanionIsReportedOutsidePrivilegedTransaction()
    {
        CapabilityDescriptorV2 capability = Capability();
        ProfileDryRunActionV1 companion = new(
            "lighting",
            "Lighting",
            "scene",
            ProfileDryRunActionState.IndependentCompanion,
            true,
            null,
            "Runs after commit.");

        ProfileDryRunResultV1 result = ProfileDryRunPlanner.Build(
            Request(Profile()),
            new Dictionary<string, CapabilityDescriptorV2> { [capability.Capability.Id] = capability },
            [companion]);

        Assert.True(result.CanApply);
        Assert.Contains("Lighting", result.IndependentDomains);
        Assert.Contains("not part of", result.ExpectedRollback, StringComparison.OrdinalIgnoreCase);
    }

    private static PreviewProfileV2Request Request(ProfileV2 profile) => new(
        profile,
        ProfileActivationSource.Manual,
        ConfirmExperimental: false,
        ConfirmedDeviceIds: [],
        ConfirmManualVoltage: false,
        KnownLightingSceneIds: [],
        KnownOsdLayoutIds: []);

    private static ProfileV2 Profile() => new(
        ProfileV2.CurrentSchemaVersion,
        "profile.test",
        "Test",
        "Dry-run test",
        [new ProfileAction("power", "adapter", "gpu.power", ControlValue.FromNumeric(90), true, 0)],
        new SafetyLimits(),
        null,
        null,
        null,
        [],
        [],
        false,
        false);

    private static CapabilityDescriptorV2 Capability() => new(
        CapabilityDescriptorV2.CurrentSchemaVersion,
        new CapabilityDescriptor(
            "gpu.power",
            "adapter",
            "gpu-1",
            "GPU power",
            CapabilityAccessState.Verified,
            AdapterExecutionContext.AdapterHost,
            ControlValueKind.Numeric,
            new NumericRange(50, 100, 1),
            "%",
            RiskLevel.Guarded,
            EvidenceLevel.ReadBackVerified,
            null,
            "Qualified exact-device control",
            true,
            ControlDomain.Gpu),
        HazardClass.Performance,
        "Controller-reported bounds",
        true,
        ResetGuarantee.ReadBackVerified,
        OwnershipState.Available,
        BootApplyPolicy.Allowed,
        ControlValue.FromNumeric(100),
        [],
        null,
        null,
        null);
}
