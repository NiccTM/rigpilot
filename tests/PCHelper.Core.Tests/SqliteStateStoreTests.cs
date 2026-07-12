using Microsoft.Data.Sqlite;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class SqliteStateStoreTests
{
    [Fact]
    public async Task StoresProfilesTransactionsAndSensorHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteStateStore store = new(Path.Combine(directory, "state.db"));
            await store.InitializeAsync(CancellationToken.None);
            ProfileV1 profile = BuiltInProfiles.Create()[0];
            await store.SaveProfileAsync(profile, CancellationToken.None);

            ProfileV1? loaded = await store.GetProfileAsync(profile.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(profile.Id, loaded.Id);
            Assert.Equal(profile.Name, loaded.Name);
            Assert.Equal(profile.Actions, loaded.Actions);
            Assert.Equal(profile.AutomationReferences, loaded.AutomationReferences);
            Assert.Equal(profile.SafetyLimits, loaded.SafetyLimits);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            ProfileTransaction pending = new(
                "transaction", 0, profile.Id, ProfileTransactionState.Applying, now, now, [], [], null);
            await store.SaveAsync(pending, CancellationToken.None);
            Assert.Equal("transaction", (await store.GetPendingAsync(CancellationToken.None))?.Id);
            await store.ClearPendingAsync("transaction", CancellationToken.None);
            Assert.Null(await store.GetPendingAsync(CancellationToken.None));

            SensorSample sample = new(
                "sensor", "adapter", "device", "CPU", now, 42, "°C", SensorQuality.Good, TimeSpan.Zero);
            await store.AppendAsync([sample], CancellationToken.None);
            IReadOnlyList<SensorSample> history = await store.QueryAsync(
                "sensor", now.AddSeconds(-1), now.AddSeconds(1), CancellationToken.None);
            Assert.Single(history);
            Assert.Equal(42, history[0].Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AggregatesRawSamplesAfterTwentyFourHoursAndExpiresAfterThirtyDays()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteStateStore store = new(Path.Combine(directory, "state.db"));
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset old = DateTimeOffset.UtcNow.AddHours(-25);
            old = new DateTimeOffset(old.Year, old.Month, old.Day, old.Hour, old.Minute, 5, TimeSpan.Zero);
            DateTimeOffset recent = DateTimeOffset.UtcNow.AddMinutes(-1);
            DateTimeOffset expired = DateTimeOffset.UtcNow.AddDays(-31);
            await store.AppendAsync(
            [
                Sample(old, 40),
                Sample(old.AddSeconds(20), 60),
                Sample(recent, 70),
                Sample(expired, 20)
            ], CancellationToken.None);

            await store.EnforceRetentionAsync(CancellationToken.None);
            IReadOnlyList<SensorSample> history = await store.QueryAsync(
                "sensor",
                DateTimeOffset.UtcNow.AddDays(-32),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Equal(2, history.Count);
            Assert.Contains(history, sample => sample.Value == 50);
            Assert.Contains(history, sample => sample.Value == 70);
            Assert.DoesNotContain(history, sample => sample.Value == 20);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigratesLegacyDenormalisedSensorSchema()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "state.db");
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        try
        {
            await using (SqliteConnection connection = new($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE sensor_samples (
                        sensor_id TEXT NOT NULL,
                        timestamp_utc INTEGER NOT NULL,
                        adapter_id TEXT NOT NULL,
                        device_id TEXT NOT NULL,
                        name TEXT NOT NULL,
                        value REAL NULL,
                        unit TEXT NOT NULL,
                        quality TEXT NOT NULL,
                        PRIMARY KEY(sensor_id, timestamp_utc)
                    );
                    INSERT INTO sensor_samples VALUES(
                        'sensor', $timestamp, 'adapter', 'device', 'CPU', 42, '°C', 'Good');
                    """;
                command.Parameters.AddWithValue("$timestamp", timestamp.ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync();
            }

            await using SqliteStateStore store = new(database);
            await store.InitializeAsync(CancellationToken.None);
            IReadOnlyList<SensorSample> history = await store.QueryAsync(
                "sensor",
                timestamp.AddSeconds(-1),
                timestamp.AddSeconds(1),
                CancellationToken.None);

            SensorSample migrated = Assert.Single(history);
            Assert.Equal("adapter", migrated.AdapterId);
            Assert.Equal("device", migrated.DeviceId);
            Assert.Equal(42, migrated.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StoresNonFiniteSensorValuesAsUnavailableNulls()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteStateStore store = new(Path.Combine(directory, "state.db"));
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            SensorSample invalid = Sample(timestamp, double.NaN) with { Quality = SensorQuality.Invalid };

            await store.AppendAsync([invalid], CancellationToken.None);
            SensorSample stored = Assert.Single(await store.QueryAsync(
                invalid.SensorId,
                timestamp.AddSeconds(-1),
                timestamp.AddSeconds(1),
                CancellationToken.None));

            Assert.Null(stored.Value);
            Assert.Equal(SensorQuality.Invalid, stored.Quality);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SensorSample Sample(DateTimeOffset timestamp, double value) => new(
        "sensor",
        "adapter",
        "device",
        "CPU",
        timestamp,
        value,
        "°C",
        SensorQuality.Good,
        TimeSpan.Zero);
}
