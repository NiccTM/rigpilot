using PCHelper.Contracts;

namespace PCHelper.Core;

public static class GpuTuneSensorBindingResolver
{
    public static TuneSensorBindingV2 Resolve(HardwareSnapshot snapshot, string targetDeviceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        HardwareDevice[] nvmlDevices = snapshot.Devices
            .Where(device => device.Kind == DeviceKind.Gpu
                && device.Properties.ContainsKey("uuid")
                && (string.Equals(device.Manufacturer, "NVIDIA", StringComparison.OrdinalIgnoreCase)
                    || device.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    || device.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (nvmlDevices.Length != 1)
        {
            throw new InvalidOperationException(
                $"Auto OC requires one exact NVIDIA NVML identity; found {nvmlDevices.Length}. Multi-GPU or ambiguous systems remain blocked.");
        }

        HardwareDevice physical = nvmlDevices[0];
        string physicalModel = NormalizeModel(physical.Model ?? physical.Name);
        HashSet<string> boundDeviceIds = snapshot.Devices
            .Where(device => device.Kind == DeviceKind.Gpu
                && (string.Equals(device.Id, physical.Id, StringComparison.Ordinal)
                    || string.Equals(NormalizeModel(device.Model ?? device.Name), physicalModel, StringComparison.OrdinalIgnoreCase)))
            .Select(device => device.Id)
            .Append(targetDeviceId)
            .ToHashSet(StringComparer.Ordinal);
        SensorSample[] sensors = snapshot.Sensors
            .Where(sensor => boundDeviceIds.Contains(sensor.DeviceId))
            .ToArray();

        string[] temperatures = sensors
            .Where(sensor => IsUnit(sensor, "°C", "Celsius")
                && (sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("Memory Junction", StringComparison.OrdinalIgnoreCase)))
            .Select(sensor => sensor.SensorId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string utilization = SelectRequired(
            sensors,
            sensor => string.Equals(sensor.Unit, "%", StringComparison.OrdinalIgnoreCase)
                && (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("GPU Load", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("Utilization", StringComparison.OrdinalIgnoreCase)),
            "GPU utilization");
        string coreClock = SelectRequired(
            sensors,
            sensor => string.Equals(sensor.Unit, "MHz", StringComparison.OrdinalIgnoreCase)
                && sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase),
            "GPU core clock");
        string memoryClock = SelectRequired(
            sensors,
            sensor => string.Equals(sensor.Unit, "MHz", StringComparison.OrdinalIgnoreCase)
                && sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase),
            "GPU memory clock");
        string? power = sensors
            .Where(sensor => string.Equals(sensor.Unit, "W", StringComparison.OrdinalIgnoreCase)
                && (sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                    || sensor.Name.Contains("Board", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(sensor => SensorPreference(sensor.Name, "Package", "Board", "GPU"))
            .Select(sensor => sensor.SensorId)
            .FirstOrDefault();
        if (temperatures.Length == 0)
        {
            throw new InvalidOperationException("Auto OC requires an exact NVIDIA GPU temperature sensor.");
        }

        return new TuneSensorBindingV2(
            TuneSensorBindingV2.CurrentSchemaVersion,
            targetDeviceId,
            boundDeviceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            temperatures,
            utilization,
            coreClock,
            memoryClock,
            power);
    }

    private static string SelectRequired(
        IEnumerable<SensorSample> sensors,
        Func<SensorSample, bool> predicate,
        string label) => sensors
            .Where(predicate)
            .OrderBy(sensor => SensorPreference(sensor.Name, "GPU Core", "GPU", string.Empty))
            .Select(sensor => sensor.SensorId)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Auto OC requires an exact {label} sensor.");

    private static int SensorPreference(string name, params string[] orderedTokens)
    {
        for (int index = 0; index < orderedTokens.Length; index++)
        {
            if (name.Contains(orderedTokens[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return orderedTokens.Length;
    }

    private static bool IsUnit(SensorSample sensor, params string[] units) =>
        units.Contains(sensor.Unit, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeModel(string value) => value
        .Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("GeForce", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Graphics", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Adapter", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim();
}
