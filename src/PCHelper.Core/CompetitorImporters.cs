using System.Globalization;
using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static class MsiAfterburnerProfileImporter
{
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumLineLength = 64 * 1024;
    private const int MaximumVfCurveCharacters = 32 * 1024;

    public static ProfileImportPreviewV1 Preview(
        string path,
        string section,
        IReadOnlyList<CapabilityDescriptorV2> capabilities)
    {
        string fullPath = ValidateFile(path, MaximumFileBytes);
        Dictionary<string, Dictionary<string, string>> ini = ParseIni(fullPath);
        if (!ini.TryGetValue(section, out Dictionary<string, string>? settings))
        {
            throw new InvalidDataException($"MSI Afterburner profile section '{section}' does not exist.");
        }

        List<ImportedSettingV1> imported = [];
        List<ProfileAction> actions = [];
        List<string> manualOnly = [];
        List<string> warnings = [];
        bool experimental = false;
        int order = 0;
        foreach ((string key, string rawValue) in settings)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || key.Equals("Format", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Mapping? mapping = CreateMapping(key, rawValue, capabilities);
            if (mapping is null)
            {
                imported.Add(new ImportedSettingV1(
                    section,
                    key,
                    rawValue,
                    null,
                    null,
                    ImportMappingState.Unmapped,
                    "This Afterburner setting has no safe RigPilot mapping."));
                continue;
            }

            if (mapping.Error is string error)
            {
                imported.Add(new ImportedSettingV1(section, key, rawValue, null, null, ImportMappingState.Invalid, error));
                continue;
            }

            CapabilityDescriptorV2? descriptor = mapping.Capability;
            if (descriptor is null)
            {
                imported.Add(new ImportedSettingV1(
                    section,
                    key,
                    rawValue,
                    null,
                    mapping.Value,
                    ImportMappingState.Unmapped,
                    "No detected capability matches this setting."));
                continue;
            }

            if (descriptor.Capability.State is not (CapabilityAccessState.Verified or CapabilityAccessState.Experimental))
            {
                imported.Add(new ImportedSettingV1(
                    section,
                    key,
                    rawValue,
                    descriptor.Capability.Id,
                    mapping.Value,
                    ImportMappingState.Blocked,
                    descriptor.Capability.Reason));
                continue;
            }

            if (!ValueFits(descriptor.Capability, mapping.Value!, out string? rangeError))
            {
                imported.Add(new ImportedSettingV1(
                    section,
                    key,
                    rawValue,
                    descriptor.Capability.Id,
                    mapping.Value,
                    ImportMappingState.Invalid,
                    rangeError!));
                continue;
            }

            string actionId = $"afterburner.{Sanitise(section)}.{Sanitise(key)}";
            bool isManualOnly = mapping.ManualOnly || descriptor.Hazard == HazardClass.Voltage;
            actions.Add(new ProfileAction(
                actionId,
                descriptor.Capability.AdapterId,
                descriptor.Capability.Id,
                mapping.Value!,
                Required: true,
                Order: order++));
            if (isManualOnly)
            {
                manualOnly.Add(actionId);
            }

            experimental |= descriptor.Capability.State == CapabilityAccessState.Experimental || isManualOnly;
            imported.Add(new ImportedSettingV1(
                section,
                key,
                rawValue,
                descriptor.Capability.Id,
                mapping.Value,
                isManualOnly ? ImportMappingState.ManualOnly : ImportMappingState.Mapped,
                isManualOnly
                    ? "Imported as Manual Only; it cannot run at boot or from automation."
                    : "Mapped to a detected RigPilot capability."));
        }

        if (actions.Count == 0)
        {
            warnings.Add("No write-eligible detected capability could accept this Afterburner profile.");
        }

        ProfileV2 profile = new(
            ProfileV2.CurrentSchemaVersion,
            $"imported.afterburner.{Sanitise(section)}",
            $"Afterburner {section}",
            $"Clean-room import from MSI Afterburner section {section}.",
            actions,
            new SafetyLimits(),
            CoolingGraphId: null,
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: manualOnly,
            AutomationReferences: [],
            IsBuiltIn: false,
            IsExperimental: experimental);
        return new ProfileImportPreviewV1(
            ProfileImportPreviewV1.CurrentSchemaVersion,
            ImportSourceKind.MsiAfterburner,
            fullPath,
            section,
            profile,
            imported,
            warnings);
    }

    public static IReadOnlyList<string> ListProfiles(string path)
    {
        string fullPath = ValidateFile(path, MaximumFileBytes);
        return ParseIni(fullPath).Keys
            .Where(name => name.Equals("Startup", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static Mapping? CreateMapping(
        string key,
        string rawValue,
        IReadOnlyList<CapabilityDescriptorV2> capabilities)
    {
        if (key.Equals("VFCurve", StringComparison.OrdinalIgnoreCase))
        {
            if (rawValue.Length > MaximumVfCurveCharacters || rawValue.Length % 2 != 0 || !rawValue.All(Uri.IsHexDigit))
            {
                return new Mapping(null, null, true, "Voltage-frequency curve is not bounded hexadecimal data.");
            }

            return new Mapping(
                Find(capabilities, ["voltage", "frequency", "curve"]),
                ControlValue.FromText(rawValue.ToUpperInvariant()),
                true,
                null);
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric)
            || !double.IsFinite(numeric))
        {
            return null;
        }

        if (key.Equals("PowerLimit", StringComparison.OrdinalIgnoreCase))
        {
            return Numeric(capabilities, numeric, ["power", "limit"]);
        }
        if (key.Equals("ThermalLimit", StringComparison.OrdinalIgnoreCase))
        {
            return Numeric(capabilities, numeric, ["temperature", "limit"], ["thermal", "limit"]);
        }
        if (key.Equals("CoreClkBoost", StringComparison.OrdinalIgnoreCase))
        {
            return Numeric(capabilities, numeric / 1000, ["core", "clock", "offset"], ["graphics", "clock", "offset"]);
        }
        if (key.Equals("MemClkBoost", StringComparison.OrdinalIgnoreCase))
        {
            return Numeric(capabilities, numeric / 1000, ["memory", "clock", "offset"]);
        }
        if (key.Equals("CoreVoltageBoost", StringComparison.OrdinalIgnoreCase))
        {
            return Numeric(capabilities, numeric, true, ["voltage"]);
        }
        if (key.Equals("FanSpeed", StringComparison.OrdinalIgnoreCase))
        {
            return Numeric(capabilities, numeric, ["fan", "1"]);
        }
        if (key.Equals("FanSpeed2", StringComparison.OrdinalIgnoreCase))
        {
            return Numeric(capabilities, numeric, ["fan", "2"]);
        }

        return null;
    }

    private static Mapping Numeric(
        IReadOnlyList<CapabilityDescriptorV2> capabilities,
        double value,
        params string[][] alternatives) => new(
            Find(capabilities, alternatives),
            ControlValue.FromNumeric(value),
            false,
            null);

    private static Mapping Numeric(
        IReadOnlyList<CapabilityDescriptorV2> capabilities,
        double value,
        bool manualOnly,
        params string[][] alternatives) => new(
            Find(capabilities, alternatives),
            ControlValue.FromNumeric(value),
            manualOnly,
            null);

    private static CapabilityDescriptorV2? Find(
        IReadOnlyList<CapabilityDescriptorV2> capabilities,
        params string[][] alternatives) => capabilities.FirstOrDefault(item =>
            item.Capability.Domain == ControlDomain.Gpu
            && alternatives.Any(words => words.All(word =>
                item.Capability.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                || item.Capability.Id.Contains(word, StringComparison.OrdinalIgnoreCase))));

    private static bool ValueFits(CapabilityDescriptor capability, ControlValue value, out string? error)
    {
        if (capability.ValueKind != value.Kind)
        {
            error = $"Detected capability expects {capability.ValueKind}, not {value.Kind}.";
            return false;
        }

        if (capability.Range is NumericRange range && value.Numeric is double numeric
            && (numeric < range.Minimum || numeric > range.Maximum))
        {
            error = $"Imported value {numeric} is outside {range.Minimum}-{range.Maximum} {capability.Unit}.".TrimEnd();
            return false;
        }

        error = null;
        return true;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        Dictionary<string, Dictionary<string, string>> result = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (string rawLine in File.ReadLines(path))
        {
            if (rawLine.Length > MaximumLineLength)
            {
                throw new InvalidDataException("Afterburner profile contains an oversized line.");
            }

            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[name] = current;
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (current is null || equals <= 0)
            {
                continue;
            }

            current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return result;
    }

    private static string ValidateFile(string path, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        FileInfo file = new(fullPath);
        if (!file.Exists || file.Length is <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidDataException($"Import file must exist and be no larger than {maximumBytes} bytes.");
        }
        return fullPath;
    }

    private static string Sanitise(string value)
    {
        char[] characters = value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(characters).Trim('-');
    }

    private sealed record Mapping(
        CapabilityDescriptorV2? Capability,
        ControlValue? Value,
        bool ManualOnly,
        string? Error);
}

public static class FanControlConfigurationImporter
{
    private const long MaximumFileBytes = 8 * 1024 * 1024;

    public static CoolingImportPreviewV1 Preview(
        string path,
        IReadOnlyDictionary<string, string> sensorMappings,
        IReadOnlyDictionary<string, string> controlMappings)
    {
        string fullPath = Path.GetFullPath(path);
        FileInfo file = new(fullPath);
        if (!file.Exists || file.Length is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException($"Fan Control configuration must be 1-{MaximumFileBytes} bytes.");
        }

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(fullPath),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        JsonElement root = document.RootElement;
        if (TryProperty(root, "FanControl", out JsonElement fanControl))
        {
            root = fanControl;
        }

        List<string> warnings = [];
        List<CoolingGraphNodeV1> nodes = [];
        Dictionary<string, string> curveNodeIds = new(StringComparer.OrdinalIgnoreCase);
        if (TryProperty(root, "FanCurves", out JsonElement curves) && curves.ValueKind == JsonValueKind.Array)
        {
            int curveIndex = 0;
            foreach (JsonElement curve in curves.EnumerateArray())
            {
                string name = String(curve, "Name") ?? $"Curve {curveIndex + 1}";
                string nodeId = UniqueId($"curve-{Sanitise(name)}", nodes.Select(node => node.Id));
                string sourceIdentifier = NestedString(curve, "SelectedTempSource", "Identifier") ?? $"unmapped.sensor.{curveIndex}";
                string sensorId = ResolveMapping(sensorMappings, sourceIdentifier, sourceIdentifier);
                string sensorNodeId = UniqueId($"sensor-{curveIndex}", nodes.Select(node => node.Id));
                nodes.Add(new CoolingGraphNodeV1(
                    sensorNodeId,
                    $"{name} source",
                    CoolingNodeKind.Sensor,
                    [],
                    sensorId,
                    [],
                    new Dictionary<string, double>()));

                CoolingGraphNodeV1 importedCurve = CreateCurve(curve, nodeId, name, sensorNodeId, warnings);
                nodes.Add(importedCurve);
                curveNodeIds[name] = nodeId;
                if (!sensorMappings.ContainsKey(sourceIdentifier))
                {
                    warnings.Add($"Temperature source '{sourceIdentifier}' is not mapped to a current sensor.");
                }
                curveIndex++;
            }
        }

        List<CoolingGraphOutputV1> outputs = [];
        List<FanCalibrationV2> calibrations = [];
        if (TryProperty(root, "Controls", out JsonElement controls) && controls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement control in controls.EnumerateArray())
            {
                string name = String(control, "NickName") ?? String(control, "Identifier") ?? "Fan control";
                string identifier = String(control, "Identifier") ?? name;
                string? capabilityId = ResolveOptionalMapping(controlMappings, identifier, name);
                string? curveName = NestedString(control, "SelectedFanCurve", "Name");
                if (capabilityId is null)
                {
                    warnings.Add($"Control '{name}' is not mapped to a current RigPilot capability.");
                    continue;
                }
                if (curveName is null || !curveNodeIds.TryGetValue(curveName, out string? sourceNodeId))
                {
                    warnings.Add($"Control '{name}' references an unavailable fan curve.");
                    continue;
                }

                double minimum = Number(control, "MinimumPercent") ?? 0;
                double stepUp = Math.Max(0.1, Number(control, "SelectedCommandStepUp") ?? 100);
                double stepDown = Math.Max(0.1, Number(control, "SelectedCommandStepDown") ?? 100);
                FanOutputMode mode = Number(control, "CommandMode") == 1 ? FanOutputMode.Rpm : FanOutputMode.DutyPercent;
                IReadOnlyList<CurvePoint> avoidBands = ReadAvoidBands(control);
                outputs.Add(new CoolingGraphOutputV1(
                    capabilityId,
                    sourceNodeId,
                    mode,
                    Math.Clamp(minimum, 0, 99),
                    100,
                    Number(control, "SelectedOffset") ?? 0,
                    stepUp,
                    stepDown,
                    avoidBands));

                FanCalibrationV2? calibration = ReadCalibration(control, capabilityId);
                if (calibration is not null)
                {
                    calibrations.Add(calibration);
                }
            }
        }

        CoolingGraphV1? graph = outputs.Count == 0
            ? null
            : new CoolingGraphV1(
                CoolingGraphV1.CurrentSchemaVersion,
                $"imported.fancontrol.{Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant()}",
                $"Fan Control {Path.GetFileNameWithoutExtension(fullPath)}",
                nodes,
                outputs);
        if (graph is not null)
        {
            warnings.AddRange(CoolingGraphValidator.Validate(graph));
        }

        return new CoolingImportPreviewV1(
            CoolingImportPreviewV1.CurrentSchemaVersion,
            ImportSourceKind.FanControl,
            fullPath,
            graph,
            calibrations,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static CoolingGraphNodeV1 CreateCurve(
        JsonElement curve,
        string nodeId,
        string name,
        string sensorNodeId,
        List<string> warnings)
    {
        if (TryProperty(curve, "Points", out JsonElement pointsElement) && pointsElement.ValueKind == JsonValueKind.Array)
        {
            List<CurvePoint> points = [];
            foreach (JsonElement point in pointsElement.EnumerateArray())
            {
                if (point.ValueKind == JsonValueKind.String && TryParsePoint(point.GetString(), out CurvePoint parsed))
                {
                    points.Add(parsed);
                }
            }

            Dictionary<string, double> parameters = ReadHysteresis(curve);
            return new CoolingGraphNodeV1(
                nodeId,
                name,
                CoolingNodeKind.Graph,
                [sensorNodeId],
                null,
                points.OrderBy(point => point.Input).ToArray(),
                parameters);
        }

        if (HasNumber(curve, "IdleTemperature", "LoadTemperature", "MinFanSpeed", "MaxFanSpeed", "Step", "Deadband"))
        {
            return new CoolingGraphNodeV1(
                nodeId,
                name,
                CoolingNodeKind.FeedbackAuto,
                [sensorNodeId],
                null,
                [],
                new Dictionary<string, double>
                {
                    ["idleTemperature"] = Number(curve, "IdleTemperature")!.Value,
                    ["loadTemperature"] = Number(curve, "LoadTemperature")!.Value,
                    ["minimum"] = Number(curve, "MinFanSpeed")!.Value,
                    ["maximum"] = Number(curve, "MaxFanSpeed")!.Value,
                    ["step"] = Number(curve, "Step")!.Value,
                    ["deadband"] = Number(curve, "Deadband")!.Value,
                    ["responseSeconds"] = Math.Max(0.1, Number(curve, "SelectedResponseTime") ?? 1)
                });
        }

        warnings.Add($"Fan Control curve '{name}' has an unsupported shape; it was imported as a 100% safety curve.");
        return new CoolingGraphNodeV1(
            nodeId,
            name,
            CoolingNodeKind.Flat,
            [],
            null,
            [],
            new Dictionary<string, double> { ["value"] = 100 });
    }

    private static Dictionary<string, double> ReadHysteresis(JsonElement curve)
    {
        Dictionary<string, double> result = [];
        if (!TryProperty(curve, "HysteresisConfig", out JsonElement config) || config.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        Add("hysteresisUp", "HysteresisValueUp");
        Add("hysteresisDown", "HysteresisValueDown");
        Add("responseUpSeconds", "ResponseTimeUp");
        Add("responseDownSeconds", "ResponseTimeDown");
        return result;

        void Add(string target, string source)
        {
            if (Number(config, source) is double value)
            {
                result[target] = Math.Max(0, value);
            }
        }
    }

    private static FanCalibrationV2? ReadCalibration(JsonElement control, string capabilityId)
    {
        if (!TryProperty(control, "Calibration", out JsonElement calibration) || calibration.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<FanCalibrationPoint> points = [];
        foreach (JsonElement row in calibration.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            JsonElement[] values = row.EnumerateArray().ToArray();
            if (values.Length >= 2 && values[0].TryGetDouble(out double duty) && values[1].TryGetDouble(out double rpm))
            {
                points.Add(new FanCalibrationPoint(duty, rpm));
            }
        }

        FanCalibrationPoint[] running = points.Where(point => point.Rpm > 0).OrderBy(point => point.DutyPercent).ToArray();
        if (running.Length < 2)
        {
            return null;
        }

        double restart = running[0].DutyPercent;
        string rpmSensor = NestedString(control, "PairedFanSensor", "Identifier") ?? $"{capabilityId}.rpm";
        return new FanCalibrationV2(
            FanCalibrationV2.CurrentSchemaVersion,
            capabilityId,
            rpmSensor,
            points,
            running.Max(point => point.Rpm),
            points.Where(point => point.Rpm <= 0).Select(point => (double?)point.DutyPercent).DefaultIfEmpty().Max(),
            restart,
            Math.Min(100, restart + 5),
            Math.Max(restart, Number(control, "SelectedStart") ?? restart),
            ReadAvoidBands(control),
            DateTimeOffset.UtcNow);
    }

    private static List<CurvePoint> ReadAvoidBands(JsonElement control)
    {
        if (!TryProperty(control, "Calibration", out JsonElement calibration) || calibration.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CurvePoint> bands = [];
        foreach (JsonElement row in calibration.EnumerateArray())
        {
            JsonElement[] values = row.ValueKind == JsonValueKind.Array ? row.EnumerateArray().ToArray() : [];
            if (values.Length >= 3
                && values[0].TryGetDouble(out double duty)
                && values[2].ValueKind == JsonValueKind.True)
            {
                bands.Add(new CurvePoint(Math.Max(0, duty - 0.5), Math.Min(100, duty + 0.5)));
            }
        }
        return bands;
    }

    private static bool TryParsePoint(string? text, out CurvePoint point)
    {
        point = new CurvePoint(0, 0);
        string[] values = text?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        if (values.Length != 2
            || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double input)
            || !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double output)
            || !double.IsFinite(input)
            || !double.IsFinite(output))
        {
            return false;
        }
        point = new CurvePoint(input, output);
        return true;
    }

    private static string ResolveMapping(IReadOnlyDictionary<string, string> mappings, string key, string fallback) =>
        mappings.TryGetValue(key, out string? value) ? value : fallback;

    private static string? ResolveOptionalMapping(IReadOnlyDictionary<string, string> mappings, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (mappings.TryGetValue(key, out string? value))
            {
                return value;
            }
        }
        return null;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? String(JsonElement element, string name) =>
        TryProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NestedString(JsonElement element, string objectName, string propertyName) =>
        TryProperty(element, objectName, out JsonElement nested) ? String(nested, propertyName) : null;

    private static double? Number(JsonElement element, string name) =>
        TryProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            ? number
            : null;

    private static bool HasNumber(JsonElement element, params string[] names) => names.All(name => Number(element, name).HasValue);

    private static string Sanitise(string value)
    {
        string result = new(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        return result.Trim('-');
    }

    private static string UniqueId(string preferred, IEnumerable<string> existing)
    {
        HashSet<string> used = existing.ToHashSet(StringComparer.Ordinal);
        string candidate = preferred;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{preferred}-{suffix++}";
        }
        return candidate;
    }
}
