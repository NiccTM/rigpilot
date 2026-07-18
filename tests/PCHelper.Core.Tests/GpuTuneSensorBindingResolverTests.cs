using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class GpuTuneSensorBindingResolverTests
{
    [Fact]
    public void ResolvesOnlyTheSingleNvmlGpuAndItsMatchingTelemetryAliases()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HardwareDevice nvml = Device("nvidia:uuid", "NVIDIA GeForce RTX 3090", uuid: "GPU-123");
        HardwareDevice lhm = Device("lhm:gpu", "NVIDIA GeForce RTX 3090");
        HardwareSnapshot snapshot = new(
            now,
            [nvml, lhm],
            [],
            [
                Sensor("temp", lhm.Id, "GPU Core", 60, "°C", now),
                Sensor("load", nvml.Id, "GPU Core Load", 80, "%", now),
                Sensor("core", lhm.Id, "GPU Core Clock", 1800, "MHz", now),
                Sensor("memory", lhm.Id, "GPU Memory Clock", 10000, "MHz", now),
                Sensor("power", nvml.Id, "GPU Board Power", 300, "W", now),
                Sensor("other", "intel:gpu", "GPU Core", 40, "°C", now)
            ],
            [],
            [],
            []);

        TuneSensorBindingV2 binding = GpuTuneSensorBindingResolver.Resolve(snapshot, "nvidia:gpu-0");

        Assert.Equal("load", binding.UtilizationSensorId);
        Assert.Equal("core", binding.CoreClockSensorId);
        Assert.Equal("memory", binding.MemoryClockSensorId);
        Assert.Equal("power", binding.PowerSensorId);
        Assert.DoesNotContain("other", binding.TemperatureSensorIds);
        Assert.Contains("nvidia:gpu-0", binding.BoundDeviceIds);
    }

    [Fact]
    public void RefusesAmbiguousNvmlGpuIdentity()
    {
        HardwareSnapshot snapshot = new(
            DateTimeOffset.UtcNow,
            [
                Device("gpu:1", "NVIDIA GeForce RTX 3090", "GPU-1"),
                Device("gpu:2", "NVIDIA GeForce RTX 3090", "GPU-2")
            ],
            [], [], [], [], []);

        Assert.Throws<InvalidOperationException>(() =>
            GpuTuneSensorBindingResolver.Resolve(snapshot, "nvidia:gpu-0"));
    }

    private static HardwareDevice Device(string id, string name, string? uuid = null) => new(
        id,
        name,
        DeviceKind.Gpu,
        "NVIDIA",
        name,
        uuid,
        uuid is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["uuid"] = uuid });

    private static SensorSample Sensor(
        string id,
        string deviceId,
        string name,
        double value,
        string unit,
        DateTimeOffset now) => new(
            id, "test", deviceId, name, now, value, unit, SensorQuality.Good, TimeSpan.Zero);
}
