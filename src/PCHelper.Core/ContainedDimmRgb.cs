using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs the DIMM RGB SMBus write behind a process boundary so a native PawnIO
/// fault cannot terminate the caller. The child is launched with
/// <c>--set-smbus-rgb RRGGBB|off</c>. Every failure mode maps to a result whose
/// outcome is not WriteIssued, so a failed pass can never be reported as an
/// issued write.
/// </summary>
public sealed class ContainedDimmRgb(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<DimmRgbResultV1> WriteAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DimmRgbResultV1.Unavailable(
                "Failed",
                $"DIMM RGB process could not be started: {exception.GetType().Name}.");
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
                return DimmRgbResultV1.Unavailable(
                    "Failed",
                    $"DIMM RGB exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return DimmRgbResultV1.Unavailable(
                    "Failed",
                    $"DIMM RGB process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return DimmRgbResultV1.Unavailable(
                    "Failed",
                    $"DIMM RGB failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return DimmRgbResultV1.Unavailable(
                    "Failed",
                    $"DIMM RGB process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static DimmRgbResultV1 Parse(string standardOutput)
    {
        int start = standardOutput?.IndexOf('{') ?? -1;
        int end = standardOutput?.LastIndexOf('}') ?? -1;
        if (standardOutput is null || start < 0 || end <= start)
        {
            return DimmRgbResultV1.Unavailable(
                "Failed",
                "DIMM RGB process produced no result payload.");
        }

        DimmRgbResultV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DimmRgbResultV1>(
                standardOutput[start..(end + 1)], JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return DimmRgbResultV1.Unavailable(
                "Failed",
                "DIMM RGB process produced malformed output.");
        }

        return parsed is null || parsed.SchemaVersion != DimmRgbResultV1.CurrentSchemaVersion
            ? DimmRgbResultV1.Unavailable(
                "Failed",
                "DIMM RGB process produced an empty or mismatched result.")
            : parsed;
    }
}
