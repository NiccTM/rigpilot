using PCHelper.App;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the profile-sharing contract: round-trip of typed actions, the forced
/// Experimental/renamed/re-identified import semantics, the stripping of
/// machine-local references, and the refusals for foreign, malformed, or
/// action-less files. Scripts cannot appear in a profile at the type level
/// (rule 6), so no test needs to strip them — nothing in ProfileV2 can carry one.
/// </summary>
public sealed class ProfileShareFileTests
{
    private static ProfileV2 SampleProfile(bool experimental = false) => new(
        ProfileV2.CurrentSchemaVersion,
        "my-tuned-profile",
        "My tuned profile",
        "Fan curve and power limit for summer.",
        [
            new ProfileAction("a1", "nvidia.gpupower", "gpupower.limit:0", ControlValue.FromNumeric(300), Required: true, Order: 0),
            new ProfileAction("a2", "nvidia.gpufan", "gpufan.duty:0", ControlValue.FromNumeric(70), Required: false, Order: 1),
        ],
        new SafetyLimits(),
        "graph-local-1",
        "scene-local-1",
        "osd-local-1",
        ["a1"],
        ["automation-rule-7"],
        IsBuiltIn: true,
        IsExperimental: experimental);

    [Fact]
    public void RoundTripPreservesTypedActionsAndSafetyPolicy()
    {
        string json = ProfileShareFile.Export(SampleProfile(), "0.5.0-alpha");

        ProfileV2 imported = ProfileShareFile.Import(json);

        Assert.Equal(2, imported.HardwareActions.Count);
        Assert.Equal("gpupower.limit:0", imported.HardwareActions[0].CapabilityId);
        Assert.Equal(300, imported.HardwareActions[0].Value.Numeric);
        Assert.Equal(["a1"], imported.ManualOnlyActionIds);
    }

    [Fact]
    public void ImportForcesTheSafetyPosture()
    {
        ProfileV2 imported = ProfileShareFile.Import(ProfileShareFile.Export(SampleProfile(experimental: false), "0.5.0-alpha"));

        Assert.True(imported.IsExperimental);   // regardless of what the file claimed
        Assert.False(imported.IsBuiltIn);
        Assert.StartsWith("imported-", imported.Id, StringComparison.Ordinal);
        Assert.EndsWith("(imported)", imported.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineLocalReferencesNeverTravel()
    {
        ProfileV2 imported = ProfileShareFile.Import(ProfileShareFile.Export(SampleProfile(), "0.5.0-alpha"));

        Assert.Null(imported.CoolingGraphId);
        Assert.Null(imported.LightingSceneId);
        Assert.Null(imported.OsdLayoutId);
        Assert.Empty(imported.AutomationReferences);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":2,"application":"RigPilot","exportedByVersion":"x","profile":null}""")]
    [InlineData("""{"schemaVersion":1,"application":"OtherApp","exportedByVersion":"x","profile":null}""")]
    public void ForeignOrMalformedFilesAreRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => ProfileShareFile.Import(json));
    }

    [Fact]
    public void ActionlessProfilesAreRefused()
    {
        ProfileV2 empty = SampleProfile() with { HardwareActions = [] };

        Assert.Throws<InvalidDataException>(() =>
            ProfileShareFile.Import(ProfileShareFile.Export(empty, "0.5.0-alpha")));
    }
}
