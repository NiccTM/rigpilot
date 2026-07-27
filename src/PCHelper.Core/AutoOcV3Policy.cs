using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static class AutoOcV3Policy
{
    public const int RequiredBaselineSamples = 3;

    /// <summary>
    /// One discarded warmup load window. Windows repeat until two consecutive
    /// ones agree in peak temperature (<see cref="HasReachedThermalPlateau"/>),
    /// so the warmup tracks the card's actual settling time instead of assuming
    /// one. A fixed 45-second warmup sized on one thermal configuration still
    /// left baselines climbing (86.4 → 88.0 °C, 4.89% variation) on another.
    /// </summary>
    public static readonly TimeSpan WarmupWindowDuration = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Upper bound on warmup windows (3 minutes of load at 15 s each) so a card
    /// that cannot reach equilibrium — dying fan, blocked intake — cannot stall
    /// the run indefinitely. Hitting the cap is reported, not silently accepted.
    /// </summary>
    public const int MaximumWarmupWindows = 12;

    /// <summary>Peak-temperature agreement between consecutive windows that counts as settled.</summary>
    public const double WarmupPlateauCelsius = 1.0;

    // --- Transient (game-like) validation ------------------------------------------------
    // A steady synthetic screen is the reason auto-overclockers "pass the test and crash in
    // the game". Held at full load the GPU sits at ONE power/voltage/frequency point — and
    // because it is power-limited there, that point is a comparatively low boost bin at high
    // voltage, which is the most forgiving part of the V/F curve. Games do not do that: load
    // rises and falls, so the card spends much of its time in high boost bins at LOW voltage,
    // and it repeatedly crosses between them. Those upper bins and the transitions into them
    // are where a too-high offset actually fails, and a constant-load screen never visits
    // them. So after the steady screen the candidate is cycled load/idle to force exactly
    // those transitions before the profile is accepted.

    /// <summary>Load/idle cycles run against the selected candidate after the steady screen.</summary>
    public const int TransientValidationCycles = 6;

    /// <summary>Load burst per cycle — long enough to reach a boost state, short enough to stay transient.</summary>
    public static readonly TimeSpan TransientLoadDuration = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Idle gap per cycle. Long enough for clocks and voltage to drop back down, so the next
    /// burst is a real transition rather than a continuation of the previous load.
    /// </summary>
    public static readonly TimeSpan TransientIdleDuration = TimeSpan.FromSeconds(6);

    // --- Low-power (high-boost) validation -----------------------------------------------
    // A whole-domain clock offset shifts every point on the V/F curve, but instability does
    // not live at every point. Screening runs at the stock power limit, where the card is
    // power-bound and therefore sits in a comparatively LOW boost bin at HIGH voltage — the
    // most tolerant place an offset can be tested. Reduce the power limit and the card does
    // the opposite: it holds higher boost bins at lower voltage, which is where a shifted
    // curve actually breaks, and it is the state a game produces whenever it is not pinning
    // the GPU flat out. So the selected candidate is re-screened with the power limit pulled
    // down, and a candidate that only survives while power-bound is rejected.
    //
    // This is deliberately NOT a VF-curve editor. Reading or writing per-point VF data means
    // undocumented NVAPI surfaces, which the documented-vendor-APIs rule forbids; the power
    // limit is a documented, bounded control that reaches the same operating states.

    /// <summary>Fraction of the stock power limit used for the high-boost re-screen.</summary>
    public const double LowPowerValidationFraction = 0.75;

    /// <summary>How long the reduced-power screen runs. Long enough to settle into the higher boost bins.</summary>
    public static readonly TimeSpan LowPowerValidationDuration = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The reduced power limit for validation: a fraction of stock, never below the
    /// controller's own minimum.
    /// </summary>
    public static double LowPowerValidationTarget(double stockValue, double minimumValue) =>
        Math.Max(minimumValue, stockValue * LowPowerValidationFraction);

    /// <summary>
    /// Describes a low-power validation failure, saying plainly why a candidate that just
    /// passed a full-load screen is still being rejected.
    /// </summary>
    public static string DescribeLowPowerFailure(double watts, string? detail) =>
        $"The candidate passed at the stock power limit but failed re-screening at {watts / 1000:0} W: "
        + $"{detail ?? "the screening monitor reported a failure"}. A reduced power limit makes the GPU hold "
        + "higher boost clocks at lower voltage, which is where a clock offset actually breaks and is the state "
        + "games produce whenever they are not loading the card flat out. An overclock that only survives while "
        + "power-bound would crash in a game, so no profile was generated.";

    /// <summary>
    /// Describes a transient-validation failure. The cycle index is reported because
    /// "failed on cycle 5 of 6" is materially different evidence from "failed immediately":
    /// the first is a marginal candidate, the second is plainly unstable.
    /// </summary>
    /// <summary>
    /// Marks a stage that was abandoned before it tested a single overclock, because the
    /// ladder's opening rung — the stock anchor — failed screening on its own.
    /// </summary>
    public const string BaselineFailedLabel = "Baseline failed screening";

    /// <summary>
    /// Explains why a stage produced no value. The distinction it draws is the reason it
    /// exists: "the search found no stable overclock" and "the stage never got to try one"
    /// are opposite findings that were previously reported with identical wording.
    ///
    /// Observed live on 2026-07-27: the core stage settled at +145 MHz with 5-6 °C of
    /// thermal margin left, and the memory stage then failed its stock rung outright at
    /// 96 °C memory junction. The owner was told "no memory candidate satisfied the
    /// selected objective and safety constraints", which reads as "your memory will not
    /// overclock" — sending them to hunt for a tuning problem when the card was simply too
    /// hot to test. No offset can fix a card that fails screening at the setting it ships
    /// with, so the message has to say that, and say what to do instead.
    /// </summary>
    public static string DescribeStageFailure(string stageName, TuneResult? result)
    {
        TuneCandidateResult? opening = result?.Candidates is { Count: > 0 } candidates
            ? candidates[0]
            : null;
        if (opening is { Passed: false }
            && result!.StatusLabel.StartsWith(BaselineFailedLabel, StringComparison.Ordinal))
        {
            return $"The {stageName} stage was abandoned before any overclock was tested: the card "
                + $"did not pass screening at its stock {stageName} setting. {opening.Message} "
                + "Nothing was overclocked and prior state was restored. This is not a limit of "
                + "the card's tuning headroom — clear the reported condition (cooling, airflow, or "
                + "ambient temperature if it is thermal) and run again.";
        }

        return $"No {stageName} candidate satisfied the selected objective and safety constraints.";
    }

    public static string DescribeTransientFailure(int cycle, int totalCycles, string? detail) =>
        $"The candidate passed the steady screen but failed transient (game-like) validation on "
        + $"load cycle {cycle} of {totalCycles}: {detail ?? "the screening monitor reported a failure"}. "
        + "Sustained load holds one voltage/frequency point; games swing between them, which is where "
        + "a marginal overclock actually fails. No profile was generated.";

    /// <summary>
    /// True when two consecutive warmup windows agree in peak temperature to
    /// within <see cref="WarmupPlateauCelsius"/>. Null readings never count as a
    /// plateau — absence of a temperature is not evidence of stability.
    /// </summary>
    public static bool HasReachedThermalPlateau(double? previousPeakCelsius, double? currentPeakCelsius) =>
        previousPeakCelsius is double previous
        && currentPeakCelsius is double current
        && double.IsFinite(previous)
        && double.IsFinite(current)
        && Math.Abs(current - previous) <= WarmupPlateauCelsius;

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
            // Performance: a candidate must actually beat stock. This is not just "an
            // overclock that is slower than stock is pointless" — on GDDR6X it is the
            // instability signal. That memory carries on-die error correction, so an
            // over-clocked module does not fail outright; it silently corrects errors and
            // THROUGHPUT FALLS while screening still reports a pass. Left unfiltered the
            // search reads those corrected runs as merely unimpressive and keeps climbing
            // into the range that corrupts the display. Treating a regression below stock as
            // ineligible stops the selection at the point where the memory began erroring.
            _ => passed.Where(candidate => candidate.ThroughputScore >= baselineThroughput)
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
