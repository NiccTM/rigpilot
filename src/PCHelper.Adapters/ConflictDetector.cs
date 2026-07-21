using System.Diagnostics;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

public static class ConflictDetector
{
    private static readonly KnownController[] KnownControllers =
    [
        new("hwinfo", "HWiNFO", ["HWiNFO64", "HWiNFO32"], [], ["Monitoring"], "Avoid simultaneous low-level polling if readings become unstable."),
        new("afterburner", "MSI Afterburner", ["MSIAfterburner"], [], ["GpuTuning", "GpuFan"], "Disable overlapping RigPilot GPU writes or close Afterburner."),
        // NZXT CAM ships a background service (service.exe under the "NZXT CAM" install
        // folder) that owns the Kraken lighting/pump even when the CAM window is closed.
        // Its process name is generic, so it is matched by install-path hint as well.
        new("nzxt-cam", "NZXT CAM", ["NZXT CAM"], ["NZXT CAM"], ["Aio", "UsbFan", "Lighting"], "Use only one owner for the affected NZXT device."),
        new("armoury-crate", "ASUS Armoury Crate", ["ArmouryCrate", "ArmouryCrate.UserSessionHelper"], ["Armoury Crate", "LightingService"], ["MotherboardFan", "Lighting", "GpuTuning"], "Use only one owner for overlapping ASUS controls."),
        new("ai-suite", "ASUS AI Suite", ["AISuite3", "FanXpert"], [], ["MotherboardFan", "CpuTuning"], "Disable Fan Xpert ownership before enabling RigPilot fan writes."),
        new("fan-control", "Fan Control", ["FanControl"], [], ["MotherboardFan", "GpuFan", "Aio"], "Use only one fan-control application at a time."),
        new("signalrgb", "SignalRGB", ["SignalRgb", "SignalRgbLauncher"], [], ["Lighting", "UsbFan"], "Use only one owner for affected lighting and fan controllers."),
        new("openrgb", "OpenRGB", ["OpenRGB"], [], ["Lighting"], "RigPilot may bridge to OpenRGB, but must not compete with another OpenRGB client."),
        new("icue", "Corsair iCUE", ["iCUE", "iCUE5"], [], ["Lighting", "UsbFan", "Aio"], "Use only one owner for affected Corsair devices."),
        new("l-connect", "L-Connect", ["L-Connect", "L-Connect 3"], [], ["Lighting", "UsbFan"], "Use only one owner for affected Lian Li controllers."),
        new("rgb-fusion", "Gigabyte RGB Fusion / AORUS Engine", ["RGBFusion", "AorusEngine", "GigabyteControlCenter", "GCC.Service"], [], ["MotherboardFan", "Lighting", "GpuTuning"], "Use only one owner for overlapping Gigabyte/AORUS controls."),
        new("mystic-light", "MSI Center / Dragon Center", ["MSI Center", "Dragon Center", "MysticLight_Service"], [], ["MotherboardFan", "Lighting"], "Use only one owner for overlapping MSI controls.")
    ];

    /// <summary>
    /// The curated process-name allowlist for a known controller id, or empty
    /// for an unrecognised id. This is the ONLY set the "close blockers" action
    /// may terminate — it never operates on an arbitrary caller-supplied name.
    /// </summary>
    public static IReadOnlyList<string> ProcessNamesFor(string conflictId) =>
        KnownControllers.FirstOrDefault(controller => string.Equals(controller.Id, conflictId, StringComparison.Ordinal))
            ?.ProcessNames ?? [];

    /// <summary>Every known-controller id, exposed so callers can validate against the allowlist.</summary>
    public static IReadOnlyList<string> KnownControllerIds =>
        [.. KnownControllers.Select(controller => controller.Id)];

    public static IReadOnlyList<ConflictDescriptor> Detect()
    {
        HashSet<string> runningNames = [];
        List<string> runningModulePaths = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    runningNames.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                }

                // Best-effort: a background vendor service (e.g. NZXT CAM's service.exe) has
                // a generic process name but a distinctive install path. Reading another
                // process's module path can fail (access denied, bitness mismatch, exit) —
                // ignore those and rely on the name match for that process.
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        runningModulePaths.Add(path);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                }
            }
        }

        return DetectFrom(runningNames, runningModulePaths);
    }

    /// <summary>
    /// Pure detection over a set of running process names and module paths, so the
    /// name/path matching is testable without enumerating live processes.
    /// </summary>
    public static IReadOnlyList<ConflictDescriptor> DetectFrom(
        ISet<string> runningProcessNames,
        IEnumerable<string> runningModulePaths)
    {
        string[] paths = [.. runningModulePaths];
        return KnownControllers.Select(controller =>
        {
            bool byName = controller.ProcessNames.Any(runningProcessNames.Contains);
            bool byPath = controller.PathHints.Count > 0
                && controller.PathHints.Any(hint => paths.Any(path =>
                    path.Contains(hint, StringComparison.OrdinalIgnoreCase)));
            return new ConflictDescriptor(
                controller.Id,
                controller.DisplayName,
                string.Join(", ", controller.ProcessNames),
                controller.ResourceFamilies,
                byName || byPath,
                controller.Guidance);
        }).ToArray();
    }

    private sealed record KnownController(
        string Id,
        string DisplayName,
        IReadOnlyList<string> ProcessNames,
        IReadOnlyList<string> PathHints,
        IReadOnlyList<string> ResourceFamilies,
        string Guidance);
}
