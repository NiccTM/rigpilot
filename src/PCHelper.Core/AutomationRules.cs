namespace PCHelper.Core;

using PCHelper.Contracts;

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

public static class AutomationRuleMatcher
{
    public static bool IsMatch(AutomationRuleV1 rule, AutomationObservation observation)
    {
        if (!rule.Enabled || rule.SchemaVersion != AutomationRuleV1.CurrentSchemaVersion)
        {
            return false;
        }

        string value = rule.TriggerValue.Trim();
        return rule.TriggerKind switch
        {
            AutomationTriggerKind.Process => observation.RunningProcesses.Any(
                process => ProcessEquals(process, value)),
            AutomationTriggerKind.ForegroundApplication => ProcessEquals(observation.ForegroundProcess, value),
            AutomationTriggerKind.Schedule => IsInSchedule(value, observation.Timestamp.TimeOfDay),
            AutomationTriggerKind.SessionLock => IsSessionMatch(value, observation.SessionLocked),
            AutomationTriggerKind.Idle => IsIdleMatch(value, observation.IdleTime),
            AutomationTriggerKind.Hotkey => string.Equals(
                NormaliseHotkey(observation.Hotkey),
                NormaliseHotkey(value),
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static string? Validate(AutomationRuleV1 rule, IReadOnlySet<string>? profileIds = null)
    {
        if (rule.SchemaVersion != AutomationRuleV1.CurrentSchemaVersion)
        {
            return $"Unsupported automation schema {rule.SchemaVersion}.";
        }

        if (string.IsNullOrWhiteSpace(rule.Id)
            || string.IsNullOrWhiteSpace(rule.Name)
            || string.IsNullOrWhiteSpace(rule.ProfileId)
            || string.IsNullOrWhiteSpace(rule.TriggerValue))
        {
            return "Rule ID, name, profile, and trigger value are required.";
        }

        if (rule.Priority is < -10_000 or > 10_000)
        {
            return "Rule priority must be between -10000 and 10000.";
        }

        if (profileIds is not null && !profileIds.Contains(rule.ProfileId))
        {
            return "The selected automation profile does not exist.";
        }

        return rule.TriggerKind switch
        {
            AutomationTriggerKind.Schedule when !TryParseSchedule(rule.TriggerValue, out _, out _) =>
                "Schedule value must use HH:mm-HH:mm.",
            AutomationTriggerKind.SessionLock when !IsSessionValue(rule.TriggerValue) =>
                "Session-lock value must be 'locked' or 'unlocked'.",
            AutomationTriggerKind.Idle when !TryParsePositiveMinutes(rule.TriggerValue, out _) =>
                "Idle value must be a positive number of minutes.",
            AutomationTriggerKind.Hotkey when !rule.TriggerValue.Contains('+', StringComparison.Ordinal) =>
                "Hotkey value must be a registered combination such as Ctrl+Alt+1.",
            _ => null
        };
    }

    private static bool ProcessEquals(string? observed, string expected)
    {
        static string Normalise(string value) => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
        return observed is not null
            && string.Equals(Normalise(observed.Trim()), Normalise(expected.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInSchedule(string value, TimeSpan now)
    {
        if (!TryParseSchedule(value, out TimeSpan start, out TimeSpan end))
        {
            return false;
        }

        return start <= end ? now >= start && now < end : now >= start || now < end;
    }

    private static bool TryParseSchedule(string value, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        string[] parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && TimeSpan.TryParseExact(parts[0], "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out start)
            && TimeSpan.TryParseExact(parts[1], "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out end)
            && start < TimeSpan.FromDays(1)
            && end < TimeSpan.FromDays(1);
    }

    private static bool IsSessionMatch(string value, bool locked) => value.Trim().ToLowerInvariant() switch
    {
        "locked" or "true" => locked,
        "unlocked" or "false" => !locked,
        _ => false
    };

    private static bool IsSessionValue(string value) => value.Trim().ToLowerInvariant() is
        "locked" or "true" or "unlocked" or "false";

    private static bool IsIdleMatch(string value, TimeSpan idle) =>
        TryParsePositiveMinutes(value, out double minutes) && idle >= TimeSpan.FromMinutes(minutes);

    private static bool TryParsePositiveMinutes(string value, out double minutes) =>
        (double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out minutes)
            || double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture,
                out minutes))
        && double.IsFinite(minutes)
        && minutes > 0;

    private static string? NormaliseHotkey(string? value) => value?.Replace(" ", string.Empty, StringComparison.Ordinal);
}

public sealed class AutomationRuleStateMachine(
    TimeSpan? entryDebounce = null,
    TimeSpan? exitDebounce = null,
    TimeSpan? switchCooldown = null)
{
    private readonly TimeSpan _entryDebounce = entryDebounce ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _exitDebounce = exitDebounce ?? TimeSpan.FromSeconds(15);
    private readonly TimeSpan _switchCooldown = switchCooldown ?? TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, RuleState> _states = new(StringComparer.Ordinal);
    private string? _currentProfileId;
    private DateTimeOffset? _lastSwitchAt;

    public AutomationDecision Evaluate(
        IReadOnlyList<AutomationRuleV1> rules,
        AutomationObservation observation,
        string? manualProfileId,
        string? defaultProfileId)
    {
        HashSet<string> liveIds = rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string removed in _states.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            _states.Remove(removed);
        }

        List<AutomationRuleMatch> active = [];
        foreach (AutomationRuleV1 rule in rules.Where(rule => rule.Enabled))
        {
            bool matched = AutomationRuleMatcher.IsMatch(rule, observation);
            _states.TryGetValue(rule.Id, out RuleState? prior);
            RuleState state = Advance(rule, prior, matched, observation.Timestamp);
            _states[rule.Id] = state;
            if (state.Active)
            {
                active.Add(new AutomationRuleMatch(rule.Id, rule.ProfileId, rule.Priority, state.ActivatedAt));
            }
        }

        string? resolved = AutomationRuleResolver.Resolve(manualProfileId, active, defaultProfileId);
        string reason = !string.IsNullOrWhiteSpace(manualProfileId)
            ? "Manual profile selection overrides automation."
            : active.Count > 0
                ? $"Resolved {active.Count} active rule(s) by priority and activation time."
                : "No debounced automation rule is active.";
        if (string.Equals(resolved, _currentProfileId, StringComparison.Ordinal))
        {
            return new AutomationDecision(resolved, false, reason, active.Select(match => match.RuleId).ToArray());
        }

        bool manual = !string.IsNullOrWhiteSpace(manualProfileId);
        if (!manual
            && _lastSwitchAt is DateTimeOffset lastSwitch
            && observation.Timestamp - lastSwitch < _switchCooldown)
        {
            return new AutomationDecision(
                _currentProfileId,
                false,
                $"Profile-switch cooldown is active for {(_switchCooldown - (observation.Timestamp - lastSwitch)).TotalSeconds:0} more seconds.",
                active.Select(match => match.RuleId).ToArray());
        }

        bool initialDefault = _currentProfileId is null
            && _lastSwitchAt is null
            && active.Count == 0
            && string.Equals(resolved, defaultProfileId, StringComparison.Ordinal);
        _currentProfileId = resolved;
        if (!initialDefault)
        {
            _lastSwitchAt = observation.Timestamp;
        }
        return new AutomationDecision(resolved, true, reason, active.Select(match => match.RuleId).ToArray());
    }

    public void SetCurrentProfile(string? profileId)
    {
        _currentProfileId = profileId;
    }

    private RuleState Advance(
        AutomationRuleV1 rule,
        RuleState? prior,
        bool matched,
        DateTimeOffset now)
    {
        RuleState state = prior ?? new RuleState(false, now, false, DateTimeOffset.MinValue, null);
        if (matched)
        {
            DateTimeOffset conditionSince = state.ConditionMatched ? state.ConditionSince : now;
            TimeSpan requiredEntry = rule.TriggerKind == AutomationTriggerKind.Hotkey
                ? TimeSpan.Zero
                : _entryDebounce;
            bool active = state.Active || now - conditionSince >= requiredEntry;
            DateTimeOffset activatedAt = active && !state.Active ? now : state.ActivatedAt;
            return new RuleState(true, conditionSince, active, activatedAt, null);
        }

        if (!state.Active)
        {
            return new RuleState(false, now, false, DateTimeOffset.MinValue, null);
        }

        DateTimeOffset exitSince = state.ExitSince ?? now;
        bool remainActive = now - exitSince < _exitDebounce;
        return new RuleState(false, now, remainActive, state.ActivatedAt, remainActive ? exitSince : null);
    }

    private sealed record RuleState(
        bool ConditionMatched,
        DateTimeOffset ConditionSince,
        bool Active,
        DateTimeOffset ActivatedAt,
        DateTimeOffset? ExitSince);
}
