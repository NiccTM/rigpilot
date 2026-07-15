using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ReliabilityLabTests
{
    [Fact]
    public void HealthRuleRequiresConfiguredConsecutiveObservationsAndClears()
    {
        HealthRuleV1 rule = new(
            HealthRuleV1.CurrentSchemaVersion,
            "health.cpu-hot",
            "CPU temperature",
            HealthRuleConditionKind.SensorAbove,
            "cpu.temp",
            85,
            ConsecutiveObservations: 2,
            Cooldown: TimeSpan.FromMinutes(1),
            HealthRuleActionKind.NotifyOnly,
            EmergencyProfileId: null,
            Enabled: true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HealthRuleEngine engine = new();

        IReadOnlyList<HealthAlertEventV1> first = engine.Evaluate([rule], [Sample(86, now)], [], [], now);
        IReadOnlyList<HealthAlertEventV1> raised = engine.Evaluate([rule], [Sample(87, now.AddSeconds(1))], [], first, now.AddSeconds(1));
        IReadOnlyList<HealthAlertEventV1> cleared = engine.Evaluate([rule], [Sample(70, now.AddSeconds(2))], [], first.Concat(raised).ToArray(), now.AddSeconds(2));

        Assert.Empty(first);
        HealthAlertEventV1 alert = Assert.Single(raised);
        Assert.Equal(HealthAlertState.Active, alert.State);
        Assert.Equal(87, alert.ObservedValue);
        Assert.Equal(HealthAlertState.Cleared, Assert.Single(cleared).State);
    }

    [Fact]
    public void SystemHealthRulesProduceTypedWheaAndDisplayAlerts()
    {
        HealthRuleEngine engine = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HealthRuleV1 whea = Rule("whea", HealthRuleConditionKind.WheaEvent);
        HealthRuleV1 display = Rule("display", HealthRuleConditionKind.DisplayDriverReset);

        IReadOnlyList<HealthAlertEventV1> alerts = engine.Evaluate(
            [whea, display],
            [],
            [
                new HealthSystemSignal(HealthSystemSignalKind.Whea, now, "A WHEA event (18) was observed."),
                new HealthSystemSignal(HealthSystemSignalKind.DisplayDriverReset, now, "A display reset was observed.")
            ],
            [],
            now);

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, item => item.Condition == HealthRuleConditionKind.WheaEvent);
        Assert.Contains(alerts, item => item.Condition == HealthRuleConditionKind.DisplayDriverReset);
    }

    [Fact]
    public void MonitoringTrendAppliesAliasesPinsAndStableSparkline()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MonitoringPreferencesV1 preferences = new(
            MonitoringPreferencesV1.CurrentSchemaVersion,
            MonitoringPreferencesV1.DefaultId,
            [new SensorAliasV1("cpu.temp", "CPU package")],
            ["cpu.temp"],
            now);

        IReadOnlyList<SensorTrendV1> trends = MonitoringWorkspace.BuildTrends(
            [Sample(50, now.AddSeconds(-3)), Sample(60, now.AddSeconds(-2)), Sample(70, now.AddSeconds(-1))],
            preferences,
            maximumPoints: 8);

        SensorTrendV1 trend = Assert.Single(trends);
        Assert.Equal("CPU package", trend.DisplayName);
        Assert.Equal(50, trend.Minimum);
        Assert.Equal(70, trend.Maximum);
        Assert.Equal(3, trend.Sparkline.Length);
    }

    [Fact]
    public void MonitoringTrendFiltersUseUnitsAndExplicitPinsWithoutChangingSourceOrder()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SensorTrendV1[] trends =
        [
            Trend("cpu.temp", "CPU package", "Â°C", [70, 74], now),
            Trend("gpu.power", "GPU power", "W", [220, 240], now),
            Trend("case.rpm", "Case fan", "RPM", [900, 920], now),
            Trend("board.voltage", "Board voltage", "V", [1.1, 1.2], now)
        ];

        IReadOnlyList<SensorTrendV1> temperatures = MonitoringWorkspace.FilterTrends(
            trends,
            MonitoringTrendScope.Temperature,
            ["case.rpm"]);
        IReadOnlyList<SensorTrendV1> pinned = MonitoringWorkspace.FilterTrends(
            trends,
            MonitoringTrendScope.Pinned,
            ["case.rpm"]);
        IReadOnlyList<SensorTrendV1> power = MonitoringWorkspace.FilterTrends(
            trends,
            MonitoringTrendScope.Power,
            []);

        Assert.Equal(["cpu.temp"], temperatures.Select(item => item.SensorId));
        Assert.Equal(["case.rpm"], pinned.Select(item => item.SensorId));
        Assert.Equal(["gpu.power"], power.Select(item => item.SensorId));
    }

    [Fact]
    public void CoolingReportDoesNotPretendRestartOrEmergencyEvidencePassed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FanCommissioningSessionV1 session = new(
            FanCommissioningSessionV1.CurrentSchemaVersion,
            "commission.cha1",
            "fan.cha1",
            "rpm.cha1",
            "CHA_FAN1",
            FanCommissioningState.Completed,
            IsCpuOrPump: false,
            AllowFanStop: false,
            HeaderConfirmed: true,
            CalibrationId: "fan.cha1",
            now,
            now,
            null,
            null,
            PhysicalHeaderObserved: true);
        FanCalibrationV2 calibration = new(
            FanCalibrationV2.CurrentSchemaVersion,
            "fan.cha1",
            "rpm.cha1",
            [new FanCalibrationPoint(100, 1500)],
            1500,
            StallDutyPercent: null,
            RestartDutyPercent: 45,
            MinimumDutyPercent: 50,
            KickStartDutyPercent: 60,
            [],
            now,
            CommissioningSessionId: session.Id,
            SupportsVerifiedFanStop: true);

        CoolingQualificationReportV1 report = Assert.Single(DeviceQualificationPlanner.BuildCoolingReports([session], [calibration]));

        Assert.Equal(QualificationStepState.Passed, report.Steps.Single(step => step.Id == "header").State);
        Assert.Equal(QualificationStepState.Passed, report.Steps.Single(step => step.Id == "operating-floor").State);
        Assert.Equal(QualificationStepState.Ready, report.Steps.Single(step => step.Id == "restart").State);
        Assert.Equal(QualificationStepState.NotRun, report.Steps.Single(step => step.Id == "emergency").State);
        Assert.Equal(QualificationStepState.NotRun, report.Steps.Single(step => step.Id == "suspend-resume").State);
    }

    [Fact]
    public void CoolingReportMarksNonStoppingControllerAsNonzeroOnlyInsteadOfFailed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FanCommissioningSessionV1 session = new(
            FanCommissioningSessionV1.CurrentSchemaVersion,
            "commission.case1",
            "fan.case1",
            "rpm.case1",
            "CHA_FAN1",
            FanCommissioningState.Completed,
            IsCpuOrPump: false,
            AllowFanStop: true,
            HeaderConfirmed: true,
            CalibrationId: "fan.case1",
            now,
            now,
            null,
            null,
            PhysicalHeaderObserved: true);
        FanCalibrationV2 calibration = new(
            FanCalibrationV2.CurrentSchemaVersion,
            "fan.case1",
            "rpm.case1",
            [new FanCalibrationPoint(0, 540), new FanCalibrationPoint(20, 540), new FanCalibrationPoint(25, 630), new FanCalibrationPoint(100, 1_980)],
            1_980,
            StallDutyPercent: null,
            RestartDutyPercent: null,
            MinimumDutyPercent: 10,
            KickStartDutyPercent: 10,
            [],
            now,
            CommissioningSessionId: session.Id,
            EffectiveFloorDutyPercent: 20,
            EffectiveFloorRpm: 540,
            FirstResponsiveDutyPercent: 25,
            NonStopFloorObserved: true);

        CoolingQualificationReportV1 report = Assert.Single(DeviceQualificationPlanner.BuildCoolingReports([session], [calibration]));

        Assert.Equal(QualificationStepState.Passed, report.Steps.Single(step => step.Id == "operating-floor").State);
        Assert.Equal(QualificationStepState.NotRun, report.Steps.Single(step => step.Id == "restart").State);
        Assert.Contains("nonzero-only", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoolingReportDoesNotAttachCalibrationToAnotherCommissioningSession()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FanCommissioningSessionV1 declared = new(
            FanCommissioningSessionV1.CurrentSchemaVersion,
            "commission.declared",
            "fan.cha1",
            "rpm.cha1",
            "CASE_FAN_1",
            FanCommissioningState.ReadyForCalibration,
            IsCpuOrPump: false,
            AllowFanStop: true,
            HeaderConfirmed: true,
            CalibrationId: null,
            now,
            now,
            "User-declared generic mapping.",
            null,
            PhysicalHeaderObserved: false);
        FanCalibrationV2 otherSessionCalibration = new(
            FanCalibrationV2.CurrentSchemaVersion,
            "fan.cha1",
            "rpm.cha1",
            [new FanCalibrationPoint(100, 1500)],
            1500,
            StallDutyPercent: null,
            RestartDutyPercent: null,
            MinimumDutyPercent: 50,
            KickStartDutyPercent: 60,
            [],
            now,
            CommissioningSessionId: "commission.other");

        CoolingQualificationReportV1 report = Assert.Single(DeviceQualificationPlanner.BuildCoolingReports(
            [declared],
            [otherSessionCalibration]));

        Assert.Equal(QualificationStepState.Ready, report.Steps.Single(step => step.Id == "header").State);
        Assert.Equal(QualificationStepState.NotRun, report.Steps.Single(step => step.Id == "calibration").State);
        Assert.Contains("user-declared", report.Steps.Single(step => step.Id == "header").Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DevicePlansKeepUnsupportedTuningBlocked()
    {
        HardwareDevice cpu = new("cpu.1", "AMD Ryzen 7 5800X", DeviceKind.Cpu, "AMD", "Ryzen 7 5800X", null, new Dictionary<string, string>());
        HardwareDevice gpu = new("gpu.1", "NVIDIA RTX 3090", DeviceKind.Gpu, "NVIDIA", "RTX 3090", null, new Dictionary<string, string>());
        HardwareSnapshot snapshot = new(DateTimeOffset.UtcNow, [cpu, gpu], [], [], [], [], []);

        IReadOnlyList<DeviceQualificationPlanV1> plans = DeviceQualificationPlanner.Build(snapshot);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, plan => Assert.Equal(CapabilityAccessState.Unsupported, plan.CapabilityState));
        Assert.All(plans, plan => Assert.Equal(QualificationStepState.Blocked, plan.Steps.Single(step => step.Id == "apply-readback").State));
    }

    [Fact]
    public void DevicePlansPreferTheExactBlockedTuningEndpointOverCompatibilityCards()
    {
        HardwareDevice cpu = new("cpu.1", "AMD Ryzen 7 5800X", DeviceKind.Cpu, "AMD", "Ryzen 7 5800X", "CPU\\AMD_5800X", new Dictionary<string, string>());
        HardwareDevice gpu = new("gpu.1", "NVIDIA GeForce RTX 3090", DeviceKind.Gpu, "NVIDIA", "GeForce RTX 3090", "PCI\\VEN_10DE", new Dictionary<string, string>());
        CapabilityDescriptor cpuCompatibility = Capability(
            "compatibility.amd-zen-3:cpu.1", "cpu.1", "Compatibility profile", CapabilityAccessState.ReadOnly, ControlDomain.Cpu,
            "Recognised family only.");
        CapabilityDescriptor cpuTuning = Capability(
            "amd.zen.tuning:cpu.1", "cpu.1", "AMD Zen tuning", CapabilityAccessState.Blocked, ControlDomain.Cpu,
            "No audited SMU endpoint is installed.");
        CapabilityDescriptor gpuCompatibility = Capability(
            "compatibility.nvidia-rtx-30:gpu.1", "gpu.1", "Compatibility profile", CapabilityAccessState.ReadOnly, ControlDomain.Gpu,
            "Recognised family only.");
        CapabilityDescriptor gpuTuning = Capability(
            "nvidia.tuning:gpu.1", "gpu.1", "NVIDIA clock, power, and voltage-frequency tuning", CapabilityAccessState.Blocked, ControlDomain.Gpu,
            "No exact board/driver tuning adapter is installed.");
        HardwareSnapshot snapshot = new(DateTimeOffset.UtcNow, [cpu, gpu], [cpuCompatibility, cpuTuning, gpuCompatibility, gpuTuning], [], [], [], []);

        IReadOnlyList<DeviceQualificationPlanV1> plans = DeviceQualificationPlanner.Build(snapshot);

        DeviceQualificationPlanV1 cpuPlan = Assert.Single(plans, plan => plan.Kind == DeviceQualificationKind.CpuTuning);
        DeviceQualificationPlanV1 gpuPlan = Assert.Single(plans, plan => plan.Kind == DeviceQualificationKind.GpuTuning);
        Assert.Equal(CapabilityAccessState.Blocked, cpuPlan.CapabilityState);
        Assert.Equal(CapabilityAccessState.Blocked, gpuPlan.CapabilityState);
        Assert.Contains("SMU", cpuPlan.Steps.Single(step => step.Id == "bounds").Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("board/driver", gpuPlan.Steps.Single(step => step.Id == "bounds").Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchingNvmlTelemetryDoesNotUnlockNvidiaTuning()
    {
        HardwareDevice gpu = new("gpu.1", "NVIDIA GeForce RTX 3090", DeviceKind.Gpu, "NVIDIA", "GeForce RTX 3090", "PCI\\VEN_10DE", new Dictionary<string, string>());
        HardwareDevice nvmlGpu = new("nvidia:gpuuuid", "NVIDIA GeForce RTX 3090", DeviceKind.Gpu, "NVIDIA", "NVIDIA GeForce RTX 3090", "GPU-UUID", new Dictionary<string, string>());
        CapabilityDescriptor telemetry = Capability(
            "nvml.telemetry:nvidia:gpuuuid", "nvidia:gpuuuid", "NVIDIA telemetry and cooling endpoint", CapabilityAccessState.ReadOnly, ControlDomain.Gpu,
            "NVML telemetry is available.", adapterId: "nvidia.nvml");
        CapabilityDescriptor publicPowerRange = new(
            "nvml.power-limit:nvidia:gpuuuid", "nvidia.nvml", "nvidia:gpuuuid", "GPU power limit", CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.SystemService, ControlValueKind.Numeric, new NumericRange(100, 385, 1), "W", RiskLevel.Experimental,
            EvidenceLevel.Detected, null, "Public NVML range.", false, ControlDomain.Gpu);
        CapabilityDescriptor tuning = Capability(
            "nvidia.tuning:gpu.1", "gpu.1", "NVIDIA clock, power, and voltage-frequency tuning", CapabilityAccessState.Blocked, ControlDomain.Gpu,
            "No exact board/driver tuning adapter is installed.");
        HardwareSnapshot snapshot = new(DateTimeOffset.UtcNow, [gpu, nvmlGpu], [telemetry, publicPowerRange, tuning], [], [], [], []);

        DeviceQualificationPlanV1 plan = Assert.Single(DeviceQualificationPlanner.Build(snapshot), item => item.DeviceId == gpu.Id);

        Assert.Equal(CapabilityAccessState.Blocked, plan.CapabilityState);
        Assert.Equal(QualificationStepState.Passed, plan.Steps.Single(step => step.Id == "driver-runtime").State);
        Assert.Equal(QualificationStepState.Blocked, plan.Steps.Single(step => step.Id == "bounds").State);
        Assert.Equal(QualificationStepState.Blocked, plan.Steps.Single(step => step.Id == "apply-readback").State);
    }

    [Fact]
    public void LightingPlanIncludesNamedAuraAndLianLiControllersWithoutGrantingWrites()
    {
        HardwareDevice aura = new("controller.aura", "ASUS Aura Controller", DeviceKind.Controller, "ASUS", null, null, new Dictionary<string, string>());
        HardwareDevice lianLi = new("controller.lianli", "Lian Li UNI HUB", DeviceKind.Controller, "Lian Li", null, null, new Dictionary<string, string>());
        HardwareSnapshot snapshot = new(DateTimeOffset.UtcNow, [aura, lianLi], [], [], [], [], []);

        IReadOnlyList<DeviceQualificationPlanV1> plans = DeviceQualificationPlanner.Build(snapshot);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, plan =>
        {
            Assert.Equal(DeviceQualificationKind.Lighting, plan.Kind);
            Assert.Equal(CapabilityAccessState.Unsupported, plan.CapabilityState);
        });
    }

    [Fact]
    public void EvidenceReportBoundsCollectionsAndRetainsRecoveryState()
    {
        HardwareSnapshot snapshot = new(
            DateTimeOffset.UtcNow,
            Enumerable.Range(0, 300).Select(index => new HardwareDevice($"device.{index}", $"Device {index}", DeviceKind.Unknown, null, null, null, new Dictionary<string, string>())).ToArray(),
            [], [], [], [], []);
        SafetyRecoveryStateV1 state = new(
            SafetyRecoveryStateV1.CurrentSchemaVersion,
            SafetyRecoveryStateV1.DefaultId,
            SafeModeEnabled: true,
            AutomationSuspended: true,
            DateTimeOffset.UtcNow,
            "Test recovery");
        SafetyRecoveryStatusV1 recovery = SafetyRecoveryPlanner.Build(state, rollbackBlocked: false, null, []);

        HardwareEvidenceReportV1 report = HardwareEvidenceBuilder.Build(snapshot, [], [], recovery, [], [], DateTimeOffset.UtcNow);

        Assert.Equal(256, report.Devices.Count);
        Assert.True(report.Recovery.State.SafeModeEnabled);
    }

    [Fact]
    public void HealthRecommendationsAreConservativeDistinctAndNotifyOnly()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SensorTrendV1 cpu = Trend("cpu.temp", "CPU Package", "°C", [70, 76], now);
        SensorTrendV1 gpu = Trend("gpu.temp", "GPU Core", "°C", [55, 61], now);
        SensorTrendV1 pump = Trend("pump.rpm", "AIO Pump", "RPM", [1800, 1820], now);

        IReadOnlyList<HealthRuleRecommendation> recommendations = HealthRuleRecommendations.Build([cpu, gpu, pump]);

        Assert.Contains(recommendations, item => item.Key == "cpu-temperature");
        Assert.Contains(recommendations, item => item.Key == "cpu-temperature-stale");
        Assert.Contains(recommendations, item => item.Key == "gpu-temperature");
        Assert.Contains(recommendations, item => item.Key == "gpu-temperature-stale");
        Assert.Contains(recommendations, item => item.Key == "pump-rpm");
        Assert.Contains(recommendations, item => item.Key == "pump-rpm-stale");
        Assert.Contains(recommendations, item => item.Key == "whea-event");
        Assert.Contains(recommendations, item => item.Key == "display-driver-reset");
        Assert.Equal(recommendations.Count, recommendations.Select(item => item.Rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(recommendations, item =>
        {
            Assert.Equal(HealthRuleActionKind.NotifyOnly, item.Rule.Action);
            Assert.True(HealthRuleEngine.Validate(item.Rule).IsValid);
        });
    }

    private static CapabilityDescriptor Capability(
        string id,
        string deviceId,
        string name,
        CapabilityAccessState state,
        ControlDomain domain,
        string reason,
        string adapterId = "test") => new(
        id,
        adapterId,
        deviceId,
        name,
        state,
        AdapterExecutionContext.AdapterHost,
        ControlValueKind.Numeric,
        null,
        null,
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        reason,
        CanResetToDefault: false,
        Domain: domain);

    private static HealthRuleV1 Rule(string id, HealthRuleConditionKind condition) => new(
        HealthRuleV1.CurrentSchemaVersion,
        id,
        id,
        condition,
        SensorId: null,
        Threshold: null,
        ConsecutiveObservations: 1,
        Cooldown: TimeSpan.Zero,
        HealthRuleActionKind.NotifyOnly,
        EmergencyProfileId: null,
        Enabled: true);

    private static SensorSample Sample(double value, DateTimeOffset timestamp) => new(
        "cpu.temp",
        "test",
        "cpu.1",
        "CPU",
        timestamp,
        value,
        "C",
        SensorQuality.Good,
        TimeSpan.FromSeconds(5));

    private static SensorTrendV1 Trend(string id, string name, string unit, IReadOnlyList<double> values, DateTimeOffset now)
    {
        SensorTrendPointV1[] points = values
            .Select((value, index) => new SensorTrendPointV1(now.AddSeconds(index), value))
            .ToArray();
        return new SensorTrendV1(
            id,
            name,
            unit,
            points,
            points.Min(point => point.Value),
            points.Max(point => point.Value),
            points[^1].Value,
            string.Empty);
    }
}
