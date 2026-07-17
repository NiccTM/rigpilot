using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfPointCollection = System.Windows.Media.PointCollection;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public sealed partial class MainViewModel
{
    private async Task AddAutomationRuleCoreAsync()
    {
        if (!CanAddAutomationRule
            || !int.TryParse(NewRulePriorityText, out int priority))
        {
            throw new InvalidOperationException("Complete the rule fields using the trigger format shown.");
        }

        AutomationRuleV1 rule = CreateAutomationRule(Guid.NewGuid().ToString("N"), priority);
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveAutomationRule,
            rule,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        AutomationRuleV1 saved = IpcJson.FromElement<AutomationRuleV1>(response.Payload) ?? rule;
        AutomationRuleDisplay? existing = AutomationRules.FirstOrDefault(item => item.Rule.Id == saved.Id);
        if (existing is not null)
        {
            AutomationRules.Remove(existing);
        }

        AutomationRules.Add(AutomationRuleDisplay.From(saved));
        ReorderAutomationRules();
        NewRuleTriggerValue = string.Empty;
        AutomationStatus = $"Saved rule '{saved.Name}'. Entry debounce is 5 seconds.";
        NotifyAutomationProperties();
        ShowNotice($"Automation rule '{saved.Name}' saved.", "Success");
    }

    private async Task DeleteAutomationRuleCoreAsync(AutomationRuleV1 rule)
    {
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.DeleteAutomationRule,
            new DeleteAutomationRuleRequest(rule.Id),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        AutomationRuleDisplay? display = AutomationRules.FirstOrDefault(item => item.Rule.Id == rule.Id);
        if (display is not null)
        {
            AutomationRules.Remove(display);
        }

        AutomationStatus = $"Deleted rule '{rule.Name}'.";
        NotifyAutomationProperties();
    }

    private async Task EvaluateAutomationAsync()
    {
        if (_automationEvaluating
            || !IsServiceOnline
            || !AutomationServiceSupported
            || IsSafeModeEnabled
            || HasActiveOperation
            || AutomationRules.Count == 0
            || DateTimeOffset.UtcNow - _lastAutomationEvaluation < TimeSpan.FromSeconds(1))
        {
            if (IsSafeModeEnabled)
            {
                AutomationStatus = "Safe mode is active; automation is suspended until an operator exits recovery mode.";
            }
            return;
        }

        _automationEvaluating = true;
        _lastAutomationEvaluation = DateTimeOffset.UtcNow;
        try
        {
            if (!_automationMachineInitialised)
            {
                _automationMachine.SetCurrentProfile(_status?.ActiveProfileId);
                _automationMachineInitialised = true;
            }

            string? hotkey = Interlocked.Exchange(ref _pendingAutomationHotkey, null);
            AutomationObservation observation = AutomationObservationProvider.Capture(_sessionLocked, hotkey);
            AutomationDecision decision = _automationMachine.Evaluate(
                AutomationRules.Select(item => item.Rule).ToArray(),
                observation,
                _manualProfileId,
                defaultProfileId: "balanced");
            AutomationStatus = decision.Reason;
            if (!decision.ShouldSwitch
                || decision.ProfileId is null
                || string.Equals(decision.ProfileId, _status?.ActiveProfileId, StringComparison.Ordinal))
            {
                return;
            }

            if (_suiteProfilesById.TryGetValue(decision.ProfileId, out ProfileV2? suiteProfile))
            {
                await ApplyProfileV2Async(suiteProfile, manualSelection: false);
                AutomationStatus = $"Automation applied {suiteProfile.Name}. {decision.Reason}";
                return;
            }

            ProfileV1? profile = Profiles.FirstOrDefault(item => string.Equals(item.Id, decision.ProfileId, StringComparison.Ordinal));
            if (profile is null)
            {
                AutomationStatus = $"Resolved profile '{decision.ProfileId}' is unavailable.";
                return;
            }

            await ApplyProfileAsync(profile, manualSelection: false);
            AutomationStatus = $"Automation applied {profile.Name}. {decision.Reason}";
        }
        catch (Exception exception)
        {
            AutomationStatus = $"Automation evaluation failed: {exception.Message}";
        }
        finally
        {
            _automationEvaluating = false;
        }
    }

    public void NotifyAutomationHotkey(string hotkey)
    {
        _pendingAutomationHotkey = hotkey;
        AutomationStatus = $"Hotkey {hotkey} received; evaluating matching rules.";
    }

    public void SetSessionLocked(bool locked)
    {
        _sessionLocked = locked;
        AutomationStatus = locked ? "Session locked; evaluating rules." : "Session unlocked; evaluating rules.";
    }

    private void ResumeAutomation()
    {
        _manualProfileId = null;
        AutomationStatus = "Manual override cleared; automation will resume on the next evaluation.";
        NotifyAutomationProperties();
    }

    private AutomationRuleV1 CreateAutomationRule(string id, int priority) => new(
        AutomationRuleV1.CurrentSchemaVersion,
        id,
        NewRuleName.Trim(),
        Enabled: true,
        NewRuleTriggerKind,
        NewRuleTriggerValue.Trim(),
        NewRuleProfile?.Id ?? string.Empty,
        priority);

    private void ReorderAutomationRules()
    {
        AutomationRuleDisplay[] ordered = AutomationRules
            .OrderByDescending(item => item.Rule.Priority)
            .ThenBy(item => item.Rule.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Replace(AutomationRules, ordered);
    }

    private string LocalProbeLabel => IsPortableMode ? Localization.L10n.Get("Portable_DataSourceLabel") : "Local probe";
}
