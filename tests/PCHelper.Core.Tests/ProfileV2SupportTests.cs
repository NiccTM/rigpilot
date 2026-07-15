using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ProfileV2SupportTests
{
    [Fact]
    public void ManualVoltageCannotRunFromAutomationOrWithoutSessionConsent()
    {
        CapabilityDescriptorV2 voltage = Capability(
            "gpu.voltage",
            HazardClass.Voltage,
            BootApplyPolicy.ManualOnly,
            new NumericRange(0, 100, 1));
        ProfileV2 profile = Profile(
            [new ProfileAction("voltage", "adapter", voltage.Capability.Id, ControlValue.FromNumeric(25), true, 0)],
            manualOnly: ["voltage"],
            experimental: true);
        Dictionary<string, CapabilityDescriptorV2> capabilities = new() { [voltage.Capability.Id] = voltage };

        ProfileValidationResult automation = ProfileV2Validator.Validate(
            profile,
            capabilities,
            ProfileActivationSource.Automation,
            confirmManualVoltage: true);
        ProfileValidationResult unconfirmed = ProfileV2Validator.Validate(
            profile,
            capabilities,
            ProfileActivationSource.Manual,
            confirmManualVoltage: false);
        ProfileValidationResult confirmed = ProfileV2Validator.Validate(
            profile,
            capabilities,
            ProfileActivationSource.Manual,
            confirmManualVoltage: true);

        Assert.False(automation.Valid);
        Assert.False(unconfirmed.Valid);
        Assert.True(confirmed.Valid);
    }

    [Fact]
    public void RequiredOwnershipConflictRejectsWhileOptionalActionIsSkipped()
    {
        CapabilityDescriptorV2 blocked = Capability(
            "gpu.power",
            HazardClass.Performance,
            BootApplyPolicy.Never,
            new NumericRange(50, 110, 1)) with { OwnershipState = OwnershipState.OwnedByAnotherApplication };
        Dictionary<string, CapabilityDescriptorV2> capabilities = new() { [blocked.Capability.Id] = blocked };

        ProfileValidationResult required = ProfileV2Validator.Validate(
            Profile([new ProfileAction("power", "adapter", blocked.Capability.Id, ControlValue.FromNumeric(90), true, 0)]),
            capabilities,
            ProfileActivationSource.Manual,
            false);
        ProfileValidationResult optional = ProfileV2Validator.Validate(
            Profile([new ProfileAction("power", "adapter", blocked.Capability.Id, ControlValue.FromNumeric(90), false, 0)]),
            capabilities,
            ProfileActivationSource.Manual,
            false);

        Assert.False(required.Valid);
        Assert.True(optional.Valid);
        Assert.Single(optional.SkippedOptionalActions);
    }

    private static ProfileV2 Profile(
        IReadOnlyList<ProfileAction> actions,
        IReadOnlyList<string>? manualOnly = null,
        bool experimental = false) => new(
            ProfileV2.CurrentSchemaVersion,
            "profile.test",
            "Test profile",
            "Test",
            actions,
            new SafetyLimits(),
            null,
            null,
            null,
            manualOnly ?? [],
            [],
            false,
            experimental);

    private static CapabilityDescriptorV2 Capability(
        string id,
        HazardClass hazard,
        BootApplyPolicy boot,
        NumericRange range) => new(
            CapabilityDescriptorV2.CurrentSchemaVersion,
            new CapabilityDescriptor(
                id,
                "adapter",
                "device",
                id,
                CapabilityAccessState.Experimental,
                AdapterExecutionContext.AdapterHost,
                ControlValueKind.Numeric,
                range,
                "%",
                RiskLevel.Experimental,
                EvidenceLevel.ReadBackVerified,
                null,
                "Test capability",
                true,
                ControlDomain.Gpu),
            hazard,
            "Vendor bounds",
            true,
            ResetGuarantee.ReadBackVerified,
            OwnershipState.Available,
            boot,
            null,
            [],
            null,
            null,
            null);
}
