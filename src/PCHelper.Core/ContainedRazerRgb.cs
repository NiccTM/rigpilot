using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs the native Razer USB lighting write behind a process boundary so a
/// native HID fault cannot terminate the caller. The child is launched with
/// <c>--set-razer-usb-rgb RRGGBB|off</c>. Every failure mode maps to a result
/// whose outcome is not WriteIssued, so a failed pass can never be reported as
/// an accepted write.
/// </summary>
public sealed class ContainedRazerRgb(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<RazerRgbResultV1> WriteAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                $"Razer lighting process could not be started: {exception.GetType().Name}.");
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
                return RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Razer lighting exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Razer lighting process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Razer lighting failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return RazerRgbResultV1.Unavailable(
                    KrakenLightingOutcome.Failed,
                    $"Razer lighting process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static RazerRgbResultV1 Parse(string standardOutput)
    {
        int start = standardOutput?.IndexOf('{') ?? -1;
        int end = standardOutput?.LastIndexOf('}') ?? -1;
        if (standardOutput is null || start < 0 || end <= start)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "Razer lighting process produced no result payload.");
        }

        RazerRgbResultV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RazerRgbResultV1>(
                standardOutput[start..(end + 1)], JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "Razer lighting process produced malformed output.");
        }

        return parsed is null || parsed.SchemaVersion != RazerRgbResultV1.CurrentSchemaVersion
            ? RazerRgbResultV1.Unavailable(
                KrakenLightingOutcome.Failed,
                "Razer lighting process produced an empty or mismatched result.")
            : parsed;
    }
}
