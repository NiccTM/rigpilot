using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ProfileValidatorTests
{
    [Fact]
    public void RequiredMissingCapabilityRejectsProfile()
    {
        ProfileV1 profile = Profile(Action(required: true), experimental: false);

        ProfileValidationResult result = ProfileValidator.Validate(
            profile,
            new Dictionary<string, CapabilityDescriptor>(),
            confirmExperimental: false);

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, error => error.Contains("not discovered", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionalMissingCapabilityIsSkipped()
    {
        ProfileV1 profile = Profile(Action(required: false), experimental: false);

        ProfileValidationResult result = ProfileValidator.Validate(
            profile,
            new Dictionary<string, CapabilityDescriptor>(),
            confirmExperimental: false);

        Assert.True(result.Valid);
        Assert.Single(result.SkippedOptionalActions);
    }

    [Fact]
    public void ExperimentalCapabilityRequiresConfirmation()
    {
        ProfileV1 profile = Profile(Action(required: true), experimental: true);
        Dictionary<string, CapabilityDescriptor> capabilities = new()
        {
            ["test.control"] = Capability(CapabilityAccessState.Experimental)
        };

        ProfileValidationResult result = ProfileValidator.Validate(profile, capabilities, confirmExperimental: false);

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, error => error.Contains("explicit", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(10.5)]
    public void OutOfRangeOrMisalignedNumericValueIsRejected(double value)
    {
        ProfileAction action = Action(required: true) with { Value = ControlValue.FromNumeric(value) };
        ProfileV1 profile = Profile(action, experimental: false);
        Dictionary<string, CapabilityDescriptor> capabilities = new()
        {
            ["test.control"] = Capability(CapabilityAccessState.Verified)
        };

        ProfileValidationResult result = ProfileValidator.Validate(profile, capabilities, confirmExperimental: false);

        Assert.False(result.Valid);
    }

    [Fact]
    public void AutomaticVoltageIncreasePolicyIsRejected()
    {
        ProfileV1 profile = Profile(Action(required: false), experimental: false) with
        {
            SafetyLimits = new SafetyLimits(AllowAutomaticVoltageIncrease: true)
        };

        ProfileValidationResult result = ProfileValidator.Validate(
            profile,
            new Dictionary<string, CapabilityDescriptor>(),
            confirmExperimental: false);

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, error => error.Contains("voltage", StringComparison.OrdinalIgnoreCase));
    }

    internal static ProfileAction Action(bool required) => new(
        "action-1",
        "test.adapter",
        "test.control",
        ControlValue.FromNumeric(50),
        required,
        10);

    internal static ProfileV1 Profile(ProfileAction action, bool experimental) => new(
        ProfileV1.CurrentSchemaVersion,
        "test-profile",
        "Test",
        "Test profile",
        [action],
        new SafetyLimits(),
        [],
        IsBuiltIn: false,
        IsExperimental: experimental);

    internal static CapabilityDescriptor Capability(CapabilityAccessState state) => new(
        "test.control",
        "test.adapter",
        "test.device",
        "Test control",
        state,
        AdapterExecutionContext.AdapterHost,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "%",
        state == CapabilityAccessState.Experimental ? RiskLevel.Experimental : RiskLevel.Safe,
        EvidenceLevel.ReadBackVerified,
        null,
        "Test capability",
        true,
        ControlDomain.Power);
}
