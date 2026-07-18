using System.Globalization;
using System.IO;
using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// One operator-declared file-backed sensor: an external tool writes a numeric
/// value into a small text file and RigPilot reads it as a sensor. Plausibility
/// bounds are mandatory so a corrupt or misconfigured file cannot feed an
/// absurd value into cooling decisions, and <see cref="StaleAfterSeconds"/>
/// bounds how old the file's last write may be before the reading degrades to
/// stale (the cooling-graph runtime's existing stale handling then commands
/// maximum cooling).
/// </summary>
public sealed record FileSensorDefinitionV1(
    int SchemaVersion,
    string Id,
    string Name,
    string FilePath,
    string Unit,
    double MinimumPlausible,
    double MaximumPlausible,
    int StaleAfterSeconds)
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumStaleSeconds = 5;
    public const int MaximumStaleSeconds = 3600;

    /// <summary>Validates one definition; returns null when valid, else the exact problem.</summary>
    public string? Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return $"Definition '{Id}': unsupported schema version {SchemaVersion}.";
        }
        if (string.IsNullOrWhiteSpace(Id) || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            return $"Definition '{Id}': the id must be non-empty ASCII letters, digits, '-' or '_'.";
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            return $"Definition '{Id}': a display name is required.";
        }
        if (string.IsNullOrWhiteSpace(FilePath) || !Path.IsPathFullyQualified(FilePath))
        {
            return $"Definition '{Id}': the file path must be fully qualified.";
        }
        if (string.IsNullOrWhiteSpace(Unit))
        {
            return $"Definition '{Id}': a unit is required (for example \"°C\").";
        }
        if (!double.IsFinite(MinimumPlausible) || !double.IsFinite(MaximumPlausible) || MinimumPlausible >= MaximumPlausible)
        {
            return $"Definition '{Id}': plausibility bounds must be finite with minimum < maximum.";
        }
        if (StaleAfterSeconds is < MinimumStaleSeconds or > MaximumStaleSeconds)
        {
            return $"Definition '{Id}': staleAfterSeconds must be between {MinimumStaleSeconds} and {MaximumStaleSeconds}.";
        }

        return null;
    }
}

/// <summary>
/// Read-only file-backed sensor inputs (the Fan Control "file sensor" parity
/// feature). Definitions live in a JSON array at
/// <c>%ProgramData%\PCHelper\file-sensors.json</c>; the adapter re-reads the
/// configuration when its timestamp changes, so edits apply on the next poll
/// without a service restart. Every reading is bounded (small files only),
/// parsed invariant-culture, and gated by mandatory plausibility bounds and a
/// staleness window. The adapter exposes no capability of any kind — a file
/// sensor can inform a cooling graph as a temperature source, but nothing can
/// ever be written through this adapter.
/// </summary>
public sealed class FileSensorAdapter : IHardwareAdapter
{
    private const string AdapterId = "filesensor";
    private const string DeviceId = "filesensor:device";
    private const int MaximumConfigurationBytes = 64 * 1024;
    private const int MaximumValueBytes = 256;
    private const int MaximumDefinitions = 32;

    private readonly string _configurationPath;
    private readonly object _sync = new();
    private IReadOnlyList<FileSensorDefinitionV1> _definitions = [];
    private IReadOnlyList<string> _configurationErrors = [];
    private DateTimeOffset _configurationTimestamp = DateTimeOffset.MinValue;

    public FileSensorAdapter(string configurationPath)
    {
        _configurationPath = Path.GetFullPath(configurationPath);
    }

    public AdapterManifest Manifest { get; } = new(
        AdapterId,
        "File-backed sensors",
        "0.5.5-alpha",
        "None (local text files)",
        "None",
        AdapterExecutionContext.SystemService,
        ["Operator-declared numeric sensor files"],
        ["FileSensorInputs", "ReadOnlySensors"]);

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<FileSensorDefinitionV1> definitions, IReadOnlyList<string> errors) = LoadConfiguration();
        List<HardwareDevice> devices = [];
        List<DiagnosticWarning> warnings = [.. errors.Select(error => new DiagnosticWarning(
            "FILE_SENSOR_CONFIGURATION",
            "Warning",
            error,
            $"Correct the definition in {_configurationPath}; invalid entries are ignored."))];
        if (definitions.Count > 0)
        {
            devices.Add(new HardwareDevice(
                DeviceId,
                "File-backed sensors",
                DeviceKind.Unknown,
                null,
                null,
                null,
                new Dictionary<string, string>
                {
                    ["configurationPath"] = _configurationPath,
                    ["definitionCount"] = definitions.Count.ToString(CultureInfo.InvariantCulture),
                }));
        }

        // Deliberately no capabilities: file sensors are inputs only.
        return Task.FromResult(new AdapterProbeResult(Manifest, devices, [], warnings));
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<FileSensorDefinitionV1> definitions, _) = LoadConfiguration();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<SensorSample> samples = [.. definitions.Select(definition => ReadOne(definition, now))];
        return Task.FromResult<IReadOnlyList<SensorSample>>(samples);
    }

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("File sensors are read-only inputs.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("File sensors are read-only inputs.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("File sensors are read-only inputs.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("File sensors expose no resettable control.");

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<FileSensorDefinitionV1> definitions, IReadOnlyList<string> errors) = LoadConfiguration();
        return Task.FromResult(new AdapterHealth(
            AdapterId,
            true,
            DateTimeOffset.UtcNow,
            definitions.Count == 0 && errors.Count == 0
                ? $"No file sensors declared. Declare them in {_configurationPath}."
                : $"{definitions.Count} file sensor(s) declared, {errors.Count} configuration error(s).",
            [.. errors]));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Reads one sensor file into a sample; every failure mode degrades to a null-valued sample rather than throwing.</summary>
    private static SensorSample ReadOne(FileSensorDefinitionV1 definition, DateTimeOffset now)
    {
        string sensorId = $"filesensor:{definition.Id}";
        SensorSample Unavailable(SensorQuality quality) => new(
            sensorId, AdapterId, DeviceId, definition.Name, now, null, definition.Unit, quality, TimeSpan.Zero);

        try
        {
            FileInfo file = new(definition.FilePath);
            if (!file.Exists || file.Length > MaximumValueBytes)
            {
                return Unavailable(SensorQuality.Unavailable);
            }

            TimeSpan age = now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            string content = File.ReadAllText(definition.FilePath).Trim();
            string firstLine = content.Split('\n', 2)[0].Trim().TrimEnd('\r');
            if (!double.TryParse(firstLine, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value))
            {
                return Unavailable(SensorQuality.Invalid);
            }

            if (value < definition.MinimumPlausible || value > definition.MaximumPlausible)
            {
                return Unavailable(SensorQuality.Invalid);
            }

            bool stale = age > TimeSpan.FromSeconds(definition.StaleAfterSeconds);
            return new SensorSample(
                sensorId,
                AdapterId,
                DeviceId,
                definition.Name,
                now,
                stale ? null : value,
                definition.Unit,
                stale ? SensorQuality.Stale : SensorQuality.Good,
                age < TimeSpan.Zero ? TimeSpan.Zero : age);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Unavailable(SensorQuality.Unavailable);
        }
    }

    private (IReadOnlyList<FileSensorDefinitionV1> Definitions, IReadOnlyList<string> Errors) LoadConfiguration()
    {
        lock (_sync)
        {
            try
            {
                FileInfo file = new(_configurationPath);
                if (!file.Exists)
                {
                    _definitions = [];
                    _configurationErrors = [];
                    _configurationTimestamp = DateTimeOffset.MinValue;
                    return (_definitions, _configurationErrors);
                }

                DateTimeOffset timestamp = new(file.LastWriteTimeUtc, TimeSpan.Zero);
                if (timestamp == _configurationTimestamp)
                {
                    return (_definitions, _configurationErrors);
                }

                if (file.Length > MaximumConfigurationBytes)
                {
                    _definitions = [];
                    _configurationErrors = [$"The configuration exceeds {MaximumConfigurationBytes} bytes and was ignored."];
                    _configurationTimestamp = timestamp;
                    return (_definitions, _configurationErrors);
                }

                List<FileSensorDefinitionV1> parsed = JsonSerializer.Deserialize(
                    File.ReadAllText(_configurationPath),
                    FileSensorJsonContext.Default.ListFileSensorDefinitionV1) ?? [];
                List<string> errors = [];
                List<FileSensorDefinitionV1> valid = [];
                HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
                foreach (FileSensorDefinitionV1 definition in parsed)
                {
                    string? problem = definition.Validate();
                    if (problem is null && !seenIds.Add(definition.Id))
                    {
                        problem = $"Definition '{definition.Id}': duplicate id.";
                    }
                    if (problem is null && valid.Count >= MaximumDefinitions)
                    {
                        problem = $"Definition '{definition.Id}': more than {MaximumDefinitions} definitions are not supported.";
                    }

                    if (problem is null)
                    {
                        valid.Add(definition);
                    }
                    else
                    {
                        errors.Add(problem);
                    }
                }

                _definitions = valid;
                _configurationErrors = errors;
                _configurationTimestamp = timestamp;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                _definitions = [];
                _configurationErrors = [$"The file-sensor configuration could not be read: {exception.GetType().Name}."];
                _configurationTimestamp = DateTimeOffset.MinValue;
            }

            return (_definitions, _configurationErrors);
        }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<FileSensorDefinitionV1>))]
internal sealed partial class FileSensorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
