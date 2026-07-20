using System.Management;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Service;

internal sealed class RuntimeTuneScreeningMonitor(
    Func<HardwareSnapshot> snapshotProvider,
    CapabilityDescriptor targetCapability,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset, string?>? hardwareEventCheck = null,
    TuneSensorBindingV2? sensorBinding = null,
    IAutoOcWorkloadController? workload = null,
    AutoOcWorkloadMode? requiredWorkloadMode = null,
    double requiredAverageLoadPercent = 20) : ITuneScreeningMonitor
{
    private readonly Func<HardwareSnapshot> _snapshotProvider = snapshotProvider;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<DateTimeOffset, string?> _hardwareEventCheck = hardwareEventCheck ?? HardwareEventLog.FindRejectionSince;
    private readonly double? _baselineClockMegahertz = BaselineClock(snapshotProvider(), targetCapability, sensorBinding);

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
        List<double> fanRpms = [];
        double smallestThermalMargin = double.PositiveInfinity;
        long? firstDispatchCount = null;
        long? lastDispatchCount = null;
        DateTimeOffset? firstDispatchAt = null;
        DateTimeOffset? lastDispatchAt = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (workload is not null && requiredWorkloadMode is AutoOcWorkloadMode requiredMode)
            {
                WorkloadHostStatusV1 host = await workload.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (!host.Authenticated
                    || !host.Ready
                    || !host.Running
                    || host.Mode != requiredMode
                    || host.MatchingHardwareAdapterCount != 1
                    || _timeProvider.GetUtcNow() - host.HeartbeatAt > TimeSpan.FromSeconds(3))
                {
                    return Reject(
                        host.Error ?? $"The exact-GPU workload host stopped responding in {requiredMode} mode.",
                        temperatures,
                        powers,
                        clocks);
                }

                firstDispatchCount ??= host.DispatchCount;
                firstDispatchAt ??= _timeProvider.GetUtcNow();
                lastDispatchCount = host.DispatchCount;
                lastDispatchAt = _timeProvider.GetUtcNow();
            }

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
            SensorSample[] currentTemperatureSamples = good
                .Where(sample => IsTemperature(sample.Unit)
                    && (sensorBinding is null || sensorBinding.TemperatureSensorIds.Contains(sample.SensorId, StringComparer.Ordinal)))
                .Where(sample => sample.Value!.Value is > -20 and < 150)
                .ToArray();
            if (currentTemperatureSamples.Length == 0)
            {
                return Reject("No fresh temperature source was available during screening.", temperatures, powers, clocks);
            }

            temperatures.AddRange(currentTemperatureSamples.Select(sample => sample.Value!.Value));

            // Each sensor is judged against the ceiling for its own class. The bound
            // set includes hot spot and memory junction, which run hotter than the
            // core by design — comparing their readings to a core ceiling rejects
            // every sample the moment the workload actually loads the card.
            foreach (SensorSample sample in currentTemperatureSamples)
            {
                double ceiling = GpuThermalCeilings.CeilingForSensor(sample.Name, plan.TemperatureCeilingCelsius);
                if (sample.Value!.Value >= ceiling)
                {
                    return Reject(
                        $"Temperature ceiling exceeded on {sample.Name}: {sample.Value!.Value:0.0} °C observed, {ceiling:0.0} °C allowed.",
                        temperatures,
                        powers,
                        clocks);
                }

                // How close the closest sensor came to ITS OWN limit. The caller
                // stops climbing on this rather than on the hottest reading, so a
                // memory junction that legitimately runs hot no longer halts the
                // search on the first candidate.
                smallestThermalMargin = Math.Min(smallestThermalMargin, ceiling - sample.Value!.Value);
            }

            SensorSample[] related = RelatedSensors(snapshot, capability, good, sensorBinding);
            powers.AddRange(related.Where(sample => string.Equals(sample.Unit, "W", StringComparison.OrdinalIgnoreCase))
                .Select(sample => sample.Value!.Value)
                .Where(value => value >= 0));
            clocks.AddRange(related.Where(sample => string.Equals(sample.Unit, "MHz", StringComparison.OrdinalIgnoreCase))
                .Select(sample => sample.Value!.Value)
                .Where(value => value > 0));
            fanRpms.AddRange(good.Where(sample => string.Equals(sample.Unit, "RPM", StringComparison.OrdinalIgnoreCase)
                    && (sensorBinding is null || sensorBinding.BoundDeviceIds.Contains(sample.DeviceId, StringComparer.Ordinal)))
                .Select(sample => sample.Value!.Value)
                .Where(value => value >= 0));
            loads.AddRange(related.Where(sample => string.Equals(sample.Unit, "%", StringComparison.OrdinalIgnoreCase)
                    && (sensorBinding is not null
                        ? string.Equals(sample.SensorId, sensorBinding.UtilizationSensorId, StringComparison.Ordinal)
                        : sample.Name.Contains("load", StringComparison.OrdinalIgnoreCase)
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
            && (loads.Count == 0 || loads.Average() < requiredAverageLoadPercent))
        {
            return Reject(
                $"The screening workload did not produce at least {requiredAverageLoadPercent:0}% measured target-device load; no stability claim was recorded.",
                temperatures,
                powers,
                clocks);
        }

        // Clock regression. The stored baseline is captured when the monitor is
        // constructed — before this mode's workload starts, while the GPU is still
        // in whatever state the previous stage left it. Comparing across two
        // different operating states is not a stability signal: on the reference
        // rig the memory stage was rejected for "9752 MHz -> 5842 MHz" purely
        // because the baseline was read in one workload state and the samples in
        // another. When a workload mode is driven, compare this run against
        // itself, which is what actually detects a clock collapsing mid-screen.
        if (capability.Domain is ControlDomain.Cpu or ControlDomain.Gpu)
        {
            if (requiredWorkloadMode is not null)
            {
                const int minimumSamplesForTrend = 6;
                if (clocks.Count >= minimumSamplesForTrend)
                {
                    int span = clocks.Count / 3;
                    double opening = clocks.Take(span).Average();
                    double closing = clocks.Skip(clocks.Count - span).Average();
                    if (opening > 0 && closing < opening * 0.97)
                    {
                        return Reject(
                            $"Clock regressed more than 3% during screening: opened at {opening:0} MHz, closed at {closing:0} MHz.",
                            temperatures,
                            powers,
                            clocks);
                    }
                }
            }
            else if (_baselineClockMegahertz is double baselineClock
                && clocks.Count > 0
                && clocks.Average() < baselineClock * 0.97)
            {
                return Reject(
                    $"Clock regression exceeded 3%: baseline {baselineClock:0} MHz, observed {clocks.Average():0} MHz.",
                    temperatures,
                    powers,
                    clocks);
            }
        }

        double? throughputScore = firstDispatchCount is long first
            && lastDispatchCount is long last
            && lastDispatchAt - firstDispatchAt is TimeSpan dispatchDuration
            && dispatchDuration > TimeSpan.Zero
                ? Math.Max(0, last - first) / dispatchDuration.TotalSeconds
                : null;
        return new TuneScreeningResult(
            true,
            "No thermal, power, WHEA, display-reset, or control-ownership rejection was observed.",
            temperatures.Count == 0 ? null : temperatures.Max(),
            powers.Count == 0 ? null : powers.Average(),
            clocks.Count == 0 ? null : clocks.Average(),
            throughputScore,
            fanRpms.Count == 0 ? null : fanRpms.Average(),
            double.IsFinite(smallestThermalMargin) ? smallestThermalMargin : null);
    }

    private static SensorSample[] RelatedSensors(
        HardwareSnapshot snapshot,
        CapabilityDescriptor capability,
        IReadOnlyList<SensorSample> good,
        TuneSensorBindingV2? binding)
    {
        if (binding is not null)
        {
            HashSet<string> sensorIds = binding.TemperatureSensorIds
                .Append(binding.UtilizationSensorId)
                .Append(binding.CoreClockSensorId)
                .Append(binding.MemoryClockSensorId)
                .Concat(binding.PowerSensorId is null ? [] : [binding.PowerSensorId])
                .ToHashSet(StringComparer.Ordinal);
            return good.Where(sample => sensorIds.Contains(sample.SensorId)).ToArray();
        }

        HardwareDevice? target = snapshot.Devices.FirstOrDefault(
            device => string.Equals(device.Id, capability.DeviceId, StringComparison.Ordinal));
        HashSet<string> relatedIds = snapshot.Devices
            .Where(device => string.Equals(device.Id, capability.DeviceId, StringComparison.Ordinal)
                || (target is not null && device.Kind == target.Kind))
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        return good.Where(sample => relatedIds.Contains(sample.DeviceId)).ToArray();
    }

    private static double? BaselineClock(
        HardwareSnapshot snapshot,
        CapabilityDescriptor capability,
        TuneSensorBindingV2? binding)
    {
        SensorSample[] good = snapshot.Sensors
            .Where(sample => sample.Quality == SensorQuality.Good
                && sample.Value is double value
                && double.IsFinite(value))
            .ToArray();
        string? exactClock = binding is null
            ? null
            : capability.Id.StartsWith("gpuclock.memory:", StringComparison.Ordinal)
                ? binding.MemoryClockSensorId
                : binding.CoreClockSensorId;
        double[] clocks = RelatedSensors(snapshot, capability, good, binding)
            .Where(sample => string.Equals(sample.Unit, "MHz", StringComparison.OrdinalIgnoreCase))
            .Where(sample => exactClock is null || string.Equals(sample.SensorId, exactClock, StringComparison.Ordinal))
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
