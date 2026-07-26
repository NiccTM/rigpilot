using System.Diagnostics;
using PCHelper.Core;

namespace PCHelper.App;

/// <summary>
/// The production <see cref="IInputSynthesisGuard"/>: it enumerates running processes and
/// asks <see cref="AntiCheatDetection"/> whether any is a known anti-cheat client or kernel
/// service. If enumeration fails for any reason it fails safe — refusing playback — because
/// the whole point of the guard is to protect the user, and a guard that silently allows on
/// error would defeat it.
/// </summary>
internal sealed class WindowsAntiCheatGuard : IInputSynthesisGuard
{
    public string? BlockingProtection()
    {
        try
        {
            string[] names = Process.GetProcesses()
                .Select(process =>
                {
                    try
                    {
                        return process.ProcessName;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                })
                .Where(name => name.Length > 0)
                .ToArray();
            return AntiCheatDetection.FindActiveProtection(names);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "an unidentified process set (RigPilot could not confirm no anti-cheat is running)";
        }
    }
}
