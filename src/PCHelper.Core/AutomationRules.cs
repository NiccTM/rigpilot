namespace PCHelper.Core;

public sealed record AutomationRuleMatch(string RuleId, string ProfileId, int Priority, DateTimeOffset ActivatedAt);

public static class AutomationRuleResolver
{
    public static string? Resolve(string? manualProfileId, IEnumerable<AutomationRuleMatch> activeRules, string? defaultProfileId)
    {
        if (!string.IsNullOrWhiteSpace(manualProfileId))
        {
            return manualProfileId;
        }

        AutomationRuleMatch? match = activeRules
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.ActivatedAt)
            .ThenBy(rule => rule.RuleId, StringComparer.Ordinal)
            .FirstOrDefault();
        return match?.ProfileId ?? defaultProfileId;
    }
}
