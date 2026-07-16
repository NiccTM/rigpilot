using System.IO;
using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the read-only file-backed sensor adapter against real temporary
/// files. No service, hardware, or elevated right is involved.
/// </summary>
public sealed class FileSensorAdapterTests : IDisposable
{
    private readonly string _directory;

    public FileSensorAdapterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"pchelper-filesensor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReadsAFreshPlausibleValueAsGoodAndExposesNoCapability()
    {
        string valuePath = WriteFile("water.txt", "31.5");
        string configPath = WriteConfig(Definition("loop-water", valuePath));
        await using FileSensorAdapter adapter = new(configPath);

        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        IReadOnlyList<SensorSample> samples = await adapter.ReadSensorsAsync(CancellationToken.None);

        Assert.Empty(probe.Capabilities); // inputs only — nothing writable, ever
        Assert.Empty(probe.Warnings);
        SensorSample sample = Assert.Single(samples);
        Assert.Equal("filesensor:loop-water", sample.SensorId);
        Assert.Equal(31.5, sample.Value);
        Assert.Equal(SensorQuality.Good, sample.Quality);
        Assert.Equal("°C", sample.Unit);
    }

    [Fact]
    public async Task DegradesAStaleFileToAStaleNullSample()
    {
        string valuePath = WriteFile("stale.txt", "40");
        File.SetLastWriteTimeUtc(valuePath, DateTime.UtcNow.AddMinutes(-10));
        string configPath = WriteConfig(Definition("stale-sensor", valuePath, staleAfterSeconds: 30));
        await using FileSensorAdapter adapter = new(configPath);

        SensorSample sample = Assert.Single(await adapter.ReadSensorsAsync(CancellationToken.None));

        Assert.Equal(SensorQuality.Stale, sample.Quality);
        Assert.Null(sample.Value); // stale input must not pose as a live reading
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("250")]   // above the plausible maximum
    [InlineData("-40")]   // below the plausible minimum
    [InlineData("NaN")]
    public async Task RejectsImplausibleOrMalformedContentAsInvalid(string content)
    {
        string valuePath = WriteFile("bad.txt", content);
        string configPath = WriteConfig(Definition("bad-sensor", valuePath));
        await using FileSensorAdapter adapter = new(configPath);

        SensorSample sample = Assert.Single(await adapter.ReadSensorsAsync(CancellationToken.None));

        Assert.Equal(SensorQuality.Invalid, sample.Quality);
        Assert.Null(sample.Value);
    }

    [Fact]
    public async Task ReportsAMissingValueFileAsUnavailable()
    {
        string configPath = WriteConfig(Definition("gone", Path.Combine(_directory, "missing.txt")));
        await using FileSensorAdapter adapter = new(configPath);

        SensorSample sample = Assert.Single(await adapter.ReadSensorsAsync(CancellationToken.None));

        Assert.Equal(SensorQuality.Unavailable, sample.Quality);
        Assert.Null(sample.Value);
    }

    [Fact]
    public async Task SurfacesInvalidDefinitionsAsWarningsAndSkipsThem()
    {
        string goodPath = WriteFile("good.txt", "22");
        string configPath = WriteConfig(
            Definition("good", goodPath),
            Definition("bad relative", "relative/path.txt"),
            Definition("good", goodPath)); // duplicate id
        await using FileSensorAdapter adapter = new(configPath);

        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        IReadOnlyList<SensorSample> samples = await adapter.ReadSensorsAsync(CancellationToken.None);

        Assert.Equal(2, probe.Warnings.Count);
        Assert.Single(samples);
        Assert.Equal(SensorQuality.Good, samples[0].Quality);
    }

    [Fact]
    public async Task PicksUpConfigurationEditsWithoutARestart()
    {
        string valuePath = WriteFile("value.txt", "20");
        string configPath = WriteConfig();
        await using FileSensorAdapter adapter = new(configPath);
        Assert.Empty(await adapter.ReadSensorsAsync(CancellationToken.None));

        File.WriteAllText(configPath, ConfigJson(Definition("late", valuePath)));
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddSeconds(2)); // guarantee a new timestamp

        SensorSample sample = Assert.Single(await adapter.ReadSensorsAsync(CancellationToken.None));
        Assert.Equal("filesensor:late", sample.SensorId);
    }

    [Fact]
    public async Task FailsSafeOnGarbageConfiguration()
    {
        string configPath = Path.Combine(_directory, "file-sensors.json");
        File.WriteAllText(configPath, "{ not json ]");
        await using FileSensorAdapter adapter = new(configPath);

        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        AdapterHealth health = await adapter.GetHealthAsync(CancellationToken.None);

        Assert.Empty(await adapter.ReadSensorsAsync(CancellationToken.None));
        Assert.Single(probe.Warnings);
        Assert.True(health.Healthy); // a config problem is a warning, not an adapter fault
    }

    private static FileSensorDefinitionV1 Definition(string id, string path, int staleAfterSeconds = 60) => new(
        FileSensorDefinitionV1.CurrentSchemaVersion,
        id,
        $"Sensor {id}",
        path,
        "°C",
        MinimumPlausible: 0,
        MaximumPlausible: 120,
        staleAfterSeconds);

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteConfig(params FileSensorDefinitionV1[] definitions)
    {
        string path = Path.Combine(_directory, "file-sensors.json");
        File.WriteAllText(path, ConfigJson(definitions));
        return path;
    }

    private static string ConfigJson(params FileSensorDefinitionV1[] definitions) =>
        System.Text.Json.JsonSerializer.Serialize(definitions.ToList());
}
