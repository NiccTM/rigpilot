using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs the native Kraken X3 lighting write behind a process boundary so a
/// native HID fault cannot terminate the caller. The child is launched with
/// <c>--set-kraken-rgb RRGGBB|off</c>. Every failure mode — start failure,
/// native crash, timeout, malformed output — maps to a result whose outcome is
/// not <see cref="KrakenLightingOutcome.WriteIssued"/>, so a failed pass can
/// never be reported as an issued write.
/// </summary>
public sealed class ContainedKrakenLighting(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<KrakenLightingResultV1> WriteAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KrakenLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"Kraken lighting process could not be started: {exception.GetType().Name}.");
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
                return KrakenLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Kraken lighting exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return KrakenLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Kraken lighting process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return KrakenLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Kraken lighting failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return KrakenLightingResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Kraken lighting process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static KrakenLightingResultV1 Parse(string standardOutput)
    {
        int start = standardOutput?.IndexOf('{') ?? -1;
        int end = standardOutput?.LastIndexOf('}') ?? -1;
        if (standardOutput is null || start < 0 || end <= start)
        {
            return KrakenLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "Kraken lighting process produced no result payload.");
        }

        KrakenLightingResultV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<KrakenLightingResultV1>(
                standardOutput[start..(end + 1)], JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return KrakenLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "Kraken lighting process produced malformed output.");
        }

        return parsed is null || parsed.SchemaVersion != KrakenLightingResultV1.CurrentSchemaVersion
            ? KrakenLightingResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "Kraken lighting process produced an empty or mismatched result.")
            : parsed;
    }
}
