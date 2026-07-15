using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// A single isolated controller-discovery attempt. The implementation launches a
/// disposable child process; this abstraction lets tests drive the containment
/// logic against crashing, hanging, and malformed children without real hardware.
/// </summary>
public interface IControllerDiscoveryProcess : IAsyncDisposable
{
    /// <summary>
    /// Waits for the child to finish within <paramref name="timeout"/>. Returns the
    /// child's standard output and exit code on a clean exit. Throws
    /// <see cref="TimeoutException"/> if the child does not exit in time (the
    /// implementation must kill it). Throws <see cref="ControllerDiscoveryProcessException"/>
    /// if the child could not be started or exited abnormally.
    /// </summary>
    Task<ControllerDiscoveryProcessExit> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record ControllerDiscoveryProcessExit(int ExitCode, string StandardOutput);

/// <summary>Raised when the discovery child cannot start or exits abnormally (native crash).</summary>
public sealed class ControllerDiscoveryProcessException(string message, int? exitCode = null)
    : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
}

/// <summary>
/// Runs USB/AIO controller discovery behind a process boundary so a native
/// HidSharp crash cannot terminate the caller. Every failure mode — start
/// failure, native crash, timeout, or malformed output — is mapped to a
/// <see cref="ControllerDiscoveryResultV1"/> whose outcome is not
/// <see cref="ControllerDiscoveryOutcome.Succeeded"/>, so USB/AIO controllers
/// stay unsupported unless a clean, well-formed inventory was returned.
/// </summary>
public sealed class ContainedControllerDiscovery(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<ControllerDiscoveryResultV1> DiscoverAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ControllerDiscoveryResultV1.Contained(
                ControllerDiscoveryOutcome.Crashed,
                $"Controller-discovery process could not be started: {exception.GetType().Name}.");
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
                return ControllerDiscoveryResultV1.Contained(
                    ControllerDiscoveryOutcome.TimedOut,
                    $"Controller discovery exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return ControllerDiscoveryResultV1.Contained(
                    ControllerDiscoveryOutcome.Crashed,
                    $"Controller-discovery process exited abnormally: {exception.Message}",
                    exception.ExitCode);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return ControllerDiscoveryResultV1.Contained(
                    ControllerDiscoveryOutcome.Crashed,
                    $"Controller discovery failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return ControllerDiscoveryResultV1.Contained(
                    ControllerDiscoveryOutcome.Crashed,
                    $"Controller-discovery process exited with code {exit.ExitCode}.",
                    exit.ExitCode);
            }

            return Parse(exit.StandardOutput, exit.ExitCode);
        }
    }

    private static ControllerDiscoveryResultV1 Parse(string standardOutput, int exitCode)
    {
        string payload = ExtractJsonLine(standardOutput);
        if (payload.Length == 0)
        {
            return ControllerDiscoveryResultV1.Contained(
                ControllerDiscoveryOutcome.EnumerationFailed,
                "Controller-discovery process produced no result payload.",
                exitCode);
        }

        ControllerDiscoveryResultV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ControllerDiscoveryResultV1>(payload, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return ControllerDiscoveryResultV1.Contained(
                ControllerDiscoveryOutcome.EnumerationFailed,
                "Controller-discovery process produced malformed output.",
                exitCode);
        }

        if (parsed is null)
        {
            return ControllerDiscoveryResultV1.Contained(
                ControllerDiscoveryOutcome.EnumerationFailed,
                "Controller-discovery process produced an empty result.",
                exitCode);
        }

        // A child may only ever report controllers on a genuine success. Any other
        // outcome must present an empty list so a partial enumeration cannot leak
        // through as usable inventory.
        if (parsed.Outcome != ControllerDiscoveryOutcome.Succeeded)
        {
            return parsed with { Controllers = [] };
        }

        return parsed;
    }

    private static string ExtractJsonLine(string standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return string.Empty;
        }

        // The child emits its result as a single JSON object, but it may be
        // pretty-printed across many lines and preceded by banner text. Take the
        // span from the first opening brace to the last closing brace; a malformed
        // payload is rejected later by the JSON deserializer.
        int start = standardOutput.IndexOf('{');
        int end = standardOutput.LastIndexOf('}');
        return start >= 0 && end > start
            ? standardOutput[start..(end + 1)]
            : string.Empty;
    }
}
