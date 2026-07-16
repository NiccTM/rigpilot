using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs the read-only Ryzen SMU feasibility pass behind a process boundary so a
/// native PawnIO fault cannot terminate the caller. The child is launched with
/// <c>--read-ryzen-smu</c> and references no tuning or register-write module
/// function. Every failure mode maps to a non-Succeeded
/// <see cref="RyzenSmuFeasibilityV1"/>, and non-Succeeded results are scrubbed
/// of numeric limits so partial reads cannot pose as qualification evidence.
/// </summary>
public sealed class ContainedRyzenSmuFeasibility(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<RyzenSmuFeasibilityV1> ReadAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.Failed,
                $"Ryzen SMU feasibility process could not be started: {exception.GetType().Name}.");
        }

        await using (process.ConfigureAwait(false))
        {
            ControllerDiscoveryProcessExit exit;
            try
            {
                exit = await process.WaitForExitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return RyzenSmuFeasibilityV1.Unavailable(
                    RyzenSmuFeasibilityOutcome.Failed,
                    $"Ryzen SMU feasibility exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return RyzenSmuFeasibilityV1.Unavailable(
                    RyzenSmuFeasibilityOutcome.Failed,
                    $"Ryzen SMU feasibility process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return RyzenSmuFeasibilityV1.Unavailable(
                    RyzenSmuFeasibilityOutcome.Failed,
                    $"Ryzen SMU feasibility failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return RyzenSmuFeasibilityV1.Unavailable(
                    RyzenSmuFeasibilityOutcome.Failed,
                    $"Ryzen SMU feasibility process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static RyzenSmuFeasibilityV1 Parse(string standardOutput)
    {
        string payload = ExtractJsonLine(standardOutput);
        if (payload.Length == 0)
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.Failed,
                "Ryzen SMU feasibility process produced no result payload.");
        }

        RyzenSmuFeasibilityV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RyzenSmuFeasibilityV1>(payload, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.Failed,
                "Ryzen SMU feasibility process produced malformed output.");
        }

        if (parsed is null || parsed.SchemaVersion != RyzenSmuFeasibilityV1.CurrentSchemaVersion)
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.Failed,
                "Ryzen SMU feasibility process produced an empty or mismatched result.");
        }

        // Limits are qualification evidence only on a clean success.
        return parsed.Outcome != RyzenSmuFeasibilityOutcome.Succeeded
            ? parsed with
            {
                PptLimitWatts = null, PptValueWatts = null,
                TdcLimitAmperes = null, TdcValueAmperes = null,
                ThmLimitCelsius = null, ThmValueCelsius = null,
                EdcLimitAmperes = null, EdcValueAmperes = null,
            }
            : parsed;
    }

    private static string ExtractJsonLine(string standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return string.Empty;
        }

        int start = standardOutput.IndexOf('{');
        int end = standardOutput.LastIndexOf('}');
        return start >= 0 && end > start
            ? standardOutput[start..(end + 1)]
            : string.Empty;
    }
}
