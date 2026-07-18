using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed record HardwareOperationEligibility(bool Eligible, string Reason)
{
    public static HardwareOperationEligibility Allow(string reason) => new(true, reason);

    public static HardwareOperationEligibility Deny(string reason) => new(false, reason);
}

public static class HardwareOperationEligibilityEvaluator
{
    public static HardwareOperationEligibility ForCalibration(
        CapabilityDescriptor capability,
        bool confirmExperimental,
        bool confirmDevice)
    {
        HardwareOperationEligibility common = EvaluateCommon(capability, confirmExperimental, confirmDevice);
        if (!common.Eligible)
        {
            return common;
        }

        if (capability.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety))
        {
            return HardwareOperationEligibility.Deny("Calibration is limited to cooling controls.");
        }

        return HardwareOperationEligibility.Allow("The controller exposes bounded writes, read-back, rollback, and firmware reset.");
    }

    public static HardwareOperationEligibility ForTuning(
        CapabilityDescriptor capability,
        TunePlan plan,
        bool confirmExperimental,
        bool confirmDevice)
    {
        HardwareOperationEligibility common = EvaluateCommon(capability, confirmExperimental, confirmDevice);
        if (!common.Eligible)
        {
            return common;
        }

        if (capability.Domain is not (ControlDomain.Cooling or ControlDomain.Cpu or ControlDomain.Gpu))
        {
            return HardwareOperationEligibility.Deny("Automatic tuning is limited to one cooling, CPU, or GPU domain at a time.");
        }

        if (capability.Name.Contains("voltage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability.Unit, "V", StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability.Unit, "mV", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareOperationEligibility.Deny("Automatic voltage adjustment is forbidden.");
        }

        if (!string.Equals(plan.DeviceId, capability.DeviceId, StringComparison.Ordinal))
        {
            return HardwareOperationEligibility.Deny("The tuning plan does not target the capability's exact device ID.");
        }

        if (!plan.Bounds.TryGetValue(capability.Id, out TuneBounds? bounds))
        {
            return HardwareOperationEligibility.Deny("The tuning plan does not contain bounds for this capability.");
        }

        NumericRange range = capability.Range!;
        if (!AreFinite(bounds.Minimum, bounds.Maximum, bounds.Step)
            || bounds.Minimum < range.Minimum
            || bounds.Maximum > range.Maximum
            || bounds.Minimum > bounds.Maximum
            || bounds.Step <= 0)
        {
            return HardwareOperationEligibility.Deny("The requested tuning bounds are invalid or exceed the adapter-reported range.");
        }

        return HardwareOperationEligibility.Allow("The tuning engine can search this bounded control without changing voltage.");
    }

    private static HardwareOperationEligibility EvaluateCommon(
        CapabilityDescriptor capability,
        bool confirmExperimental,
        bool confirmDevice)
    {
        if (capability.State == CapabilityAccessState.Blocked)
        {
            return HardwareOperationEligibility.Deny(
                $"Blocked by {capability.ConflictOwner ?? "another controller"}: {capability.Reason}");
        }

        if (capability.State is CapabilityAccessState.ReadOnly or CapabilityAccessState.Unsupported or CapabilityAccessState.Faulted)
        {
            return HardwareOperationEligibility.Deny(capability.Reason);
        }

        if (capability.State == CapabilityAccessState.Experimental && (!confirmExperimental || !confirmDevice))
        {
            return HardwareOperationEligibility.Deny(
                "Experimental operations require both the global advanced-write acknowledgement and exact-device confirmation.");
        }

        if (capability.ValueKind != ControlValueKind.Numeric || capability.Range is not NumericRange range)
        {
            return HardwareOperationEligibility.Deny("The operation requires a numeric capability with discoverable bounds.");
        }

        if (!AreFinite(range.Minimum, range.Maximum, range.Step)
            || range.Minimum > range.Maximum
            || range.Step <= 0)
        {
            return HardwareOperationEligibility.Deny("The adapter reported invalid numeric bounds.");
        }

        if (!capability.CanResetToDefault)
        {
            return HardwareOperationEligibility.Deny("The adapter does not expose a verified firmware/default reset path.");
        }

        return HardwareOperationEligibility.Allow("Eligible.");
    }

    private static bool AreFinite(params double[] values) => values.All(double.IsFinite);
}

public sealed class HardwareOperationRecoveryException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

public sealed class HardwareSafetyException(string message) : InvalidOperationException(message);

public sealed class FanCalibrationEngine(
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? timeProvider = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<FanCalibrationResult> RunAsync(
        StartCalibrationRequest request,
        CapabilityDescriptor capability,
        IHardwareAdapter adapter,
        Action<double, string>? reportProgress,
        CancellationToken cancellationToken)
    {
        HardwareOperationEligibility eligibility = HardwareOperationEligibilityEvaluator.ForCalibration(
            capability,
            request.ConfirmExperimental,
            request.ConfirmDevice);
        if (!eligibility.Eligible)
        {
            throw new InvalidOperationException(eligibility.Reason);
        }

        if (request.AllowFanStop
            && (capability.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Zero-RPM calibration is forbidden for CPU fans and pumps.");
        }

        TimeSpan settlingTime = request.SettlingTime ?? TimeSpan.FromSeconds(3);
        if (settlingTime < TimeSpan.Zero || settlingTime > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Settling time must be between 0 and 30 seconds.");
        }

        TimeSpan sampleInterval = request.SampleInterval ?? TimeSpan.FromMilliseconds(500);
        if (sampleInterval < TimeSpan.Zero || sampleInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "RPM sample interval must be between 0 and 5 seconds.");
        }

        if (request.StableSampleCount is < 2 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Stable RPM sample count must be between 2 and 10.");
        }

        if (request.MaximumSampleCount < request.StableSampleCount || request.MaximumSampleCount > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum RPM sample count must include the stable window and cannot exceed 30.");
        }

        if (!double.IsFinite(request.StabilityTolerancePercent)
            || request.StabilityTolerancePercent is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "RPM stability tolerance must be between 1% and 25%.");
        }

        if (request.RestartVerificationCycles is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Restart verification cycles must be between 1 and 5.");
        }

        FanCalibrationTemperatureLimit[] temperatureLimits = (request.TemperatureLimits ?? [])
            .DistinctBy(limit => limit.SensorId, StringComparer.Ordinal)
            .ToArray();
        if (temperatureLimits.Length > 8
            || temperatureLimits.Any(limit => string.IsNullOrWhiteSpace(limit.SensorId)
                || !double.IsFinite(limit.MaximumCelsius)
                || limit.MaximumCelsius is < 40 or > 110))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Calibration accepts at most eight temperature limits between 40 and 110 °C.");
        }

        Dictionary<string, double> maximumTemperatures = new(StringComparer.Ordinal);

        NumericRange range = capability.Range!;
        double maximumDuty = range.Maximum;
        ProfileAction initialAction = CreateAction(capability, maximumDuty);
        PreparedAction original = await adapter.PrepareAsync(initialAction, cancellationToken).ConfigureAwait(false);
        bool operationSucceeded = false;
        bool returnToFirmwareControl = false;
        try
        {
            List<FanCalibrationPoint> measurements = [];
            double[] descending = BuildCalibrationSteps(range, request.AllowFanStop);
            reportProgress?.Invoke(1, "Commanding maximum cooling before calibration.");
            StableRpmMeasurement maximumMeasurement = await ApplyAndMeasureAsync(
                original,
                maximumDuty,
                request.RpmSensorId,
                settlingTime,
                sampleInterval,
                request.StableSampleCount,
                request.MaximumSampleCount,
                request.StabilityTolerancePercent,
                requireStable: true,
                temperatureLimits,
                maximumTemperatures,
                adapter,
                cancellationToken).ConfigureAwait(false);
            double maximumRpm = maximumMeasurement.Rpm;
            if (maximumRpm < 100)
            {
                throw new HardwareSafetyException("The selected RPM source did not respond at maximum duty.");
            }

            measurements.Add(ToPoint(maximumDuty, maximumMeasurement));
            double stallThreshold = Math.Max(100, maximumRpm * 0.05);
            double? stallDuty = null;
            int index = 0;
            foreach (double duty in descending.Where(value => value < maximumDuty))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                reportProgress?.Invoke(
                    5 + (55d * index / Math.Max(1, descending.Length - 1)),
                    $"Measuring {duty:0}% duty.");
                StableRpmMeasurement measurement = await ApplyAndMeasureAsync(
                    original,
                    duty,
                    request.RpmSensorId,
                    settlingTime,
                    sampleInterval,
                    request.StableSampleCount,
                    request.MaximumSampleCount,
                    request.StabilityTolerancePercent,
                    requireStable: false,
                    temperatureLimits,
                    maximumTemperatures,
                    adapter,
                    cancellationToken).ConfigureAwait(false);
                double rpm = measurement.Rpm;
                measurements.Add(ToPoint(duty, measurement));
                if (request.AllowFanStop && rpm < stallThreshold)
                {
                    stallDuty ??= duty;
                }
            }

            FanResponseCharacterization response = CharacterizeResponse(
                measurements,
                range,
                stallThreshold,
                request.AllowFanStop);

            double? restartDuty = null;
            double? verifiedStopDuty = null;
            int restartVerificationCyclesCompleted = 0;
            if (stallDuty is double stalled)
            {
                verifiedStopDuty = measurements
                    .Where(point => point.Rpm < stallThreshold)
                    .Min(point => point.DutyPercent);
                double[] ascending = BuildRestartSteps(range, stalled);
                for (int restartIndex = 0; restartIndex < ascending.Length; restartIndex++)
                {
                    double duty = ascending[restartIndex];
                    reportProgress?.Invoke(
                        65 + (25d * (restartIndex + 1) / Math.Max(1, ascending.Length)),
                        $"Verifying restart candidate {duty:0}%.");
                    int candidateCycles = 0;
                    bool candidateFailed = false;
                    for (int cycle = 0; cycle < request.RestartVerificationCycles; cycle++)
                    {
                        FanStateConfirmation stopped = await ApplyAndConfirmFanStateAsync(
                            original,
                            verifiedStopDuty.Value,
                            request.RpmSensorId,
                            settlingTime,
                            sampleInterval,
                            request.StableSampleCount,
                            request.MaximumSampleCount,
                            request.StabilityTolerancePercent,
                            stallThreshold,
                            expectRunning: false,
                            temperatureLimits,
                            maximumTemperatures,
                            adapter,
                            cancellationToken).ConfigureAwait(false);
                        measurements.Add(ToPoint(verifiedStopDuty.Value, stopped.Measurement));
                        if (!stopped.Confirmed)
                        {
                            throw new HardwareSafetyException("The fan did not stop consistently during restart verification.");
                        }

                        FanStateConfirmation restarted = await ApplyAndConfirmFanStateAsync(
                            original,
                            duty,
                            request.RpmSensorId,
                            settlingTime,
                            sampleInterval,
                            request.StableSampleCount,
                            request.MaximumSampleCount,
                            request.StabilityTolerancePercent,
                            stallThreshold,
                            expectRunning: true,
                            temperatureLimits,
                            maximumTemperatures,
                            adapter,
                            cancellationToken).ConfigureAwait(false);
                        measurements.Add(ToPoint(duty, restarted.Measurement));
                        if (!restarted.Confirmed)
                        {
                            FanStateConfirmation recovery = await ApplyAndConfirmFanStateAsync(
                                original,
                                maximumDuty,
                                request.RpmSensorId,
                                settlingTime,
                                sampleInterval,
                                request.StableSampleCount,
                                request.MaximumSampleCount,
                                request.StabilityTolerancePercent,
                                stallThreshold,
                                expectRunning: true,
                                temperatureLimits,
                                maximumTemperatures,
                                adapter,
                                cancellationToken).ConfigureAwait(false);
                            measurements.Add(ToPoint(maximumDuty, recovery.Measurement));
                            if (!recovery.Confirmed)
                            {
                                throw new HardwareSafetyException("The fan failed to recover at maximum duty after a rejected restart candidate.");
                            }

                            candidateFailed = true;
                            break;
                        }

                        candidateCycles++;
                    }

                    if (!candidateFailed && candidateCycles == request.RestartVerificationCycles)
                    {
                        restartDuty = duty;
                        restartVerificationCyclesCompleted = candidateCycles;
                        break;
                    }
                }

                if (restartDuty is null)
                {
                    throw new HardwareSafetyException("The fan stopped but no duty completed every required restart verification cycle.");
                }
            }

            bool restartVerified = restartDuty.HasValue
                && restartVerificationCyclesCompleted >= request.RestartVerificationCycles;
            double measuredMinimum = restartVerified
                ? Math.Min(maximumDuty, restartDuty!.Value + 5)
                : Math.Min(
                    maximumDuty,
                    measurements.Where(point => point.Rpm >= stallThreshold).Min(point => point.DutyPercent) + 5);
            double minimumDuty = restartVerified
                ? AlignUp(range, measuredMinimum)
                : AlignUpToSafetyBand(range, measuredMinimum);
            maximumRpm = measurements.Max(point => point.Rpm);
            reportProgress?.Invoke(95, "Restoring the previous firmware or software fan policy.");
            operationSucceeded = true;
            return new FanCalibrationResult(
                capability.Id,
                request.RpmSensorId,
                maximumRpm,
                stallDuty,
                restartDuty,
                minimumDuty,
                restartVerified,
                measurements,
                restartVerificationCyclesCompleted,
                request.StableSampleCount,
                sampleInterval,
                request.StabilityTolerancePercent,
                measurements.All(point => point.Stable),
                verifiedStopDuty,
                maximumTemperatures,
                response.EffectiveFloorDutyPercent,
                response.EffectiveFloorRpm,
                response.FirstResponsiveDutyPercent,
                response.NonStopFloorObserved);
        }
        catch (HardwareSafetyException)
        {
            returnToFirmwareControl = true;
            await TryApplyMaximumAsync(original, range.Maximum, adapter).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryApplyMaximumAsync(original, range.Maximum, adapter).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (returnToFirmwareControl)
            {
                await RestoreFirmwareControlAsync(capability, original, adapter).ConfigureAwait(false);
            }
            else
            {
                await RestoreOriginalAsync(capability, original, adapter, operationSucceeded).ConfigureAwait(false);
            }
            reportProgress?.Invoke(100, "Calibration finished; the prior control policy was restored.");
        }
    }

    private async Task<StableRpmMeasurement> ApplyAndMeasureAsync(
        PreparedAction original,
        double duty,
        string rpmSensorId,
        TimeSpan settlingTime,
        TimeSpan sampleInterval,
        int stableSampleCount,
        int maximumSampleCount,
        double stabilityTolerancePercent,
        bool requireStable,
        IReadOnlyList<FanCalibrationTemperatureLimit> temperatureLimits,
        IDictionary<string, double> maximumTemperatures,
        IHardwareAdapter adapter,
        CancellationToken cancellationToken)
    {
        PreparedAction step = WithNumericValue(original, duty);
        await adapter.ApplyAsync(step, cancellationToken).ConfigureAwait(false);
        ActionVerification verification = await adapter.VerifyAsync(step, cancellationToken).ConfigureAwait(false);
        if (!verification.Success)
        {
            throw new InvalidOperationException($"Control read-back failed at {duty:0}%: {verification.Message}");
        }

        await _delay(settlingTime, cancellationToken).ConfigureAwait(false);
        List<double> samples = new(maximumSampleCount);
        for (int sampleIndex = 0; sampleIndex < maximumSampleCount; sampleIndex++)
        {
            IReadOnlyList<SensorSample> sensors = await adapter.ReadSensorsAsync(cancellationToken).ConfigureAwait(false);
            SensorSample sample = sensors.FirstOrDefault(item => string.Equals(item.SensorId, rpmSensorId, StringComparison.Ordinal))
                ?? throw new HardwareSafetyException("The selected RPM sensor is no longer available.");
            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan maximumFreshness = TimeSpan.FromSeconds(Math.Max(
                3,
                settlingTime.TotalSeconds + (maximumSampleCount * sampleInterval.TotalSeconds) + 2));
            if (sample.Quality != SensorQuality.Good
                || sample.Value is not double rpm
                || !double.IsFinite(rpm)
                || rpm < 0
                || now - sample.Timestamp > maximumFreshness)
            {
                throw new HardwareSafetyException("The selected RPM sensor is stale or invalid; calibration was stopped safely.");
            }

            ValidateTemperatures(
                sensors,
                temperatureLimits,
                maximumTemperatures,
                now,
                maximumFreshness);

            samples.Add(rpm);
            if (samples.Count >= stableSampleCount)
            {
                double[] window = samples.TakeLast(stableSampleCount).OrderBy(value => value).ToArray();
                double median = Median(window);
                double spread = window[^1] - window[0];
                double tolerance = Math.Max(100, Math.Abs(median) * stabilityTolerancePercent / 100);
                if (spread <= tolerance)
                {
                    return new StableRpmMeasurement(median, samples.Count, spread, Stable: true);
                }
            }

            if (sampleIndex + 1 < maximumSampleCount)
            {
                await _delay(sampleInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        double[] finalWindow = samples.TakeLast(stableSampleCount).OrderBy(value => value).ToArray();
        double finalMedian = Median(finalWindow);
        double finalSpread = finalWindow[^1] - finalWindow[0];
        if (!requireStable)
        {
            return new StableRpmMeasurement(finalMedian, samples.Count, finalSpread, Stable: false);
        }

        throw new HardwareSafetyException(
            $"Fan RPM did not settle at {duty:0}% after {maximumSampleCount} samples; firmware control was restored.");
    }

    private async Task<FanStateConfirmation> ApplyAndConfirmFanStateAsync(
        PreparedAction original,
        double duty,
        string rpmSensorId,
        TimeSpan settlingTime,
        TimeSpan sampleInterval,
        int requiredConsecutiveSamples,
        int maximumSampleCount,
        double stabilityTolerancePercent,
        double runningThreshold,
        bool expectRunning,
        IReadOnlyList<FanCalibrationTemperatureLimit> temperatureLimits,
        IDictionary<string, double> maximumTemperatures,
        IHardwareAdapter adapter,
        CancellationToken cancellationToken)
    {
        PreparedAction step = WithNumericValue(original, duty);
        await adapter.ApplyAsync(step, cancellationToken).ConfigureAwait(false);
        ActionVerification verification = await adapter.VerifyAsync(step, cancellationToken).ConfigureAwait(false);
        if (!verification.Success)
        {
            throw new InvalidOperationException($"Control read-back failed at {duty:0}%: {verification.Message}");
        }

        await _delay(settlingTime, cancellationToken).ConfigureAwait(false);
        List<double> samples = new(maximumSampleCount);
        int consecutive = 0;
        for (int sampleIndex = 0; sampleIndex < maximumSampleCount; sampleIndex++)
        {
            IReadOnlyList<SensorSample> sensors = await adapter.ReadSensorsAsync(cancellationToken).ConfigureAwait(false);
            SensorSample sample = sensors.FirstOrDefault(item => string.Equals(item.SensorId, rpmSensorId, StringComparison.Ordinal))
                ?? throw new HardwareSafetyException("The selected RPM sensor is no longer available.");
            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan maximumFreshness = TimeSpan.FromSeconds(Math.Max(
                3,
                settlingTime.TotalSeconds + (maximumSampleCount * sampleInterval.TotalSeconds) + 2));
            if (sample.Quality != SensorQuality.Good
                || sample.Value is not double rpm
                || !double.IsFinite(rpm)
                || rpm < 0
                || now - sample.Timestamp > maximumFreshness)
            {
                throw new HardwareSafetyException("The selected RPM sensor is stale or invalid; calibration was stopped safely.");
            }

            ValidateTemperatures(
                sensors,
                temperatureLimits,
                maximumTemperatures,
                now,
                maximumFreshness);

            samples.Add(rpm);
            bool matches = expectRunning ? rpm >= runningThreshold : rpm < runningThreshold;
            consecutive = matches ? consecutive + 1 : 0;
            if (consecutive >= requiredConsecutiveSamples)
            {
                double[] window = samples.TakeLast(requiredConsecutiveSamples).OrderBy(value => value).ToArray();
                double median = Median(window);
                double spread = window[^1] - window[0];
                double tolerance = Math.Max(100, Math.Abs(median) * stabilityTolerancePercent / 100);
                return new FanStateConfirmation(
                    Confirmed: true,
                    new StableRpmMeasurement(median, samples.Count, spread, Stable: spread <= tolerance));
            }

            if (sampleIndex + 1 < maximumSampleCount)
            {
                await _delay(sampleInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        double[] finalWindow = samples.TakeLast(requiredConsecutiveSamples).OrderBy(value => value).ToArray();
        double finalMedian = Median(finalWindow);
        double finalSpread = finalWindow[^1] - finalWindow[0];
        return new FanStateConfirmation(
            Confirmed: false,
            new StableRpmMeasurement(finalMedian, samples.Count, finalSpread, Stable: false));
    }

    private static void ValidateTemperatures(
        IReadOnlyList<SensorSample> sensors,
        IReadOnlyList<FanCalibrationTemperatureLimit> limits,
        IDictionary<string, double> maximumTemperatures,
        DateTimeOffset now,
        TimeSpan maximumFreshness)
    {
        foreach (FanCalibrationTemperatureLimit limit in limits)
        {
            SensorSample temperature = sensors.FirstOrDefault(sensor =>
                    string.Equals(sensor.SensorId, limit.SensorId, StringComparison.Ordinal))
                ?? throw new HardwareSafetyException($"Safety temperature sensor {limit.SensorId} disappeared during calibration.");
            if (temperature.Quality != SensorQuality.Good
                || temperature.Value is not double value
                || !double.IsFinite(value)
                || now - temperature.Timestamp > maximumFreshness)
            {
                throw new HardwareSafetyException($"Safety temperature sensor {limit.SensorId} became stale or invalid.");
            }

            if (!maximumTemperatures.TryGetValue(limit.SensorId, out double previous) || value > previous)
            {
                maximumTemperatures[limit.SensorId] = value;
            }

            if (value >= limit.MaximumCelsius)
            {
                throw new HardwareSafetyException(
                    $"Temperature safety ceiling reached: {temperature.Name} was {value:0.0} °C (limit {limit.MaximumCelsius:0.0} °C).");
            }
        }
    }

    private static FanCalibrationPoint ToPoint(double duty, StableRpmMeasurement measurement) => new(
        duty,
        measurement.Rpm,
        measurement.SampleCount,
        measurement.Spread,
        measurement.Stable);

    /// <summary>
    /// Derives the actual low-speed envelope from this exact controller and
    /// paired tachometer. A controller that keeps the fan spinning at its
    /// minimum command is a valid nonzero-control candidate, but never a
    /// zero-RPM candidate. The plateau and first responsive command make the
    /// curve useful on individual systems instead of assuming a generic fan.
    /// </summary>
    private static FanResponseCharacterization CharacterizeResponse(
        IReadOnlyList<FanCalibrationPoint> measurements,
        NumericRange range,
        double runningThreshold,
        bool scannedToControllerMinimum)
    {
        FanCalibrationPoint[] points = measurements
            .Where(point => double.IsFinite(point.DutyPercent)
                && double.IsFinite(point.Rpm)
                && point.Rpm >= 0)
            .OrderBy(point => point.DutyPercent)
            .ToArray();
        if (points.Length == 0)
        {
            return new FanResponseCharacterization(null, null, null, false);
        }

        double floorRpm = points.Min(point => point.Rpm);
        // This threshold is deliberately tighter than the measurement-stability
        // tolerance: it detects a real rise from a controller-enforced low-RPM
        // plateau without reacting to ordinary tachometer jitter.
        double floorTolerance = Math.Max(10, Math.Abs(floorRpm) * 0.05);
        FanCalibrationPoint[] floorPoints = points
            .Where(point => point.Rpm <= floorRpm + floorTolerance)
            .ToArray();
        double? firstResponsiveDuty = points
            .Where(point => point.Rpm > floorRpm + floorTolerance)
            .Select(point => (double?)point.DutyPercent)
            .FirstOrDefault();
        double? effectiveFloorDuty = firstResponsiveDuty.HasValue
            ? floorPoints.Max(point => point.DutyPercent)
            : null;
        double epsilon = Math.Max(0.001, range.Step / 1000d);
        bool reachedControllerMinimum = points.Any(point => Math.Abs(point.DutyPercent - range.Minimum) <= epsilon);
        bool nonStopFloorObserved = scannedToControllerMinimum
            && reachedControllerMinimum
            && floorRpm >= runningThreshold
            && firstResponsiveDuty.HasValue;

        return new FanResponseCharacterization(
            effectiveFloorDuty,
            floorRpm,
            firstResponsiveDuty,
            nonStopFloorObserved);
    }

    private static double Median(double[] ordered) => ordered.Length % 2 == 1
        ? ordered[ordered.Length / 2]
        : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;

    private static double AlignUpToSafetyBand(NumericRange range, double value)
    {
        double safetyBand = Math.Max(10, range.Step);
        double banded = Math.Ceiling((value - 1e-9) / safetyBand) * safetyBand;
        return Math.Min(range.Maximum, AlignUp(range, Math.Max(range.Minimum, banded)));
    }

    private sealed record StableRpmMeasurement(double Rpm, int SampleCount, double Spread, bool Stable);

    private sealed record FanStateConfirmation(bool Confirmed, StableRpmMeasurement Measurement);

    private sealed record FanResponseCharacterization(
        double? EffectiveFloorDutyPercent,
        double? EffectiveFloorRpm,
        double? FirstResponsiveDutyPercent,
        bool NonStopFloorObserved);

    private static double[] BuildCalibrationSteps(NumericRange range, bool allowStop)
    {
        double floor = allowStop ? range.Minimum : Math.Max(range.Minimum, Math.Min(20, range.Maximum));
        List<double> values = [range.Maximum];
        for (double candidate = range.Maximum - 5; candidate >= floor - 1e-6; candidate -= 5)
        {
            double aligned = AlignDown(range, candidate);
            if (aligned >= floor - 1e-6 && !values.Contains(aligned))
            {
                values.Add(aligned);
            }
        }

        double alignedFloor = AlignUp(range, floor);
        if (!values.Contains(alignedFloor))
        {
            values.Add(alignedFloor);
        }

        return values.OrderByDescending(value => value).ToArray();
    }

    private static double[] BuildRestartSteps(NumericRange range, double stalledDuty)
    {
        List<double> values = [];
        for (double candidate = stalledDuty + 5; candidate <= range.Maximum + 1e-6; candidate += 5)
        {
            double aligned = AlignUp(range, candidate);
            if (aligned <= range.Maximum + 1e-6 && !values.Contains(aligned))
            {
                values.Add(aligned);
            }
        }

        if (!values.Contains(range.Maximum))
        {
            values.Add(range.Maximum);
        }

        return values.OrderBy(value => value).ToArray();
    }

    private static ProfileAction CreateAction(CapabilityDescriptor capability, double value) => new(
        $"calibrate:{Guid.NewGuid():N}",
        capability.AdapterId,
        capability.Id,
        ControlValue.FromNumeric(value),
        Required: true,
        Order: 0);

    private static PreparedAction WithNumericValue(PreparedAction original, double value) => original with
    {
        Action = original.Action with { Value = ControlValue.FromNumeric(value) }
    };

    private static async Task TryApplyMaximumAsync(PreparedAction original, double maximum, IHardwareAdapter adapter)
    {
        try
        {
            PreparedAction safe = WithNumericValue(original, maximum);
            await adapter.ApplyAsync(safe, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The mandatory restore attempt below remains the final recovery path.
        }
    }

    private static async Task RestoreOriginalAsync(
        CapabilityDescriptor capability,
        PreparedAction original,
        IHardwareAdapter adapter,
        bool operationSucceeded)
    {
        try
        {
            await adapter.RollbackAsync(original, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackError)
        {
            try
            {
                await adapter.ResetToDefaultAsync(capability.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception resetError)
            {
                throw new HardwareOperationRecoveryException(
                    $"Rollback and firmware/default reset both failed: {rollbackError.Message}; {resetError.Message}",
                    new AggregateException(rollbackError, resetError));
            }

            string prefix = operationSucceeded ? "Calibration measurements completed, but" : "Calibration failed and";
            throw new HardwareOperationRecoveryException(
                $"{prefix} the previous control policy could not be restored. Firmware/default control was restored instead.",
                rollbackError);
        }
    }

    private static async Task RestoreFirmwareControlAsync(
        CapabilityDescriptor capability,
        PreparedAction original,
        IHardwareAdapter adapter)
    {
        try
        {
            await adapter.ResetToDefaultAsync(capability.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception resetError)
        {
            try
            {
                await adapter.RollbackAsync(original, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new HardwareOperationRecoveryException(
                    $"Safety reset and rollback both failed: {resetError.Message}; {rollbackError.Message}",
                    new AggregateException(resetError, rollbackError));
            }

            throw new HardwareOperationRecoveryException(
                "A stale calibration source forced firmware recovery, but default reset failed. The previous state was restored; further writes are blocked for inspection.",
                resetError);
        }
    }

    private static double AlignDown(NumericRange range, double value)
    {
        double steps = Math.Floor(((value - range.Minimum) / range.Step) + 1e-9);
        return Math.Clamp(range.Minimum + (steps * range.Step), range.Minimum, range.Maximum);
    }

    private static double AlignUp(NumericRange range, double value)
    {
        double steps = Math.Ceiling(((value - range.Minimum) / range.Step) - 1e-9);
        return Math.Clamp(range.Minimum + (steps * range.Step), range.Minimum, range.Maximum);
    }
}

public interface ITuneScreeningMonitor
{
    Task<TuneScreeningResult> ScreenAsync(
        CapabilityDescriptor capability,
        TunePlan plan,
        TimeSpan duration,
        CancellationToken cancellationToken);
}

public static class TuneCandidateGenerator
{
    public static IReadOnlyList<double> Generate(
        NumericRange adapterRange,
        TuneBounds requestedBounds,
        TuneDirection direction,
        int maximumCandidates)
    {
        if (maximumCandidates is < 2 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates), "Candidate count must be between 2 and 50.");
        }

        double minimum = Math.Max(adapterRange.Minimum, requestedBounds.Minimum);
        double maximum = Math.Min(adapterRange.Maximum, requestedBounds.Maximum);
        double step = Math.Max(adapterRange.Step, requestedBounds.Step);
        if (!double.IsFinite(minimum)
            || !double.IsFinite(maximum)
            || !double.IsFinite(step)
            || minimum > maximum
            || step <= 0)
        {
            throw new ArgumentException("Tuning bounds are invalid.", nameof(requestedBounds));
        }

        List<double> all = [];
        for (double value = minimum; value <= maximum + (step * 1e-6); value += step)
        {
            all.Add(Math.Min(maximum, value));
            if (all.Count > 10_000)
            {
                throw new ArgumentException("Tuning bounds contain too many candidates.", nameof(requestedBounds));
            }
        }

        if (all.Count == 0 || Math.Abs(all[^1] - maximum) > 1e-6)
        {
            all.Add(maximum);
        }

        IReadOnlyList<double> sampled;
        if (all.Count <= maximumCandidates)
        {
            sampled = all;
        }
        else
        {
            sampled = Enumerable.Range(0, maximumCandidates)
                .Select(index => all[(int)Math.Round(index * (all.Count - 1d) / (maximumCandidates - 1d))])
                .Distinct()
                .ToArray();
        }

        return direction == TuneDirection.Minimize
            ? sampled.OrderByDescending(value => value).ToArray()
            : sampled.OrderBy(value => value).ToArray();
    }
}

/// <summary>
/// Pure helpers for the refined GPU Auto-OC search: locate the stability edge
/// precisely between the last stable and first failing coarse candidate, then
/// back off by a safety margin so the shipped result carries headroom instead
/// of sitting on the edge. This is how a careful overclocker (and OC Scanner)
/// works: climb until it breaks, find the exact break point, then step back.
/// No voltage is involved at any layer — only clock-offset values move.
/// </summary>
public static class GpuAutoOcSearch
{
    /// <summary>
    /// Evenly-spaced values strictly between the last stable value and the
    /// first failing one, ordered from stable toward failing so the caller can
    /// climb and stop at the first that fails. Empty when count &lt; 1 or the
    /// two bounds are equal.
    /// </summary>
    public static IReadOnlyList<double> FineCandidates(double lastStable, double firstFail, int count)
    {
        if (count < 1 || Math.Abs(firstFail - lastStable) <= double.Epsilon)
        {
            return [];
        }

        List<double> candidates = [];
        for (int index = 1; index <= count; index++)
        {
            double fraction = (double)index / (count + 1);
            candidates.Add(lastStable + ((firstFail - lastStable) * fraction));
        }

        return candidates;
    }

    /// <summary>
    /// Backs the best stable value off by <paramref name="margin"/> toward the
    /// safe side of the search: downward when maximizing (a lower clock is
    /// safer), upward when minimizing. Clamped to the search range.
    /// </summary>
    public static double ApplyMargin(double best, double margin, TuneDirection direction, double minimum, double maximum)
    {
        double backedOff = direction == TuneDirection.Maximize ? best - Math.Abs(margin) : best + Math.Abs(margin);
        return Math.Clamp(backedOff, minimum, maximum);
    }

    /// <summary>Snaps a value onto the control's step grid, clamped to the range.</summary>
    public static double SnapToStep(double value, double minimum, double maximum, double step)
    {
        if (step <= 0 || !double.IsFinite(step))
        {
            return Math.Clamp(value, minimum, maximum);
        }

        double snapped = minimum + (Math.Round((value - minimum) / step) * step);
        return Math.Clamp(snapped, minimum, maximum);
    }

    /// <summary>
    /// Refinement candidates sit right at the stability edge, so they earn a
    /// longer screen than the fast coarse climb — doubled, capped at the 5-minute
    /// per-candidate limit. A zero coarse time (fast tests) stays zero.
    /// </summary>
    public static TimeSpan RefinementScreeningTime(TimeSpan coarseCandidateTime)
    {
        if (coarseCandidateTime <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        TimeSpan doubled = coarseCandidateTime * 2;
        TimeSpan cap = TimeSpan.FromMinutes(5);
        return doubled > cap ? cap : doubled;
    }

    /// <summary>
    /// True when a passing candidate's peak temperature has reached within the
    /// thermal-headroom band of the ceiling — the practical thermal edge, where
    /// climbing further would trade away cooling headroom. Ignored when headroom
    /// is zero or no peak temperature was recorded.
    /// </summary>
    public static bool ReachedThermalHeadroom(double? peakTemperatureCelsius, double ceilingCelsius, double headroomCelsius)
    {
        return headroomCelsius > 0
            && peakTemperatureCelsius is double peak
            && double.IsFinite(peak)
            && peak >= ceilingCelsius - headroomCelsius;
    }
}

public static class HardwareTuneEngine
{
    public static async Task<TuneResult> RunAsync(
        StartTuneRequest request,
        CapabilityDescriptor capability,
        IHardwareAdapter adapter,
        ITuneScreeningMonitor monitor,
        Action<double, string>? reportProgress,
        CancellationToken cancellationToken,
        bool retainSelectedOnSuccess = false)
    {
        HardwareOperationEligibility eligibility = HardwareOperationEligibilityEvaluator.ForTuning(
            capability,
            request.Plan,
            request.ConfirmExperimental,
            request.ConfirmDevice);
        if (!eligibility.Eligible)
        {
            throw new InvalidOperationException(eligibility.Reason);
        }

        TuneBounds bounds = request.Plan.Bounds[capability.Id];
        double[] generatedCandidates = TuneCandidateGenerator.Generate(
            capability.Range!,
            bounds,
            request.Direction,
            request.MaximumCandidates).ToArray();
        TimeSpan candidateTime = request.CandidateScreeningTime ?? TimeSpan.FromSeconds(30);
        if (candidateTime < TimeSpan.Zero || candidateTime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Candidate screening time must be between 0 and 5 minutes.");
        }

        ProfileAction initialAction = CreateTuneAction(capability, generatedCandidates[0], request.Plan.Id);
        PreparedAction original = await adapter.PrepareAsync(initialAction, cancellationToken).ConfigureAwait(false);
        double[] candidates = original.PreviousValue?.Numeric is double previous && double.IsFinite(previous)
            ? generatedCandidates.Where(value => request.Direction == TuneDirection.Minimize
                    ? value <= previous + 1e-6
                    : value >= previous - 1e-6)
                .ToArray()
            : generatedCandidates;
        bool operationSucceeded = false;
        bool retainedForCompositeScreening = false;
        try
        {
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException("The current control value is outside the requested bounds in the selected tuning direction.");
            }

            // Effective search range for the refinement and safety-margin math.
            // The fine snap uses the DRIVER step (the hardware's finest valid
            // grid), not the coarse plan step, so refined candidates can land
            // between the coarse ones instead of snapping back onto them.
            double effectiveMin = Math.Max(capability.Range!.Minimum, bounds.Minimum);
            double effectiveMax = Math.Min(capability.Range!.Maximum, bounds.Maximum);
            double effectiveStep = capability.Range!.Step;

            List<TuneCandidateResult> results = [];
            double? selected = null;
            double? firstFailingValue = null;
            bool stoppedForThermalHeadroom = false;
            for (int index = 0; index < candidates.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double candidate = candidates[index];
                reportProgress?.Invoke(
                    5 + (45d * index / Math.Max(1, candidates.Length)),
                    $"Testing candidate {candidate:0.###} {capability.Unit}".TrimEnd());
                CandidateOutcome outcome = await TestCandidateAsync(
                    adapter, monitor, original, capability, request.Plan, candidate, candidateTime, cancellationToken).ConfigureAwait(false);
                results.Add(outcome.Result);
                if (!outcome.Passed)
                {
                    firstFailingValue = candidate;
                    break;
                }

                selected = candidate;

                // Thermal-headroom stop: this candidate is stable but already
                // near the temperature ceiling, so keep it and stop climbing —
                // going higher would trade away cooling headroom, not add it.
                if (GpuAutoOcSearch.ReachedThermalHeadroom(
                    outcome.Result.Screening.MaximumTemperatureCelsius,
                    request.Plan.TemperatureCeilingCelsius,
                    request.ThermalHeadroomCelsius))
                {
                    stoppedForThermalHeadroom = true;
                    break;
                }
            }

            if (selected is null)
            {
                operationSucceeded = true;
                return new TuneResult(
                    capability.Id,
                    "No candidate passed screening",
                    null,
                    results,
                    null);
            }

            // Refinement: bisect the gap between the last stable candidate and
            // the first failing one to locate the true stability edge, so the
            // coarse step size no longer caps how close to the limit we get.
            // Skipped when the climb already stopped for thermal headroom.
            if (request.RefinementCandidates > 0 && !stoppedForThermalHeadroom && firstFailingValue is double firstFail)
            {
                IReadOnlyList<double> fine = GpuAutoOcSearch.FineCandidates(
                    selected.Value, firstFail, request.RefinementCandidates);
                TimeSpan refinementTime = GpuAutoOcSearch.RefinementScreeningTime(candidateTime);
                for (int index = 0; index < fine.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double candidate = GpuAutoOcSearch.SnapToStep(fine[index], effectiveMin, effectiveMax, effectiveStep);
                    if (candidate <= selected.Value + 1e-6 && request.Direction == TuneDirection.Maximize)
                    {
                        continue; // snapped back onto an already-passed value
                    }

                    reportProgress?.Invoke(
                        50 + (10d * index / Math.Max(1, fine.Count)),
                        $"Refining near the stability edge: {candidate:0.###} {capability.Unit}".TrimEnd());
                    CandidateOutcome outcome = await TestCandidateAsync(
                        adapter, monitor, original, capability, request.Plan, candidate, refinementTime, cancellationToken).ConfigureAwait(false);
                    results.Add(outcome.Result);
                    if (!outcome.Passed)
                    {
                        break;
                    }

                    selected = candidate;
                    if (GpuAutoOcSearch.ReachedThermalHeadroom(
                        outcome.Result.Screening.MaximumTemperatureCelsius,
                        request.Plan.TemperatureCeilingCelsius,
                        request.ThermalHeadroomCelsius))
                    {
                        break;
                    }
                }
            }

            // Safety margin: back off from the edge so the shipped result has
            // headroom. The final long screening then runs on the backed-off value.
            double shipValue = selected.Value;
            string marginNote = string.Empty;
            if (request.SafetyMargin > 0)
            {
                double backedOff = GpuAutoOcSearch.SnapToStep(
                    GpuAutoOcSearch.ApplyMargin(selected.Value, request.SafetyMargin, request.Direction, effectiveMin, effectiveMax),
                    effectiveMin,
                    effectiveMax,
                    effectiveStep);
                if (Math.Abs(backedOff - selected.Value) > 1e-6)
                {
                    marginNote = $" A {Math.Abs(selected.Value - backedOff):0.#} {capability.Unit} stability margin was applied below the observed limit of {selected.Value:0.#} {capability.Unit}.".Replace("  ", " ");
                    shipValue = backedOff;
                }
            }

            reportProgress?.Invoke(65, $"Running final {request.Plan.ScreeningDuration.TotalMinutes:0.#}-minute screening.");
            PreparedAction finalCandidate = WithNumericValue(original, shipValue);
            await adapter.ApplyAsync(finalCandidate, cancellationToken).ConfigureAwait(false);
            ActionVerification finalVerification = await adapter.VerifyAsync(finalCandidate, cancellationToken).ConfigureAwait(false);
            if (!finalVerification.Success)
            {
                TuneScreeningResult failedReadBack = new(false, finalVerification.Message, null, null, null);
                results.Add(new TuneCandidateResult(shipValue, false, finalVerification.Message, failedReadBack));
                operationSucceeded = true;
                return new TuneResult(capability.Id, "Final read-back failed", null, results, null);
            }

            TuneScreeningResult finalScreening = await monitor.ScreenAsync(
                capability,
                request.Plan,
                request.Plan.ScreeningDuration,
                cancellationToken).ConfigureAwait(false);
            results.Add(new TuneCandidateResult(shipValue, finalScreening.Passed, finalScreening.Message, finalScreening));
            if (!finalScreening.Passed)
            {
                operationSucceeded = true;
                return new TuneResult(capability.Id, "Final screening rejected the candidate", null, results, null);
            }

            ProfileV1 generated = CreateGeneratedProfile(request, capability, shipValue, marginNote);
            string label = request.Plan.ScreeningDuration >= TimeSpan.FromMinutes(10)
                ? "Passed 10-minute screening"
                : "Passed test screening";
            reportProgress?.Invoke(95, "Restoring the prior control state and saving the generated profile.");
            operationSucceeded = true;
            retainedForCompositeScreening = retainSelectedOnSuccess;
            return new TuneResult(capability.Id, label, shipValue, results, generated);
        }
        finally
        {
            if (!retainedForCompositeScreening)
            {
                await RestoreOriginalAsync(capability, original, adapter, operationSucceeded).ConfigureAwait(false);
                reportProgress?.Invoke(100, "Tuning finished; the prior control state was restored.");
            }
            else
            {
                reportProgress?.Invoke(100, "Candidate retained temporarily for composite screening.");
            }
        }
    }

    private readonly record struct CandidateOutcome(bool Passed, TuneCandidateResult Result);

    /// <summary>
    /// Applies one candidate, verifies read-back, and screens it for the
    /// per-candidate duration. A read-back failure counts as a failing
    /// candidate (never crashes the search).
    /// </summary>
    private static async Task<CandidateOutcome> TestCandidateAsync(
        IHardwareAdapter adapter,
        ITuneScreeningMonitor monitor,
        PreparedAction original,
        CapabilityDescriptor capability,
        TunePlan plan,
        double candidate,
        TimeSpan candidateTime,
        CancellationToken cancellationToken)
    {
        PreparedAction prepared = WithNumericValue(original, candidate);
        await adapter.ApplyAsync(prepared, cancellationToken).ConfigureAwait(false);
        ActionVerification verification = await adapter.VerifyAsync(prepared, cancellationToken).ConfigureAwait(false);
        if (!verification.Success)
        {
            TuneScreeningResult failedReadBack = new(false, verification.Message, null, null, null);
            return new CandidateOutcome(false, new TuneCandidateResult(candidate, false, verification.Message, failedReadBack));
        }

        TuneScreeningResult screening = await monitor.ScreenAsync(capability, plan, candidateTime, cancellationToken).ConfigureAwait(false);
        return new CandidateOutcome(screening.Passed, new TuneCandidateResult(candidate, screening.Passed, screening.Message, screening));
    }

    private static ProfileAction CreateTuneAction(CapabilityDescriptor capability, double value, string planId) => new(
        $"tune:{planId}:{capability.Id}",
        capability.AdapterId,
        capability.Id,
        ControlValue.FromNumeric(value),
        Required: true,
        Order: 0);

    private static PreparedAction WithNumericValue(PreparedAction original, double value) => original with
    {
        Action = original.Action with { Value = ControlValue.FromNumeric(value) }
    };

    private static ProfileV1 CreateGeneratedProfile(
        StartTuneRequest request,
        CapabilityDescriptor capability,
        double value,
        string marginNote = "")
    {
        string id = $"tuned-{request.Plan.Objective.ToString().ToLowerInvariant()}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        bool experimental = capability.State == CapabilityAccessState.Experimental
            || capability.Risk is RiskLevel.Experimental or RiskLevel.Critical;
        return new ProfileV1(
            ProfileV1.CurrentSchemaVersion,
            id,
            $"Tuned {request.Plan.Objective}",
            $"Generated for {capability.Name}. Passed 10-minute screening; provisional evidence only.{marginNote}",
            [CreateTuneAction(capability, value, request.Plan.Id)],
            new SafetyLimits(),
            [],
            IsBuiltIn: false,
            IsExperimental: experimental);
    }

    private static async Task RestoreOriginalAsync(
        CapabilityDescriptor capability,
        PreparedAction original,
        IHardwareAdapter adapter,
        bool operationSucceeded)
    {
        try
        {
            await adapter.RollbackAsync(original, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackError)
        {
            try
            {
                await adapter.ResetToDefaultAsync(capability.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception resetError)
            {
                throw new HardwareOperationRecoveryException(
                    $"Rollback and firmware/default reset both failed: {rollbackError.Message}; {resetError.Message}",
                    new AggregateException(rollbackError, resetError));
            }

            string prefix = operationSucceeded ? "Tuning completed, but" : "Tuning failed and";
            throw new HardwareOperationRecoveryException(
                $"{prefix} the previous control state could not be restored. Firmware/default control was restored instead.",
                rollbackError);
        }
    }
}
