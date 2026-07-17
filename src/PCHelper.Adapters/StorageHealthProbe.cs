using System.Management;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only drive-health snapshot from the Windows Storage provider
/// (root\Microsoft\Windows\Storage): MSFT_PhysicalDisk for identity, class,
/// and OS health status; MSFT_StorageReliabilityCounter for temperature, wear,
/// and power-on hours where the drive exposes them. Counters are matched to
/// disks by the shared identity tail of their provider ObjectId. There is no
/// storage write path anywhere in RigPilot; a query failure degrades to an
/// explanatory report, never an exception to the caller.
/// </summary>
public static class StorageHealthProbe
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public static StorageHealthReportV1 Read()
    {
        List<IReadOnlyDictionary<string, object?>> disks;
        List<IReadOnlyDictionary<string, object?>> counters;
        try
        {
            disks = Query("SELECT * FROM MSFT_PhysicalDisk");
            counters = Query("SELECT * FROM MSFT_StorageReliabilityCounter");
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return StorageHealthReportV1.Unavailable(
                $"The Windows Storage provider could not be queried: {exception.GetType().Name}.");
        }

        return Build(disks, counters);
    }

    /// <summary>Pure mapping core, separated so tests can supply provider rows.</summary>
    public static StorageHealthReportV1 Build(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> physicalDisks,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> reliabilityCounters)
    {
        ArgumentNullException.ThrowIfNull(physicalDisks);
        ArgumentNullException.ThrowIfNull(reliabilityCounters);
        List<StorageDeviceHealthV1> devices = [];
        foreach (IReadOnlyDictionary<string, object?> disk in physicalDisks)
        {
            string? identity = ExtractIdentityTail(GetString(disk, "ObjectId"));
            IReadOnlyDictionary<string, object?>? counter = identity is null
                ? null
                : reliabilityCounters.FirstOrDefault(row =>
                    string.Equals(ExtractIdentityTail(GetString(row, "ObjectId")), identity, StringComparison.OrdinalIgnoreCase));

            double? temperature = GetDouble(counter, "Temperature");
            if (temperature is <= 0 or > 150)
            {
                temperature = null; // 0 and out-of-band values mean "not reported", not a reading
            }

            double? wear = GetDouble(counter, "Wear");
            double? powerOnHours = GetDouble(counter, "PowerOnHours");
            double sizeBytes = GetDouble(disk, "Size") ?? 0;
            devices.Add(new StorageDeviceHealthV1(
                GetString(disk, "FriendlyName") ?? "Unknown drive",
                MediaTypeName(GetDouble(disk, "MediaType")),
                BusTypeName(GetDouble(disk, "BusType")),
                HealthStatusName(GetDouble(disk, "HealthStatus")),
                Math.Round(sizeBytes / 1_000_000_000d, 1),
                temperature,
                wear is >= 0 and <= 100 ? (int)wear : null,
                powerOnHours is > 0 ? powerOnHours : null));
        }

        return new StorageHealthReportV1(
            StorageHealthReportV1.CurrentSchemaVersion,
            devices,
            devices.Count > 0
                ? $"{devices.Count} physical drive(s) reported by the Windows Storage provider. Read-only evidence; RigPilot has no storage write path."
                : "The Windows Storage provider reported no physical drives.");
    }

    /// <summary>
    /// The identity tail shared by a disk and its reliability counter: the
    /// final <c>{…}</c> group of the provider ObjectId (the per-device unique
    /// id). Null when the ObjectId has no brace group.
    /// </summary>
    public static string? ExtractIdentityTail(string? objectId)
    {
        if (string.IsNullOrEmpty(objectId))
        {
            return null;
        }

        int close = objectId.LastIndexOf('}');
        if (close < 0)
        {
            return null;
        }

        int open = objectId.LastIndexOf('{', close);
        return open < 0 ? null : objectId[open..(close + 1)];
    }

    public static string MediaTypeName(double? mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unspecified",
    };

    public static string BusTypeName(double? busType) => busType switch
    {
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        _ => "Other",
    };

    public static string HealthStatusName(double? healthStatus) => healthStatus switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };

    private static List<IReadOnlyDictionary<string, object?>> Query(string wql)
    {
        List<IReadOnlyDictionary<string, object?>> rows = [];
        using ManagementObjectSearcher searcher = new(StorageNamespace, wql);
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                Dictionary<string, object?> row = new(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in item.Properties)
                {
                    row[property.Name] = property.Value;
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?>? row, string key) =>
        row is not null && row.TryGetValue(key, out object? value) ? value?.ToString() : null;

    private static double? GetDouble(IReadOnlyDictionary<string, object?>? row, string key)
    {
        if (row is null || !row.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
