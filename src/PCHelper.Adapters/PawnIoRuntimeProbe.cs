using System.Management;
using System.Runtime.InteropServices;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only presence evidence for the signed PawnIO driver and its user-mode library.
/// This never loads a PawnIO module, opens an executor handle, or issues any privileged
/// ioctl; the only native call it makes is <c>pawnio_version</c>, which reads a version
/// constant from the user-mode library. It reports whether the rule-compliant SMU/MSR/SMBus
/// transport is installed, functional, and its kernel driver is running. Detection alone
/// never unlocks a write — CPU/SMU tuning stays Blocked.
/// </summary>
public sealed record PawnIoRuntimeStatus(
    bool LibraryPresent,
    bool DriverRunning,
    string? LibraryPath,
    string? LibraryVersion = null)
{
    /// <summary>True only when both the user-mode library is present and the driver runs.</summary>
    public bool Available => LibraryPresent && DriverRunning;

    /// <summary>True when the library additionally answered a version query.</summary>
    public bool Functional => Available && !string.IsNullOrEmpty(LibraryVersion);

    /// <summary>One-line, human-readable evidence suitable for a capability reason string.</summary>
    public string Describe() => (LibraryPresent, DriverRunning) switch
    {
        (true, true) when !string.IsNullOrEmpty(LibraryVersion) =>
            $"the signed PawnIO driver is present and running (library {LibraryVersion})",
        (true, true) => "the signed PawnIO driver is present and running",
        (true, false) => "the signed PawnIO library is installed but its kernel driver is not running",
        (false, _) => "the signed PawnIO runtime is not installed",
    };
}

public static class PawnIoRuntimeProbe
{
    private const string DriverName = "PawnIO";

    /// <summary>
    /// Detects the installed PawnIO runtime. Safe and read-only: file presence, a WMI query
    /// of the driver state, and (when present) a single <c>pawnio_version</c> call. Any
    /// failure degrades to "not detected" rather than throwing.
    /// </summary>
    public static PawnIoRuntimeStatus Detect()
    {
        string? libraryPath = ResolveLibraryPath();
        PawnIoRuntimeStatus status = Evaluate(libraryPath, ReadDriverState());
        // Confirm the library is functional (not just present) with a version query only
        // when the driver is also running, so we never load the DLL on a machine where the
        // transport could not work anyway.
        return status.Available && libraryPath is not null
            ? status with { LibraryVersion = TryReadLibraryVersion(libraryPath) }
            : status;
    }

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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnioVersionDelegate(out uint version);

    /// <summary>
    /// Calls <c>pawnio_version</c> to confirm the user-mode library loads and is functional.
    /// This reads a version constant only — it opens no executor handle, loads no module, and
    /// issues no ioctl. Returns a "major.minor.patch" string, or null on any failure.
    /// </summary>
    private static string? TryReadLibraryVersion(string libraryPath)
    {
        nint handle = 0;
        try
        {
            if (!NativeLibrary.TryLoad(libraryPath, out handle))
            {
                return null;
            }

            if (!NativeLibrary.TryGetExport(handle, "pawnio_version", out nint export))
            {
                return null;
            }

            PawnioVersionDelegate version = Marshal.GetDelegateForFunctionPointer<PawnioVersionDelegate>(export);
            if (version(out uint packed) != 0)
            {
                return null;
            }

            uint major = (packed >> 16) & 0xFF;
            uint minor = (packed >> 8) & 0xFF;
            uint patch = packed & 0xFF;
            return $"{major}.{minor}.{patch}";
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            return null;
        }
        finally
        {
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
            }
        }
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
