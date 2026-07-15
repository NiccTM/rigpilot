using System.IO;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using PCHelper.Contracts;
using WindowsColor = Windows.UI.Color;

namespace PCHelper.App;

public sealed record DynamicLightingDevice(
    string Id,
    string Name,
    int LampCount,
    bool IsEnabled,
    double BrightnessLevel,
    string Kind);

public static class DynamicLightingBridge
{
    public static async Task<IReadOnlyList<DynamicLightingDevice>> ProbeAsync(CancellationToken cancellationToken)
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
            .AsTask(cancellationToken);
        List<DynamicLightingDevice> results = [];
        foreach (DeviceInformation device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LampArray? array = await LampArray.FromIdAsync(device.Id).AsTask(cancellationToken);
            if (array is null)
            {
                continue;
            }
            results.Add(new DynamicLightingDevice(
                device.Id,
                device.Name,
                array.LampCount,
                array.IsEnabled,
                array.BrightnessLevel,
                array.LampArrayKind.ToString()));
        }
        return results;
    }

    public static async Task ApplyStaticSceneAsync(
        LightingSceneV1 scene,
        string rgbHex,
        CancellationToken cancellationToken)
    {
        if (scene.BrightnessPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(scene), "Scene brightness must be 0-100%.");
        }
        WindowsColor colour = ParseColour(rgbHex);
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
            .AsTask(cancellationToken);
        Dictionary<string, DeviceInformation> byId = devices.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, LightingZoneV1> deviceZones in scene.Zones.GroupBy(zone => zone.DeviceId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(deviceZones.Key, out DeviceInformation? device))
            {
                throw new InvalidOperationException($"Dynamic Lighting device '{deviceZones.Key}' is unavailable.");
            }
            LampArray? array = await LampArray.FromIdAsync(device.Id).AsTask(cancellationToken)
                ?? throw new InvalidOperationException($"Dynamic Lighting device '{device.Name}' could not be opened.");
            array.IsEnabled = !scene.DisabledDeviceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase);
            array.BrightnessLevel = scene.BrightnessPercent / 100;
            if (!array.IsEnabled)
            {
                continue;
            }
            foreach (LightingZoneV1 zone in deviceZones)
            {
                int[] indices = zone.LedIndices.Distinct().Order().ToArray();
                if (indices.Any(index => index < 0 || index >= array.LampCount))
                {
                    throw new InvalidDataException($"Lighting zone '{zone.Id}' contains an LED index outside device bounds.");
                }
                array.SetSingleColorForIndices(colour, indices);
            }
        }
    }

    private static WindowsColor ParseColour(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string text = value.TrimStart('#');
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            throw new FormatException("Lighting colour must be a six-digit RGB hexadecimal value.");
        }
        return WindowsColor.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
