using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Produces exact-device vendor-control cards without inventing a write path.
/// Runtime presence is useful evidence, but no ADLX, IGCL, NVAPI, SMU, or MSR
/// endpoint becomes writable until its individual apply/read-back/reset tests
/// are included in a signed built-in adapter or a reviewed pack.
/// </summary>
public sealed class VendorControlEligibilityAdapter : IHardwareAdapter, IAdapterTopologyCachePolicy
{
    private string? _lastError;

    public AdapterManifest Manifest { get; } = new(
        "vendor.control-eligibility",
        "Vendor control eligibility",
        "0.5.0-alpha",
        "GPL-3.0-only",
        "Vendor display runtime where applicable",
        AdapterExecutionContext.AdapterHost,
        ["AMD Ryzen and Threadripper 5000-9000", "Intel Core 12th-14th and Core Ultra 200", "NVIDIA RTX 20-50", "AMD Radeon RX 5000-9000", "Intel Arc A/B", "ASUS/ROG/TUF, Gigabyte/AORUS, MSI/Mystic Light, ASRock/Polychrome, ZOTAC/SPECTRA, EVGA/K|NGP|N, Sapphire, PowerColor, XFX, PNY, Palit, Gainward, INNO3D, GALAX/KFA2, Colorful, Maxsun, Yeston, Manli, Leadtek, Sparkle, and OEM graphics-board identities", "ASUS, MSI, Gigabyte, and ASRock desktop boards"],
        ["CompatibilityCatalogReadOnly", "GpuBoardPartnerReadOnly", "GpuRgbEligibilityReadOnly", "CpuTuningBlocked", "GpuTuningBlocked", "AdlxReadOnly", "IgclReadOnly"]);

    public TimeSpan TopologyCacheDuration => TimeSpan.FromMinutes(5);

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<CapabilityDescriptor> capabilities = [];
        List<DiagnosticWarning> warnings = [];
        try
        {
            string biosVersion = TryGetBiosVersion();
            PawnIoRuntimeStatus pawnIo = PawnIoRuntimeProbe.Detect();
            Query("Win32_Processor", row =>
            {
                string name = GetString(row, "Name")?.Trim() ?? "Unknown processor";
                string manufacturer = GetString(row, "Manufacturer") ?? "Unknown";
                string deviceId = StableIds.Create("cpu", manufacturer, name);
                HardwareCompatibilityMatch compatibility = HardwareCompatibilityCatalog.ClassifyCpu(manufacturer, name);
                AddCompatibilityCard(capabilities, deviceId, compatibility, ControlDomain.Cpu);
                string recognised = compatibility.IsRecognized ? $"{compatibility.DisplayName} is recognized from the Windows identity. " : string.Empty;
                if (manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
                {
                    string pawnIoEvidence = pawnIo.Available
                        ? $"A rule-compliant SMU path is reachable: {pawnIo.Describe()} (RyzenSMU module transport)."
                        : $"A rule-compliant SMU path exists (signed PawnIO with a RyzenSMU module) but {pawnIo.Describe()}.";
                    string reason = $"{recognised}AMD Zen tuning is detected on BIOS {biosVersion}. {pawnIoEvidence} Curve Optimizer / PBO writes stay blocked until exact per-family mailbox bounds, applied-curve read-back, guaranteed stock reset, and a boot-recovery revert are qualified. See docs/qualification/cpu-tuning-and-intel-arc.md.";
                    capabilities.Add(Feasibility(
                        $"amd.zen.feasibility:{deviceId}",
                        deviceId,
                        "AMD Zen transport feasibility",
                        ControlDomain.Cpu,
                        reason));
                    capabilities.Add(Blocked(
                        $"amd.zen.tuning:{deviceId}",
                        deviceId,
                        "AMD Zen tuning",
                        ControlDomain.Cpu,
                        reason));
                }
                else if (manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    string reason = $"{recognised}Intel CPU tuning is detected on BIOS {biosVersion}, but no audited XTU, MSR, or reset-safe endpoint is included in this alpha.";
                    capabilities.Add(Feasibility(
                        $"intel.cpu.feasibility:{deviceId}",
                        deviceId,
                        "Intel CPU transport feasibility",
                        ControlDomain.Cpu,
                        reason));
                    capabilities.Add(Blocked(
                        $"intel.cpu.tuning:{deviceId}",
                        deviceId,
                        "Intel CPU tuning",
                        ControlDomain.Cpu,
                        reason));
                }
            });

            Query("Win32_BaseBoard", row =>
            {
                string manufacturer = GetString(row, "Manufacturer")?.Trim() ?? "Unknown";
                string product = GetString(row, "Product")?.Trim() ?? "Unknown motherboard";
                string version = GetString(row, "Version") ?? string.Empty;
                string deviceId = StableIds.Create("motherboard", manufacturer, product, version);
                AddCompatibilityCard(
                    capabilities,
                    deviceId,
                    HardwareCompatibilityCatalog.ClassifyMotherboard(manufacturer, product),
                    ControlDomain.Other);
            });

            HashSet<string> seenMemoryKits = [];
            Query("Win32_PhysicalMemory", row =>
            {
                string manufacturer = GetString(row, "Manufacturer")?.Trim() ?? "Unknown";
                string partNumber = GetString(row, "PartNumber")?.Trim() ?? string.Empty;
                if (partNumber.Length == 0 || !seenMemoryKits.Add(partNumber))
                {
                    return; // one card per distinct kit, not one per stick
                }

                // G.Skill Trident Z RGB part numbers end in a Z...R pattern
                // (e.g. F4-4000C15-8GTZR); the RGB controllers sit on SMBus.
                bool isTridentZRgb = manufacturer.Contains("SKILL", StringComparison.OrdinalIgnoreCase)
                    && partNumber.Contains("TZ", StringComparison.OrdinalIgnoreCase)
                    && partNumber.EndsWith("R", StringComparison.OrdinalIgnoreCase);
                if (!isTridentZRgb)
                {
                    return;
                }

                string deviceId = StableIds.Create("memory", manufacturer, partNumber);
                bool audited = EneSmbusRgbProtocol.IsKitAudited(partNumber);
                string reason = audited
                    ? $"G.Skill Trident Z RGB ({partNumber}) is controllable: this kit passed its witnessed first-light (ENE 'DIMM_LED-0103' identified over signed PawnIO SMBus) and static colour is available from the Lighting page. Writes are default-deny address-gated — no SPD, thermal, or PMIC address is ever written — and all sticks change together at the factory-default controller address."
                    : $"G.Skill Trident Z RGB ({partNumber}) is recognized. Its RGB controllers sit on the system SMBus at addresses 0x70-0x77. RigPilot's SMBus write path is default-deny address-gated (SPD/thermal/PMIC ranges permanently blocked) and rule-compliant via signed PawnIO, but a live write stays gated until this exact kit passes a witnessed first-light. No SPD or sensor address is ever written.";
                capabilities.Add(Feasibility(
                    $"gskill.tridentz.rgb.feasibility:{deviceId}",
                    deviceId,
                    "G.Skill Trident Z RGB (SMBus) feasibility",
                    ControlDomain.Lighting,
                    reason));
            });

            Query("Win32_VideoController", row =>
            {
                string name = GetString(row, "Name")?.Trim() ?? "Unknown display adapter";
                string pnpId = GetString(row, "PNPDeviceID") ?? string.Empty;
                string driver = GetString(row, "DriverVersion") ?? "unknown";
                string deviceId = StableIds.Create("gpu", name, pnpId);
                HardwareCompatibilityMatch compatibility = HardwareCompatibilityCatalog.ClassifyGpu(null, name);
                HardwareCompatibilityMatch boardPartner = HardwareCompatibilityCatalog.ClassifyGpuBoardPartner(
                    pnpId,
                    GetString(row, "AdapterCompatibility"),
                    name);
                AddCompatibilityCard(capabilities, deviceId, compatibility, ControlDomain.Gpu);
                AddGpuBoardRgbEligibility(capabilities, deviceId, boardPartner);
                string recognised = compatibility.IsRecognized ? $"{compatibility.DisplayName} is recognized from the Windows identity. " : string.Empty;
                string boardIdentity = boardPartner.IsRecognized ? $"{boardPartner.DisplayName} is identified from the PCI subsystem or an explicit board name. " : string.Empty;
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    bool nvapiRuntime = IsRuntimeAvailable("nvapi64.dll", "nvapi.dll");
                    capabilities.Add(new CapabilityDescriptor(
                        $"nvidia.nvapi.runtime:{deviceId}",
                        Manifest.Id,
                        deviceId,
                        "NVIDIA NVAPI runtime feasibility",
                        nvapiRuntime ? CapabilityAccessState.ReadOnly : CapabilityAccessState.Blocked,
                        AdapterExecutionContext.AdapterHost,
                        ControlValueKind.Text,
                        null,
                        null,
                        RiskLevel.Guarded,
                        EvidenceLevel.Detected,
                        null,
                        nvapiRuntime
                            ? $"{recognised}{boardIdentity}NVAPI is present beside the installed NVIDIA driver {driver}. Its presence is telemetry/cooling feasibility only; no NVAPI tuning or reset endpoint is exposed."
                            : $"{recognised}{boardIdentity}NVAPI was not found beside NVIDIA driver {driver}. No private or fallback low-level path is used.",
                        CanResetToDefault: false,
                        Domain: ControlDomain.Gpu));
                    capabilities.Add(Blocked(
                        $"nvidia.tuning:{deviceId}",
                        deviceId,
                        "NVIDIA clock, power, and voltage-frequency tuning",
                        ControlDomain.Gpu,
                        $"{recognised}{boardIdentity}NVIDIA board '{name}' on driver {driver} is detected. NVML telemetry is separate; tuning remains blocked until an exact board/driver apply, read-back, reset, and driver-restart test is certified."));
                }
                else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    bool runtime = IsRuntimeAvailable("amdadlx64.dll", "amdadlx.dll");
                    capabilities.Add(new CapabilityDescriptor(
                        $"amd.adlx:{deviceId}",
                        Manifest.Id,
                        deviceId,
                        "AMD ADLX tuning bridge",
                        runtime ? CapabilityAccessState.ReadOnly : CapabilityAccessState.Blocked,
                        AdapterExecutionContext.AdapterHost,
                        ControlValueKind.Numeric,
                        null,
                        null,
                        RiskLevel.Experimental,
                        EvidenceLevel.Detected,
                        null,
                        runtime
                            ? $"{recognised}AMD ADLX runtime is available for '{name}', but this alpha has not qualified bounded manual fan or tuning calls on the exact driver {driver}."
                            : $"AMD ADLX runtime was not found for '{name}'. Install the vendor driver runtime; no fallback low-level write path is used.",
                        CanResetToDefault: false,
                        Domain: ControlDomain.Gpu));
                }
                else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) && name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                {
                    bool runtime = IsRuntimeAvailable("igcl.dll");
                    capabilities.Add(new CapabilityDescriptor(
                        $"intel.igcl:{deviceId}",
                        Manifest.Id,
                        deviceId,
                        "Intel Graphics Control Library bridge",
                        runtime ? CapabilityAccessState.ReadOnly : CapabilityAccessState.Blocked,
                        AdapterExecutionContext.AdapterHost,
                        ControlValueKind.Numeric,
                        null,
                        null,
                        RiskLevel.Experimental,
                        EvidenceLevel.Detected,
                        null,
                        runtime
                            ? $"{recognised}Intel IGCL runtime is available for '{name}', but no exact-driver manual tuning and reset evidence exists in this alpha."
                            : $"Intel IGCL runtime was not found for '{name}'. No fallback low-level write path is used.",
                        CanResetToDefault: false,
                        Domain: ControlDomain.Gpu));
                }
            });
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            _lastError = exception.Message;
            warnings.Add(new DiagnosticWarning(
                "VENDOR_ELIGIBILITY_FAILED",
                "Information",
                $"Vendor control eligibility discovery failed: {exception.Message}",
                "Hardware writes remain unavailable until a supported adapter can probe the device."));
        }
        return Task.FromResult(new AdapterProbeResult(Manifest, [], capabilities, warnings));
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SensorSample>>([]);

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vendor tuning eligibility cards do not implement a hardware write endpoint.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vendor tuning eligibility cards do not implement a hardware write endpoint.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vendor tuning eligibility cards do not implement a hardware write endpoint.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Vendor tuning eligibility cards do not implement a reset endpoint.");

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new AdapterHealth(
        Manifest.Id,
        _lastError is null,
        DateTimeOffset.UtcNow,
        _lastError ?? "Exact vendor capability gates were evaluated without exposing unqualified writes.",
        _lastError is null ? [] : [_lastError]));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CapabilityDescriptor Blocked(
        string id,
        string deviceId,
        string name,
        ControlDomain domain,
        string reason) => new(
            id,
            "vendor.control-eligibility",
            deviceId,
            name,
            CapabilityAccessState.Blocked,
            AdapterExecutionContext.AdapterHost,
            ControlValueKind.Numeric,
            null,
            null,
            RiskLevel.Experimental,
            EvidenceLevel.Detected,
            null,
            reason,
        CanResetToDefault: false,
        Domain: domain);

    private static CapabilityDescriptor Feasibility(
        string id,
        string deviceId,
        string name,
        ControlDomain domain,
        string reason) => new(
        id,
        "vendor.control-eligibility",
        deviceId,
        name,
        CapabilityAccessState.ReadOnly,
        AdapterExecutionContext.AdapterHost,
        ControlValueKind.Text,
        null,
        null,
        RiskLevel.Safe,
        EvidenceLevel.Detected,
        null,
        reason,
        CanResetToDefault: false,
        Domain: domain);

    private static void AddCompatibilityCard(
        List<CapabilityDescriptor> capabilities,
        string deviceId,
        HardwareCompatibilityMatch compatibility,
        ControlDomain domain)
    {
        if (!compatibility.IsRecognized)
        {
            return;
        }

        capabilities.Add(new CapabilityDescriptor(
            $"compatibility.{compatibility.FamilyId}:{deviceId}",
            "vendor.control-eligibility",
            deviceId,
            $"Compatibility profile: {compatibility.DisplayName}",
            CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.AdapterHost,
            ControlValueKind.Text,
            null,
            null,
            RiskLevel.Safe,
            EvidenceLevel.Detected,
            null,
            $"{compatibility.Summary} Classification is derived from Windows-reported identity and does not qualify a write endpoint.",
            CanResetToDefault: false,
            Domain: domain));
    }

    private static void AddGpuBoardRgbEligibility(
        List<CapabilityDescriptor> capabilities,
        string deviceId,
        HardwareCompatibilityMatch boardPartner)
    {
        if (!boardPartner.IsRecognized)
        {
            return;
        }

        capabilities.Add(new CapabilityDescriptor(
            $"gpu.rgb.eligibility:{boardPartner.FamilyId}:{deviceId}",
            "vendor.control-eligibility",
            deviceId,
            $"{boardPartner.DisplayName} RGB eligibility",
            CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.UserSession,
            ControlValueKind.Colour,
            null,
            null,
            RiskLevel.Guarded,
            EvidenceLevel.Detected,
            null,
            $"{boardPartner.Summary} RigPilot can use Windows Dynamic Lighting or the explicitly enabled local OpenRGB bridge only if that path enumerates the exact controller. No native GPU RGB protocol is enabled.",
            CanResetToDefault: false,
            Domain: ControlDomain.Lighting));
    }

    private static void Query(string className, Action<ManagementBaseObject> consume)
    {
        using ManagementObjectSearcher searcher = new($"SELECT * FROM {className}");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject row in results)
        {
            using (row)
            {
                consume(row);
            }
        }
    }

    private static string TryGetBiosVersion()
    {
        try
        {
            string? version = null;
            Query("Win32_BIOS", row => version ??= GetString(row, "SMBIOSBIOSVersion")?.Trim());
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static string? GetString(ManagementBaseObject row, string property)
    {
        object? value = row.Properties[property]?.Value;
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool IsRuntimeAvailable(params string[] names)
    {
        foreach (string name in names)
        {
            if (NativeLibrary.TryLoad(name, out nint handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        return false;
    }
}
