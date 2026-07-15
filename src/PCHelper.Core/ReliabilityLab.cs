using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Deterministic health-rule evaluation. The service owns persistence and any
/// later hardware action; this component only derives typed alerts from typed
/// samples and system signals.
/// </summary>
public sealed class HealthRuleEngine
{
    private readonly Dictionary<string, int> _consecutiveMatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastRaised = new(StringComparer.Ordinal);

    public static SuiteValidationResult Validate(HealthRuleV1 rule)
    {
        List<string> errors = [];
        if (rule.SchemaVersion != HealthRuleV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported health-rule schema {rule.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 96)
        {
            errors.Add("Health rules require an ID and a name up to 96 characters.");
        }
        if (rule.ConsecutiveObservations is < 1 or > 60)
        {
            errors.Add("Consecutive observations must be from 1 through 60.");
        }
        if (rule.Condition is HealthRuleConditionKind.WheaEvent or HealthRuleConditionKind.DisplayDriverReset
            && rule.ConsecutiveObservations != 1)
        {
            errors.Add("Windows event rules must use one observation because each event is edge-triggered.");
        }
        if (rule.Cooldown < TimeSpan.Zero || rule.Cooldown > TimeSpan.FromDays(7))
        {
            errors.Add("Health-rule cooldown must be between zero and seven days.");
        }
        bool needsSensor = rule.Condition is HealthRuleConditionKind.SensorAbove
            or HealthRuleConditionKind.SensorBelow
            or HealthRuleConditionKind.SensorStale
            or HealthRuleConditionKind.FanBelow;
        if (needsSensor && string.IsNullOrWhiteSpace(rule.SensorId))
        {
            errors.Add("This health-rule condition requires an exact sensor ID.");
        }
        bool needsThreshold = rule.Condition is HealthRuleConditionKind.SensorAbove
            or HealthRuleConditionKind.SensorBelow
            or HealthRuleConditionKind.FanBelow;
        if (needsThreshold && (rule.Threshold is not double threshold || !double.IsFinite(threshold)))
        {
            errors.Add("This health-rule condition requires a finite threshold.");
        }
        if (rule.Condition == HealthRuleConditionKind.FanBelow && rule.Threshold is double fanThreshold && fanThreshold < 0)
        {
            errors.Add("A fan threshold cannot be negative.");
        }
        if (rule.Action == HealthRuleActionKind.RequestEmergencyProfile && string.IsNullOrWhiteSpace(rule.EmergencyProfileId))
        {
            errors.Add("An emergency-profile action requires a typed profile ID.");
        }
        return new SuiteValidationResult(errors.Count == 0, errors, []);
    }

    public IReadOnlyList<HealthAlertEventV1> Evaluate(
        IReadOnlyList<HealthRuleV1> rules,
        IReadOnlyList<SensorSample> sensors,
        IReadOnlyList<HealthSystemSignal> systemSignals,
        IReadOnlyList<HealthAlertEventV1> existing,
        DateTimeOffset now)
    {
        List<HealthAlertEventV1> changes = [];
        foreach (HealthRuleV1 rule in rules.Where(item => item.Enabled))
        {
            SuiteValidationResult validation = Validate(rule);
            if (!validation.IsValid)
            {
                continue;
            }

            HealthRuleMatch match = Match(rule, sensors, systemSignals, now);
            HealthAlertEventV1? active = existing
                .Where(item => item.RuleId == rule.Id && item.State is HealthAlertState.Active or HealthAlertState.Acknowledged)
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
            if (!match.IsMatch)
            {
                _consecutiveMatches.Remove(rule.Id);
                if (active is not null)
                {
                    changes.Add(active with
                    {
                        State = HealthAlertState.Cleared,
                        UpdatedAt = now,
                        ActionResult = active.ActionResult ?? "Condition cleared."
                    });
                }
                continue;
            }

            int count = _consecutiveMatches.TryGetValue(rule.Id, out int previous) ? previous + 1 : 1;
            _consecutiveMatches[rule.Id] = count;
            if (active is not null || count < rule.ConsecutiveObservations)
            {
                continue;
            }
            if (_lastRaised.TryGetValue(rule.Id, out DateTimeOffset last) && now - last < rule.Cooldown)
            {
                continue;
            }

            _lastRaised[rule.Id] = now;
            changes.Add(new HealthAlertEventV1(
                HealthAlertEventV1.CurrentSchemaVersion,
                $"alert.{rule.Id}.{now.UtcTicks}",
                rule.Id,
                rule.Name,
                rule.Condition,
                rule.Action,
                HealthAlertState.Active,
                now,
                now,
                match.Message,
                rule.SensorId,
                match.ObservedValue,
                match.Unit,
                rule.EmergencyProfileId,
                ActionExecuted: false,
                ActionResult: null));
        }
        return changes;
    }

    private static HealthRuleMatch Match(
        HealthRuleV1 rule,
        IReadOnlyList<SensorSample> sensors,
        IReadOnlyList<HealthSystemSignal> systemSignals,
        DateTimeOffset now)
    {
        SensorSample? sample = string.IsNullOrWhiteSpace(rule.SensorId)
            ? null
            : sensors.Where(item => item.SensorId == rule.SensorId).OrderByDescending(item => item.Timestamp).FirstOrDefault();
        return rule.Condition switch
        {
            HealthRuleConditionKind.SensorAbove when sample?.Value is double value && value >= rule.Threshold =>
                new(true, value, sample.Unit, $"{sample.Name} reached {value:F1} {sample.Unit}, at or above the {rule.Threshold:F1} threshold."),
            HealthRuleConditionKind.SensorBelow when sample?.Value is double value && value <= rule.Threshold =>
                new(true, value, sample.Unit, $"{sample.Name} reached {value:F1} {sample.Unit}, at or below the {rule.Threshold:F1} threshold."),
            HealthRuleConditionKind.FanBelow when sample?.Value is double value
                && string.Equals(sample.Unit, "RPM", StringComparison.OrdinalIgnoreCase)
                && value <= rule.Threshold =>
                new(true, value, sample.Unit, $"{sample.Name} is at {value:F0} RPM, at or below the {rule.Threshold:F0} RPM floor."),
            HealthRuleConditionKind.SensorStale when sample is null
                || sample.Quality is SensorQuality.Stale or SensorQuality.Unavailable or SensorQuality.Invalid
                || now - sample.Timestamp > sample.Freshness =>
                new(true, sample?.Value, sample?.Unit, sample is null
                    ? "The selected sensor is unavailable."
                    : $"{sample.Name} is stale or unavailable."),
            HealthRuleConditionKind.WheaEvent when systemSignals.Any(item => item.Kind == HealthSystemSignalKind.Whea) =>
                new(true, null, null, systemSignals.First(item => item.Kind == HealthSystemSignalKind.Whea).Message),
            HealthRuleConditionKind.DisplayDriverReset when systemSignals.Any(item => item.Kind == HealthSystemSignalKind.DisplayDriverReset) =>
                new(true, null, null, systemSignals.First(item => item.Kind == HealthSystemSignalKind.DisplayDriverReset).Message),
            _ => new(false, null, null, string.Empty)
        };
    }

    private sealed record HealthRuleMatch(bool IsMatch, double? ObservedValue, string? Unit, string Message);
}

public enum HealthSystemSignalKind
{
    Whea,
    DisplayDriverReset
}

public sealed record HealthSystemSignal(HealthSystemSignalKind Kind, DateTimeOffset Timestamp, string Message);

/// <summary>
/// Conservative, opt-in health-rule suggestions for a newly commissioned
/// machine. Every suggestion is notify-only: installing the baseline cannot
/// change a fan, power policy, profile, or automation state.
/// </summary>
public sealed record HealthRuleRecommendation(string Key, HealthRuleV1 Rule, string Guidance);

public static class HealthRuleRecommendations
{
    public static IReadOnlyList<HealthRuleRecommendation> Build(IReadOnlyList<SensorTrendV1> trends)
    {
        List<HealthRuleRecommendation> recommendations = [];
        SensorTrendV1[] source = trends
            .Where(trend => !string.IsNullOrWhiteSpace(trend.SensorId))
            .ToArray();

        SensorTrendV1? cpuTemperature = source
            .Where(IsTemperature)
            .OrderBy(CpuTemperatureRank)
            .FirstOrDefault(trend => IsCpuTemperature(trend));
        if (cpuTemperature is not null)
        {
            recommendations.Add(SensorAbove(
                "cpu-temperature",
                "CPU temperature warning",
                cpuTemperature,
                threshold: 85,
                "Notifies after three high CPU-temperature observations. It does not change hardware."));
            recommendations.Add(SensorStale(
                "cpu-temperature-stale",
                "CPU temperature telemetry stale",
                cpuTemperature,
                "Notifies after three unavailable CPU-temperature observations. It does not change hardware."));
        }

        SensorTrendV1? gpuTemperature = source
            .Where(IsTemperature)
            .OrderBy(GpuTemperatureRank)
            .FirstOrDefault(trend => IsGpuTemperature(trend));
        if (gpuTemperature is not null)
        {
            recommendations.Add(SensorAbove(
                "gpu-temperature",
                "GPU temperature warning",
                gpuTemperature,
                threshold: 85,
                "Notifies after three high GPU-temperature observations. It does not change hardware."));
            recommendations.Add(SensorStale(
                "gpu-temperature-stale",
                "GPU temperature telemetry stale",
                gpuTemperature,
                "Notifies after three unavailable GPU-temperature observations. It does not change hardware."));
        }

        SensorTrendV1? pump = source
            .Where(trend => string.Equals(NormalizeUnit(trend.Unit), "RPM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(trend => trend.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(trend => Contains(trend, "pump"));
        if (pump is not null)
        {
            recommendations.Add(new HealthRuleRecommendation(
                "pump-rpm",
                CreateRule(
                    "pump-rpm",
                    "Pump RPM warning",
                    HealthRuleConditionKind.FanBelow,
                    pump.SensorId,
                    threshold: 800,
                    observations: 3,
                    cooldown: TimeSpan.FromSeconds(60)),
                "Notifies after three low pump-RPM observations. Verify the real pump floor before changing this threshold."));
            recommendations.Add(SensorStale(
                "pump-rpm-stale",
                "Pump RPM telemetry stale",
                pump,
                "Notifies after three unavailable pump-RPM observations. It does not change the pump or fan policy."));
        }

        recommendations.Add(new HealthRuleRecommendation(
            "whea-event",
            CreateRule(
                "whea-event",
                "WHEA hardware warning",
                HealthRuleConditionKind.WheaEvent,
                sensorId: null,
                threshold: null,
                observations: 1,
                cooldown: TimeSpan.FromMinutes(5)),
            "Notifies when Windows records a WHEA hardware event. It does not alter a profile or hardware setting."));
        recommendations.Add(new HealthRuleRecommendation(
            "display-driver-reset",
            CreateRule(
                "display-driver-reset",
                "Display-driver reset warning",
                HealthRuleConditionKind.DisplayDriverReset,
                sensorId: null,
                threshold: null,
                observations: 1,
                cooldown: TimeSpan.FromMinutes(5)),
            "Notifies when Windows records a display-driver reset. It does not alter a profile or hardware setting."));
        return recommendations;
    }

    private static HealthRuleRecommendation SensorAbove(
        string key,
        string name,
        SensorTrendV1 trend,
        double threshold,
        string guidance) => new(
        key,
        CreateRule(key, name, HealthRuleConditionKind.SensorAbove, trend.SensorId, threshold, observations: 3, cooldown: TimeSpan.FromSeconds(60)),
        guidance);

    private static HealthRuleRecommendation SensorStale(
        string key,
        string name,
        SensorTrendV1 trend,
        string guidance) => new(
        key,
        CreateRule(key, name, HealthRuleConditionKind.SensorStale, trend.SensorId, threshold: null, observations: 3, cooldown: TimeSpan.FromMinutes(5)),
        guidance);

    private static HealthRuleV1 CreateRule(
        string key,
        string name,
        HealthRuleConditionKind condition,
        string? sensorId,
        double? threshold,
        int observations,
        TimeSpan cooldown)
    {
        string identity = $"{key}|{sensorId ?? "system"}";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        string id = $"health.baseline.{key}.{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
        return new HealthRuleV1(
            HealthRuleV1.CurrentSchemaVersion,
            id,
            name,
            condition,
            sensorId,
            threshold,
            observations,
            cooldown,
            HealthRuleActionKind.NotifyOnly,
            EmergencyProfileId: null,
            Enabled: true);
    }

    private static bool IsTemperature(SensorTrendV1 trend) =>
        string.Equals(NormalizeUnit(trend.Unit), "°C", StringComparison.OrdinalIgnoreCase);

    private static bool IsCpuTemperature(SensorTrendV1 trend) =>
        Contains(trend, "cpu") || Contains(trend, "tctl") || Contains(trend, "package") || Contains(trend, "amdcpu");

    private static bool IsGpuTemperature(SensorTrendV1 trend) =>
        Contains(trend, "gpu") || Contains(trend, "nvidia") || Contains(trend, "radeon") || Contains(trend, "arc");

    private static int CpuTemperatureRank(SensorTrendV1 trend) =>
        Contains(trend, "tctl") ? 0 : Contains(trend, "package") ? 1 : Contains(trend, "cpu") ? 2 : 3;

    private static int GpuTemperatureRank(SensorTrendV1 trend) =>
        Contains(trend, "hot spot") || Contains(trend, "junction") ? 2 : Contains(trend, "core") ? 0 : 1;

    private static bool Contains(SensorTrendV1 trend, string value) =>
        trend.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase)
        || trend.SensorId.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUnit(string unit) => unit
        .Replace("\u00C2\u00B0C", "\u00B0C", StringComparison.Ordinal)
        .Replace("Celsius", "\u00B0C", StringComparison.OrdinalIgnoreCase)
        .Trim();
}

/// <summary>
/// Small deterministic monitoring helpers shared by the dashboard and tests.
/// Sparkline text deliberately avoids a rendering dependency while retaining
/// history ordering and comparison information for accessible UI surfaces.
/// </summary>
public static class MonitoringWorkspace
{
    private const string Blocks = "▁▂▃▄▅▆▇█";

    public static IReadOnlyList<SensorTrendV1> BuildTrends(
        IReadOnlyList<SensorSample> samples,
        MonitoringPreferencesV1 preferences,
        int maximumPoints = 24)
    {
        if (maximumPoints is < 2 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPoints));
        }
        IReadOnlyDictionary<string, string> aliases = preferences.Aliases
            .Where(item => !string.IsNullOrWhiteSpace(item.SensorId) && !string.IsNullOrWhiteSpace(item.Alias))
            .GroupBy(item => item.SensorId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Alias.Trim(), StringComparer.Ordinal);
        return samples
            .Where(sample => sample.Value is double value && double.IsFinite(value))
            .GroupBy(sample => sample.SensorId, StringComparer.Ordinal)
            .Select(group => BuildTrend(group.OrderBy(sample => sample.Timestamp).ToArray(), aliases, maximumPoints))
            .OrderByDescending(item => preferences.PinnedSensorIds.Contains(item.SensorId, StringComparer.Ordinal))
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Narrows an already-built trend set for the dashboard without changing
    /// history collection or the user's saved monitoring preferences.
    /// </summary>
    public static IReadOnlyList<SensorTrendV1> FilterTrends(
        IReadOnlyList<SensorTrendV1> trends,
        MonitoringTrendScope scope,
        IReadOnlyCollection<string> pinnedSensorIds)
    {
        ArgumentNullException.ThrowIfNull(trends);
        ArgumentNullException.ThrowIfNull(pinnedSensorIds);

        HashSet<string> pinned = new(pinnedSensorIds, StringComparer.Ordinal);
        return trends
            .Where(trend => MatchesScope(trend, scope, pinned))
            .ToArray();
    }

    private static SensorTrendV1 BuildTrend(
        IReadOnlyList<SensorSample> source,
        IReadOnlyDictionary<string, string> aliases,
        int maximumPoints)
    {
        SensorSample latest = source[^1];
        SensorTrendPointV1[] points = Downsample(source, maximumPoints)
            .Select(sample => new SensorTrendPointV1(sample.Timestamp, sample.Value!.Value))
            .ToArray();
        double minimum = points.Min(point => point.Value);
        double maximum = points.Max(point => point.Value);
        return new SensorTrendV1(
            latest.SensorId,
            aliases.TryGetValue(latest.SensorId, out string? alias) ? alias : latest.Name,
            latest.Unit,
            points,
            minimum,
            maximum,
            latest.Value,
            BuildSparkline(points.Select(point => point.Value).ToArray(), minimum, maximum));
    }

    private static IReadOnlyList<SensorSample> Downsample(IReadOnlyList<SensorSample> source, int maximumPoints)
    {
        if (source.Count <= maximumPoints)
        {
            return source;
        }
        double stride = (source.Count - 1d) / (maximumPoints - 1d);
        return Enumerable.Range(0, maximumPoints)
            .Select(index => source[(int)Math.Round(index * stride, MidpointRounding.AwayFromZero)])
            .ToArray();
    }

    public static string BuildSparkline(IReadOnlyList<double> values, double minimum, double maximum)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            return new string(Blocks[Blocks.Length / 2], values.Count);
        }
        return new string(values.Select(value =>
        {
            double ratio = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
            int index = Math.Min(Blocks.Length - 1, (int)Math.Round(ratio * (Blocks.Length - 1), MidpointRounding.AwayFromZero));
            return Blocks[index];
        }).ToArray());
    }

    private static bool MatchesScope(
        SensorTrendV1 trend,
        MonitoringTrendScope scope,
        HashSet<string> pinnedSensorIds) => scope switch
    {
        MonitoringTrendScope.All => true,
        MonitoringTrendScope.Pinned => pinnedSensorIds.Contains(trend.SensorId),
        MonitoringTrendScope.Temperature => IsTemperatureUnit(trend.Unit),
        MonitoringTrendScope.Fan => string.Equals(trend.Unit.Trim(), "RPM", StringComparison.OrdinalIgnoreCase),
        MonitoringTrendScope.Power => string.Equals(trend.Unit.Trim(), "W", StringComparison.OrdinalIgnoreCase)
            || trend.Unit.Contains("watt", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsTemperatureUnit(string unit)
    {
        string normalized = unit
            .Replace("\u00C2\u00B0C", "\u00B0C", StringComparison.Ordinal)
            .Replace("Celsius", "\u00B0C", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return normalized is "C" or "\u00B0C" || normalized.Contains("\u00B0C", StringComparison.Ordinal);
    }
}

public static class DeviceQualificationPlanner
{
    public static IReadOnlyList<DeviceQualificationPlanV1> Build(HardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<DeviceQualificationPlanV1> plans = [];

        plans.AddRange(snapshot.Devices
            .Where(device => device.Kind == DeviceKind.Cpu)
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(device => BuildCpuPlan(device, snapshot)));
        plans.AddRange(snapshot.Devices
            .Where(device => device.Kind == DeviceKind.Gpu)
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(device => BuildGpuPlan(device, snapshot)));
        plans.AddRange(snapshot.Devices
            .Where(device => device.Kind == DeviceKind.Lighting
                || device.Kind == DeviceKind.Controller && IsLikelyLightingController(device, snapshot.Capabilities))
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(device => BuildLightingPlan(device, snapshot)));

        return plans;
    }

    /// <summary>
    /// Selects only a CPU tuning endpoint. Compatibility cards are deliberately
    /// excluded: a recognised Zen or Core family must never make the tuning
    /// plan look more qualified than the actual write endpoint.
    /// </summary>
    private static DeviceQualificationPlanV1 BuildCpuPlan(HardwareDevice device, HardwareSnapshot snapshot)
    {
        CapabilityDescriptor? endpoint = snapshot.Capabilities
            .Where(capability => capability.DeviceId == device.Id && capability.Domain == ControlDomain.Cpu)
            .Where(IsCpuTuningEndpoint)
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        CapabilityAccessState state = endpoint?.State ?? CapabilityAccessState.Unsupported;
        bool exerciseReady = IsWritePathReadyForQualification(endpoint);
        string manufacturer = device.Manufacturer ?? "Unknown manufacturer";
        string transportEvidence = endpoint?.Reason ?? BuildMissingCpuTransportReason(device);

        return new DeviceQualificationPlanV1(
            DeviceQualificationPlanV1.CurrentSchemaVersion,
            $"qualification.{DeviceQualificationKind.CpuTuning}.{device.Id}",
            DeviceQualificationKind.CpuTuning,
            device.Id,
            device.Name,
            state,
            [
                IdentityStep(device, snapshot),
                new DeviceQualificationStepV1(
                    "transport",
                    "Audited vendor transport",
                    exerciseReady ? QualificationStepState.Ready : QualificationStepState.Blocked,
                    transportEvidence,
                    manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                        ? "Add a reviewed SMU/PawnIO endpoint that publishes exact limits, read-back, default reset, and crash recovery for this CPU."
                        : "Add a reviewed vendor or MSR endpoint that publishes exact limits, read-back, default reset, and crash recovery for this CPU."),
                BoundsStep(endpoint, "Do not infer CPU limits from another BIOS, AGESA version, or processor."),
                ApplyReadBackStep(endpoint, exerciseReady, "Keep CPU tuning unavailable until a reviewed transaction succeeds on this exact CPU and BIOS."),
                ResetStep(endpoint, exerciseReady, "A failed or absent CPU reset path blocks all tuning qualification."),
                new DeviceQualificationStepV1(
                    "screening",
                    "WHEA, active-use, and cold-boot screening",
                    QualificationStepState.NotRun,
                    "No exact CPU tuning candidate has completed fault screening.",
                    "Run one bounded control at a time; reject WHEA events, regressions, crashes, or failed cold boots.")
            ],
            state is CapabilityAccessState.Verified or CapabilityAccessState.Experimental
                ? "An exact CPU endpoint is present, but every listed gate still controls whether a write can be exercised."
                : "CPU tuning remains unavailable because no exact reset-safe endpoint is exposed.");
    }

    /// <summary>
    /// Separates public GPU telemetry/bounds from the tuning endpoint. NVML
    /// data is useful feasibility evidence, but it is never promoted into
    /// tuning support merely because a driver reported a range.
    /// </summary>
    private static DeviceQualificationPlanV1 BuildGpuPlan(HardwareDevice device, HardwareSnapshot snapshot)
    {
        CapabilityDescriptor? endpoint = snapshot.Capabilities
            .Where(capability => capability.DeviceId == device.Id && capability.Domain == ControlDomain.Gpu)
            .Where(IsGpuTuningEndpoint)
            .OrderBy(CapabilityRank)
            .ThenBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        CapabilityAccessState state = endpoint?.State ?? CapabilityAccessState.Unsupported;
        bool exerciseReady = IsWritePathReadyForQualification(endpoint);
        bool publicTelemetry = HasMatchingPublicGpuTelemetry(device, snapshot);
        string runtimeEvidence = BuildGpuRuntimeEvidence(device, endpoint, publicTelemetry);
        QualificationStepState runtimeState = publicTelemetry || endpoint?.State == CapabilityAccessState.ReadOnly
            ? QualificationStepState.Passed
            : exerciseReady
                ? QualificationStepState.Ready
                : QualificationStepState.Blocked;

        return new DeviceQualificationPlanV1(
            DeviceQualificationPlanV1.CurrentSchemaVersion,
            $"qualification.{DeviceQualificationKind.GpuTuning}.{device.Id}",
            DeviceQualificationKind.GpuTuning,
            device.Id,
            device.Name,
            state,
            [
                IdentityStep(device),
                new DeviceQualificationStepV1(
                    "driver-runtime",
                    "Exact driver and vendor runtime",
                    runtimeState,
                    runtimeEvidence,
                    "Keep telemetry and bounds separate from a control endpoint; driver identity alone never permits a write."),
                BoundsStep(endpoint, "Public telemetry ranges are informative only. The future tuning adapter must publish its own driver-gated bounds."),
                ApplyReadBackStep(endpoint, exerciseReady, "Require a reviewed single-domain transaction, exact driver gate, and read-back before enabling GPU tuning."),
                ResetStep(endpoint, exerciseReady, "A failed or absent GPU default reset blocks tuning qualification."),
                new DeviceQualificationStepV1(
                    "screening",
                    "Thermal, display-reset, and active-use screening",
                    QualificationStepState.NotRun,
                    "No exact GPU tuning candidate has completed physical screening.",
                    "Reject thermal-limit violations, display-driver resets, performance regressions, crashes, and failed boot recovery.")
            ],
            state is CapabilityAccessState.Verified or CapabilityAccessState.Experimental
                ? "An exact GPU endpoint is present, but public telemetry remains separate from write qualification."
                : "GPU tuning remains unavailable; any detected telemetry or bounds are read-only evidence only.");
    }

    private static DeviceQualificationPlanV1 BuildLightingPlan(HardwareDevice device, HardwareSnapshot snapshot)
    {
        CapabilityDescriptor? endpoint = snapshot.Capabilities
            .Where(capability => capability.DeviceId == device.Id && capability.Domain == ControlDomain.Lighting)
            .Where(IsDirectLightingEndpoint)
            .OrderBy(DirectLightingCapabilityRank)
            .ThenBy(CapabilityRank)
            .ThenBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        CapabilityAccessState state = endpoint?.State ?? CapabilityAccessState.Unsupported;
        bool exerciseReady = IsWritePathReadyForQualification(endpoint);
        string identity = DeviceIdentityEvidence(device);
        string endpointEvidence = endpoint?.Reason ?? "No direct lighting endpoint is published for this controller.";

        return new DeviceQualificationPlanV1(
            DeviceQualificationPlanV1.CurrentSchemaVersion,
            $"qualification.{DeviceQualificationKind.Lighting}.{device.Id}",
            DeviceQualificationKind.Lighting,
            device.Id,
            device.Name,
            state,
            [
                new DeviceQualificationStepV1(
                    "identity",
                    "Exact controller identity",
                    QualificationStepState.Passed,
                    identity,
                    "Record PnP or USB/HID identity, firmware, owner, and the adapter-pack version before any direct test."),
                new DeviceQualificationStepV1(
                    "containment",
                    "Adapter Host containment and timeout",
                    exerciseReady ? QualificationStepState.Ready : QualificationStepState.Blocked,
                    endpointEvidence,
                    "Direct USB/HID packs must survive crash, timeout, unplug, and malformed-response tests before a static scene is attempted."),
                new DeviceQualificationStepV1(
                    "static-scene-readback",
                    "Static-scene apply and read-back",
                    exerciseReady ? QualificationStepState.Ready : QualificationStepState.Blocked,
                    exerciseReady ? "A reset-safe direct endpoint reports read-back evidence." : "No direct static-scene apply/read-back endpoint is available.",
                    "Use Windows Dynamic Lighting or a user-enabled local OpenRGB bridge for normal output; do not infer a raw protocol from the manufacturer."),
                ResetStep(endpoint, exerciseReady, "A verified default/off reset is required after every direct lighting test."),
                new DeviceQualificationStepV1(
                    "ownership-faults",
                    "Ownership, unplug, and recovery tests",
                    QualificationStepState.NotRun,
                    "No exact controller fault-containment record is attached.",
                    "Test conflicting writers, device removal, host crash, and restart recovery before direct-output qualification.")
            ],
            state is CapabilityAccessState.Verified or CapabilityAccessState.Experimental
                ? "A direct lighting endpoint is present, but containment and recovery evidence still determine whether it can be exercised."
                : "Direct lighting remains read-only or unsupported; standard Windows and local OpenRGB routes are evaluated independently.");
    }

    private static DeviceQualificationStepV1 IdentityStep(HardwareDevice device, HardwareSnapshot? snapshot = null) => new(
        "identity",
        "Exact identity and driver/firmware gate",
        QualificationStepState.Passed,
        DeviceIdentityEvidence(device, snapshot),
        "Record exact device ID, driver or firmware version, and adapter version for every physical qualification run.");

    private static DeviceQualificationStepV1 BoundsStep(CapabilityDescriptor? endpoint, string guidance)
    {
        bool hasBounds = endpoint?.Range is not null;
        return new DeviceQualificationStepV1(
            "bounds",
            "Discover valid bounds",
            hasBounds ? QualificationStepState.Passed : QualificationStepState.Blocked,
            hasBounds
                ? $"Exact endpoint '{endpoint!.Name}' publishes {endpoint.Range!.Minimum:0.##} to {endpoint.Range.Maximum:0.##} {endpoint.Unit ?? "units"}."
                : endpoint?.Reason ?? "No exact bounded control endpoint is published.",
            guidance);
    }

    private static DeviceQualificationStepV1 ApplyReadBackStep(CapabilityDescriptor? endpoint, bool exerciseReady, string guidance) => new(
        "apply-readback",
        "Prepare, apply, and read back",
        exerciseReady ? QualificationStepState.Ready : QualificationStepState.Blocked,
        exerciseReady
            ? "The exact endpoint publishes bounded values and read-back evidence; run it only through a reviewed transaction."
            : endpoint?.Reason ?? "No exact apply/read-back endpoint is available.",
        guidance);

    private static DeviceQualificationStepV1 ResetStep(CapabilityDescriptor? endpoint, bool exerciseReady, string guidance) => new(
        "reset",
        "Return to default",
        exerciseReady && endpoint!.CanResetToDefault ? QualificationStepState.Ready : QualificationStepState.Blocked,
        exerciseReady && endpoint!.CanResetToDefault
            ? "The exact endpoint declares a default-reset path; physical reset verification remains required."
            : endpoint?.Reason ?? "No exact default-reset endpoint is available.",
        guidance);

    private static bool IsWritePathReadyForQualification(CapabilityDescriptor? capability) => capability is not null
        && capability.State is CapabilityAccessState.Verified or CapabilityAccessState.Experimental
        && capability.Range is not null
        && capability.Evidence >= EvidenceLevel.ReadBackVerified
        && capability.CanResetToDefault;

    private static bool IsCpuTuningEndpoint(CapabilityDescriptor capability) =>
        capability.Id.StartsWith("amd.zen.tuning:", StringComparison.OrdinalIgnoreCase)
        || capability.Id.StartsWith("intel.cpu.tuning:", StringComparison.OrdinalIgnoreCase)
        || capability.Name.Contains("CPU tuning", StringComparison.OrdinalIgnoreCase)
        || capability.Name.Contains("Zen tuning", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuTuningEndpoint(CapabilityDescriptor capability) =>
        capability.Id.StartsWith("nvidia.tuning:", StringComparison.OrdinalIgnoreCase)
        || capability.Id.StartsWith("amd.adlx:", StringComparison.OrdinalIgnoreCase)
        || capability.Id.StartsWith("intel.igcl:", StringComparison.OrdinalIgnoreCase)
        || capability.Name.Contains("tuning", StringComparison.OrdinalIgnoreCase)
            && !capability.AdapterId.Equals("nvidia.nvml", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectLightingEndpoint(CapabilityDescriptor capability) =>
        capability.Id.StartsWith("peripheral.", StringComparison.OrdinalIgnoreCase)
        || capability.Id.StartsWith("gpu.rgb.", StringComparison.OrdinalIgnoreCase)
        || capability.Name.Contains("direct", StringComparison.OrdinalIgnoreCase)
        || capability.AdapterId.Contains("adapter", StringComparison.OrdinalIgnoreCase);

    private static int DirectLightingCapabilityRank(CapabilityDescriptor capability) => capability.Id.StartsWith("peripheral.", StringComparison.OrdinalIgnoreCase)
        ? 0
        : capability.Name.Contains("direct", StringComparison.OrdinalIgnoreCase)
            ? 1
            : capability.Id.StartsWith("gpu.rgb.", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 3;

    private static int CapabilityRank(CapabilityDescriptor capability) => capability.State switch
    {
        CapabilityAccessState.Verified => 0,
        CapabilityAccessState.Experimental => 1,
        CapabilityAccessState.ReadOnly => 2,
        CapabilityAccessState.Blocked => 3,
        CapabilityAccessState.Faulted => 4,
        _ => 5
    };

    private static string BuildMissingCpuTransportReason(HardwareDevice device)
    {
        string identity = DeviceIdentityEvidence(device);
        return device.Manufacturer?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true
            || device.Name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
                ? $"{identity} No audited SMU/PawnIO endpoint with bounds, read-back, default reset, and crash recovery is included."
                : $"{identity} No audited vendor or MSR endpoint with bounds, read-back, default reset, and crash recovery is included.";
    }

    private static bool HasMatchingPublicGpuTelemetry(HardwareDevice device, HardwareSnapshot snapshot)
    {
        if (!IsNvidia(device))
        {
            return false;
        }

        HardwareDevice[] nvmlDevices = snapshot.Devices
            .Where(candidate => candidate.Id.StartsWith("nvidia:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return nvmlDevices.Any(candidate => SameGpuIdentity(device.Name, candidate.Name)
            && snapshot.Capabilities.Any(capability => capability.AdapterId.Equals("nvidia.nvml", StringComparison.OrdinalIgnoreCase)
                && capability.DeviceId.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase)
                && capability.Id.StartsWith("nvml.telemetry:", StringComparison.OrdinalIgnoreCase)));
    }

    private static string BuildGpuRuntimeEvidence(HardwareDevice device, CapabilityDescriptor? endpoint, bool publicTelemetry)
    {
        if (publicTelemetry)
        {
            return "The matching NVIDIA device exposes public NVML telemetry and bounds on the installed driver. NVML remains telemetry-only here and is not a tuning or reset endpoint.";
        }

        if (endpoint is not null)
        {
            return endpoint.State == CapabilityAccessState.ReadOnly
                ? $"{endpoint.Name} runtime was detected, but it exposes no reviewed write/reset endpoint for this exact device. {endpoint.Reason}"
                : endpoint.Reason;
        }

        return IsNvidia(device)
            ? "No matching NVML telemetry identity was observed for this NVIDIA GPU. Do not borrow driver or bounds evidence from another adapter."
            : "No exact vendor runtime and driver gate was observed for a reviewed tuning endpoint.";
    }

    private static bool IsNvidia(HardwareDevice device) => device.Manufacturer?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true
        || device.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || device.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    private static bool SameGpuIdentity(string first, string second)
    {
        string normalisedFirst = new string(first.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        string normalisedSecond = new string(second.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalisedFirst == normalisedSecond
            || normalisedFirst.Contains(normalisedSecond, StringComparison.Ordinal)
            || normalisedSecond.Contains(normalisedFirst, StringComparison.Ordinal);
    }

    private static string DeviceIdentityEvidence(HardwareDevice device, HardwareSnapshot? snapshot = null)
    {
        List<string> parts = [$"Windows reports '{device.Name}' ({device.Id})."];
        if (!string.IsNullOrWhiteSpace(device.Manufacturer))
        {
            parts.Add($"Manufacturer: {device.Manufacturer}.");
        }
        if (!string.IsNullOrWhiteSpace(device.PnpId))
        {
            parts.Add("An exact hardware identity is present.");
        }
        if (device.Properties.TryGetValue("driverVersion", out string? driver) && !string.IsNullOrWhiteSpace(driver))
        {
            parts.Add($"Driver {driver}.");
        }
        if (device.Properties.TryGetValue("biosVersion", out string? bios) && !string.IsNullOrWhiteSpace(bios))
        {
            parts.Add($"BIOS {bios}.");
        }
        if (device.Properties.TryGetValue("firmwareVersion", out string? firmware) && !string.IsNullOrWhiteSpace(firmware))
        {
            parts.Add($"Firmware {firmware}.");
        }
        if (device.Kind == DeviceKind.Cpu && snapshot is not null)
        {
            HardwareDevice? biosDevice = snapshot.Devices.FirstOrDefault(candidate => candidate.Kind == DeviceKind.Bios);
            if (biosDevice is not null && !string.IsNullOrWhiteSpace(biosDevice.Model))
            {
                parts.Add($"Observed SMBIOS version {biosDevice.Model}.");
            }
        }
        return string.Join(' ', parts);
    }

    private static bool IsLikelyLightingController(
        HardwareDevice device,
        IReadOnlyList<CapabilityDescriptor> capabilities) =>
        capabilities.Any(capability => capability.DeviceId == device.Id && capability.Domain == ControlDomain.Lighting)
        || device.Name.Contains("Aura", StringComparison.OrdinalIgnoreCase)
        || device.Name.Contains("Lian Li", StringComparison.OrdinalIgnoreCase)
        || device.Name.Contains("RGB", StringComparison.OrdinalIgnoreCase)
        || device.Name.Contains("LED", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<CoolingQualificationReportV1> BuildCoolingReports(
        IReadOnlyList<FanCommissioningSessionV1> sessions,
        IReadOnlyList<FanCalibrationV2> calibrations)
    {
        return sessions.OrderBy(session => session.HeaderName, StringComparer.OrdinalIgnoreCase)
            .Select(session =>
            {
                FanCalibrationV2? calibration = calibrations
                    .Where(item => item.CapabilityId == session.CapabilityId
                        && string.Equals(item.CommissioningSessionId, session.Id, StringComparison.Ordinal))
                    .OrderByDescending(item => item.VerifiedAt)
                    .FirstOrDefault();
                QualificationStepState headerState = session.PhysicalHeaderObserved
                    ? QualificationStepState.Passed
                    : session.HeaderConfirmed
                        ? QualificationStepState.Ready
                        : QualificationStepState.NotRun;
                string headerEvidence = session.PhysicalHeaderObserved
                    ? "Physical header was observed and confirmed in commissioning."
                    : session.HeaderConfirmed
                        ? "Header is user-declared only; physical fan observation is still required."
                        : session.Error ?? "Header identity is not confirmed.";
                bool calibrated = calibration is not null;
                double? restartDuty = calibration?.RestartDutyPercent;
                bool nonZeroCurveQualified = calibration is not null
                    && FanCalibrationPolicy.SupportsNonZeroCurve(calibration);
                bool restartVerified = calibration is not null
                    && FanCalibrationPolicy.SupportsVerifiedFanStop(calibration);
                string floorEvidence = calibration is null
                    ? "No completed calibration is linked."
                    : nonZeroCurveQualified
                        ? calibration.NonStopFloorObserved
                            ? $"Controller minimum remained running; calibrated nonzero floor is {calibration.MinimumDutyPercent:F0}% (plateau through {calibration.EffectiveFloorDutyPercent:F0}% / {calibration.EffectiveFloorRpm:F0} RPM)."
                            : $"Repeated restart evidence supports a {calibration.MinimumDutyPercent:F0}% positive operating floor."
                        : "The calibration does not prove a stable per-output nonzero floor.";
                return new CoolingQualificationReportV1(
                    CoolingQualificationReportV1.CurrentSchemaVersion,
                    session.Id,
                    session.CapabilityId,
                    session.HeaderName,
                    session.State,
                    [
                        new DeviceQualificationStepV1("header", "Header identity", headerState, headerEvidence, "Pulse only with explicit acknowledgement and no conflicting writer."),
                        new DeviceQualificationStepV1("calibration", "Bounded calibration", calibrated ? QualificationStepState.Passed : QualificationStepState.NotRun, calibrated ? $"Maximum {calibration!.MaximumRpm:F0} RPM recorded." : "No completed calibration is linked.", "Run one control at a time and restore the prior policy after every attempt."),
                        new DeviceQualificationStepV1("operating-floor", "Per-output nonzero floor", nonZeroCurveQualified ? QualificationStepState.Passed : calibrated ? QualificationStepState.Blocked : QualificationStepState.NotRun, floorEvidence, "A measured nonzero floor is sufficient for a nonzero curve; it never enables fan stop."),
                        new DeviceQualificationStepV1("restart", "Repeated restart verification", restartVerified ? QualificationStepState.Ready : QualificationStepState.NotRun, restartVerified ? $"Candidate restart duty {restartDuty!.Value:F0}% completed the stored repeated-restart gate." : calibration?.NonStopFloorObserved == true ? "The controller stayed running at minimum duty. A nonzero curve may be used, but zero-RPM remains forbidden." : "No verified restart duty is available.", "Keep the conservative floor and zero-RPM prohibition until repeated restarts pass."),
                        new DeviceQualificationStepV1("emergency", "Emergency response within one second", QualificationStepState.NotRun, "No timed emergency result is attached.", "Inject a stale/critical source through the fake adapter first, then run a controlled physical test."),
                        new DeviceQualificationStepV1("suspend-resume", "Suspend/resume and default recovery", QualificationStepState.NotRun, "No resume/default-reset record is attached.", "Test only after all active competing writers have been released."),
                        new DeviceQualificationStepV1("reset", "Firmware/default reset", session.State == FanCommissioningState.Completed ? QualificationStepState.Ready : QualificationStepState.NotRun, "The commissioning workflow requires firmware/default recovery on cancellation and failure.", "Record a successful reset after each physical write test.")
                    ],
                    session.State == FanCommissioningState.Completed
                        ? restartVerified
                            ? "Commissioned evidence exists, but emergency, suspend/resume, and reset evidence still determine qualification."
                            : nonZeroCurveQualified
                                ? "Commissioned nonzero-only curve evidence exists. Zero-RPM remains disabled; emergency, suspend/resume, and reset evidence still determine qualification."
                                : "Commissioned calibration is incomplete; keep firmware/default control until a stable per-output floor is captured."
                        : session.HeaderConfirmed
                            ? "A user-declared header and bounded calibration evidence exist, but physical observation and restart verification still block commissioning."
                            : "Complete header confirmation and bounded calibration before attempting subsequent cooling evidence." );
            })
            .ToArray();
    }
}

public static class SafetyRecoveryPlanner
{
    public static SafetyRecoveryStatusV1 Build(
        SafetyRecoveryStateV1 state,
        bool rollbackBlocked,
        HardwareOperationStatus? latestOperation,
        IReadOnlyList<FanCommissioningSessionV1> sessions)
    {
        FanCommissioningSessionV1[] recovery = sessions
            .Where(session => session.State == FanCommissioningState.RecoveryRequired)
            .ToArray();
        string guidance = rollbackBlocked
            ? "A rollback remains incomplete. Keep safe mode enabled, recover the exact control to firmware/default, then reboot before new writes."
            : recovery.Length > 0
                ? "One or more commissioning sessions need firmware/default recovery before further cooling writes."
                : state.SafeModeEnabled
                    ? "Safe mode is active: automation and alert-driven profile requests are suspended. Monitoring remains available."
                    : "No pending recovery state is reported. Monitoring and explicitly requested verified operations remain available.";
        return new SafetyRecoveryStatusV1(
            SafetyRecoveryStatusV1.CurrentSchemaVersion,
            state,
            rollbackBlocked,
            latestOperation,
            recovery,
            guidance);
    }
}

public static class HardwareEvidenceBuilder
{
    public static HardwareEvidenceReportV1 Build(
        HardwareSnapshot snapshot,
        IReadOnlyList<HealthAlertEventV1> alerts,
        IReadOnlyList<AdapterTraceEvent> trace,
        SafetyRecoveryStatusV1 recovery,
        IReadOnlyList<DeviceQualificationPlanV1> plans,
        IReadOnlyList<CoolingQualificationReportV1> cooling,
        DateTimeOffset now)
    {
        return new HardwareEvidenceReportV1(
            HardwareEvidenceReportV1.CurrentSchemaVersion,
            $"evidence.{now:yyyyMMddHHmmss}.{Guid.NewGuid():N}",
            now,
            snapshot.Devices.Take(256).Select(device => new EvidenceDeviceV1(
                device.Id,
                device.Kind.ToString(),
                device.Name,
                device.Manufacturer,
                device.Model,
                device.PnpId)).ToArray(),
            snapshot.AdapterHealth.Take(64).ToArray(),
            snapshot.Capabilities.Take(512).ToArray(),
            alerts.OrderByDescending(alert => alert.UpdatedAt).Take(256).ToArray(),
            trace.OrderByDescending(item => item.Timestamp).Take(512).ToArray(),
            recovery,
            plans.Take(128).ToArray(),
            cooling.Take(128).ToArray());
    }
}
