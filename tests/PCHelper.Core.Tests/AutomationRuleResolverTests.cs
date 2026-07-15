using PCHelper.Contracts;
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

    [Fact]
    public void MatcherSupportsOvernightSchedulesIdleAndForegroundProcess()
    {
        DateTimeOffset timestamp = new(2026, 7, 12, 23, 30, 0, TimeSpan.Zero);
        AutomationObservation observation = new(
            timestamp,
            new HashSet<string>(["Plexamp"], StringComparer.OrdinalIgnoreCase),
            "Game",
            SessionLocked: false,
            IdleTime: TimeSpan.FromMinutes(12),
            Hotkey: null);

        Assert.True(AutomationRuleMatcher.IsMatch(Rule(AutomationTriggerKind.Schedule, "22:00-06:00"), observation));
        Assert.True(AutomationRuleMatcher.IsMatch(Rule(AutomationTriggerKind.Idle, "10"), observation));
        Assert.True(AutomationRuleMatcher.IsMatch(Rule(AutomationTriggerKind.ForegroundApplication, "game.exe"), observation));
        Assert.False(AutomationRuleMatcher.IsMatch(Rule(AutomationTriggerKind.SessionLock, "locked"), observation));
    }

    [Fact]
    public void StateMachineAppliesDebounceCooldownAndImmediateManualOverride()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AutomationRuleV1 rule = Rule(AutomationTriggerKind.Process, "game") with
        {
            ProfileId = "performance",
            Priority = 50
        };
        AutomationRuleStateMachine machine = new();

        AutomationDecision entering = machine.Evaluate([rule], Observation(now, processRunning: true), null, null);
        AutomationDecision active = machine.Evaluate([rule], Observation(now.AddSeconds(5), processRunning: true), null, null);
        AutomationDecision manual = machine.Evaluate([rule], Observation(now.AddSeconds(6), processRunning: true), "quiet", null);

        Assert.False(entering.ShouldSwitch);
        Assert.True(active.ShouldSwitch);
        Assert.Equal("performance", active.ProfileId);
        Assert.True(manual.ShouldSwitch);
        Assert.Equal("quiet", manual.ProfileId);
        Assert.Contains("Manual", manual.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisteredHotkeyRuleActivatesWithoutFiveSecondHold()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AutomationRuleV1 rule = Rule(AutomationTriggerKind.Hotkey, "Ctrl+Alt+1");
        AutomationRuleStateMachine machine = new();

        AutomationDecision decision = machine.Evaluate(
            [rule],
            Observation(now, processRunning: false) with { Hotkey = "Ctrl+Alt+1" },
            null,
            null);

        Assert.True(decision.ShouldSwitch);
        Assert.Equal("balanced", decision.ProfileId);
    }

    [Fact]
    public void InitialDefaultDoesNotCooldownFirstDebouncedRule()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AutomationRuleV1 rule = Rule(AutomationTriggerKind.Process, "game") with { ProfileId = "performance" };
        AutomationRuleStateMachine machine = new();

        AutomationDecision initial = machine.Evaluate(
            [rule],
            Observation(now, processRunning: true),
            null,
            "balanced");
        AutomationDecision active = machine.Evaluate(
            [rule],
            Observation(now.AddSeconds(5), processRunning: true),
            null,
            "balanced");

        Assert.True(initial.ShouldSwitch);
        Assert.Equal("balanced", initial.ProfileId);
        Assert.True(active.ShouldSwitch);
        Assert.Equal("performance", active.ProfileId);
    }

    private static AutomationRuleV1 Rule(AutomationTriggerKind kind, string value) => new(
        AutomationRuleV1.CurrentSchemaVersion,
        "rule",
        "Test rule",
        Enabled: true,
        kind,
        value,
        "balanced",
        Priority: 10);

    private static AutomationObservation Observation(DateTimeOffset timestamp, bool processRunning) => new(
        timestamp,
        processRunning
            ? new HashSet<string>(["game"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        null,
        SessionLocked: false,
        IdleTime: TimeSpan.Zero,
        Hotkey: null);
}
