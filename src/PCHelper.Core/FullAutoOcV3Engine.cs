using System.Runtime.ExceptionServices;
using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed record AutoOcTuneStage(
    StartTuneRequest Request,
    CapabilityDescriptor Capability,
    IHardwareAdapter Adapter);

/// <summary>
/// Objective-aware Auto OC operation. It measures three stock-state baselines,
/// rejects noisy baselines, screens every candidate with measured workload
/// throughput, optionally includes a bounded power-limit control, runs one
/// combined final screen, then restores and reads back every prior value.
/// </summary>
public static class FullAutoOcV3Engine
{
    public static async Task<AutoOcResultV3> RunAsync(
        string deviceId,
        AutoOcObjectiveConstraintsV3 constraints,
        HardwareFingerprintV1 fingerprint,
        AutoOcTuneStage core,
        AutoOcTuneStage memory,
        AutoOcTuneStage? power,
        Func<AutoOcWorkloadMode, ITuneScreeningMonitor> monitorFactory,
        IAutoOcWorkloadController workload,
        Action<double, string>? reportProgress,
        CancellationToken cancellationToken,
        IAutoOcCandidateJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(monitorFactory);
        ArgumentNullException.ThrowIfNull(workload);
        ValidateStage(deviceId, core, "core");
        ValidateStage(deviceId, memory, "memory");
        if (power is not null)
        {
            ValidateStage(deviceId, power, "power limit");
        }
        string? constraintError = AutoOcV3Policy.Validate(constraints);
        if (constraintError is not null)
        {
            throw new ArgumentException(constraintError, nameof(constraints));
        }

        TimeSpan baselineDuration = constraints.BaselineSampleDuration ?? TimeSpan.FromSeconds(10);
        TimeSpan candidateDuration = constraints.CandidateScreeningDuration ?? TimeSpan.FromSeconds(30);
        TimeSpan finalDuration = constraints.FinalScreeningDuration ?? TimeSpan.FromMinutes(20);
        ValidateDuration(baselineDuration, TimeSpan.FromMinutes(2), "baseline sample");
        ValidateDuration(candidateDuration, TimeSpan.FromMinutes(10), "candidate screen");
        ValidateDuration(finalDuration, TimeSpan.FromMinutes(30), "final screen");

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        AutoOcTuneStage[] stages = power is null ? [core, memory] : [core, memory, power];
        Dictionary<string, PreparedAction> originals = new(StringComparer.Ordinal);
        foreach (AutoOcTuneStage stage in stages)
        {
            originals[stage.Capability.Id] = await stage.Adapter.PrepareAsync(
                CreateAction(stage.Capability, stage.Capability.Range?.Default ?? 0, "capture"),
                cancellationToken).ConfigureAwait(false);
        }

        List<AutoOcMeasurementV3> baselines = [];
        List<AutoOcCandidateScoreV3> candidateScores = [];
        TuneResult? coreResult = null;
        TuneResult? memoryResult = null;
        TuneResult? powerResult = null;
        TuneScreeningResult? combined = null;
        AutoOcMeasurementV3? finalMeasurement = null;
        ProfileV2? generated = null;
        double? baselineVariation = null;
        double? coreValue = null;
        double? memoryValue = null;
        double? powerValue = null;
        bool objectiveVerified = false;
        string message = "Auto OC V3 did not start.";
        Exception? operationError = null;
        Exception? workloadStopError = null;
        List<HardwareStateVerification> restorationVerifications = [];
        List<Exception> restorationErrors = [];
        // Which control each restoration failure belongs to. Without this the
        // durable record can only say "3 controls failed" and not which ones.
        List<string> restorationFailureDetails = [];

        try
        {
            await RequireModeAsync(workload, AutoOcWorkloadMode.Combined, cancellationToken).ConfigureAwait(false);
            TunePlan baselinePlan = WithConstraints(core.Request.Plan, constraints, baselineDuration);

            // Warm the card to a measured plateau before sampling anything. A GPU
            // boosts highest when cold and settles as it heats, so a baseline
            // taken during the transient is noise: a cold first sample produced
            // 4.03% variation against a 3% limit while the two warm samples
            // agreed to 0.13%. A fixed warmup duration is not enough — how long
            // the transient lasts depends on the fan curve and the ambient, and
            // a 45-second warmup measured on one thermal configuration still
            // left baselines climbing (86.4 → 88.0 °C, 4.89%) on another. So warm
            // in short windows and stop when consecutive windows agree in peak
            // temperature; the cap bounds the cost when equilibrium is far away,
            // and hitting it is reported so a rejected run says why.
            double? previousPeakCelsius = null;
            bool plateauReached = false;
            for (int window = 1; window <= AutoOcV3Policy.MaximumWarmupWindows; window++)
            {
                reportProgress?.Invoke(0, $"Warming the GPU to a thermal plateau (window {window} of at most {AutoOcV3Policy.MaximumWarmupWindows}).");
                TuneScreeningResult warm = await monitorFactory(AutoOcWorkloadMode.Combined)
                    .ScreenAsync(core.Capability, baselinePlan, AutoOcV3Policy.WarmupWindowDuration, cancellationToken)
                    .ConfigureAwait(false);
                if (AutoOcV3Policy.HasReachedThermalPlateau(previousPeakCelsius, warm.MaximumTemperatureCelsius))
                {
                    plateauReached = true;
                    break;
                }

                previousPeakCelsius = warm.MaximumTemperatureCelsius;
            }

            if (!plateauReached)
            {
                reportProgress?.Invoke(
                    0,
                    "The GPU was still heating when the warmup budget ran out; baselines may vary more than the limit allows.");
            }

            for (int index = 0; index < AutoOcV3Policy.RequiredBaselineSamples; index++)
            {
                reportProgress?.Invoke(index * 5, $"Baseline sample {index + 1} of {AutoOcV3Policy.RequiredBaselineSamples}.");
                TuneScreeningResult sample = await monitorFactory(AutoOcWorkloadMode.Combined)
                    .ScreenAsync(core.Capability, baselinePlan, baselineDuration, cancellationToken)
                    .ConfigureAwait(false);
                baselines.Add(AutoOcV3Policy.Measurement($"Baseline {index + 1}", baselineDuration, sample));
                if (!sample.Passed)
                {
                    message = $"Baseline sample {index + 1} was rejected: {sample.Message}";
                    break;
                }
            }

            if (baselines.Count == AutoOcV3Policy.RequiredBaselineSamples
                && AutoOcV3Policy.TryMeasureBaselineVariation(baselines, out double variation, out string variationMessage))
            {
                baselineVariation = variation;
                if (variation > constraints.MaximumBaselineVariationPercent)
                {
                    message = $"{variationMessage} The {constraints.MaximumBaselineVariationPercent:0.#}% limit was exceeded; no tuning candidate was applied.";
                }
                else
                {
                    double baselineThroughput = baselines.Average(sample => sample.ThroughputScore!.Value);
                    (coreResult, coreValue) = await RunStageAsync(
                        "Core",
                        core,
                        AutoOcWorkloadMode.Core,
                        constraints,
                        candidateDuration,
                        baselineThroughput,
                        monitorFactory,
                        workload,
                        15,
                        22,
                        reportProgress,
                        candidateScores,
                        journal,
                        cancellationToken).ConfigureAwait(false);
                    if (coreValue is null)
                    {
                        message = AutoOcV3Policy.DescribeStageFailure("core", coreResult);
                    }
                    else
                    {
                        (memoryResult, memoryValue) = await RunStageAsync(
                            "Memory",
                            memory,
                            AutoOcWorkloadMode.Memory,
                            constraints,
                            candidateDuration,
                            baselineThroughput,
                            monitorFactory,
                            workload,
                            37,
                            22,
                            reportProgress,
                            candidateScores,
                            journal,
                            cancellationToken).ConfigureAwait(false);
                        if (memoryValue is null)
                        {
                            message = AutoOcV3Policy.DescribeStageFailure("memory", memoryResult);
                        }
                        else
                        {
                            if (power is not null)
                            {
                                (powerResult, powerValue) = await RunStageAsync(
                                    "Power",
                                    power,
                                    AutoOcWorkloadMode.Combined,
                                    constraints,
                                    candidateDuration,
                                    baselineThroughput,
                                    monitorFactory,
                                    workload,
                                    59,
                                    16,
                                    reportProgress,
                                    candidateScores,
                                    journal,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            if (power is not null && powerValue is null)
                            {
                                message = "The requested power-limit capability produced no candidate that satisfied the selected objective.";
                            }
                            else
                            {
                                await RequireModeAsync(workload, AutoOcWorkloadMode.Combined, cancellationToken).ConfigureAwait(false);
                                reportProgress?.Invoke(76, $"Running the final {finalDuration.TotalMinutes:0.#}-minute combined screen.");
                                TunePlan finalPlan = WithConstraints(core.Request.Plan, constraints, finalDuration);
                                combined = await monitorFactory(AutoOcWorkloadMode.Combined)
                                    .ScreenAsync(core.Capability, finalPlan, finalDuration, cancellationToken)
                                    .ConfigureAwait(false);
                                finalMeasurement = AutoOcV3Policy.Measurement("Combined final", finalDuration, combined);
                                string? finalError = AutoOcV3Policy.ValidateFinalMeasurement(
                                    finalMeasurement,
                                    candidateScores,
                                    constraints,
                                    baselineThroughput);
                                // Transient validation: the steady screen has passed, so the
                                // candidate is stable at ONE sustained operating point. Games
                                // are not sustained — cycle load and idle to force the boost
                                // and voltage transitions a constant load never exercises, and
                                // reject the candidate if any cycle fails. This is what stops a
                                // result that "passes the test" from crashing in a game.
                                if (finalError is null)
                                {
                                    finalError = await RunTransientValidationAsync(
                                        core,
                                        constraints,
                                        monitorFactory,
                                        workload,
                                        reportProgress,
                                        cancellationToken).ConfigureAwait(false);
                                }

                                // High-boost validation: everything so far ran at the stock
                                // power limit, where the card is power-bound and sits in a
                                // forgiving low-boost/high-voltage state. Pull the limit down
                                // and it holds higher boost bins at lower voltage — where a
                                // shifted V/F curve actually breaks, and the state a game
                                // produces whenever it is not loading the GPU flat out.
                                if (finalError is null && power is not null)
                                {
                                    finalError = await RunLowPowerValidationAsync(
                                        core,
                                        power,
                                        powerValue,
                                        constraints,
                                        monitorFactory,
                                        workload,
                                        reportProgress,
                                        cancellationToken).ConfigureAwait(false);
                                }

                                if (finalError is null)
                                {
                                    generated = CreateProfile(
                                        deviceId,
                                        constraints.Objective,
                                        fingerprint,
                                        core.Capability,
                                        coreValue.Value,
                                        memory.Capability,
                                        memoryValue.Value,
                                        power?.Capability,
                                        powerValue,
                                        finalDuration);
                                    objectiveVerified = true;
                                    message = constraints.RequestPresentMonValidation
                                        ? "The synthetic safety gate passed. Optional PresentMon validation remains supplementary and cannot weaken this result."
                                        : "The objective and synthetic safety gate passed; the generated profile remains provisional.";
                                }
                                else
                                {
                                    message = finalError;
                                }
                            }
                        }
                    }
                }
            }
            else if (baselines.Count == AutoOcV3Policy.RequiredBaselineSamples)
            {
                message = "Baseline throughput could not be measured consistently; no tuning candidate was applied.";
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
        }
        finally
        {
            try
            {
                await workload.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                workloadStopError = exception;
            }

            foreach (AutoOcTuneStage stage in stages.Reverse())
            {
                try
                {
                    HardwareStateVerification verification = await RestoreAndVerifyAsync(
                        stage,
                        originals[stage.Capability.Id]).ConfigureAwait(false);
                    restorationVerifications.Add(verification);
                }
                catch (Exception exception)
                {
                    restorationErrors.Add(exception);
                    restorationFailureDetails.Add(
                        $"{stage.Capability.Name} ({stage.Capability.Id}): {exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        if (restorationErrors.Count > 0)
        {
            // Name the controls and the exact reasons in the message itself.
            // The inner AggregateException is retained for callers, but only
            // this string reaches the durable operation record — and the
            // adapter trace that would otherwise hold the detail is a bounded
            // in-memory buffer that the recommended "restart the service"
            // remedy flushes. Losing the cause of a RecoveryRequired at the
            // moment the operator follows the instructions is the worst
            // possible time to lose it.
            throw new HardwareOperationRecoveryException(
                $"Auto OC V3 attempted every hardware restore but could not prove {restorationErrors.Count} control state(s). "
                + string.Join(" | ", restorationFailureDetails),
                new AggregateException(restorationErrors));
        }
        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }
        if (workloadStopError is not null)
        {
            throw new InvalidOperationException(
                $"Hardware state was restored, but the workload host did not acknowledge stop: {workloadStopError.Message}",
                workloadStopError);
        }

        RestorationProofV1 restoration = new(
            PriorStateRestored: true,
            HardwareStateKnown: true,
            DateTimeOffset.UtcNow,
            restorationVerifications,
            $"Prior state for {restorationVerifications.Count} requested control{(restorationVerifications.Count == 1 ? "" : "s")} was restored and read back.");
        return new AutoOcResultV3(
            AutoOcResultV3.CurrentSchemaVersion,
            deviceId,
            coreResult,
            memoryResult,
            powerResult,
            combined,
            coreValue,
            memoryValue,
            powerValue,
            baselines,
            finalMeasurement,
            candidateScores,
            baselineVariation,
            fingerprint,
            objectiveVerified ? AutoOcValidationState.Provisional : AutoOcValidationState.Rejected,
            objectiveVerified,
            restoration,
            objectiveVerified ? generated : null,
            startedAt,
            DateTimeOffset.UtcNow,
            $"{message} {restoration.Message}");
    }

    /// <summary>
    /// Cycles the workload between load and idle against the already-selected candidate, so
    /// the card repeatedly climbs into its high boost bins (low voltage, high frequency) and
    /// falls back out of them. A sustained screen pins the GPU to a single power-limited
    /// operating point and never visits those bins, which is precisely why a candidate can
    /// pass a synthetic test and still fail in a game. Returns null when every cycle holds,
    /// or a description of the first failing cycle.
    /// </summary>
    private static async Task<string?> RunTransientValidationAsync(
        AutoOcTuneStage core,
        AutoOcObjectiveConstraintsV3 constraints,
        Func<AutoOcWorkloadMode, ITuneScreeningMonitor> monitorFactory,
        IAutoOcWorkloadController workload,
        Action<double, string>? reportProgress,
        CancellationToken cancellationToken)
    {
        TunePlan transientPlan = WithConstraints(
            core.Request.Plan,
            constraints,
            AutoOcV3Policy.TransientLoadDuration);
        for (int cycle = 1; cycle <= AutoOcV3Policy.TransientValidationCycles; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke(
                92 + (6d * cycle / AutoOcV3Policy.TransientValidationCycles),
                $"Transient validation: load cycle {cycle} of {AutoOcV3Policy.TransientValidationCycles}.");

            // Drop to idle first so each burst is a genuine transition rather than a
            // continuation of the load that came before it.
            await RequireModeAsync(workload, AutoOcWorkloadMode.Stopped, cancellationToken).ConfigureAwait(false);
            await Task.Delay(AutoOcV3Policy.TransientIdleDuration, cancellationToken).ConfigureAwait(false);

            await RequireModeAsync(workload, AutoOcWorkloadMode.Combined, cancellationToken).ConfigureAwait(false);
            TuneScreeningResult burst = await monitorFactory(AutoOcWorkloadMode.Combined)
                .ScreenAsync(core.Capability, transientPlan, AutoOcV3Policy.TransientLoadDuration, cancellationToken)
                .ConfigureAwait(false);
            if (!burst.Passed)
            {
                return AutoOcV3Policy.DescribeTransientFailure(
                    cycle,
                    AutoOcV3Policy.TransientValidationCycles,
                    burst.Message);
            }
        }

        return null;
    }

    private static async Task<(TuneResult Result, double? SelectedValue)> RunStageAsync(
        string stageName,
        AutoOcTuneStage stage,
        AutoOcWorkloadMode mode,
        AutoOcObjectiveConstraintsV3 constraints,
        TimeSpan candidateDuration,
        double baselineThroughput,
        Func<AutoOcWorkloadMode, ITuneScreeningMonitor> monitorFactory,
        IAutoOcWorkloadController workload,
        double progressStart,
        double progressSpan,
        Action<double, string>? reportProgress,
        List<AutoOcCandidateScoreV3> allScores,
        IAutoOcCandidateJournal? journal,
        CancellationToken cancellationToken)
    {
        await RequireModeAsync(workload, mode, cancellationToken).ConfigureAwait(false);
        StartTuneRequest request = stage.Request with
        {
            Plan = WithConstraints(stage.Request.Plan, constraints, candidateDuration),
            CandidateScreeningTime = candidateDuration
        };
        TuneResult result = await HardwareTuneEngine.RunAsync(
            request,
            stage.Capability,
            stage.Adapter,
            monitorFactory(mode),
            (progress, text) => reportProgress?.Invoke(
                progressStart + (progress * progressSpan / 100),
                $"{stageName}: {text}"),
            cancellationToken,
            retainSelectedOnSuccess: true,
            journal: journal).ConfigureAwait(false);
        IReadOnlyList<AutoOcCandidateScoreV3> scores = AutoOcV3Policy.ScoreCandidates(
            stageName,
            result,
            constraints.Objective);
        allScores.AddRange(scores);
        AutoOcCandidateScoreV3? selected = AutoOcV3Policy.SelectBestCandidate(scores, constraints, baselineThroughput);
        if (selected is null)
        {
            return (result, null);
        }

        if (result.SelectedValue != selected.Value)
        {
            await ApplyAndVerifyCandidateAsync(stage, selected.Value, cancellationToken).ConfigureAwait(false);
        }

        return (result with
        {
            SelectedValue = selected.Value,
            StatusLabel = $"{constraints.Objective} objective candidate selected from measured results"
        }, selected.Value);
    }

    /// <summary>
    /// Re-screens the selected candidate with the power limit reduced, so the GPU is forced
    /// into the high-boost / low-voltage states a stock-power screen never reaches. The power
    /// limit is always put back — on success, on failure, and on error — because leaving the
    /// card power-starved would be a worse outcome than any verdict this phase produces.
    /// Returns null when the candidate holds, or a description of the failure.
    /// </summary>
    private static async Task<string?> RunLowPowerValidationAsync(
        AutoOcTuneStage core,
        AutoOcTuneStage power,
        double? selectedPowerValue,
        AutoOcObjectiveConstraintsV3 constraints,
        Func<AutoOcWorkloadMode, ITuneScreeningMonitor> monitorFactory,
        IAutoOcWorkloadController workload,
        Action<double, string>? reportProgress,
        CancellationToken cancellationToken)
    {
        NumericRange? range = power.Capability.Range;
        if (range?.Default is not double stock)
        {
            // Without a controller-reported stock value there is nothing to reduce from, so
            // skip rather than guess at a power figure.
            return null;
        }

        double restoreValue = selectedPowerValue ?? stock;
        double reduced = AutoOcV3Policy.LowPowerValidationTarget(stock, range.Minimum);
        if (reduced >= restoreValue - 1e-6)
        {
            // Already at or below the validation target; the screen would prove nothing.
            return null;
        }

        reportProgress?.Invoke(99, $"High-boost validation at {reduced / 1000:0} W.");
        try
        {
            await ApplyAndVerifyCandidateAsync(power, reduced, cancellationToken).ConfigureAwait(false);
            await RequireModeAsync(workload, AutoOcWorkloadMode.Combined, cancellationToken).ConfigureAwait(false);
            TuneScreeningResult screen = await monitorFactory(AutoOcWorkloadMode.Combined)
                .ScreenAsync(
                    core.Capability,
                    WithConstraints(core.Request.Plan, constraints, AutoOcV3Policy.LowPowerValidationDuration),
                    AutoOcV3Policy.LowPowerValidationDuration,
                    cancellationToken)
                .ConfigureAwait(false);
            return screen.Passed ? null : AutoOcV3Policy.DescribeLowPowerFailure(reduced, screen.Message);
        }
        finally
        {
            // Restore unconditionally, and with no cancellation token: a cancelled run must
            // still leave the power limit where it was rather than power-starved.
            await ApplyAndVerifyCandidateAsync(power, restoreValue, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ApplyAndVerifyCandidateAsync(
        AutoOcTuneStage stage,
        double value,
        CancellationToken cancellationToken)
    {
        ProfileAction action = CreateAction(stage.Capability, value, "objective-selected");
        PreparedAction prepared = await stage.Adapter.PrepareAsync(action, cancellationToken).ConfigureAwait(false);
        try
        {
            await stage.Adapter.ApplyAsync(prepared, cancellationToken).ConfigureAwait(false);
            ActionVerification verification = await stage.Adapter.VerifyAsync(prepared, cancellationToken).ConfigureAwait(false);
            if (!verification.Success)
            {
                throw new ProfileVerificationException(verification.Message);
            }
        }
        catch
        {
            await stage.Adapter.RollbackAsync(prepared, CancellationToken.None).ConfigureAwait(false);
            if (stage.Adapter is not IHardwareStateVerifier verifier
                || !(await verifier.VerifyRollbackStateAsync(prepared, CancellationToken.None).ConfigureAwait(false)).Success)
            {
                throw new HardwareOperationRecoveryException(
                    $"The {stage.Capability.Name} objective-selection write failed and its rollback could not be proved.",
                    new InvalidOperationException("Objective-selection rollback read-back failed."));
            }
            throw;
        }
    }

    private static Task<HardwareStateVerification> RestoreAndVerifyAsync(
        AutoOcTuneStage stage,
        PreparedAction original) => HardwareRestoreVerification.RestoreAndVerifyAsync(
            stage.Capability, original, stage.Adapter);

    private static async Task RequireModeAsync(
        IAutoOcWorkloadController workload,
        AutoOcWorkloadMode mode,
        CancellationToken cancellationToken)
    {
        WorkloadHostStatusV1 status = await workload.SetModeAsync(mode, cancellationToken).ConfigureAwait(false);
        if (!status.Authenticated
            || !status.Ready
            || !status.Running
            || status.Mode != mode
            || status.MatchingHardwareAdapterCount != 1
            || DateTimeOffset.UtcNow - status.HeartbeatAt > TimeSpan.FromSeconds(3))
        {
            throw new InvalidOperationException(status.Error ?? $"The workload host did not enter {mode} on one exact hardware adapter.");
        }
    }

    private static TunePlan WithConstraints(
        TunePlan plan,
        AutoOcObjectiveConstraintsV3 constraints,
        TimeSpan duration) => plan with
    {
        Objective = constraints.Objective,
        ScreeningDuration = duration,
        TemperatureCeilingCelsius = constraints.TemperatureCeilingCelsius,
        PowerCeilingWatts = constraints.PowerCeilingWatts
    };

    private static ProfileV2 CreateProfile(
        string deviceId,
        TuningObjective objective,
        HardwareFingerprintV1 fingerprint,
        CapabilityDescriptor core,
        double coreValue,
        CapabilityDescriptor memory,
        double memoryValue,
        CapabilityDescriptor? power,
        double? powerValue,
        TimeSpan finalDuration)
    {
        List<ProfileAction> actions =
        [
            CreateAction(core, coreValue, "core-result") with { Order = 0 },
            CreateAction(memory, memoryValue, "memory-result") with { Order = 1 }
        ];
        if (power is not null && powerValue is double selectedPower)
        {
            actions.Add(CreateAction(power, selectedPower, "power-result") with { Order = 2 });
        }

        return new ProfileV2(
            ProfileV2.CurrentSchemaVersion,
            $"auto-oc-v3-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            $"{objective} GPU Auto OC",
            $"Provisional exact-device result for {deviceId}; fingerprint {fingerprint.FingerprintSha256[..12]}, three-sample baseline, and {finalDuration.TotalMinutes:0.#}-minute combined screen. Driver or VBIOS changes invalidate it.",
            actions,
            new SafetyLimits(),
            CoolingGraphId: null,
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: [],
            AutomationReferences: [],
            IsBuiltIn: false,
            IsExperimental: true);
    }

    private static ProfileAction CreateAction(CapabilityDescriptor capability, double value, string suffix) => new(
        $"auto-oc-v3:{suffix}:{capability.Id}",
        capability.AdapterId,
        capability.Id,
        ControlValue.FromNumeric(value),
        Required: true,
        Order: 0);

    private static void ValidateStage(string deviceId, AutoOcTuneStage stage, string label)
    {
        if (!string.Equals(stage.Capability.DeviceId, deviceId, StringComparison.Ordinal)
            || stage.Capability.Range is null
            || stage.Capability.ValueKind != ControlValueKind.Numeric)
        {
            throw new ArgumentException($"Auto OC V3 requires a bounded numeric {label} control on the exact target device.");
        }
    }

    private static void ValidateDuration(TimeSpan duration, TimeSpan maximum, string label)
    {
        if (duration < TimeSpan.Zero || duration > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), $"The {label} duration must be between zero and {maximum}.");
        }
    }
}
