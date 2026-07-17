using System.Diagnostics;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

public static class ConflictDetector
{
    private static readonly KnownController[] KnownControllers =
    [
        new("hwinfo", "HWiNFO", ["HWiNFO64", "HWiNFO32"], ["Monitoring"], "Avoid simultaneous low-level polling if readings become unstable."),
        new("afterburner", "MSI Afterburner", ["MSIAfterburner"], ["GpuTuning", "GpuFan"], "Disable overlapping RigPilot GPU writes or close Afterburner."),
        new("nzxt-cam", "NZXT CAM", ["NZXT CAM"], ["Aio", "UsbFan", "Lighting"], "Use only one owner for the affected NZXT device."),
        new("armoury-crate", "ASUS Armoury Crate", ["ArmouryCrate", "ArmouryCrate.UserSessionHelper"], ["MotherboardFan", "Lighting", "GpuTuning"], "Use only one owner for overlapping ASUS controls."),
        new("ai-suite", "ASUS AI Suite", ["AISuite3", "FanXpert"], ["MotherboardFan", "CpuTuning"], "Disable Fan Xpert ownership before enabling RigPilot fan writes."),
        new("fan-control", "Fan Control", ["FanControl"], ["MotherboardFan", "GpuFan", "Aio"], "Use only one fan-control application at a time."),
        new("signalrgb", "SignalRGB", ["SignalRgb", "SignalRgbLauncher"], ["Lighting", "UsbFan"], "Use only one owner for affected lighting and fan controllers."),
        new("openrgb", "OpenRGB", ["OpenRGB"], ["Lighting"], "RigPilot may bridge to OpenRGB, but must not compete with another OpenRGB client."),
        new("icue", "Corsair iCUE", ["iCUE", "iCUE5"], ["Lighting", "UsbFan", "Aio"], "Use only one owner for affected Corsair devices."),
        new("l-connect", "L-Connect", ["L-Connect", "L-Connect 3"], ["Lighting", "UsbFan"], "Use only one owner for affected Lian Li controllers."),
        new("rgb-fusion", "Gigabyte RGB Fusion / AORUS Engine", ["RGBFusion", "AorusEngine", "GigabyteControlCenter", "GCC.Service"], ["MotherboardFan", "Lighting", "GpuTuning"], "Use only one owner for overlapping Gigabyte/AORUS controls."),
        new("mystic-light", "MSI Center / Dragon Center", ["MSI Center", "Dragon Center", "MysticLight_Service"], ["MotherboardFan", "Lighting"], "Use only one owner for overlapping MSI controls.")
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
        HashSet<string> running = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    running.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return KnownControllers.Select(controller => new ConflictDescriptor(
            controller.Id,
            controller.DisplayName,
            string.Join(", ", controller.ProcessNames),
            controller.ResourceFamilies,
            controller.ProcessNames.Any(name => running.Contains(name)),
            controller.Guidance)).ToArray();
    }

    private sealed record KnownController(
        string Id,
        string DisplayName,
        IReadOnlyList<string> ProcessNames,
        IReadOnlyList<string> ResourceFamilies,
        string Guidance);
}
