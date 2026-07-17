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
    private async Task SaveMonitoringPreferencesCoreAsync()
    {
        SensorTrendDisplay trend = SelectedMonitoringTrend
            ?? throw new InvalidOperationException("Select a live sensor before saving its monitoring preferences.");
        List<SensorAliasV1> aliases = _monitoringPreferences.Aliases
            .Where(alias => !string.Equals(alias.SensorId, trend.SensorId, StringComparison.Ordinal))
            .ToList();
        string aliasText = SensorAliasText.Trim();
        if (!string.IsNullOrWhiteSpace(aliasText))
        {
            aliases.Add(new SensorAliasV1(trend.SensorId, aliasText));
        }

        HashSet<string> pins = new(_monitoringPreferences.PinnedSensorIds, StringComparer.Ordinal);
        if (SelectedSensorPinned)
        {
            pins.Add(trend.SensorId);
        }
        else
        {
            pins.Remove(trend.SensorId);
        }

        MonitoringPreferencesV1 preferences = new(
            MonitoringPreferencesV1.CurrentSchemaVersion,
            MonitoringPreferencesV1.DefaultId,
            aliases,
            pins.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            DateTimeOffset.UtcNow);
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveMonitoringPreferences,
                preferences,
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        _monitoringPreferences = IpcJson.FromElement<MonitoringPreferencesV1>(response.Payload) ?? preferences;
        UpdateMonitoringTrends();
        ShowNotice("Saved the local sensor alias and pin preference.", "Success");
    }

    private Task AddMonitoringComparisonSensorCoreAsync()
    {
        SensorTrendDisplay trend = SelectedMonitoringComparisonTrend
            ?? throw new InvalidOperationException("Select a live sensor to add to the comparison workspace.");
        if (!CanAddMonitoringComparisonSensor)
        {
            throw new InvalidOperationException("Choose a distinct live sensor and keep the comparison workspace to four series or fewer.");
        }

        _monitoringComparisonLayout = _monitoringComparisonLayout with
        {
            SensorIds = _monitoringComparisonLayout.SensorIds.Concat([trend.SensorId]).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        UpdateMonitoringComparison();
        MonitoringComparisonStatus = "Comparison changed locally. Save it to keep this selection for your signed-in user.";
        return Task.CompletedTask;
    }

    private Task RemoveMonitoringComparisonSensorCoreAsync(SensorComparisonSeriesDisplay series)
    {
        _monitoringComparisonLayout = _monitoringComparisonLayout with
        {
            SensorIds = _monitoringComparisonLayout.SensorIds
                .Where(id => !string.Equals(id, series.SensorId, StringComparison.Ordinal))
                .ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        UpdateMonitoringComparison();
        MonitoringComparisonStatus = "Comparison changed locally. Save it to keep this selection for your signed-in user.";
        return Task.CompletedTask;
    }

    private async Task SaveMonitoringComparisonLayoutCoreAsync()
    {
        MonitoringComparisonLayoutV1 layout = _monitoringComparisonLayout with
        {
            SchemaVersion = MonitoringComparisonLayoutV1.CurrentSchemaVersion,
            Id = MonitoringComparisonLayoutV1.DefaultId,
            SensorIds = MonitoringComparisonSeries.Select(series => series.SensorId).ToArray(),
            NormalizeEachSeries = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveMonitoringComparisonLayout,
                layout,
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        _monitoringComparisonLayout = IpcJson.FromElement<MonitoringComparisonLayoutV1>(response.Payload) ?? layout;
        UpdateMonitoringComparison();
        MonitoringComparisonStatus = MonitoringComparisonSeries.Count == 0
            ? "Saved an empty comparison workspace. Add up to four live sensors when you want to compare movement."
            : $"Saved {MonitoringComparisonSeries.Count} normalized comparison series for this signed-in user.";
        ShowNotice("Saved the local monitoring comparison workspace.", "Success");
    }

    private async Task AddRecommendedHealthRulesCoreAsync()
    {
        IReadOnlyList<HealthRuleRecommendation> recommendations = HealthRuleRecommendations.Build(
            MonitoringTrends.Select(trend => trend.Trend).ToArray());
        HealthRuleRecommendation[] pending = recommendations
            .Where(recommendation => !HealthRules.Any(existing => SameHealthRule(existing.Rule, recommendation.Rule)))
            .ToArray();
        if (pending.Length == 0)
        {
            HealthRecommendationStatus = "All currently applicable notify-only baseline rules are already installed.";
            ShowNotice(HealthRecommendationStatus, "Info");
            return;
        }

        foreach (HealthRuleRecommendation recommendation in pending)
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SaveHealthRule,
                    recommendation.Rule,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(response);
            UpdateStateRevision(response);
        }

        await RefreshReliabilityAsync(_lifetime.Token);
        HealthRecommendationStatus = $"Installed {pending.Length} notify-only baseline rule(s). Review the sensor-specific thresholds before relying on them.";
        ShowNotice(HealthRecommendationStatus, "Success");
    }

    private bool TryBuildHealthRule(out HealthRuleV1 rule)
    {
        rule = default!;
        string name = NewHealthRuleName.Trim();
        bool usesSensor = NewHealthRuleCondition is HealthRuleConditionKind.SensorAbove
            or HealthRuleConditionKind.SensorBelow
            or HealthRuleConditionKind.SensorStale
            or HealthRuleConditionKind.FanBelow;
        bool usesThreshold = NewHealthRuleCondition is HealthRuleConditionKind.SensorAbove
            or HealthRuleConditionKind.SensorBelow
            or HealthRuleConditionKind.FanBelow;
        if (name.Length is 0 or > 96
            || (usesSensor && SelectedHealthTrend is null)
            || !int.TryParse(NewHealthConsecutiveText, out int consecutive)
            || consecutive is < 1 or > 60
            || !int.TryParse(NewHealthCooldownText, out int cooldownSeconds)
            || cooldownSeconds is < 0 or > 604800)
        {
            return false;
        }

        double? threshold = null;
        if (usesThreshold)
        {
            if (!TryParseDouble(NewHealthThresholdText, out double parsedThreshold) || !double.IsFinite(parsedThreshold))
            {
                return false;
            }
            threshold = parsedThreshold;
        }

        string? profileId = NewHealthRuleAction == HealthRuleActionKind.RequestEmergencyProfile
            ? SelectedEmergencyProfile?.Id
            : null;
        if (NewHealthRuleAction == HealthRuleActionKind.RequestEmergencyProfile && string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        rule = new HealthRuleV1(
            HealthRuleV1.CurrentSchemaVersion,
            $"health.{Guid.NewGuid():N}",
            name,
            NewHealthRuleCondition,
            usesSensor ? SelectedHealthTrend!.SensorId : null,
            threshold,
            consecutive,
            TimeSpan.FromSeconds(cooldownSeconds),
            NewHealthRuleAction,
            profileId,
            Enabled: true);
        return HealthRuleEngine.Validate(rule).IsValid;
    }

    private async Task SaveHealthRuleCoreAsync()
    {
        if (!TryBuildHealthRule(out HealthRuleV1 rule))
        {
            throw new InvalidOperationException("Complete the rule name, source, threshold (when required), consecutive observations, cooldown, and emergency profile selection.");
        }
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveHealthRule,
                rule,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshReliabilityAsync(_lifetime.Token);
        ShowNotice($"Saved health rule '{rule.Name}'.", "Success");
    }

    private async Task DeleteHealthRuleCoreAsync(HealthRuleV1 rule)
    {
        DeleteHealthRuleRequestV1 payload = new(DeleteHealthRuleRequestV1.CurrentSchemaVersion, rule.Id);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.DeleteHealthRule,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshReliabilityAsync(_lifetime.Token);
        ShowNotice($"Deleted health rule '{rule.Name}'.", "Info");
    }

    private async Task AcknowledgeHealthAlertCoreAsync(HealthAlertEventV1 alert)
    {
        AcknowledgeHealthAlertRequestV1 payload = new(AcknowledgeHealthAlertRequestV1.CurrentSchemaVersion, alert.Id);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.AcknowledgeHealthAlert,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshReliabilityAsync(_lifetime.Token);
        ShowNotice("Health alert acknowledged. Its condition remains monitored until it clears.", "Info");
    }

    private async Task SetSafeModeCoreAsync(bool enabled)
    {
        string reason = SafeModeReason.Trim();
        if (reason.Length is 0 or > 256)
        {
            throw new InvalidOperationException("Provide a safe-mode reason up to 256 characters.");
        }
        SetSafeModeRequestV1 payload = new(SetSafeModeRequestV1.CurrentSchemaVersion, enabled, reason);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetSafeMode,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        _safetyRecoveryStatus = IpcJson.FromElement<SafetyRecoveryStatusV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty safe-mode state.");
        UpdateStateRevision(response);
        NotifyReliabilityProperties();
        ShowNotice(enabled
            ? "Safe mode is active. Automation and alert-driven profile requests are suspended; no hardware reset was assumed."
            : "Safe mode was exited. Review the recovery guidance before enabling automation or experimental writes.",
            enabled ? "Warning" : "Info");
    }

    private OsdLayoutV1 ResolveDesktopOsdLayout()
    {
        if (SelectedDesktopOsdLayout is not null)
        {
            return SelectedDesktopOsdLayout;
        }
        if (_snapshot is null)
        {
            throw new InvalidOperationException("No hardware snapshot is available for the desktop OSD.");
        }

        SensorSample[] selected = SelectImportantSensors(_snapshot).Take(8).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("No usable sensors are available for the automatic desktop OSD.");
        }
        return new OsdLayoutV1(
            OsdLayoutV1.CurrentSchemaVersion,
            "osd.automatic-live",
            "Automatic live sensors",
            selected.Select((sensor, index) => new OsdWidgetV1(
                sensor.SensorId,
                sensor.Name,
                OsdFormatFor(sensor.Unit),
                index / 2,
                index % 2,
                OsdColourFor(sensor.Unit))).ToArray(),
            0.92,
            1,
            ShowGraph: false);
    }
}
