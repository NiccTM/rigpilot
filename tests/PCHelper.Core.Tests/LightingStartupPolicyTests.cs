using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Restoring lighting at service start is an unattended hardware write, so what it will
/// accept is pinned here: a real six-digit colour, and routes drawn only from the native
/// writers that can run without a signed-in user.
/// </summary>
public sealed class LightingStartupPolicyTests
{
    [Theory]
    [InlineData("#7A2BFF", "7A2BFF")]
    [InlineData("7a2bff", "7A2BFF")]
    [InlineData("  #00ff00  ", "00FF00")]
    [InlineData("000000", "000000")]
    public void NormalisesAColourToSixUpperCaseHexDigits(string input, string expected)
    {
        Assert.Equal(expected, LightingStartupPolicy.NormaliseColour(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("#FFF")]
    [InlineData("7A2BF")]
    [InlineData("7A2BFFF")]
    [InlineData("ZZZZZZ")]
    [InlineData("#7A2BFG")]
    public void RejectsAnythingThatIsNotASixDigitColour(string? input)
    {
        Assert.Null(LightingStartupPolicy.NormaliseColour(input));
    }

    [Fact]
    public void AcceptsAColourOnKnownRoutes()
    {
        Assert.Null(LightingStartupPolicy.ValidateEnable("#7A2BFF", ["native:aura", "native:razer"]));
    }

    [Fact]
    public void RefusesAColourThatIsNotSixDigits()
    {
        string? error = LightingStartupPolicy.ValidateEnable("#FFF", ["native:aura"]);
        Assert.Contains("six-digit", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesWhenNoRouteIsNamed()
    {
        string? error = LightingStartupPolicy.ValidateEnable("#7A2BFF", []);
        Assert.Contains("no lighting route", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unknown route would be written blind at boot with nobody watching. Only the
    /// native writers that can run without a signed-in user are allowed here — an
    /// OpenRGB-bridge or Dynamic Lighting endpoint does not exist at the login screen.
    /// </summary>
    [Theory]
    [InlineData("openrgb:0")]
    [InlineData("dynamic:LampArray-1")]
    [InlineData("native:unknown")]
    [InlineData("")]
    public void RefusesARouteThatCannotBeDrivenAtStartup(string routeId)
    {
        string? error = LightingStartupPolicy.ValidateEnable("#7A2BFF", [routeId]);
        Assert.Contains("not a native lighting route", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnowsExactlyTheFourNativeRoutes()
    {
        Assert.Equal(
            ["native:kraken", "native:aura", "native:dimm", "native:razer"],
            LightingStartupPolicy.KnownRouteIds);
        Assert.All(LightingStartupPolicy.KnownRouteIds, route => Assert.True(LightingStartupPolicy.IsKnownRoute(route)));
    }

    /// <summary>
    /// The dashboard offers every known route and lets the service keep the ones that
    /// actually light. That only works if the whole known set validates.
    /// </summary>
    [Fact]
    public void TheFullKnownRouteSetIsAcceptedAsSubmittedByTheDashboard()
    {
        Assert.Null(LightingStartupPolicy.ValidateEnable("#7A2BFF", [.. LightingStartupPolicy.KnownRouteIds]));
    }

    /// <summary>
    /// Black stays a legal colour here: the CLI can deliberately persist "off", and the
    /// policy has no way to tell a chosen black from an accidental one. The dashboard
    /// carries that judgement instead, because only it knows the brightness slider was at
    /// zero — which is how a real save came out black without anyone meaning it.
    /// </summary>
    [Fact]
    public void BlackRemainsAValidColourAtThePolicyLayer()
    {
        Assert.Equal("000000", LightingStartupPolicy.NormaliseColour("#000000"));
        Assert.Null(LightingStartupPolicy.ValidateEnable("#000000", ["native:aura"]));
    }
}
