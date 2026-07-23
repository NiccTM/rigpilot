using PCHelper.App;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class OnboardingWorkflowTests
{
    [Fact]
    public void CapabilitySummarySeparatesEvidenceBridgeAndConflictState()
    {
        HardwareSnapshot snapshot = Snapshot(
            [
                Capability("verified", CapabilityAccessState.Verified),
                Capability("experimental", CapabilityAccessState.Experimental),
                Capability("readonly", CapabilityAccessState.ReadOnly, AdapterExecutionContext.UserSession),
                Capability("blocked", CapabilityAccessState.Blocked),
                Capability("faulted", CapabilityAccessState.Faulted)
            ],
            [],
            [new ConflictDescriptor("owner", "Other owner", "owner", ["Cooling"], true, "Close it.")]);

        OnboardingCapabilitySummary result = OnboardingWorkflow.SummarizeCapabilities(snapshot);

        Assert.Equal(1, result.VerifiedCount);
        Assert.Equal(1, result.ExperimentalCount);
        Assert.Equal(1, result.ReadOnlyCount);
        Assert.Equal(2, result.BlockedCount);
        Assert.Equal(1, result.BridgedCount);
        Assert.Equal(1, result.ActiveConflictCount);
        Assert.Contains("affected writes remain blocked", result.ToDisplayText(), StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineUsesOnlyFreshFiniteOperatingMetrics()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HardwareSnapshot first = Snapshot([], [
            Sensor("gpu-temp", "GPU temperature", 60, "°C", SensorQuality.Good, now),
            Sensor("gpu-power", "GPU power", 200, "W", SensorQuality.Good, now),
            Sensor("stale", "Stale temperature", 99, "°C", SensorQuality.Stale, now)
        ], []);
        HardwareSnapshot second = Snapshot([], [
            Sensor("gpu-temp", "GPU temperature", 66, "°C", SensorQuality.Good, now.AddSeconds(1)),
            Sensor("gpu-power", "GPU power", 220, "W", SensorQuality.Good, now.AddSeconds(1)),
            Sensor("invalid", "Invalid fan", double.NaN, "RPM", SensorQuality.Good, now.AddSeconds(1))
        ], []);

        OnboardingBaselineSummary result = OnboardingWorkflow.SummarizeBaseline([first, second]);

        Assert.Equal(2, result.SnapshotCount);
        Assert.Equal(2, result.Metrics.Count);
        OnboardingMetricSummary temperature = Assert.Single(result.Metrics, metric => metric.SensorId == "gpu-temp");
        Assert.Equal(63, temperature.Average);
        Assert.Equal(60, temperature.Minimum);
        Assert.Equal(66, temperature.Maximum);
        Assert.InRange(temperature.VariationPercent, 9.52, 9.53);
    }

    [Theory]
    [InlineData("Quiet", "quiet")]
    [InlineData("Efficiency", "efficiency")]
    [InlineData("Performance", "performance")]
    public void ModeChoiceMapsToBuiltInProfile(string mode, string expected) =>
        Assert.Equal(expected, OnboardingWorkflow.ProfileId(Enum.Parse<OnboardingModeChoice>(mode)));

    private static HardwareSnapshot Snapshot(
        IReadOnlyList<CapabilityDescriptor> capabilities,
        IReadOnlyList<SensorSample> sensors,
        IReadOnlyList<ConflictDescriptor> conflicts) => new(
            DateTimeOffset.UtcNow,
            [new HardwareDevice("device", "Device", DeviceKind.Unknown, null, null, null, new Dictionary<string, string>())],
            capabilities,
            sensors,
            conflicts,
            [],
            []);

    private static CapabilityDescriptor Capability(
        string id,
        CapabilityAccessState state,
        AdapterExecutionContext context = AdapterExecutionContext.SystemService) => new(
            id,
            "adapter",
            "device",
            id,
            state,
            context,
            ControlValueKind.Boolean,
            null,
            null,
            RiskLevel.Safe,
            state == CapabilityAccessState.Verified ? EvidenceLevel.ReadBackVerified : EvidenceLevel.Detected,
            null,
            "test",
            state == CapabilityAccessState.Verified);

    private static SensorSample Sensor(
        string id,
        string name,
        double value,
        string unit,
        SensorQuality quality,
        DateTimeOffset timestamp) => new(
            id,
            "adapter",
            "device",
            name,
            timestamp,
            value,
            unit,
            quality,
            TimeSpan.Zero);
}
