using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the storage-health mapping over synthetic Windows Storage provider
/// rows: counter-to-disk identity matching, enum name mapping, the
/// not-reported semantics for absent or implausible counters, and the honest
/// empty report. No WMI query runs in these tests.
/// </summary>
public sealed class StorageHealthProbeTests
{
    private static Dictionary<string, object?> Disk(string objectId, string name, int mediaType, int busType, int health, ulong size) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectId"] = objectId,
            ["FriendlyName"] = name,
            ["MediaType"] = (ushort)mediaType,
            ["BusType"] = (ushort)busType,
            ["HealthStatus"] = (ushort)health,
            ["Size"] = size,
        };

    private static Dictionary<string, object?> Counter(string objectId, int temperature, int wear, uint powerOnHours) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectId"] = objectId,
            ["Temperature"] = (ushort)temperature,
            ["Wear"] = (byte)wear,
            ["PowerOnHours"] = powerOnHours,
        };

    [Fact]
    public void CountersMatchTheirDiskByObjectIdIdentityTail()
    {
        StorageHealthReportV1 report = StorageHealthProbe.Build(
            [Disk("SPACES_PhysicalDisk.ObjectId=\"{s1}:PD:{aaaa-1}\"", "Samsung SSD 980 Pro", 4, 17, 0, 1_000_204_886_016)],
            [
                Counter("SPACES_StorageReliabilityCounter.ObjectId=\"{s1}:PD:{bbbb-2}\"", 99, 99, 9),
                Counter("SPACES_StorageReliabilityCounter.ObjectId=\"{s1}:PD:{aaaa-1}\"", 43, 2, 8760),
            ]);

        StorageDeviceHealthV1 device = Assert.Single(report.Devices);
        Assert.Equal("Samsung SSD 980 Pro", device.FriendlyName);
        Assert.Equal("SSD", device.MediaType);
        Assert.Equal("NVMe", device.BusType);
        Assert.Equal("Healthy", device.HealthStatus);
        Assert.Equal(1000.2, device.SizeGigabytes);
        Assert.Equal(43, device.TemperatureCelsius);
        Assert.Equal(2, device.WearPercent);
        Assert.Equal(8760, device.PowerOnHours);
    }

    [Fact]
    public void MissingCountersStayNullInsteadOfInvented()
    {
        StorageHealthReportV1 report = StorageHealthProbe.Build(
            [Disk("PD.ObjectId=\"{s1}:PD:{cccc-3}\"", "WDC HDD", 3, 11, 1, 4_000_000_000_000)],
            []);

        StorageDeviceHealthV1 device = Assert.Single(report.Devices);
        Assert.Equal("HDD", device.MediaType);
        Assert.Equal("SATA", device.BusType);
        Assert.Equal("Warning", device.HealthStatus);
        Assert.Null(device.TemperatureCelsius);
        Assert.Null(device.WearPercent);
        Assert.Null(device.PowerOnHours);
    }

    [Fact]
    public void ZeroTemperatureMeansNotReported()
    {
        StorageHealthReportV1 report = StorageHealthProbe.Build(
            [Disk("PD.ObjectId=\"{s1}:PD:{dddd-4}\"", "SATA SSD", 4, 11, 0, 500_000_000_000)],
            [Counter("RC.ObjectId=\"{s1}:PD:{dddd-4}\"", 0, 0, 0)]);

        StorageDeviceHealthV1 device = Assert.Single(report.Devices);
        Assert.Null(device.TemperatureCelsius);
        Assert.Equal(0, device.WearPercent); // 0% wear is a real reading
        Assert.Null(device.PowerOnHours);    // 0 hours is not
    }

    [Fact]
    public void EmptyProviderYieldsAnHonestEmptyReport()
    {
        StorageHealthReportV1 report = StorageHealthProbe.Build([], []);

        Assert.Empty(report.Devices);
        Assert.Contains("no physical drives", report.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("no braces here", null)]
    [InlineData("X.ObjectId=\"{s1}:PD:{eeee-5}\"", "{eeee-5}")]
    public void IdentityTailExtractionIsExact(string? objectId, string? expected)
    {
        Assert.Equal(expected, StorageHealthProbe.ExtractIdentityTail(objectId));
    }
}
