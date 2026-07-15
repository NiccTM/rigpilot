using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Converts calibration evidence into the small set of safety decisions used
/// by commissioning and the cooling graph.  A fan that demonstrably remains
/// running at the controller's minimum is useful: it can use a non-zero curve
/// tailored to that controller.  It is not evidence that the fan can safely
/// stop and restart.
/// </summary>
public static class FanCalibrationPolicy
{
    public static bool SupportsVerifiedFanStop(FanCalibrationResult result) =>
        result.RestartVerified
        && result.RestartVerificationCyclesCompleted > 0
        && result.StallDutyPercent.HasValue
        && result.RestartDutyPercent is > 0;

    public static bool SupportsVerifiedFanStop(FanCalibrationV2 calibration)
    {
        if (calibration.SupportsVerifiedFanStop)
        {
            return true;
        }

        // Version 2 records were emitted only after the service had completed
        // the repeated restart gate. Preserve their already-earned evidence
        // during the schema 3 upgrade; do not extend this inference to new or
        // imported schema 3 records.
        return calibration.SchemaVersion < FanCalibrationV2.CurrentSchemaVersion
            && calibration.StallDutyPercent.HasValue
            && calibration.RestartDutyPercent is > 0;
    }

    public static bool SupportsNonZeroCurve(FanCalibrationResult result) =>
        result.AllMeasurementsStable
        && IsPositiveFinite(result.MinimumDutyPercent)
        && (SupportsVerifiedFanStop(result) || result.NonStopFloorObserved);

    public static bool SupportsNonZeroCurve(FanCalibrationV2 calibration) =>
        calibration.Measurements.Count > 0
        && calibration.Measurements.All(point => point.Stable)
        && IsPositiveFinite(calibration.MinimumDutyPercent)
        && (SupportsVerifiedFanStop(calibration) || calibration.NonStopFloorObserved);

    /// <summary>
    /// Validates the declared graph envelope against a physical calibration.
    /// The graph must advertise the measured non-zero floor unless it has
    /// explicit repeated stop/restart evidence for an exact 0% request.
    /// </summary>
    public static string? ValidateOutput(CoolingGraphOutputV1 output, FanCalibrationV2 calibration)
    {
        if (!SupportsNonZeroCurve(calibration))
        {
            return $"Cooling output '{output.CapabilityId}' has no stable measured non-zero operating floor.";
        }

        if (output.Maximum + 1e-6 < calibration.MinimumDutyPercent)
        {
            return $"Cooling output '{output.CapabilityId}' caps at {output.Maximum:0.#}%, below its measured safe floor of {calibration.MinimumDutyPercent:0.#}%.";
        }

        if (output.Minimum <= 0 && !SupportsVerifiedFanStop(calibration))
        {
            return $"Cooling output '{output.CapabilityId}' requests zero RPM without repeated stop/restart verification. Use a minimum of at least {calibration.MinimumDutyPercent:0.#}%.";
        }

        if (output.Minimum > 0 && output.Minimum + 1e-6 < calibration.MinimumDutyPercent)
        {
            return $"Cooling output '{output.CapabilityId}' has a {output.Minimum:0.#}% floor, below its measured safe floor of {calibration.MinimumDutyPercent:0.#}%.";
        }

        return null;
    }

    /// <summary>
    /// Applies the calibrated floor defensively at evaluation time as well as
    /// at graph validation time. Only an exact 0% request may pass below the
    /// floor, and only after a verified stop/restart sequence.
    /// </summary>
    public static double EnforceSafeDuty(double requestedDutyPercent, FanCalibrationV2 calibration)
    {
        if (!double.IsFinite(requestedDutyPercent))
        {
            throw new CoolingGraphEvaluationException($"Calibration for '{calibration.CapabilityId}' received a non-finite duty request.");
        }
        if (!IsPositiveFinite(calibration.MinimumDutyPercent))
        {
            throw new CoolingGraphEvaluationException($"Calibration for '{calibration.CapabilityId}' has no valid measured non-zero floor.");
        }

        if (requestedDutyPercent <= 0 && SupportsVerifiedFanStop(calibration))
        {
            return 0;
        }

        return Math.Max(requestedDutyPercent, calibration.MinimumDutyPercent);
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
