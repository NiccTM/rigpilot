using System.Globalization;
using System.Management;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Enumerates only the directly observed RGB-class USB/HID devices that need
/// containment before a future device pack can write to them. This adapter
/// deliberately performs no HID or USB I/O.
/// </summary>
public sealed class WindowsPeripheralInventoryAdapter : IHardwareAdapter, IAdapterTopologyCachePolicy
{
    private const string AsusAuraHardwareId = "VID_0B05&PID_18F3";
    private const string RazerHardwareId = "VID_1532&PID_0F13";
    private string? _lastError;

    public AdapterManifest Manifest { get; } = new(
        "windows.peripheral-inventory",
        "Windows peripheral inventory",
        "0.5.5-alpha",
        "GPL-3.0-only",
        null,
        AdapterExecutionContext.SystemService,
        ["HID LampArray", "ASUS/ROG/TUF/Aura", "MSI/Mystic Light", "Gigabyte/AORUS/RGB Fusion", "ASRock/Polychrome", "EVGA/K|NGP|N", "ZOTAC/SPECTRA", "Sapphire/TriXX Glow", "PowerColor/DevilZone", "PNY/EPIC-X", "Palit/GameRock", "GALAX/KFA2", "Corsair", "Logitech", "Razer", "SteelSeries", "NZXT", "Cooler Master", "Lian Li", "Thermaltake", "G.Skill", "HyperX"],
        ["PeripheralInventoryReadOnly", "AuraReadOnly", "HidPackReadOnly"]);

    public TimeSpan TopologyCacheDuration => TimeSpan.FromSeconds(30);

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<HardwareDevice> devices = [];
        List<CapabilityDescriptor> capabilities = [];
        List<DiagnosticWarning> warnings = [];
        List<DetectedPeripheral> observed = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT Name, Manufacturer, PNPDeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                using (row)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string pnpId = GetString(row, "PNPDeviceID") ?? string.Empty;
                    string[] hardwareIds = GetStrings(row, "HardwareID");
                    if (!HardwareCompatibilityCatalog.IsUsbOrHidTransport(pnpId, hardwareIds))
                    {
                        continue;
                    }
                    string combined = string.Join(';', hardwareIds.Append(pnpId));
                    string? matchedHardwareId = MatchKnownDevice(combined);
                    string name = GetString(row, "Name")?.Trim()
                        ?? (matchedHardwareId == AsusAuraHardwareId
                            ? "ASUS Aura controller"
                            : "Detected HID lighting device");
                    string manufacturer = GetString(row, "Manufacturer")?.Trim()
                        ?? (matchedHardwareId == AsusAuraHardwareId ? "ASUS" : "Unknown");
                    HardwareCompatibilityMatch compatibility = ClassifyPeripheral(
                        matchedHardwareId,
                        manufacturer,
                        name,
                        combined);
                    if (!compatibility.IsRecognized)
                    {
                        continue;
                    }
                    string hardwareId = matchedHardwareId ?? StablePeripheralIdentity(hardwareIds, pnpId, compatibility.FamilyId);
                    observed.Add(new DetectedPeripheral(
                        hardwareId,
                        name,
                        manufacturer,
                        pnpId,
                        GetString(row, "ConfigManagerErrorCode") ?? "unknown",
                        hardwareIds,
                        compatibility,
                        IsLightingPeripheral(matchedHardwareId, compatibility, $"{combined};{name}")));
                }
            }

            foreach (IGrouping<string, DetectedPeripheral> group in observed.GroupBy(item => item.HardwareId, StringComparer.OrdinalIgnoreCase))
            {
                DetectedPeripheral primary = group
                    .OrderBy(item => PeripheralNameRank(item.Name, item.HardwareId))
                    .ThenByDescending(item => item.PnpId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(item => item.PnpId, StringComparer.OrdinalIgnoreCase)
                    .First();
                string deviceId = StableIds.Create("lighting", group.Key);
                Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["hardwareId"] = group.Key,
                    ["pnpDeviceId"] = primary.PnpId,
                    ["configManagerErrorCode"] = primary.ConfigManagerErrorCode,
                    ["interfaceCount"] = group.Count().ToString(CultureInfo.InvariantCulture)
                };
                string[] hardwareIds = group.SelectMany(item => item.HardwareIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (hardwareIds.Length > 0)
                {
                    properties["hardwareIds"] = string.Join(';', hardwareIds);
                }
                HardwareCompatibilityCatalog.AddToProperties(properties, primary.Compatibility);

                devices.Add(new HardwareDevice(
                    deviceId,
                    primary.Name,
                    primary.IsLighting ? DeviceKind.Lighting : DeviceKind.Controller,
                    primary.Manufacturer,
                    group.Key,
                    primary.PnpId,
                    properties));
                capabilities.Add(new CapabilityDescriptor(
                    $"peripheral.readonly:{deviceId}",
                    Manifest.Id,
                    deviceId,
                    $"{primary.Name} direct control",
                    CapabilityAccessState.ReadOnly,
                    AdapterExecutionContext.AdapterHost,
                    ControlValueKind.Boolean,
                    null,
                    null,
                    RiskLevel.Guarded,
                    EvidenceLevel.Detected,
                    null,
                    $"{primary.Compatibility.DisplayName} is detected across {group.Count()} interface(s). Its direct USB/HID protocol remains isolated until an exact-device Adapter Host containment, timeout, reset, and ownership test passes.",
                    CanResetToDefault: false,
                    Domain: primary.IsLighting ? ControlDomain.Lighting : ControlDomain.Other));
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            _lastError = exception.Message;
            warnings.Add(new DiagnosticWarning(
                "PERIPHERAL_INVENTORY_FAILED",
                "Information",
                $"Read-only RGB peripheral inventory was unavailable: {exception.Message}",
                "Dynamic Lighting and OpenRGB remain independent of this discovery-only adapter."));
        }

        return Task.FromResult(new AdapterProbeResult(Manifest, devices, capabilities, warnings));
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SensorSample>>([]);

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Direct USB/HID device packs are not qualified for writes.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Direct USB/HID device packs are not qualified for writes.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Direct USB/HID device packs are not qualified for writes.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("No direct USB/HID reset endpoint is qualified.");

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new AdapterHealth(
        Manifest.Id,
        _lastError is null,
        DateTimeOffset.UtcNow,
        _lastError is null
            ? "Read-only ASUS Aura and HID peripheral discovery is available."
            : "Read-only peripheral discovery needs attention.",
        _lastError is null ? [] : [_lastError]));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string? MatchKnownDevice(string value)
    {
        if (value.Contains(AsusAuraHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return AsusAuraHardwareId;
        }
        return value.Contains(RazerHardwareId, StringComparison.OrdinalIgnoreCase) ? RazerHardwareId : null;
    }

    private static HardwareCompatibilityMatch ClassifyPeripheral(
        string? matchedHardwareId,
        string manufacturer,
        string name,
        string combined)
    {
        if (string.Equals(matchedHardwareId, AsusAuraHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return new HardwareCompatibilityMatch(
                "asus-aura-controller",
                "ASUS Aura controller",
                "Recognized for read-only inventory and capability reporting. This family match does not qualify any hardware write.",
                IsRecognized: true);
        }
        if (string.Equals(matchedHardwareId, RazerHardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return new HardwareCompatibilityMatch(
                "razer-hid-controller",
                "Razer HID lighting controller",
                "Recognized for read-only inventory and capability reporting. This family match does not qualify any hardware write.",
                IsRecognized: true);
        }
        return HardwareCompatibilityCatalog.ClassifyPeripheral(manufacturer, name, combined);
    }

    private static string StablePeripheralIdentity(
        IReadOnlyList<string> hardwareIds,
        string pnpId,
        string fallbackFamilyId)
    {
        string candidate = hardwareIds.Append(pnpId)
            .FirstOrDefault(value => value.Contains("VID_", StringComparison.OrdinalIgnoreCase)
                && value.Contains("PID_", StringComparison.OrdinalIgnoreCase))
            ?? fallbackFamilyId;
        int interfaceSuffix = candidate.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        return interfaceSuffix > 0 ? candidate[..interfaceSuffix] : candidate;
    }

    private static bool IsLightingPeripheral(
        string? matchedHardwareId,
        HardwareCompatibilityMatch compatibility,
        string combined) => matchedHardwareId is not null
        || string.Equals(compatibility.FamilyId, "hid-lamparray", StringComparison.Ordinal)
        || ContainsAny(combined, "RGB", "LIGHT", "LED", "AURA", "CHROMA", "LIGHTSYNC", "SPECTRA", "MYSTIC", "FUSION", "POLYCHROME", "ICUE", "KINGPIN");

    private static bool ContainsAny(string value, params string[] candidates) => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static int PeripheralNameRank(string name, string hardwareId)
    {
        if (hardwareId == AsusAuraHardwareId && name.Contains("Aura", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (hardwareId == RazerHardwareId
            && (name.Contains("Lian", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Razer Control", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }
        return name.Contains("HID", StringComparison.OrdinalIgnoreCase)
            || name.Contains("USB", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static string? GetString(ManagementBaseObject row, string property)
    {
        object? value = row.Properties[property]?.Value;
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string[] GetStrings(ManagementBaseObject row, string property) => row.Properties[property]?.Value switch
    {
        string[] array => array.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
        string value when !string.IsNullOrWhiteSpace(value) => [value],
        _ => []
    };

    private sealed record DetectedPeripheral(
        string HardwareId,
        string Name,
        string Manufacturer,
        string PnpId,
        string ConfigManagerErrorCode,
        IReadOnlyList<string> HardwareIds,
        HardwareCompatibilityMatch Compatibility,
        bool IsLighting);
}
