using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Runs read-only HID peripheral enumeration behind a process boundary so a native HidSharp
/// crash cannot terminate the caller. It reuses the generic discovery-process abstraction
/// (<see cref="IControllerDiscoveryProcess"/>) — the child is launched with
/// <c>--discover-hid</c>. Every failure mode (start failure, native crash, timeout, or
/// malformed output) maps to a <see cref="HidInventoryResultV1"/> whose outcome is not
/// <see cref="HidInventoryOutcome.Succeeded"/> and whose device list is empty, so no partial
/// enumeration can leak through as usable inventory.
/// </summary>
public sealed class ContainedHidInventory(
    Func<IControllerDiscoveryProcess> processFactory,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<HidInventoryResultV1> DiscoverAsync(CancellationToken cancellationToken)
    {
        IControllerDiscoveryProcess process;
        try
        {
            process = processFactory();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HidInventoryResultV1.Failed(
                $"HID inventory process could not be started: {exception.GetType().Name}.");
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
                return HidInventoryResultV1.Failed(
                    $"HID inventory exceeded {_timeout.TotalSeconds:0}s and was terminated.");
            }
            catch (ControllerDiscoveryProcessException exception)
            {
                return HidInventoryResultV1.Failed(
                    $"HID inventory process exited abnormally: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return HidInventoryResultV1.Failed(
                    $"HID inventory failed to complete: {exception.GetType().Name}.");
            }

            if (exit.ExitCode != 0)
            {
                return HidInventoryResultV1.Failed(
                    $"HID inventory process exited with code {exit.ExitCode}.");
            }

            return Parse(exit.StandardOutput);
        }
    }

    private static HidInventoryResultV1 Parse(string standardOutput)
    {
        string payload = ExtractJsonLine(standardOutput);
        if (payload.Length == 0)
        {
            return HidInventoryResultV1.Failed("HID inventory process produced no result payload.");
        }

        HidInventoryResultV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<HidInventoryResultV1>(payload, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return HidInventoryResultV1.Failed("HID inventory process produced malformed output.");
        }

        if (parsed is null)
        {
            return HidInventoryResultV1.Failed("HID inventory process produced an empty result.");
        }

        // Devices are authoritative only on a clean success; any other outcome presents an
        // empty list so a partial enumeration cannot leak through as usable inventory.
        return parsed.Outcome != HidInventoryOutcome.Succeeded
            ? parsed with { Devices = [] }
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
