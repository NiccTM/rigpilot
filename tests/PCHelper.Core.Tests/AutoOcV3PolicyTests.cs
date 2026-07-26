using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class AutoOcV3PolicyTests
{
    [Fact]
    public void StableThreeSampleBaselinePassesVariationGate()
    {
        AutoOcMeasurementV3[] samples =
        [
            Measurement(100),
            Measurement(101),
            Measurement(99)
        ];

        bool measured = AutoOcV3Policy.TryMeasureBaselineVariation(samples, out double variation, out _);

        Assert.True(measured);
        Assert.Equal(2, variation, 6);
    }

    [Fact]
    public void EfficiencySelectsBestPerWattOnlyWithinTwoPercentOfBestPerformance()
    {
        AutoOcObjectiveConstraintsV3 constraints = Constraints(TuningObjective.Efficiency);
        AutoOcCandidateScoreV3[] candidates =
        [
            Candidate(100, throughput: 100, power: 200, objectiveScore: 0.5),
            Candidate(90, throughput: 99, power: 150, objectiveScore: 0.66),
            Candidate(80, throughput: 95, power: 100, objectiveScore: 0.95)
        ];

        AutoOcCandidateScoreV3? selected = AutoOcV3Policy.SelectBestCandidate(candidates, constraints, baselineThroughput: 100);

        Assert.NotNull(selected);
        Assert.Equal(90, selected.Value);
    }

    [Fact]
    public void QuietRequiresFanRpmAndNinetyFivePercentPerformanceRetention()
    {
        AutoOcObjectiveConstraintsV3 constraints = Constraints(TuningObjective.Quiet);
        AutoOcCandidateScoreV3[] candidates =
        [
            Candidate(1, throughput: 100, power: 200, objectiveScore: -1200, rpm: 1200),
            Candidate(2, throughput: 96, power: 170, objectiveScore: -900, rpm: 900),
            Candidate(3, throughput: 94, power: 140, objectiveScore: -700, rpm: 700),
            Candidate(4, throughput: 99, power: 180, objectiveScore: null, rpm: null)
        ];

        AutoOcCandidateScoreV3? selected = AutoOcV3Policy.SelectBestCandidate(candidates, constraints, baselineThroughput: 100);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Value);
    }

    [Fact]
    public void FingerprintChangesWhenDriverOrVbiosChanges()
    {
        HardwareSnapshot first = Snapshot("95.02.42.00.A1", "610.62");
        HardwareSnapshot second = Snapshot("95.02.42.00.A2", "610.62");

        Assert.True(HardwareFingerprintBuilder.TryCreate(first, "gpu:0", ["gpu:0"], out HardwareFingerprintV1? firstFingerprint, out _));
        Assert.True(HardwareFingerprintBuilder.TryCreate(second, "gpu:0", ["gpu:0"], out HardwareFingerprintV1? secondFingerprint, out _));
        Assert.NotEqual(firstFingerprint!.FingerprintSha256, secondFingerprint!.FingerprintSha256);
    }

    [Fact]
    public void FingerprintFailsClosedWithoutVbios()
    {
        HardwareSnapshot snapshot = Snapshot(string.Empty, "610.62");

        bool created = HardwareFingerprintBuilder.TryCreate(snapshot, "gpu:0", ["gpu:0"], out _, out string reason);

        Assert.False(created);
        Assert.Contains("VBIOS", reason, StringComparison.Ordinal);
    }

    private static AutoOcObjectiveConstraintsV3 Constraints(TuningObjective objective) => new(objective);

    private static AutoOcMeasurementV3 Measurement(double throughput) => new(
        "baseline",
        TimeSpan.FromSeconds(10),
        true,
        throughput,
        200,
        60,
        1000,
        1900,
        "pass");

    private static AutoOcCandidateScoreV3 Candidate(
        double value,
        double throughput,
        double power,
        double? objectiveScore,
        double? rpm = 1000) => new(
            "Power",
            value,
            true,
            throughput,
            power,
            rpm,
            60,
            objectiveScore,
            "pass");

    private static HardwareSnapshot Snapshot(string vbios, string driver) => new(
        DateTimeOffset.UtcNow,
        [new HardwareDevice(
            "gpu:0",
            "GPU",
            DeviceKind.Gpu,
            "NVIDIA",
            "GPU",
            "PCI\\VEN_10DE&DEV_2204",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["uuid"] = "GPU-test",
                ["vbiosVersion"] = vbios,
                ["driverVersion"] = driver
            })],
        [],
        [],
        [],
        [],
        []);
}
