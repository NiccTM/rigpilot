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
        CancellationToken cancellationToken)
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
                        cancellationToken).ConfigureAwait(false);
                    if (coreValue is null)
                    {
                        message = "No core candidate satisfied the selected objective and safety constraints.";
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
                            cancellationToken).ConfigureAwait(false);
                        if (memoryValue is null)
                        {
                            message = "No memory candidate satisfied the selected objective and safety constraints.";
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
            $"Prior state for {restorationVerifications.Count} requested control{(restorationVerifications.Count == 1 ? " was" : "s were")} restored and read back.");
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
            retainSelectedOnSuccess: true).ConfigureAwait(false);
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
