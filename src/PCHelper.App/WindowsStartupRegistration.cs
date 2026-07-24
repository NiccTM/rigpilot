using System.IO;
using Microsoft.Win32;

namespace PCHelper.App;

/// <summary>
/// Optional "start RigPilot when I sign in" registration.
///
/// It uses the per-user HKCU Run key, so enabling it needs no administrator rights,
/// touches no machine-wide state, and is independent of the Windows service — the
/// service has its own boot policy and keeps running whether or not the dashboard
/// launches at sign-in. The dashboard is registered with <c>--tray</c> so signing in
/// restores it quietly to the notification area instead of opening a window.
/// </summary>
internal static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RigPilot";
    private const string TrayArgument = "--tray";

    /// <summary>The exact command this build would register, or null when the executable path is unknown.</summary>
    private static string? CurrentCommand => BuildCommand(Environment.ProcessPath);

    /// <summary>False when the executable path cannot be resolved, so the option is disabled rather than broken.</summary>
    public static bool IsSupported => CurrentCommand is not null;

    /// <summary>
    /// Builds the sign-in command only for the official app host. This prevents a
    /// test runner, snapshot host, or <c>dotnet.exe</c> invocation from ever being
    /// persisted as the user's startup program.
    /// </summary>
    internal static string? BuildCommand(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !string.Equals(
                Path.GetFileName(executablePath),
                "PCHelper.App.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return $"\"{Path.GetFullPath(executablePath)}\" {TrayArgument}";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static bool IsRegisteredCommand(string? registered, string? expected) =>
        !string.IsNullOrWhiteSpace(registered)
        && !string.IsNullOrWhiteSpace(expected)
        && string.Equals(registered.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only when the registered command is exactly THIS executable. An entry left
    /// behind by a different install or an older path reads as disabled, so the switch
    /// never claims a sign-in launch that would actually start another build.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return IsRegisteredCommand(
                key?.GetValue(ValueName) as string,
                CurrentCommand);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds or removes the per-user sign-in entry. Failures propagate so the caller can
    /// report them instead of leaving the switch showing a state that was never written.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = CurrentCommand
                ?? throw new InvalidOperationException("The RigPilot executable path is unavailable.");
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The per-user Windows Run key could not be opened.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
