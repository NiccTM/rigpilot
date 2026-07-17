using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs the native Kraken X3 pump-duty write behind a process boundary so a
/// native HID fault cannot terminate the caller. The child is launched with
/// <c>--set-kraken-pump &lt;duty&gt;</c>. Every failure mode — start failure,
/// native crash, timeout, malformed output — maps to a result whose outcome is
/// neither <see cref="KrakenPumpOutcome.ReadBackVerified"/> nor
/// <see cref="KrakenPumpOutcome.WriteIssued"/>, so a failed pass can never be
/// reported as an issued write.
/// </summary>
public sealed class ContainedKrakenPump(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<KrakenPumpResultV1> WriteAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KrakenPumpResultV1.Unavailable(
                KrakenPumpOutcome.Failed,
                $"Kraken pump process could not be started: {exception.GetType().Name}.");
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
                return KrakenPumpResultV1.Unavailable(
                    KrakenPumpOutcome.Failed,
                    $"Kraken pump write exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return KrakenPumpResultV1.Unavailable(
                    KrakenPumpOutcome.Failed,
                    $"Kraken pump process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return KrakenPumpResultV1.Unavailable(
                    KrakenPumpOutcome.Failed,
                    $"Kraken pump write failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return KrakenPumpResultV1.Unavailable(
                    KrakenPumpOutcome.Failed,
                    $"Kraken pump process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static KrakenPumpResultV1 Parse(string standardOutput)
    {
        int start = standardOutput?.IndexOf('{') ?? -1;
        int end = standardOutput?.LastIndexOf('}') ?? -1;
        if (standardOutput is null || start < 0 || end <= start)
        {
            return KrakenPumpResultV1.Unavailable(
                KrakenPumpOutcome.Failed,
                "Kraken pump process produced no result payload.");
        }

        KrakenPumpResultV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<KrakenPumpResultV1>(
                standardOutput[start..(end + 1)], JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return KrakenPumpResultV1.Unavailable(
                KrakenPumpOutcome.Failed,
                "Kraken pump process produced malformed output.");
        }

        return parsed is null || parsed.SchemaVersion != KrakenPumpResultV1.CurrentSchemaVersion
            ? KrakenPumpResultV1.Unavailable(
                KrakenPumpOutcome.Failed,
                "Kraken pump process produced an empty or mismatched result.")
            : parsed;
    }
}
