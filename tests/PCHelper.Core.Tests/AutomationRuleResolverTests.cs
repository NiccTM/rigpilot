using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class AutomationRuleResolverTests
{
    [Fact]
    public void ManualProfileWins()
    {
        string? result = AutomationRuleResolver.Resolve(
            "manual",
            [new AutomationRuleMatch("game", "performance", 100, DateTimeOffset.UtcNow)],
            "balanced");

        Assert.Equal("manual", result);
    }

    [Fact]
    public void HighestPriorityThenMostRecentRuleWins()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AutomationRuleMatch[] rules =
        [
            new("old-high", "quiet", 100, now.AddMinutes(-1)),
            new("new-high", "performance", 100, now),
            new("low", "balanced", 10, now.AddMinutes(1))
        ];

        string? result = AutomationRuleResolver.Resolve(null, rules, "default");

        Assert.Equal("performance", result);
    }
}
