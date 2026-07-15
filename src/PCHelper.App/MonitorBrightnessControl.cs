using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Forms = System.Windows.Forms;
using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>
/// Same-user display brightness bridge. External monitors use the Windows
/// Monitor Configuration API (DDC/CI); Windows-managed internal panels use
/// the WMI brightness provider. Neither path is owned by the service because
/// Session 0 does not reliably enumerate the interactive desktop's displays.
/// </summary>
public interface IMonitorBrightnessBackend
{
    IReadOnlyList<MonitorBrightnessDeviceV1> Discover();

    Task<MonitorBrightnessApplyResultV1> SetBrightnessAsync(
        SetMonitorBrightnessRequestV1 request,
        CancellationToken cancellationToken);
}

public sealed class WindowsMonitorBrightnessBackend : IMonitorBrightnessBackend
{
    private const uint McCapsBrightness = 0x00000002;
    private const uint MaximumPhysicalMonitorsPerDisplay = 16;

    public IReadOnlyList<MonitorBrightnessDeviceV1> Discover()
    {
        List<MonitorBrightnessDeviceV1> monitors = [];
        try
        {
            DiscoverDdcCi(monitors);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            // On a non-standard Windows image, leave physical displays visible
            // through Screen enumeration below with an explicit no-DDC reason.
            AddLogicalScreenFallbacks(monitors, "Windows could not load the DDC/CI monitor configuration API.");
        }

        try
        {
            foreach (WmiBrightnessCandidate candidate in DiscoverWmiCandidates())
            {
                monitors.Add(new MonitorBrightnessDeviceV1(
                    MonitorBrightnessDeviceV1.CurrentSchemaVersion,
                    BuildWmiId(candidate.InstanceName),
                    candidate.DisplayName,
                    null,
                    MonitorBrightnessTransport.Wmi,
                    candidate.CanWrite ? CapabilityAccessState.Experimental : CapabilityAccessState.ReadOnly,
                    0,
                    100,
                    candidate.CurrentPercent,
                    candidate.CanWrite
                        ? "Windows exposes this panel through WMI. A write is confirmed by immediate read-back."
                        : "Windows reports this panel's brightness, but no active WMI brightness method is available."));
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            // WMI brightness is optional and normally applies only to internal
            // panels. DDC/CI and logical fallback entries remain useful.
        }

        return monitors
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.DisplayDeviceName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<MonitorBrightnessApplyResultV1> SetBrightnessAsync(
        SetMonitorBrightnessRequestV1 request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SchemaVersion != SetMonitorBrightnessRequestV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(request.MonitorId)
            || request.BrightnessPercent is < 0 or > 100)
        {
            throw new InvalidDataException("The monitor brightness request is invalid.");
        }

        return Task.FromResult(request.MonitorId.StartsWith("ddcci:", StringComparison.OrdinalIgnoreCase)
            ? SetDdcCiBrightness(request.MonitorId, request.BrightnessPercent, cancellationToken)
            : request.MonitorId.StartsWith("wmi:", StringComparison.OrdinalIgnoreCase)
                ? SetWmiBrightness(request.MonitorId, request.BrightnessPercent, cancellationToken)
                : Failed(request.MonitorId, request.BrightnessPercent, "The monitor identifier is not owned by a supported local brightness transport."));
    }

    private static void DiscoverDdcCi(List<MonitorBrightnessDeviceV1> monitors)
    {
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            if (!TryGetDisplayInfo(monitor, out DisplayMonitorInfo display))
            {
                return true;
            }

            if (!TryOpenPhysicalMonitors(monitor, out PhysicalMonitor[] physicalMonitors, out string diagnostic))
            {
                monitors.Add(LogicalFallback(display, diagnostic));
                return true;
            }

            try
            {
                for (int index = 0; index < physicalMonitors.Length; index++)
                {
                    monitors.Add(DescribeDdcCiMonitor(display, physicalMonitors[index], index));
                }
            }
            finally
            {
                _ = DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
            }

            return true;
        };
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        AddLogicalScreenFallbacks(monitors, "Windows did not expose this display through DDC/CI.");
    }

    private static MonitorBrightnessApplyResultV1 SetDdcCiBrightness(
        string monitorId,
        int targetPercent,
        CancellationToken cancellationToken)
    {
        MonitorBrightnessApplyResultV1? result = null;
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetDisplayInfo(monitor, out DisplayMonitorInfo display)
                || !TryOpenPhysicalMonitors(monitor, out PhysicalMonitor[] physicalMonitors, out _))
            {
                return true;
            }

            try
            {
                for (int index = 0; index < physicalMonitors.Length; index++)
                {
                    if (!string.Equals(BuildDdcCiId(display.DeviceName, index), monitorId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result = ApplyDdcCiBrightness(monitorId, targetPercent, physicalMonitors[index]);
                    return false;
                }
            }
            finally
            {
                _ = DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors);
            }

            return true;
        };
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result ?? Failed(monitorId, targetPercent, "The selected DDC/CI monitor is no longer connected or no longer exposes a physical monitor handle.");
    }

    private static MonitorBrightnessApplyResultV1 ApplyDdcCiBrightness(
        string monitorId,
        int targetPercent,
        PhysicalMonitor monitor)
    {
        if (GetMonitorCapabilities(monitor.Handle, out uint capabilities, out _) && (capabilities & McCapsBrightness) == 0)
        {
            return Failed(monitorId, targetPercent, "The monitor reports that DDC/CI brightness control is not supported.");
        }
        if (!GetMonitorBrightness(monitor.Handle, out uint minimum, out uint current, out uint maximum)
            || maximum <= minimum)
        {
            return Failed(monitorId, targetPercent, "Windows could not read a usable DDC/CI brightness range before applying the change.");
        }

        uint requestedRaw = PercentToRaw(targetPercent, minimum, maximum);
        if (!SetMonitorBrightness(monitor.Handle, requestedRaw))
        {
            return Failed(monitorId, targetPercent, "The monitor rejected the DDC/CI brightness command. Its on-screen menu or another display utility may own brightness control.");
        }

        if (!GetMonitorBrightness(monitor.Handle, out uint readMinimum, out uint observedRaw, out uint readMaximum)
            || readMaximum <= readMinimum)
        {
            _ = SetMonitorBrightness(monitor.Handle, current);
            return new MonitorBrightnessApplyResultV1(
                MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
                monitorId,
                targetPercent,
                null,
                Applied: false,
                ReadBackVerified: false,
                RollbackAttempted: true,
                "The monitor accepted a brightness command but did not provide a valid read-back. RigPilot attempted to restore the prior value.");
        }

        int observedPercent = RawToPercent(observedRaw, readMinimum, readMaximum);
        int tolerance = Math.Max(1, (int)Math.Ceiling(100d / Math.Max(1d, readMaximum - readMinimum)));
        if (Math.Abs(observedPercent - targetPercent) <= tolerance)
        {
            return new MonitorBrightnessApplyResultV1(
                MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
                monitorId,
                targetPercent,
                observedPercent,
                Applied: true,
                ReadBackVerified: true,
                RollbackAttempted: false,
                "DDC/CI brightness was applied and read back from the selected monitor.");
        }

        _ = SetMonitorBrightness(monitor.Handle, current);
        return new MonitorBrightnessApplyResultV1(
            MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
            monitorId,
            targetPercent,
            observedPercent,
            Applied: false,
            ReadBackVerified: false,
            RollbackAttempted: true,
            "The monitor read back a different brightness value. RigPilot attempted to restore the prior value.");
    }

    private static MonitorBrightnessApplyResultV1 SetWmiBrightness(
        string monitorId,
        int targetPercent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WmiBrightnessCandidate? candidate;
        try
        {
            candidate = DiscoverWmiCandidates()
                .FirstOrDefault(item => string.Equals(BuildWmiId(item.InstanceName), monitorId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            return Failed(monitorId, targetPercent, "Windows could not query the WMI brightness provider for the selected panel.");
        }

        if (candidate is null)
        {
            return Failed(monitorId, targetPercent, "The selected Windows-managed panel is no longer active.");
        }
        if (!candidate.CanWrite || candidate.CurrentPercent is not int originalPercent)
        {
            return Failed(monitorId, targetPercent, "Windows reports the panel as read-only; no active WMI brightness method is available.");
        }
        try
        {
            if (!TryInvokeWmiSetBrightness(candidate.InstanceName, targetPercent, out uint returnCode) || returnCode != 0)
            {
                return Failed(monitorId, targetPercent, $"The Windows brightness provider rejected the request (return code {returnCode}).");
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException or FormatException)
        {
            bool rollbackAfterProviderFailure = AttemptWmiRollback(candidate.InstanceName, originalPercent);
            return new MonitorBrightnessApplyResultV1(
                MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
                monitorId,
                targetPercent,
                null,
                Applied: false,
                ReadBackVerified: false,
                RollbackAttempted: rollbackAfterProviderFailure,
                "The Windows brightness provider failed while applying the request. RigPilot attempted to restore the prior value.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        WmiBrightnessCandidate? observed;
        try
        {
            observed = DiscoverWmiCandidates()
                .FirstOrDefault(item => string.Equals(item.InstanceName, candidate.InstanceName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            bool rollbackAfterReadbackFailure = AttemptWmiRollback(candidate.InstanceName, originalPercent);
            return new MonitorBrightnessApplyResultV1(
                MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
                monitorId,
                targetPercent,
                null,
                Applied: false,
                ReadBackVerified: false,
                RollbackAttempted: rollbackAfterReadbackFailure,
                "Windows could not read back the panel brightness. RigPilot attempted to restore the prior value.");
        }
        if (observed?.CurrentPercent == targetPercent)
        {
            return new MonitorBrightnessApplyResultV1(
                MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
                monitorId,
                targetPercent,
                observed.CurrentPercent,
                Applied: true,
                ReadBackVerified: true,
                RollbackAttempted: false,
                "Windows panel brightness was applied and read back through WMI.");
        }

        bool rollbackAttempted = AttemptWmiRollback(candidate.InstanceName, originalPercent);
        return new MonitorBrightnessApplyResultV1(
            MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
            monitorId,
            targetPercent,
            observed?.CurrentPercent,
            Applied: false,
            ReadBackVerified: false,
            RollbackAttempted: rollbackAttempted,
            "Windows did not read back the requested panel brightness. RigPilot attempted to restore the prior value.");
    }

    private static MonitorBrightnessDeviceV1 DescribeDdcCiMonitor(
        DisplayMonitorInfo display,
        PhysicalMonitor monitor,
        int index)
    {
        string monitorId = BuildDdcCiId(display.DeviceName, index);
        string description = string.IsNullOrWhiteSpace(monitor.Description)
            ? "Physical monitor"
            : Trim(monitor.Description, 120);
        string displayName = $"{display.Label} — {description}";
        bool capabilityReported = GetMonitorCapabilities(monitor.Handle, out uint capabilities, out _);
        if (capabilityReported && (capabilities & McCapsBrightness) == 0)
        {
            return new MonitorBrightnessDeviceV1(
                MonitorBrightnessDeviceV1.CurrentSchemaVersion,
                monitorId,
                displayName,
                display.DeviceName,
                MonitorBrightnessTransport.DdcCi,
                CapabilityAccessState.ReadOnly,
                null,
                null,
                null,
                "The monitor exposes DDC/CI but reports no brightness-control capability.");
        }
        if (!GetMonitorBrightness(monitor.Handle, out uint minimum, out uint current, out uint maximum)
            || maximum <= minimum)
        {
            return new MonitorBrightnessDeviceV1(
                MonitorBrightnessDeviceV1.CurrentSchemaVersion,
                monitorId,
                displayName,
                display.DeviceName,
                MonitorBrightnessTransport.DdcCi,
                CapabilityAccessState.ReadOnly,
                null,
                null,
                null,
                "Windows could not read a usable DDC/CI brightness range from this monitor.");
        }

        return new MonitorBrightnessDeviceV1(
            MonitorBrightnessDeviceV1.CurrentSchemaVersion,
            monitorId,
            displayName,
            display.DeviceName,
            MonitorBrightnessTransport.DdcCi,
            CapabilityAccessState.Experimental,
            0,
            100,
            RawToPercent(current, minimum, maximum),
            capabilityReported
                ? "DDC/CI brightness range detected. Changes are explicitly confirmed and read back."
                : "DDC/CI brightness range detected, but capability flags were unavailable. Changes are explicitly confirmed and read back.");
    }

    private static void AddLogicalScreenFallbacks(List<MonitorBrightnessDeviceV1> monitors, string reason)
    {
        HashSet<string> knownDisplayDevices = monitors
            .Select(device => device.DisplayDeviceName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            if (knownDisplayDevices.Contains(screen.DeviceName))
            {
                continue;
            }
            DisplayMonitorInfo display = CreateDisplayInfo(screen.DeviceName, screen.Primary, screen.Bounds.Width, screen.Bounds.Height);
            monitors.Add(LogicalFallback(display, reason));
        }
    }

    private static MonitorBrightnessDeviceV1 LogicalFallback(DisplayMonitorInfo display, string reason) => new(
        MonitorBrightnessDeviceV1.CurrentSchemaVersion,
        $"display:{StableToken(display.DeviceName)}",
        display.Label,
        display.DeviceName,
        null,
        CapabilityAccessState.Unsupported,
        null,
        null,
        null,
        reason);

    private static bool TryGetDisplayInfo(IntPtr monitor, out DisplayMonitorInfo display)
    {
        MonitorInfoEx native = new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        if (!GetMonitorInfo(monitor, ref native) || string.IsNullOrWhiteSpace(native.DeviceName))
        {
            display = new DisplayMonitorInfo(string.Empty, string.Empty);
            return false;
        }

        Forms.Screen? screen = Forms.Screen.AllScreens.FirstOrDefault(item =>
            string.Equals(item.DeviceName, native.DeviceName, StringComparison.OrdinalIgnoreCase));
        display = screen is null
            ? CreateDisplayInfo(native.DeviceName, primary: false, native.Monitor.Right - native.Monitor.Left, native.Monitor.Bottom - native.Monitor.Top)
            : CreateDisplayInfo(screen.DeviceName, screen.Primary, screen.Bounds.Width, screen.Bounds.Height);
        return true;
    }

    private static DisplayMonitorInfo CreateDisplayInfo(string deviceName, bool primary, int width, int height) => new(
        deviceName,
        $"{(primary ? "Primary " : string.Empty)}display {deviceName} ({Math.Max(0, width)} x {Math.Max(0, height)})");

    private static bool TryOpenPhysicalMonitors(IntPtr monitor, out PhysicalMonitor[] physicalMonitors, out string diagnostic)
    {
        physicalMonitors = [];
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0)
        {
            diagnostic = "Windows did not expose a DDC/CI physical-monitor handle for this display.";
            return false;
        }
        if (count > MaximumPhysicalMonitorsPerDisplay)
        {
            diagnostic = "Windows returned an invalid physical-monitor count for this display.";
            return false;
        }

        physicalMonitors = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physicalMonitors))
        {
            physicalMonitors = [];
            diagnostic = "Windows could not enumerate the DDC/CI physical-monitor handles for this display.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static List<WmiBrightnessCandidate> DiscoverWmiCandidates()
    {
        HashSet<string> activeMethods = [];
        using (ManagementObjectSearcher methodSearcher = new(@"root\WMI", "SELECT InstanceName, Active FROM WmiMonitorBrightnessMethods"))
        using (ManagementObjectCollection methodResults = methodSearcher.Get())
        {
            foreach (ManagementBaseObject entry in methodResults.Cast<ManagementBaseObject>())
            {
                string? instanceName = Convert.ToString(entry["InstanceName"], CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(instanceName) && ReadBoolean(entry["Active"]))
                {
                    activeMethods.Add(instanceName);
                }
            }
        }

        List<WmiBrightnessCandidate> candidates = [];
        using (ManagementObjectSearcher brightnessSearcher = new(@"root\WMI", "SELECT InstanceName, Active, CurrentBrightness FROM WmiMonitorBrightness"))
        using (ManagementObjectCollection brightnessResults = brightnessSearcher.Get())
        {
            int ordinal = 0;
            foreach (ManagementBaseObject entry in brightnessResults.Cast<ManagementBaseObject>())
            {
                string? instanceName = Convert.ToString(entry["InstanceName"], CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(instanceName) || !ReadBoolean(entry["Active"]))
                {
                    continue;
                }

                ordinal++;
                int? current = ReadPercentage(entry["CurrentBrightness"]);
                candidates.Add(new WmiBrightnessCandidate(
                    instanceName,
                    $"Windows-managed panel {ordinal} — {Trim(instanceName, 80)}",
                    current,
                    current is not null && activeMethods.Contains(instanceName)));
            }
        }
        return candidates;
    }

    private static bool TryInvokeWmiSetBrightness(string instanceName, int percent, out uint returnCode)
    {
        returnCode = uint.MaxValue;
        using ManagementObjectSearcher searcher = new(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementObject entry in results.Cast<ManagementObject>())
        {
            if (!string.Equals(Convert.ToString(entry["InstanceName"], CultureInfo.InvariantCulture), instanceName, StringComparison.OrdinalIgnoreCase)
                || !ReadBoolean(entry["Active"]))
            {
                continue;
            }

            object? result = entry.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)percent]);
            returnCode = ReadReturnCode(result);
            return true;
        }
        return false;
    }

    private static bool AttemptWmiRollback(string instanceName, int percent)
    {
        try
        {
            _ = TryInvokeWmiSetBrightness(instanceName, percent, out _);
            return true;
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException or FormatException)
        {
            return false;
        }
    }

    private static uint ReadReturnCode(object? value)
    {
        if (value is null)
        {
            return 0;
        }
        if (value is ManagementBaseObject resultObject && resultObject["ReturnValue"] is not null)
        {
            return Convert.ToUInt32(resultObject["ReturnValue"], CultureInfo.InvariantCulture);
        }
        return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool ReadBoolean(object? value)
    {
        try
        {
            return value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int? ReadPercentage(object? value)
    {
        try
        {
            int percent = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return percent is >= 0 and <= 100 ? percent : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static int RawToPercent(uint raw, uint minimum, uint maximum)
    {
        if (maximum <= minimum)
        {
            return 0;
        }
        double bounded = Math.Clamp(raw, minimum, maximum);
        return Math.Clamp(
            (int)Math.Round((bounded - minimum) * 100d / (maximum - minimum), MidpointRounding.AwayFromZero),
            0,
            100);
    }

    private static uint PercentToRaw(int percent, uint minimum, uint maximum)
    {
        double raw = minimum + ((maximum - minimum) * (Math.Clamp(percent, 0, 100) / 100d));
        return (uint)Math.Clamp(
            Math.Round(raw, MidpointRounding.AwayFromZero),
            minimum,
            maximum);
    }

    private static MonitorBrightnessApplyResultV1 Failed(string monitorId, int requestedPercent, string message) => new(
        MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
        monitorId,
        requestedPercent,
        null,
        Applied: false,
        ReadBackVerified: false,
        RollbackAttempted: false,
        message);

    private static string BuildDdcCiId(string deviceName, int index) => $"ddcci:{StableToken($"{deviceName}|{index}")}";

    private static string BuildWmiId(string instanceName) => $"wmi:{StableToken(instanceName)}";

    private static string StableToken(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
    }

    private static string Trim(string value, int maximumLength) => value.Length <= maximumLength
        ? value
        : $"{value[..Math.Max(0, maximumLength - 3)]}...";

    private sealed record DisplayMonitorInfo(string DeviceName, string Label);

    private sealed record WmiBrightnessCandidate(
        string InstanceName,
        string DisplayName,
        int? CurrentPercent,
        bool CanWrite);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr clipRectangle, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("Dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr monitor,
        uint count,
        [Out] PhysicalMonitor[] physicalMonitors);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] physicalMonitors);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorCapabilities(IntPtr monitor, out uint capabilities, out uint supportedColorTemperatures);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimumBrightness, out uint currentBrightness, out uint maximumBrightness);

    [DllImport("Dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint newBrightness);
}
