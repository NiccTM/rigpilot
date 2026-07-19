using PCHelper.Contracts;
using System.Runtime.ExceptionServices;

namespace PCHelper.Core;

public interface IAutoOcWorkloadController
{
    Task<WorkloadHostStatusV1> SetModeAsync(AutoOcWorkloadMode mode, CancellationToken cancellationToken);

    Task<WorkloadHostStatusV1> GetStatusAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs core and memory searches as one operation. The core result remains
/// applied only long enough to tune memory and run the combined screen; both
/// controls are then rolled back and read back before a result is returned.
/// </summary>
public static class FullAutoOcEngine
{
    public static async Task<AutoOcResultV2> RunAsync(
        string deviceId,
        StartTuneRequest coreRequest,
        CapabilityDescriptor coreCapability,
        IHardwareAdapter coreAdapter,
        StartTuneRequest memoryRequest,
        CapabilityDescriptor memoryCapability,
        IHardwareAdapter memoryAdapter,
        TimeSpan combinedScreeningTime,
        Func<AutoOcWorkloadMode, ITuneScreeningMonitor> monitorFactory,
        IAutoOcWorkloadController workload,
        Action<double, string>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(monitorFactory);
        if (!string.Equals(coreCapability.DeviceId, deviceId, StringComparison.Ordinal)
            || !string.Equals(memoryCapability.DeviceId, deviceId, StringComparison.Ordinal)
            || combinedScreeningTime < TimeSpan.Zero
            || combinedScreeningTime > TimeSpan.FromHours(1))
        {
            throw new ArgumentException("Auto OC requires two bounded controls on the exact same device and a bounded combined screen.");
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        PreparedAction coreOriginal = await coreAdapter.PrepareAsync(
            CreateAction(coreCapability, coreCapability.Range?.Default ?? 0, "capture-core"),
            cancellationToken).ConfigureAwait(false);
        PreparedAction memoryOriginal = await memoryAdapter.PrepareAsync(
            CreateAction(memoryCapability, memoryCapability.Range?.Default ?? 0, "capture-memory"),
            cancellationToken).ConfigureAwait(false);

        TuneResult? coreResult = null;
        TuneResult? memoryResult = null;
        TuneScreeningResult? combined = null;
        ProfileV2? generated = null;
        bool allVerified = false;
        bool priorStateRestored = false;
        bool hardwareStateKnown = false;
        string message = "Auto OC did not start.";
        Exception? workloadStopError = null;
        Exception? operationError = null;
        Exception? restorationError = null;
        try
        {
            await RequireModeAsync(workload, AutoOcWorkloadMode.Core, cancellationToken).ConfigureAwait(false);
            coreResult = await HardwareTuneEngine.RunAsync(
                coreRequest,
                coreCapability,
                coreAdapter,
                monitorFactory(AutoOcWorkloadMode.Core),
                (progress, text) => reportProgress?.Invoke(progress * 0.4, $"Core: {text}"),
                cancellationToken,
                retainSelectedOnSuccess: true).ConfigureAwait(false);
            if (coreResult.SelectedValue is not double coreValue)
            {
                message = $"Core search stopped: {coreResult.StatusLabel}.";
            }
            else
            {
                await RequireModeAsync(workload, AutoOcWorkloadMode.Memory, cancellationToken).ConfigureAwait(false);
                memoryResult = await HardwareTuneEngine.RunAsync(
                    memoryRequest,
                    memoryCapability,
                    memoryAdapter,
                    monitorFactory(AutoOcWorkloadMode.Memory),
                    (progress, text) => reportProgress?.Invoke(40 + (progress * 0.4), $"Memory: {text}"),
                    cancellationToken,
                    retainSelectedOnSuccess: true).ConfigureAwait(false);
                if (memoryResult.SelectedValue is not double memoryValue)
                {
                    message = $"Core passed, but memory search stopped: {memoryResult.StatusLabel}.";
                }
                else
                {
                    await RequireModeAsync(workload, AutoOcWorkloadMode.Combined, cancellationToken).ConfigureAwait(false);
                    reportProgress?.Invoke(82, $"Running final {combinedScreeningTime.TotalMinutes:0.#}-minute combined screen.");
                    TunePlan combinedPlan = coreRequest.Plan with { ScreeningDuration = combinedScreeningTime };
                    combined = await monitorFactory(AutoOcWorkloadMode.Combined)
                        .ScreenAsync(coreCapability, combinedPlan, combinedScreeningTime, cancellationToken)
                        .ConfigureAwait(false);
                    if (combined.Passed)
                    {
                        generated = CreateCombinedProfile(
                            deviceId,
                            coreCapability,
                            coreValue,
                            memoryCapability,
                            memoryValue,
                            combinedScreeningTime);
                        allVerified = true;
                        message = "Core and memory passed the combined screen; the result is provisional and ready for explicit application.";
                    }
                    else
                    {
                        message = $"Combined screening rejected the pair: {combined.Message}";
                    }
                }
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
        }
        finally
        {
            List<Exception> restorationErrors = [];
            try
            {
                await workload.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                workloadStopError = exception;
            }

            List<string> restorationFailureDetails = [];
            try
            {
                await RestoreAndVerifyAsync(memoryCapability, memoryOriginal, memoryAdapter).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                restorationErrors.Add(exception);
                restorationFailureDetails.Add(
                    $"{memoryCapability.Name} ({memoryCapability.Id}): {exception.GetType().Name}: {exception.Message}");
            }

            try
            {
                await RestoreAndVerifyAsync(coreCapability, coreOriginal, coreAdapter).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                restorationErrors.Add(exception);
                restorationFailureDetails.Add(
                    $"{coreCapability.Name} ({coreCapability.Id}): {exception.GetType().Name}: {exception.Message}");
            }

            if (restorationErrors.Count > 0)
            {
                // The per-control reasons must live in the message: this string
                // is what survives into the durable operation record, whereas
                // the adapter trace is bounded in memory and is flushed by the
                // service restart the failure message tells the operator to do.
                restorationError = new HardwareOperationRecoveryException(
                    $"Auto OC attempted both hardware restores, but could not prove {restorationErrors.Count} control state(s). "
                    + string.Join(" | ", restorationFailureDetails),
                    new AggregateException(restorationErrors));
            }
            else
            {
                priorStateRestored = true;
                hardwareStateKnown = true;
                reportProgress?.Invoke(100, "Auto OC finished; the prior core and memory state was read back.");
            }
        }

        if (restorationError is not null)
        {
            ExceptionDispatchInfo.Capture(restorationError).Throw();
        }

        if (operationError is not null)
        {
            if (workloadStopError is not null)
            {
                throw new InvalidOperationException(
                    "Auto OC failed and its workload host did not acknowledge stop.",
                    new AggregateException(operationError, workloadStopError));
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        if (workloadStopError is not null)
        {
            throw new InvalidOperationException(
                $"Hardware was restored, but the workload host did not acknowledge stop: {workloadStopError.Message}",
                workloadStopError);
        }

        return new AutoOcResultV2(
            AutoOcResultV2.CurrentSchemaVersion,
            deviceId,
            coreResult,
            memoryResult,
            combined,
            coreResult?.SelectedValue,
            memoryResult?.SelectedValue,
            allVerified,
            priorStateRestored,
            hardwareStateKnown,
            generated,
            startedAt,
            DateTimeOffset.UtcNow,
            $"{message} Prior hardware state was restored and verified.");
    }

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

    private static async Task RestoreAndVerifyAsync(
        CapabilityDescriptor capability,
        PreparedAction original,
        IHardwareAdapter adapter)
    {
        try
        {
            await HardwareRestoreVerification
                .RestoreAndVerifyAsync(capability, original, adapter)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new HardwareOperationRecoveryException(
                $"Auto OC could not prove restoration of '{capability.Name}': {exception.Message}",
                exception);
        }
    }

    private static ProfileAction CreateAction(
        CapabilityDescriptor capability,
        double value,
        string suffix) => new(
            $"auto-oc:{suffix}:{capability.Id}",
            capability.AdapterId,
            capability.Id,
            ControlValue.FromNumeric(value),
            Required: true,
            Order: 0);

    private static ProfileV2 CreateCombinedProfile(
        string deviceId,
        CapabilityDescriptor core,
        double coreValue,
        CapabilityDescriptor memory,
        double memoryValue,
        TimeSpan combinedScreeningTime)
    {
        string id = $"auto-oc-combined-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        return new ProfileV2(
            ProfileV2.CurrentSchemaVersion,
            id,
            "Screened GPU Auto OC",
            $"Provisional result for {deviceId}; passed core, memory, and a {combinedScreeningTime.TotalMinutes:0.#}-minute combined workload screen on this PC.",
            [
                CreateAction(core, coreValue, "core-result"),
                CreateAction(memory, memoryValue, "memory-result") with { Order = 1 }
            ],
            new SafetyLimits(),
            CoolingGraphId: null,
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: [],
            AutomationReferences: [],
            IsBuiltIn: false,
            IsExperimental: true);
    }
}
