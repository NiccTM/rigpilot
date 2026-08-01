using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class CompatibilityReportBuilderTests
{
    [Fact]
    public void RemovesSensitivePropertiesAndText()
    {
        HardwareDevice device = new(
            "device",
            "GPU",
            DeviceKind.Gpu,
            "Vendor",
            "Model",
            "PCI\\VEN_1234",
            new Dictionary<string, string>
            {
                ["serialNumber"] = "secret",
                ["driverVersion"] = "1.2.3",
                ["installPath"] = @"C:\Users\Alice\tool"
            });
        HardwareSnapshot snapshot = new(
            DateTimeOffset.UtcNow,
            [device], [], [], [], [], []);

        CompatibilityReportV1 report = CompatibilityReportBuilder.Build(
            snapshot,
            "0.2",
            new Dictionary<string, string> { ["framework"] = "10", ["userName"] = "Alice" },
            [@"C:\Users\Alice\file.log contacted 192.168.1.2 from 00:11:22:33:44:55"],
            userApproved: false);

        Assert.DoesNotContain("serialNumber", report.Snapshot.Devices[0].Properties.Keys);
        Assert.DoesNotContain("installPath", report.Snapshot.Devices[0].Properties.Keys);
        Assert.Equal("1.2.3", report.Snapshot.Devices[0].Properties["driverVersion"]);
        Assert.DoesNotContain("userName", report.Runtime.Keys);
        Assert.Contains("[redacted]", report.SanitisedLogLines[0]);
        Assert.Contains("[redacted-ip]", report.SanitisedLogLines[0]);
        Assert.Contains("[redacted-mac]", report.SanitisedLogLines[0]);
        Assert.False(report.UserApproved);
    }

    [Fact]
    public void RemovesNetworkDevicesAndTheirSensorsAndCapabilities()
    {
        HardwareDevice network = new(
            "network-device",
            "Private network adapter name",
            DeviceKind.Network,
            null,
            null,
            null,
            new Dictionary<string, string>());
        SensorSample sensor = new(
            "network-sensor",
            "adapter",
            network.Id,
            "Network Throughput",
            DateTimeOffset.UtcNow,
            42,
            "B/s",
            SensorQuality.Good,
            TimeSpan.Zero);
        CapabilityDescriptor capability = new(
            "network-capability",
            "adapter",
            network.Id,
            "Network control",
            CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.UserSession,
            ControlValueKind.Numeric,
            null,
            "B/s",
            RiskLevel.Safe,
            EvidenceLevel.Detected,
            null,
            "Read-only",
            false);
        HardwareSnapshot snapshot = new(
            DateTimeOffset.UtcNow,
            [network],
            [capability],
            [sensor],
            [],
            [],
            []);

        CompatibilityReportV1 report = CompatibilityReportBuilder.Build(
            snapshot,
            "0.2",
            new Dictionary<string, string>(),
            [],
            userApproved: false);

        Assert.Empty(report.Snapshot.Devices);
        Assert.Empty(report.Snapshot.Sensors);
        Assert.Empty(report.Snapshot.Capabilities);
    }

    /// <summary>
    /// Adapter health text is adapter-authored free text — it carries load
    /// failures, declared file-sensor locations, and raw exception messages, all
    /// of which routinely contain a full user path. It travels in the same
    /// snapshot as the warnings that are already redacted, and the report exists
    /// to be shared, so it has to be redacted on the same terms.
    /// </summary>
    [Fact]
    public void RedactsAdapterHealthText()
    {
        AdapterHealth health = new(
            "librehardwaremonitor",
            false,
            DateTimeOffset.UtcNow,
            @"Could not load C:\Users\Alice\AppData\Local\pack\lhm.dll.",
            [
                @"Declare file sensors in C:\Users\Alice\sensors.json.",
                "Reached 10.42.7.9 from 00:11:22:33:44:55."
            ]);
        HardwareSnapshot snapshot = new(
            DateTimeOffset.UtcNow,
            [], [], [], [], [],
            [health]);

        CompatibilityReportV1 report = CompatibilityReportBuilder.Build(
            snapshot,
            "0.2",
            new Dictionary<string, string>(),
            [],
            userApproved: false);

        AdapterHealth redacted = Assert.Single(report.Snapshot.AdapterHealth);
        Assert.Equal("librehardwaremonitor", redacted.AdapterId);
        Assert.DoesNotContain("Alice", redacted.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", redacted.Message);
        Assert.DoesNotContain("Alice", redacted.Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted-ip]", redacted.Errors[1]);
        Assert.Contains("[redacted-mac]", redacted.Errors[1]);
    }
}
