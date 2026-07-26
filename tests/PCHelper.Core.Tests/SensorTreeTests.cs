using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The suite captures every sensor but only ever showed a flat list plus a curated subset.
/// On the reference machine that is 235 rows sorted by nothing in particular, which cannot
/// answer "what is this device doing?". These tests pin the device → category → sensor
/// hierarchy and the search that has to keep it intact.
/// </summary>
public sealed class SensorTreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static HardwareDevice Device(string id, string name, DeviceKind kind = DeviceKind.Gpu) =>
        new(id, name, kind, null, null, null, new Dictionary<string, string>());

    private static SensorSample Sensor(string id, string device, string name, double value, string unit) =>
        new(id, "adapter", device, name, Now, value, unit, SensorQuality.Good, TimeSpan.Zero);

    [Fact]
    public void SensorsAreGroupedUnderTheirDeviceAndCategory()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("gpu-0", "NVIDIA GeForce RTX 3090")],
            [
                Sensor("s1", "gpu-0", "GPU Hot Spot", 58, "°C"),
                Sensor("s2", "gpu-0", "GPU Core", 48, "°C"),
                Sensor("s3", "gpu-0", "GPU Fan", 1200, "RPM"),
            ]);

        SensorTreeDevice device = Assert.Single(tree);
        Assert.Equal("NVIDIA GeForce RTX 3090", device.DeviceName);
        Assert.Equal(3, device.SensorCount);
        Assert.Equal(["Fans", "Temperatures"], device.Groups.Select(group => group.Name));
        // Readings sort by name so the same sensor is always in the same place.
        Assert.Equal(["GPU Core", "GPU Hot Spot"], device.Groups[1].Readings.Select(reading => reading.Name));
    }

    [Theory]
    [InlineData("°C", "Temperatures")]
    [InlineData("RPM", "Fans")]
    [InlineData("W", "Power")]
    [InlineData("V", "Voltages")]
    [InlineData("MHz", "Clocks")]
    [InlineData("%", "Load and duty")]
    public void UnitsMapToReadableCategories(string unit, string expected)
    {
        Assert.Equal(expected, SensorTree.CategoryFor(unit));
    }

    [Fact]
    public void AnUnknownUnitKeepsItsOwnHeadingRatherThanBecomingOther()
    {
        // A new sensor type should appear correctly grouped without a code change, and
        // without being swept into an "Other" bucket that hides what it is.
        Assert.Equal("kPa", SensorTree.CategoryFor("kPa"));
    }

    [Fact]
    public void ASensorWhoseDeviceIsMissingIsStillShown()
    {
        // Dropping a reading because its device is absent from the snapshot would be the one
        // failure a sensor tree must not have.
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [],
            [Sensor("s1", "ghost-device", "Orphan Temp", 40, "°C")]);

        SensorTreeDevice device = Assert.Single(tree);
        Assert.Equal("ghost-device", device.DeviceName);
        Assert.Equal(1, device.SensorCount);
    }

    [Fact]
    public void ADeviceWithABlankNameFallsBackToItsIdentifier()
    {
        // Observed in the first render of this view: a present-but-unnamed device produced a
        // nameless row labelled only with its sensor count, which is unidentifiable.
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("lpc-0", "   ")],
            [Sensor("s1", "lpc-0", "Fan #1", 900, "RPM")]);

        Assert.Equal("lpc-0", Assert.Single(tree).DeviceName);
    }

    [Fact]
    public void DevicesWithNoSensorsAreOmitted()
    {
        Assert.Empty(SensorTree.Build([Device("gpu-0", "Idle GPU")], []));
    }

    [Fact]
    public void SearchingForADeviceKeepsAllOfItsSensors()
    {
        // Searching for a GPU means wanting to see that GPU, not just the sensors whose names
        // happen to repeat the device name.
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("gpu-0", "RTX 3090"), Device("cpu-0", "Ryzen 5800X", DeviceKind.Cpu)],
            [
                Sensor("s1", "gpu-0", "Hot Spot", 58, "°C"),
                Sensor("s2", "gpu-0", "Fan", 1200, "RPM"),
                Sensor("s3", "cpu-0", "Tctl", 65, "°C"),
            ]);

        SensorTreeDevice match = Assert.Single(SensorTree.Filter(tree, "3090"));
        Assert.Equal(2, match.SensorCount);
    }

    [Fact]
    public void SearchingForASensorNarrowsWithinTheDeviceButKeepsTheHierarchy()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("gpu-0", "RTX 3090")],
            [
                Sensor("s1", "gpu-0", "Hot Spot", 58, "°C"),
                Sensor("s2", "gpu-0", "Memory Junction", 56, "°C"),
                Sensor("s3", "gpu-0", "Fan", 1200, "RPM"),
            ]);

        SensorTreeDevice match = Assert.Single(SensorTree.Filter(tree, "hot"));
        Assert.Equal("RTX 3090", match.DeviceName);
        SensorTreeGroup group = Assert.Single(match.Groups);
        Assert.Equal("Temperatures", group.Name);
        Assert.Equal("Hot Spot", Assert.Single(group.Readings).Name);
    }

    [Fact]
    public void AnEmptyQueryReturnsTheWholeTree()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("gpu-0", "RTX 3090")],
            [Sensor("s1", "gpu-0", "Hot Spot", 58, "°C")]);

        Assert.Same(tree, SensorTree.Filter(tree, "   "));
    }
}
