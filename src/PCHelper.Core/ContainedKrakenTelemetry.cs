using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs the read-only Kraken X3 telemetry pass behind a process boundary so a native
/// HID fault cannot terminate the caller. The child is launched with <c>--read-kraken</c>
/// and performs no HID writes (the Kraken firmware streams status unsolicited). Every
/// failure mode — start failure, native crash, timeout, malformed output — maps to a
/// <see cref="KrakenTelemetryV1"/> whose outcome is not
/// <see cref="KrakenTelemetryOutcome.Succeeded"/>, so no partial read can masquerade
/// as live telemetry.
/// </summary>
public sealed class ContainedKrakenTelemetry(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<KrakenTelemetryV1> ReadAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KrakenTelemetryV1.Unavailable(
                KrakenTelemetryOutcome.Failed,
                $"Kraken telemetry process could not be started: {exception.GetType().Name}.");
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
                return KrakenTelemetryV1.Unavailable(
                    KrakenTelemetryOutcome.Failed,
                    $"Kraken telemetry exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return KrakenTelemetryV1.Unavailable(
                    KrakenTelemetryOutcome.Failed,
                    $"Kraken telemetry process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return KrakenTelemetryV1.Unavailable(
                    KrakenTelemetryOutcome.Failed,
                    $"Kraken telemetry failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return KrakenTelemetryV1.Unavailable(
                    KrakenTelemetryOutcome.Failed,
                    $"Kraken telemetry process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static KrakenTelemetryV1 Parse(string standardOutput)
    {
        string payload = ExtractJsonLine(standardOutput);
        if (payload.Length == 0)
        {
            return KrakenTelemetryV1.Unavailable(
                KrakenTelemetryOutcome.Failed,
                "Kraken telemetry process produced no result payload.");
        }

        KrakenTelemetryV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<KrakenTelemetryV1>(payload, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return KrakenTelemetryV1.Unavailable(
                KrakenTelemetryOutcome.Failed,
                "Kraken telemetry process produced malformed output.");
        }

        if (parsed is null || parsed.SchemaVersion != KrakenTelemetryV1.CurrentSchemaVersion)
        {
            return KrakenTelemetryV1.Unavailable(
                KrakenTelemetryOutcome.Failed,
                "Kraken telemetry process produced an empty or mismatched result.");
        }

        // Readings are authoritative only on a clean success; any other outcome
        // presents empty telemetry so a partial read cannot pose as live data.
        return parsed.Outcome != KrakenTelemetryOutcome.Succeeded
            ? parsed with { LiquidTemperatureCelsius = null, PumpSpeedRpm = null, PumpDutyPercent = null }
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
