using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using PCHelper.Contracts;
using Windows.Graphics.Capture;
using Windows.Management.Deployment;

namespace PCHelper.App;

/// <summary>
/// Read-only external-overlay/capture discovery. The RTSS ABI is not written
/// until an audited exact-version bridge exists; this probe prevents the UI
/// from treating a running RTSS instance as permission to inject or mutate it.
/// </summary>
public static class OverlayBridgeProbe
{
    private static readonly string[] RtssMapNames = ["RTSSSharedMemoryV2", @"Global\RTSSSharedMemoryV2"];

    public static OverlayBridgeStatusV1 Probe()
    {
        RtssBridgeStatusV1 rtss = ProbeRtss();
        bool gameBarInstalled = IsGameBarInstalled(out string gameBarMessage);
        bool captureSupported = IsGraphicsCaptureSupported(out string captureMessage);
        return new OverlayBridgeStatusV1(rtss, gameBarInstalled, captureSupported, gameBarMessage, captureMessage);
    }

    /// <summary>
    /// A side-effect-free capability check for the future WGC recorder. Frame
    /// acquisition is available only after explicit system-picker consent; an
    /// encoder and audio pipeline are intentionally not claimed here.
    /// </summary>
    public static WgcRecordingPreflightV1 ProbeRecordingPreflight()
    {
        bool supported = IsGraphicsCaptureSupported(out string message);
        return new WgcRecordingPreflightV1(
            WgcRecordingPreflightV1.CurrentSchemaVersion,
            supported,
            SystemPickerRequired: true,
            EncoderConfigured: false,
            supported
                ? "Windows Graphics Capture can acquire consented frames. RigPilot still requires a reviewed Media Foundation encoder and audio pipeline before video recording is enabled."
                : message);
    }

    private static RtssBridgeStatusV1 ProbeRtss()
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessesByName("RTSS").OrderBy(item => item.Id).FirstOrDefault();
            string? executable = process is null ? null : TryGetPath(process);
            bool mapAvailable = false;
            foreach (string mapName in RtssMapNames)
            {
                try
                {
                    using MemoryMappedFile map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
                    mapAvailable = true;
                    break;
                }
                catch (FileNotFoundException)
                {
                    // Try the next conventional map name.
                }
                catch (UnauthorizedAccessException)
                {
                    return new RtssBridgeStatusV1(
                        process is not null,
                        false,
                        executable,
                        "RTSS shared memory is present but not readable by the signed-in user. RigPilot will not bypass its access controls.");
                }
            }
            return new RtssBridgeStatusV1(
                process is not null,
                mapAvailable,
                executable,
                mapAvailable
                    ? "RTSS shared memory is visible. RigPilot can validate OSD frames, but direct RTSS writes remain disabled until the installed ABI is audited."
                    : process is not null
                        ? "RTSS is running but its shared-memory endpoint was not found. Start RTSS normally and keep its ownership settings intact."
                        : "RTSS is not running. RigPilot does not bundle or launch RTSS.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new RtssBridgeStatusV1(false, false, null, $"RTSS discovery failed: {exception.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool IsGameBarInstalled(out string message)
    {
        try
        {
            PackageManager manager = new();
            bool found = manager.FindPackagesForUser(string.Empty).Any(package =>
                string.Equals(package.Id.Name, "Microsoft.XboxGamingOverlay", StringComparison.OrdinalIgnoreCase));
            message = found
                ? "Xbox Game Bar is installed. The optional UWP widget may communicate only with the user agent after package-SID pipe ACLs and MSIX signing are configured."
                : "Xbox Game Bar is not installed for this user.";
            return found;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            message = $"Xbox Game Bar discovery failed: {exception.Message}";
            return false;
        }
    }

    private static bool IsGraphicsCaptureSupported(out string message)
    {
        try
        {
            bool supported = GraphicsCaptureSession.IsSupported();
            message = supported
                ? "Windows Graphics Capture is supported. Explicit local PNG snapshots are available now; WGC video, HDR conversion, Media Foundation encoding, and WASAPI audio remain separately gated."
                : "Windows Graphics Capture is unavailable on this Windows build. Explicit local GDI PNG snapshots may still be available; video capture remains unavailable.";
            return supported;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TypeInitializationException)
        {
            message = $"Windows Graphics Capture discovery failed: {exception.Message}";
            return false;
        }
    }

    private static string? TryGetPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception) { return null; }
    }
}
