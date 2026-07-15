using System.Management;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Service;

internal sealed class RuntimeTuneScreeningMonitor(
    Func<HardwareSnapshot> snapshotProvider,
    CapabilityDescriptor targetCapability,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset, string?>? hardwareEventCheck = null) : ITuneScreeningMonitor
{
    private readonly Func<HardwareSnapshot> _snapshotProvider = snapshotProvider;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<DateTimeOffset, string?> _hardwareEventCheck = hardwareEventCheck ?? HardwareEventLog.FindRejectionSince;
    private readonly double? _baselineClockMegahertz = BaselineClock(snapshotProvider(), targetCapability);

    public async Task<TuneScreeningResult> ScreenAsync(
        CapabilityDescriptor capability,
        TunePlan plan,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        DateTimeOffset endsAt = startedAt + duration;
        List<double> temperatures = [];
        List<double> powers = [];
        List<double> clocks = [];
        List<double> loads = [];
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            HardwareSnapshot snapshot = _snapshotProvider();
            CapabilityDescriptor? current = snapshot.Capabilities.FirstOrDefault(
                item => string.Equals(item.Id, capability.Id, StringComparison.Ordinal));
            if (current is null)
            {
                return Reject("The tuned capability disappeared during screening.", temperatures, powers, clocks);
            }

            if (current.State == CapabilityAccessState.Blocked)
            {
                return Reject(
                    $"A competing writer took ownership during screening: {current.ConflictOwner ?? current.Reason}",
                    temperatures,
                    powers,
                    clocks);
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            SensorSample[] good = snapshot.Sensors
                .Where(sample => sample.Quality == SensorQuality.Good
                    && sample.Value is double value
                    && double.IsFinite(value)
                    && now - sample.Timestamp <= TimeSpan.FromSeconds(3))
                .ToArray();
            double[] currentTemperatures = good
                .Where(sample => IsTemperature(sample.Unit))
                .Select(sample => sample.Value!.Value)
                .Where(value => value is > -20 and < 150)
                .ToArray();
            if (currentTemperatures.Length == 0)
            {
                return Reject("No fresh temperature source was available during screening.", temperatures, powers, clocks);
            }

            temperatures.AddRange(currentTemperatures);
            if (currentTemperatures.Max() >= plan.TemperatureCeilingCelsius)
            {
                return Reject(
                    $"Temperature ceiling exceeded: {currentTemperatures.Max():0.0} °C observed, {plan.TemperatureCeilingCelsius:0.0} °C allowed.",
                    temperatures,
                    powers,
                    clocks);
            }

            SensorSample[] related = RelatedSensors(snapshot, capability, good);
            powers.AddRange(related.Where(sample => string.Equals(sample.Unit, "W", StringComparison.OrdinalIgnoreCase))
                .Select(sample => sample.Value!.Value)
                .Where(value => value >= 0));
            clocks.AddRange(related.Where(sample => string.Equals(sample.Unit, "MHz", StringComparison.OrdinalIgnoreCase))
                .Select(sample => sample.Value!.Value)
                .Where(value => value > 0));
            loads.AddRange(related.Where(sample => string.Equals(sample.Unit, "%", StringComparison.OrdinalIgnoreCase)
                    && (sample.Name.Contains("load", StringComparison.OrdinalIgnoreCase)
                        || sample.Name.Contains("util", StringComparison.OrdinalIgnoreCase)))
                .Select(sample => sample.Value!.Value)
                .Where(value => value is >= 0 and <= 100));

            if (plan.PowerCeilingWatts is double powerCeiling
                && powers.Count > 0
                && powers.Max() > powerCeiling)
            {
                return Reject(
                    $"Power ceiling exceeded: {powers.Max():0.0} W observed, {powerCeiling:0.0} W allowed.",
                    temperatures,
                    powers,
                    clocks);
            }

            TimeSpan remaining = endsAt - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                await _delay(
                    remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        while (_timeProvider.GetUtcNow() < endsAt);

        string? hardwareError = _hardwareEventCheck(startedAt);
        if (!string.IsNullOrWhiteSpace(hardwareError))
        {
            return Reject(hardwareError, temperatures, powers, clocks);
        }

        if (capability.Domain is ControlDomain.Cpu or ControlDomain.Gpu
            && (loads.Count == 0 || loads.Average() < 20))
        {
            return Reject(
                "The screening workload did not produce at least 20% measured device load; no stability claim was recorded.",
                temperatures,
                powers,
                clocks);
        }

        if (capability.Domain is ControlDomain.Cpu or ControlDomain.Gpu
            && _baselineClockMegahertz is double baselineClock
            && clocks.Count > 0
            && clocks.Average() < baselineClock * 0.97)
        {
            return Reject(
                $"Clock regression exceeded 3%: baseline {baselineClock:0} MHz, observed {clocks.Average():0} MHz.",
                temperatures,
                powers,
                clocks);
        }

        return new TuneScreeningResult(
            true,
            "No thermal, power, WHEA, display-reset, or control-ownership rejection was observed.",
            temperatures.Count == 0 ? null : temperatures.Max(),
            powers.Count == 0 ? null : powers.Average(),
            clocks.Count == 0 ? null : clocks.Average());
    }

    private static SensorSample[] RelatedSensors(
        HardwareSnapshot snapshot,
        CapabilityDescriptor capability,
        IReadOnlyList<SensorSample> good)
    {
        HardwareDevice? target = snapshot.Devices.FirstOrDefault(
            device => string.Equals(device.Id, capability.DeviceId, StringComparison.Ordinal));
        HashSet<string> relatedIds = snapshot.Devices
            .Where(device => string.Equals(device.Id, capability.DeviceId, StringComparison.Ordinal)
                || (target is not null && device.Kind == target.Kind))
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        return good.Where(sample => relatedIds.Contains(sample.DeviceId)).ToArray();
    }

    private static double? BaselineClock(HardwareSnapshot snapshot, CapabilityDescriptor capability)
    {
        SensorSample[] good = snapshot.Sensors
            .Where(sample => sample.Quality == SensorQuality.Good
                && sample.Value is double value
                && double.IsFinite(value))
            .ToArray();
        double[] clocks = RelatedSensors(snapshot, capability, good)
            .Where(sample => string.Equals(sample.Unit, "MHz", StringComparison.OrdinalIgnoreCase))
            .Select(sample => sample.Value!.Value)
            .Where(value => value > 0)
            .ToArray();
        return clocks.Length == 0 ? null : clocks.Average();
    }

    private static TuneScreeningResult Reject(
        string message,
        List<double> temperatures,
        List<double> powers,
        List<double> clocks) => new(
            false,
            message,
            temperatures.Count == 0 ? null : temperatures.Max(),
            powers.Count == 0 ? null : powers.Average(),
            clocks.Count == 0 ? null : clocks.Average());

    private static bool IsTemperature(string unit) =>
        string.Equals(unit, "°C", StringComparison.OrdinalIgnoreCase)
        || string.Equals(unit, "Â°C", StringComparison.OrdinalIgnoreCase)
        || string.Equals(unit, "Celsius", StringComparison.OrdinalIgnoreCase);
}

internal static class HardwareEventLog
{
    public static string? FindRejectionSince(DateTimeOffset since)
    {
        try
        {
            string timestamp = ManagementDateTimeConverter.ToDmtfDateTime(since.UtcDateTime);
            string query = $"SELECT EventCode, SourceName, Message FROM Win32_NTLogEvent "
                + $"WHERE Logfile='System' AND TimeGenerated >= '{timestamp}' "
                + "AND (EventCode=41 OR EventCode=4101 OR SourceName='Microsoft-Windows-WHEA-Logger')";
            using ManagementObjectSearcher searcher = new(query);
            foreach (ManagementBaseObject entry in searcher.Get().Cast<ManagementBaseObject>())
            {
                uint eventCode = Convert.ToUInt32(entry["EventCode"], System.Globalization.CultureInfo.InvariantCulture);
                string source = Convert.ToString(entry["SourceName"], System.Globalization.CultureInfo.InvariantCulture) ?? "System";
                if (eventCode == 4101)
                {
                    return "Display-driver reset event 4101 occurred during screening.";
                }

                if (eventCode == 41)
                {
                    return "Unexpected reboot event 41 occurred during screening.";
                }

                return $"WHEA hardware-error event from {source} occurred during screening.";
            }

            return null;
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException)
        {
            return $"Windows hardware-error log could not be inspected: {exception.Message}";
        }
    }
}
