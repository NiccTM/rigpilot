using System.Text.RegularExpressions;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static partial class CompatibilityReportBuilder
{
    private static readonly string[] SensitivePropertyFragments =
    [
        "serial", "hostname", "computername", "username", "user", "path", "mac", "ipaddress", "ssid"
    ];

    public static CompatibilityReportV1 Build(
        HardwareSnapshot snapshot,
        string appVersion,
        IReadOnlyDictionary<string, string> runtime,
        IEnumerable<string> logLines,
        bool userApproved)
    {
        HashSet<string> excludedDeviceIds = snapshot.Devices
            .Where(device => device.Kind == DeviceKind.Network)
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        HardwareSnapshot redacted = snapshot with
        {
            Devices = snapshot.Devices
                .Where(device => !excludedDeviceIds.Contains(device.Id))
                .Select(RedactDevice)
                .ToArray(),
            Capabilities = snapshot.Capabilities
                .Where(capability => !excludedDeviceIds.Contains(capability.DeviceId))
                .ToArray(),
            Sensors = snapshot.Sensors
                .Where(sensor => !excludedDeviceIds.Contains(sensor.DeviceId))
                .ToArray(),
            Warnings = snapshot.Warnings.Select(RedactWarning).ToArray()
        };

        Dictionary<string, string> safeRuntime = runtime
            .Where(pair => !IsSensitiveKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => RedactText(pair.Value), StringComparer.OrdinalIgnoreCase);

        return new CompatibilityReportV1(
            1,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            appVersion,
            redacted,
            safeRuntime,
            logLines.Take(2000).Select(RedactText).ToArray(),
            userApproved);
    }

    public static string RedactText(string value)
    {
        string redacted = WindowsUserPathRegex().Replace(value, "$1\\[redacted]\\");
        redacted = UncPathRegex().Replace(redacted, "\\\\[redacted]\\");
        redacted = MacAddressRegex().Replace(redacted, "[redacted-mac]");
        redacted = Ipv4Regex().Replace(redacted, "[redacted-ip]");
        return redacted;
    }

    private static HardwareDevice RedactDevice(HardwareDevice device)
    {
        IReadOnlyDictionary<string, string> properties = device.Properties
            .Where(pair => !IsSensitiveKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => RedactText(pair.Value), StringComparer.OrdinalIgnoreCase);
        return device with { Properties = properties };
    }

    private static DiagnosticWarning RedactWarning(DiagnosticWarning warning) => warning with
    {
        Message = RedactText(warning.Message),
        Remediation = warning.Remediation is null ? null : RedactText(warning.Remediation)
    };

    private static bool IsSensitiveKey(string key) => SensitivePropertyFragments.Any(
        fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?i)([a-z]:\\users)\\[^\\]+\\")]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex(@"\\\\[^\\\s]+\\")]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b")]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex Ipv4Regex();
}
