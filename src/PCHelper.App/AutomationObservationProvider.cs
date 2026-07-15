using System.Diagnostics;
using System.Runtime.InteropServices;
using PCHelper.Contracts;

namespace PCHelper.App;

internal static class AutomationObservationProvider
{
    public static AutomationObservation Capture(bool sessionLocked, string? hotkey)
    {
        HashSet<string> processes = new(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    processes.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        string? foreground = null;
        IntPtr window = GetForegroundWindow();
        if (window != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(window, out uint processId);
            try
            {
                using Process process = Process.GetProcessById(checked((int)processId));
                foreground = process.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
            }
        }

        LastInputInfo input = new() { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        TimeSpan idle = TimeSpan.Zero;
        if (GetLastInputInfo(ref input))
        {
            uint elapsedMilliseconds = unchecked((uint)GetTickCount64()) - input.TickCount;
            idle = TimeSpan.FromMilliseconds(elapsedMilliseconds);
        }

        return new AutomationObservation(
            DateTimeOffset.Now,
            processes,
            foreground,
            sessionLocked,
            idle,
            hotkey);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;

        public uint TickCount;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
