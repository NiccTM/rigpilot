using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>One sensor as it appears in the tree, carrying its reading and freshness.</summary>
public sealed record SensorTreeReading(
    string SensorId,
    string Name,
    double? Value,
    string Unit,
    SensorQuality Quality);

/// <summary>
/// A group of sensors that share a unit within one device — "Temperatures", "Fans",
/// "Voltages" — which is how a sensor tree is actually read: nobody scans a flat list
/// looking for the one temperature that matters.
/// </summary>
public sealed record SensorTreeGroup(
    string Name,
    string Unit,
    IReadOnlyList<SensorTreeReading> Readings);

/// <summary>One device and the grouped sensors reported against it.</summary>
public sealed record SensorTreeDevice(
    string DeviceId,
    string DeviceName,
    DeviceKind Kind,
    IReadOnlyList<SensorTreeGroup> Groups)
{
    public int SensorCount => Groups.Sum(group => group.Readings.Count);
}

/// <summary>
/// Builds the hierarchical device → category → sensor view that a monitoring tool is
/// expected to have (HWiNFO's sensor tree is the reference).
///
/// The suite already captures every sensor, but only ever presented them as one flat list
/// plus a curated "important" subset. That is fine for a dashboard and useless for actually
/// finding something: a machine here reports 235 sensors, and a flat list of 235 rows sorted
/// by nothing in particular cannot answer "what is this device doing?".
///
/// Grouping is by UNIT rather than by parsing sensor names. Names are adapter-specific and
/// change between vendors and driver versions, so matching on them would be a per-adapter
/// heuristic that silently rots; the unit is already normalised, always present, and is
/// exactly what makes two readings comparable.
/// </summary>
public static class SensorTree
{
    /// <summary>
    /// Maps a unit to the category heading it belongs under. Unknown units keep their own
    /// unit as the heading rather than being dumped into "Other", so a new sensor type shows
    /// up correctly grouped without a code change.
    /// </summary>
    public static string CategoryFor(string unit) => unit switch
    {
        "°C" or "C" => "Temperatures",
        "RPM" => "Fans",
        "%" => "Load and duty",
        "W" => "Power",
        "V" => "Voltages",
        "MHz" or "GHz" => "Clocks",
        "MB" or "GB" or "GiB" or "MiB" => "Memory",
        "" or null => "Readings",
        _ => unit
    };

    /// <summary>
    /// Builds the tree. Devices with no sensors are omitted — a tree exists to show readings,
    /// and an empty branch is noise. Sensors whose device is not in the snapshot are kept
    /// under their device id rather than dropped, because a reading with nowhere to belong is
    /// still a reading and hiding it would be the one failure a sensor tree must not have.
    /// </summary>
    public static IReadOnlyList<SensorTreeDevice> Build(
        IReadOnlyList<HardwareDevice> devices,
        IReadOnlyList<SensorSample> sensors)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(sensors);
        Dictionary<string, HardwareDevice> byId = devices
            .GroupBy(device => device.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return [.. sensors
            .GroupBy(sensor => sensor.DeviceId, StringComparer.Ordinal)
            .Select(deviceGroup =>
            {
                byId.TryGetValue(deviceGroup.Key, out HardwareDevice? device);
                IReadOnlyList<SensorTreeGroup> groups = [.. deviceGroup
                    .GroupBy(sensor => CategoryFor(sensor.Unit), StringComparer.Ordinal)
                    .Select(unitGroup => new SensorTreeGroup(
                        unitGroup.Key,
                        unitGroup.First().Unit,
                        [.. unitGroup
                            .OrderBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(sensor => new SensorTreeReading(
                                sensor.SensorId,
                                sensor.Name,
                                sensor.Value,
                                sensor.Unit,
                                sensor.Quality))]))
                    .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)];
                // A device can be present but unnamed, which rendered as a nameless row
                // labelled only with its sensor count — visible in the first render of this
                // view. Blank is treated the same as absent so every branch is identifiable.
                return new SensorTreeDevice(
                    deviceGroup.Key,
                    string.IsNullOrWhiteSpace(device?.Name) ? deviceGroup.Key : device!.Name,
                    device?.Kind ?? DeviceKind.Unknown,
                    groups);
            })
            .OrderBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Filters the tree to devices, groups, or sensors matching the query, keeping the
    /// hierarchy intact. An empty query returns the tree unchanged. Matching a DEVICE keeps
    /// all of its sensors, because searching for a GPU means wanting to see that GPU.
    /// </summary>
    public static IReadOnlyList<SensorTreeDevice> Filter(IReadOnlyList<SensorTreeDevice> tree, string? query)
    {
        ArgumentNullException.ThrowIfNull(tree);
        string trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return tree;
        }

        List<SensorTreeDevice> matches = [];
        foreach (SensorTreeDevice device in tree)
        {
            if (device.DeviceName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(device);
                continue;
            }

            SensorTreeGroup[] groups = [.. device.Groups
                .Select(group => group.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                    ? group
                    : group with
                    {
                        Readings = [.. group.Readings.Where(reading =>
                            reading.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))]
                    })
                .Where(group => group.Readings.Count > 0)];
            if (groups.Length > 0)
            {
                matches.Add(device with { Groups = groups });
            }
        }

        return matches;
    }
}
