using System.Management;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only presence evidence for the signed PawnIO driver and its user-mode library.
/// This never loads a PawnIO module, opens a handle, or issues any ioctl; it only reports
/// whether the rule-compliant SMU/MSR/SMBus transport is installed and its kernel driver is
/// running. Detection alone never unlocks a write — CPU/SMU tuning stays Blocked.
/// </summary>
public sealed record PawnIoRuntimeStatus(bool LibraryPresent, bool DriverRunning, string? LibraryPath)
{
    /// <summary>True only when both the user-mode library is present and the driver runs.</summary>
    public bool Available => LibraryPresent && DriverRunning;

    /// <summary>One-line, human-readable evidence suitable for a capability reason string.</summary>
    public string Describe() => (LibraryPresent, DriverRunning) switch
    {
        (true, true) => "the signed PawnIO driver is present and running",
        (true, false) => "the signed PawnIO library is installed but its kernel driver is not running",
        (false, _) => "the signed PawnIO runtime is not installed",
    };
}

public static class PawnIoRuntimeProbe
{
    private const string DriverName = "PawnIO";

    /// <summary>
    /// Detects the installed PawnIO runtime. Safe and read-only: file presence plus a WMI
    /// query of the driver state. Any failure degrades to "not detected" rather than throwing.
    /// </summary>
    public static PawnIoRuntimeStatus Detect() => Evaluate(ResolveLibraryPath(), ReadDriverState());

    /// <summary>
    /// Pure evaluation over already-resolved inputs, split out for testing without touching
    /// the real filesystem or WMI.
    /// </summary>
    public static PawnIoRuntimeStatus Evaluate(string? libraryPath, string? driverState) => new(
        LibraryPresent: !string.IsNullOrEmpty(libraryPath),
        DriverRunning: string.Equals(driverState, "Running", StringComparison.OrdinalIgnoreCase),
        LibraryPath: libraryPath);

    private static string? ResolveLibraryPath()
    {
        foreach (Environment.SpecialFolder root in (ReadOnlySpan<Environment.SpecialFolder>)
                 [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86])
        {
            string baseDirectory = Environment.GetFolderPath(root);
            if (string.IsNullOrEmpty(baseDirectory))
            {
                continue;
            }

            string candidate = Path.Combine(baseDirectory, "PawnIO", "PawnIOLib.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ReadDriverState()
    {
        try
        {
            string? state = null;
            using ManagementObjectSearcher searcher = new(
                $"SELECT State FROM Win32_SystemDriver WHERE Name = '{DriverName}'");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                using (row)
                {
                    state ??= row.Properties["State"]?.Value?.ToString();
                }
            }

            return state;
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
