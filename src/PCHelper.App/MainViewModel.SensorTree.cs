using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.App;

/// <summary>One sensor row in the tree, with its reading already formatted for display.</summary>
public sealed record SensorTreeReadingDisplay(string Name, string DisplayValue, bool IsStale);

/// <summary>A unit-based category under a device, e.g. "Temperatures".</summary>
public sealed record SensorTreeGroupDisplay(string Name, IReadOnlyList<SensorTreeReadingDisplay> Readings)
{
    public string CountLabel => Readings.Count == 1 ? "1 reading" : $"{Readings.Count} readings";
}

/// <summary>A device and its grouped sensors.</summary>
public sealed record SensorTreeDeviceDisplay(
    string DeviceId,
    string DeviceName,
    string KindLabel,
    IReadOnlyList<SensorTreeGroupDisplay> Groups)
{
    public string SummaryLabel
    {
        get
        {
            int count = Groups.Sum(group => group.Readings.Count);
            return count == 1 ? "1 sensor" : $"{count} sensors";
        }
    }
}

public sealed partial class MainViewModel
{
    /// <summary>
    /// The full device to category to sensor view. Every captured sensor appears here, which
    /// is the difference between this and the curated "important sensors" list: that one
    /// answers "is anything wrong?", this one answers "what is this device actually doing?".
    /// </summary>
    public ObservableCollection<SensorTreeDeviceDisplay> SensorTree { get; } =
        new BatchedObservableCollection<SensorTreeDeviceDisplay>();

    public bool HasSensorTree => SensorTree.Count > 0;

    public string SensorTreeSummary { get; private set; } = "No sensors captured yet";

    /// <summary>
    /// Rebuilds the tree from the current snapshot, honouring the Devices page search so one
    /// search box narrows both the device list and the sensor tree rather than the page
    /// having two competing filters.
    /// </summary>
    private void UpdateSensorTree()
    {
        if (_snapshot is null)
        {
            Replace(SensorTree, []);
            SensorTreeSummary = "No sensors captured yet";
            OnPropertyChanged(nameof(HasSensorTree));
            OnPropertyChanged(nameof(SensorTreeSummary));
            return;
        }

        IReadOnlyList<SensorTreeDevice> tree = Core.SensorTree.Build(_snapshot.Devices, _snapshot.Sensors);
        int totalSensors = tree.Sum(device => device.SensorCount);
        IReadOnlyList<SensorTreeDevice> visible = Core.SensorTree.Filter(tree, DeviceSearchText);

        Replace(
            SensorTree,
            visible.Select(device => new SensorTreeDeviceDisplay(
                device.DeviceId,
                device.DeviceName,
                device.Kind.ToString(),
                [.. device.Groups.Select(group => new SensorTreeGroupDisplay(
                    group.Name,
                    [.. group.Readings.Select(reading => new SensorTreeReadingDisplay(
                        reading.Name,
                        FormatSensorReading(reading),
                        reading.Quality is SensorQuality.Stale or SensorQuality.Unavailable))]))])),
            device => device.DeviceId,
            System.StringComparer.Ordinal);

        int shown = visible.Sum(device => device.SensorCount);
        SensorTreeSummary = totalSensors == 0
            ? "No sensors captured yet"
            : shown == totalSensors
                ? $"{totalSensors} sensors across {tree.Count} devices"
                : $"{shown} of {totalSensors} sensors";
        OnPropertyChanged(nameof(HasSensorTree));
        OnPropertyChanged(nameof(SensorTreeSummary));
    }

    /// <summary>
    /// Formats a reading with its unit. An absent value shows an em dash rather than "0",
    /// because a sensor that is not reporting is materially different from one reading zero —
    /// conflating them is how a stopped fan looks like a working one.
    /// </summary>
    private static string FormatSensorReading(SensorTreeReading reading)
    {
        if (reading.Value is not double value || !double.IsFinite(value))
        {
            return "—";
        }

        string number = System.Math.Abs(value) >= 1000
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(reading.Unit) ? number : $"{number} {reading.Unit}";
    }
}
