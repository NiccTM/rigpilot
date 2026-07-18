using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static class AutoOcV3Policy
{
    public const int RequiredBaselineSamples = 3;

    public static string? Validate(AutoOcObjectiveConstraintsV3 constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        if (constraints.MaximumBaselineVariationPercent is <= 0 or > 10)
        {
            return "Maximum baseline variation must be greater than 0% and no more than 10%.";
        }
        if (constraints.MinimumEfficiencyPerformancePercent is < 90 or > 100)
        {
            return "Efficiency performance retention must be between 90% and 100%.";
        }
        if (constraints.MinimumQuietPerformancePercent is < 90 or > 100)
        {
            return "Quiet performance retention must be between 90% and 100%.";
        }
        if (constraints.TemperatureCeilingCelsius is < 40 or > 95)
        {
            return "The Auto OC temperature ceiling must be between 40 and 95 °C.";
        }

        return null;
    }

    public static bool TryMeasureBaselineVariation(
        IReadOnlyList<AutoOcMeasurementV3> measurements,
        out double variationPercent,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        double[] values = measurements
            .Where(measurement => measurement.Passed)
            .Select(measurement => measurement.ThroughputScore)
            .Where(value => value is double number && double.IsFinite(number) && number > 0)
            .Select(value => value!.Value)
            .ToArray();
        if (measurements.Count != RequiredBaselineSamples || values.Length != RequiredBaselineSamples)
        {
            variationPercent = double.PositiveInfinity;
            reason = "Auto OC requires exactly three successful baseline throughput measurements.";
            return false;
        }

        double average = values.Average();
        variationPercent = (values.Max() - values.Min()) / average * 100;
        reason = $"Baseline throughput variation was {variationPercent.ToString("0.##", CultureInfo.InvariantCulture)}%.";
        return true;
    }

    public static IReadOnlyList<AutoOcCandidateScoreV3> ScoreCandidates(
        string stage,
        TuneResult? result,
        TuningObjective objective)
    {
        if (result is null)
        {
            return [];
        }

        return result.Candidates.Select(candidate =>
        {
            TuneScreeningResult screening = candidate.Screening;
            double? score = objective switch
            {
                TuningObjective.Performance => Positive(screening.ThroughputScore),
                TuningObjective.Efficiency => Positive(screening.ThroughputScore) is double throughput
                    && Positive(screening.AveragePowerWatts) is double power
                        ? throughput / power
                        : null,
                TuningObjective.Quiet => Positive(screening.ThroughputScore) is not null
                    && NonNegative(screening.AverageFanRpm) is double rpm
                        ? -rpm
                        : null,
                _ => null
            };
            return new AutoOcCandidateScoreV3(
                stage,
                candidate.Value,
                candidate.Passed,
                screening.ThroughputScore,
                screening.AveragePowerWatts,
                screening.AverageFanRpm,
                screening.MaximumTemperatureCelsius,
                score,
                candidate.Message);
        }).ToArray();
    }

    public static AutoOcCandidateScoreV3? SelectBestCandidate(
        IReadOnlyList<AutoOcCandidateScoreV3> candidates,
        AutoOcObjectiveConstraintsV3 constraints,
        double baselineThroughput)
    {
        AutoOcCandidateScoreV3[] passed = candidates
            .Where(candidate => candidate.Passed
                && candidate.ObjectiveScore is double score
                && double.IsFinite(score)
                && Positive(candidate.ThroughputScore) is not null)
            .ToArray();
        if (passed.Length == 0)
        {
            return null;
        }

        double bestStableThroughput = passed.Max(candidate => candidate.ThroughputScore!.Value);
        IEnumerable<AutoOcCandidateScoreV3> eligible = constraints.Objective switch
        {
            TuningObjective.Efficiency => passed.Where(candidate =>
                candidate.ThroughputScore >= bestStableThroughput * constraints.MinimumEfficiencyPerformancePercent / 100),
            TuningObjective.Quiet => passed.Where(candidate =>
                candidate.ThroughputScore >= baselineThroughput * constraints.MinimumQuietPerformancePercent / 100),
            _ => passed
        };
        return eligible
            .OrderByDescending(candidate => candidate.ObjectiveScore)
            .ThenBy(candidate => candidate.Value)
            .FirstOrDefault();
    }

    public static string? ValidateFinalMeasurement(
        AutoOcMeasurementV3 measurement,
        IReadOnlyList<AutoOcCandidateScoreV3> candidates,
        AutoOcObjectiveConstraintsV3 constraints,
        double baselineThroughput)
    {
        if (!measurement.Passed || Positive(measurement.ThroughputScore) is not double throughput)
        {
            return "The final screen did not return a successful measured-throughput result.";
        }

        if (constraints.Objective == TuningObjective.Quiet)
        {
            if (measurement.AverageFanRpm is not double rpm || !double.IsFinite(rpm) || rpm < 0)
            {
                return "Quiet validation requires measured fan RPM; no acoustic result was inferred.";
            }
            double minimum = baselineThroughput * constraints.MinimumQuietPerformancePercent / 100;
            if (throughput < minimum)
            {
                return $"Quiet validation retained less than {constraints.MinimumQuietPerformancePercent:0.#}% of baseline throughput.";
            }
        }

        if (constraints.Objective == TuningObjective.Efficiency)
        {
            double? best = candidates
                .Where(candidate => candidate.Passed)
                .Select(candidate => Positive(candidate.ThroughputScore))
                .Where(value => value is not null)
                .DefaultIfEmpty()
                .Max();
            if (best is not double bestThroughput
                || throughput < bestThroughput * constraints.MinimumEfficiencyPerformancePercent / 100)
            {
                return $"Efficiency validation was not within {100 - constraints.MinimumEfficiencyPerformancePercent:0.#}% of the best stable measured throughput.";
            }
        }

        return null;
    }

    public static AutoOcMeasurementV3 Measurement(string label, TimeSpan duration, TuneScreeningResult result) => new(
        label,
        duration,
        result.Passed,
        result.ThroughputScore,
        result.AveragePowerWatts,
        result.MaximumTemperatureCelsius,
        result.AverageFanRpm,
        result.AverageClockMegahertz,
        result.Message);

    private static double? Positive(double? value) => value is double number && double.IsFinite(number) && number > 0
        ? number
        : null;

    private static double? NonNegative(double? value) => value is double number && double.IsFinite(number) && number >= 0
        ? number
        : null;
}

public static class HardwareFingerprintBuilder
{
    public static bool TryCreate(
        HardwareSnapshot snapshot,
        string deviceId,
        IEnumerable<string> relatedDeviceIds,
        out HardwareFingerprintV1? fingerprint,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<string> ids = relatedDeviceIds.Append(deviceId).ToHashSet(StringComparer.Ordinal);
        HardwareDevice[] devices = snapshot.Devices.Where(device => ids.Contains(device.Id)).ToArray();
        string? identity = devices.Select(DeviceIdentity).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        string? pnpId = devices.Select(device => device.PnpId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        string? vbios = Property(devices, "vbiosVersion");
        string? driver = Property(devices, "driverVersion");
        if (string.IsNullOrWhiteSpace(identity)
            || string.IsNullOrWhiteSpace(vbios)
            || string.IsNullOrWhiteSpace(driver))
        {
            fingerprint = null;
            reason = "Auto OC V3 requires exact GPU identity, VBIOS version, and display-driver version before tuning.";
            return false;
        }

        string canonical = string.Join("\n", deviceId, identity, pnpId ?? string.Empty, vbios, driver);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        fingerprint = new HardwareFingerprintV1(
            HardwareFingerprintV1.CurrentSchemaVersion,
            deviceId,
            identity,
            pnpId,
            vbios,
            driver,
            hash);
        reason = "Exact GPU identity, VBIOS, and driver fingerprint captured.";
        return true;
    }

    private static string? DeviceIdentity(HardwareDevice device)
    {
        if (device.Properties.TryGetValue("uuid", out string? uuid) && !string.IsNullOrWhiteSpace(uuid))
        {
            return uuid;
        }
        return !string.IsNullOrWhiteSpace(device.PnpId)
            ? device.PnpId
            : !string.IsNullOrWhiteSpace(device.Model) ? device.Model : device.Name;
    }

    private static string? Property(IEnumerable<HardwareDevice> devices, string name) => devices
        .Select(device => device.Properties.TryGetValue(name, out string? value) ? value : null)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
