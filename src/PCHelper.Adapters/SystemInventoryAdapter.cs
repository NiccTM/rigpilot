using System.Globalization;
using System.Management;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

public sealed class SystemInventoryAdapter : IHardwareAdapter
{
    public AdapterManifest Manifest { get; } = new(
        "windows.inventory",
        "Windows Inventory",
        "0.5.0-alpha",
        "GPL-3.0-only",
        null,
        AdapterExecutionContext.SystemService,
        ["Windows 10 22H2 x64", "Windows 11 x64"],
        ["Inventory"]);

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<HardwareDevice> devices = [];
        List<DiagnosticWarning> warnings = [];

        QuerySingle("Win32_OperatingSystem", row =>
        {
            string caption = GetString(row, "Caption") ?? "Windows";
            string build = GetString(row, "BuildNumber") ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            devices.Add(new HardwareDevice(
                "windows:operating-system",
                caption,
                DeviceKind.OperatingSystem,
                "Microsoft",
                GetString(row, "Version"),
                null,
                Properties(
                    ("buildNumber", build),
                    ("architecture", GetString(row, "OSArchitecture")))));

            if (string.Equals(build, "19045", StringComparison.Ordinal))
            {
                warnings.Add(new DiagnosticWarning(
                    "WINDOWS_10_EOL",
                    "Warning",
                    "Windows 10 22H2 is a compatibility target and no longer receives standard Microsoft security support.",
                    "Use a supported Windows 11 release where possible."));
            }
        }, warnings);

        QuerySingle("Win32_BaseBoard", row =>
        {
            string manufacturer = GetString(row, "Manufacturer") ?? "Unknown";
            string product = GetString(row, "Product") ?? "Unknown motherboard";
            HardwareCompatibilityMatch compatibility = HardwareCompatibilityCatalog.ClassifyMotherboard(manufacturer, product);
            devices.Add(new HardwareDevice(
                StableIds.Create("motherboard", manufacturer, product, GetString(row, "Version")),
                $"{manufacturer} {product}".Trim(),
                DeviceKind.Motherboard,
                manufacturer,
                product,
                null,
                PropertiesWithCompatibility(
                    compatibility,
                    ("version", GetString(row, "Version")))));
        }, warnings);

        QuerySingle("Win32_BIOS", row =>
        {
            string manufacturer = GetString(row, "Manufacturer") ?? "Unknown";
            string version = GetString(row, "SMBIOSBIOSVersion") ?? "Unknown";
            devices.Add(new HardwareDevice(
                StableIds.Create("bios", manufacturer, version),
                $"BIOS {version}",
                DeviceKind.Bios,
                manufacturer,
                version,
                null,
                Properties(("releaseDate", FormatWmiDate(GetString(row, "ReleaseDate"))))));
        }, warnings);

        QueryMany("Win32_Processor", row =>
        {
            string name = GetString(row, "Name")?.Trim() ?? "Unknown processor";
            string manufacturer = GetString(row, "Manufacturer") ?? "Unknown";
            HardwareCompatibilityMatch compatibility = HardwareCompatibilityCatalog.ClassifyCpu(manufacturer, name);
            devices.Add(new HardwareDevice(
                StableIds.Create("cpu", manufacturer, name),
                name,
                DeviceKind.Cpu,
                manufacturer,
                name,
                null,
                PropertiesWithCompatibility(
                    compatibility,
                    ("cores", GetString(row, "NumberOfCores")),
                    ("logicalProcessors", GetString(row, "NumberOfLogicalProcessors")),
                    ("maxClockMHz", GetString(row, "MaxClockSpeed")))));
        }, warnings);

        QueryMany("Win32_VideoController", row =>
        {
            string name = GetString(row, "Name")?.Trim() ?? "Unknown display adapter";
            string pnpId = GetString(row, "PNPDeviceID") ?? string.Empty;
            string? manufacturer = InferGpuManufacturer(name);
            HardwareCompatibilityMatch compatibility = HardwareCompatibilityCatalog.ClassifyGpu(manufacturer, name);
            HardwareCompatibilityMatch boardPartner = HardwareCompatibilityCatalog.ClassifyGpuBoardPartner(
                pnpId,
                GetString(row, "AdapterCompatibility"),
                name);
            devices.Add(new HardwareDevice(
                StableIds.Create("gpu", name, pnpId),
                name,
                DeviceKind.Gpu,
                manufacturer,
                name,
                pnpId,
                PropertiesWithCompatibility(
                    compatibility,
                    ("driverVersion", GetString(row, "DriverVersion")),
                    ("adapterRamBytes", GetString(row, "AdapterRAM")),
                    ("boardPartnerFamily", boardPartner.IsRecognized ? boardPartner.FamilyId : null),
                    ("boardPartnerLabel", boardPartner.IsRecognized ? boardPartner.DisplayName : null),
                    ("boardPartnerEvidence", boardPartner.IsRecognized ? boardPartner.Summary : null))));
        }, warnings);

        return Task.FromResult(new AdapterProbeResult(Manifest, devices, [], warnings));
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SensorSample>>([]);

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The inventory adapter is read-only.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The inventory adapter is read-only.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The inventory adapter is read-only.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(
        new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "Windows inventory is available.", []));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void QuerySingle(string className, Action<ManagementBaseObject> consume, ICollection<DiagnosticWarning> warnings) =>
        QueryMany(className, consume, warnings, takeOne: true);

    private static void QueryMany(
        string className,
        Action<ManagementBaseObject> consume,
        ICollection<DiagnosticWarning> warnings,
        bool takeOne = false)
    {
        try
        {
            using ManagementObjectSearcher searcher = new($"SELECT * FROM {className}");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                using (row)
                {
                    consume(row);
                }

                if (takeOne)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            warnings.Add(new DiagnosticWarning(
                "CIM_QUERY_FAILED",
                "Warning",
                $"{className} inventory failed: {exception.Message}",
                "Run the read-only probe again or inspect Windows Management Instrumentation health."));
        }
    }

    private static string? GetString(ManagementBaseObject row, string property)
    {
        object? value = row.Properties[property]?.Value;
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string? FormatWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(value).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Properties(params (string Key, string? Value)[] values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .ToDictionary(value => value.Key, value => value.Value!, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> PropertiesWithCompatibility(
        HardwareCompatibilityMatch compatibility,
        params (string Key, string? Value)[] values)
    {
        Dictionary<string, string> properties = Properties(values);
        HardwareCompatibilityCatalog.AddToProperties(properties, compatibility);
        return properties;
    }

    private static string? InferGpuManufacturer(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        return name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : null;
    }
}
