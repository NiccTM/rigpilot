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

    /// <summary>
    /// Observed on the reference machine: LibreHardwareMonitor reports the fourth NVMe
    /// device with a name that is 40 spaces, so the Devices page sensor tree drew a row
    /// carrying nothing but "11 sensors" and sorted it above every named device. A branch
    /// the user cannot identify is not a tree. The device ID is the fallback because it is
    /// the one value that is always present and always unique.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("                                        ")]
    public void ADeviceWhoseNameIsBlankOrWhitespaceFallsBackToItsIdentifier(string reportedName)
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("lhm.device:/nvme/3", reportedName, DeviceKind.Storage)],
            [Sensor("s1", "lhm.device:/nvme/3", "Temperature", 41, "°C")]);

        SensorTreeDevice device = Assert.Single(tree);
        Assert.Equal("lhm.device:/nvme/3", device.DeviceName);
        Assert.False(string.IsNullOrWhiteSpace(device.DeviceName));
    }

    /// <summary>
    /// The fallback has to be total. Stopping at the device ID left the reference machine
    /// still drawing a nameless "11 sensors" row sorted above every named device, because
    /// the value the chain trusted to be present was itself blank.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public void ADeviceWithNeitherANameNorAnIdentifierIsStillLabelled(string blankId)
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device(blankId, "   ", DeviceKind.Storage)],
            [Sensor("s1", blankId, "Temperature", 41, "°C")]);

        SensorTreeDevice device = Assert.Single(tree);
        Assert.Equal(SensorTree.UnnamedDeviceLabel, device.DeviceName);
        Assert.False(string.IsNullOrWhiteSpace(device.DeviceName));
    }

    /// <summary>
    /// The actual defect. LibreHardwareMonitor reports the reference machine's fourth NVMe
    /// with a name of forty NUL characters. NUL is a control character, NOT whitespace, so
    /// <c>IsNullOrWhiteSpace</c> answered false, the string counted as a usable name, and
    /// WPF rendered it as nothing - a row labelled only "11 sensors", sorted above every
    /// named device because U+0000 orders before 'A'. Every prior fallback was correct and
    /// simply never reached.
    /// </summary>
    [Theory]
    [InlineData("\0")]
    [InlineData("\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n\t")]
    [InlineData("")]
    [InlineData(" \0 \t ")]
    public void ADeviceNameWithNoVisibleCharactersFallsBackToItsIdentifier(string invisibleName)
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("lhm.device:/nvme/3", invisibleName, DeviceKind.Storage)],
            [Sensor("s1", "lhm.device:/nvme/3", "Temperature", 41, "°C")]);

        SensorTreeDevice device = Assert.Single(tree);
        Assert.Equal("lhm.device:/nvme/3", device.DeviceName);
        Assert.True(HasVisibleText(device.DeviceName));
    }

    /// <summary>A dirty-but-readable name keeps its text rather than falling back.</summary>
    [Fact]
    public void ControlCharactersAreStrippedFromAnOtherwiseReadableName()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("lhm.device:/nvme/3", "\0Sabrent Rocket 4.0 1TB\0", DeviceKind.Storage)],
            [Sensor("s1", "lhm.device:/nvme/3", "Temperature", 41, "°C")]);

        Assert.Equal("Sabrent Rocket 4.0 1TB", Assert.Single(tree).DeviceName);
    }

    /// <summary>The telemetry must survive the rename; a nameless device is never dropped.</summary>
    [Fact]
    public void EverySensorIsPreservedUnderAFallenBackLabel()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("lhm.device:/nvme/3", new string('\0', 40), DeviceKind.Storage)],
            [
                Sensor("s1", "lhm.device:/nvme/3", "Temperature", 41, "°C"),
                Sensor("s2", "lhm.device:/nvme/3", "Available Spare", 100, "%"),
                Sensor("s3", "lhm.device:/nvme/3", "Read Rate", 12, "MB/s"),
            ]);

        SensorTreeDevice device = Assert.Single(tree);
        Assert.Equal(3, device.SensorCount);
        Assert.True(HasVisibleText(device.DeviceName));
    }

    /// <summary>
    /// The invariant the Devices page depends on, asserted over a realistic mixed snapshot:
    /// every rendered group header carries text a person can actually see.
    /// </summary>
    [Fact]
    public void EveryDeviceInTheTreeHasAVisibleLabel()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [
                Device("lhm.device:/amdcpu/0", "AMD Ryzen 7 5800X", DeviceKind.Cpu),
                Device("lhm.device:/nvme/3", new string('\0', 40), DeviceKind.Storage),
                Device("", "   ", DeviceKind.Unknown),
                Device("lhm.device:/ssd/0", "WDC  WDS200T2B0A-00SM50 ", DeviceKind.Storage),
            ],
            [
                Sensor("a", "lhm.device:/amdcpu/0", "Core", 60, "°C"),
                Sensor("b", "lhm.device:/nvme/3", "Temperature", 41, "°C"),
                Sensor("c", "", "Orphan", 1, "°C"),
                Sensor("d", "lhm.device:/ssd/0", "Temperature", 35, "°C"),
            ]);

        Assert.Equal(4, tree.Count);
        Assert.All(tree, device => Assert.True(
            HasVisibleText(device.DeviceName),
            $"Device '{device.DeviceId}' rendered a header with no visible text."));
        Assert.Contains(tree, device => device.DeviceName == SensorTree.UnnamedDeviceLabel);
    }

    private static bool HasVisibleText(string? value) =>
        value is not null
        && value.Any(character => !char.IsWhiteSpace(character) && !char.IsControl(character));

    /// <summary>A blank name must stay findable by the identifier the tree now shows.</summary>
    [Fact]
    public void AFallenBackDeviceNameIsStillSearchable()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [Device("lhm.device:/nvme/3", "   ", DeviceKind.Storage)],
            [Sensor("s1", "lhm.device:/nvme/3", "Temperature", 41, "°C")]);

        Assert.Single(SensorTree.Filter(tree, "nvme"));
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
