using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs the native AURA addressable USB lighting write behind a process
/// boundary so a native HID fault cannot terminate the caller. The child is
/// launched with <c>--set-aura-rgb RRGGBB|off</c>. Every failure mode maps to a
/// result whose outcome is not <see cref="KrakenLightingOutcome.WriteIssued"/>,
/// so a failed pass can never be reported as an issued write.
/// </summary>
public sealed class ContainedAuraLighting(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<AuraLightingResultV1> WriteAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"AURA lighting process could not be started: {exception.GetType().Name}.");
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
                return AuraLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"AURA lighting exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return AuraLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"AURA lighting process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return AuraLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"AURA lighting failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return AuraLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"AURA lighting process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static AuraLightingResultV1 Parse(string standardOutput)
    {
        int start = standardOutput?.IndexOf('{') ?? -1;
        int end = standardOutput?.LastIndexOf('}') ?? -1;
        if (standardOutput is null || start < 0 || end <= start)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "AURA lighting process produced no result payload.");
        }

        AuraLightingResultV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AuraLightingResultV1>(
                standardOutput[start..(end + 1)], JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "AURA lighting process produced malformed output.");
        }

        return parsed is null || parsed.SchemaVersion != AuraLightingResultV1.CurrentSchemaVersion
            ? AuraLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "AURA lighting process produced an empty or mismatched result.")
            : parsed;
    }
}
