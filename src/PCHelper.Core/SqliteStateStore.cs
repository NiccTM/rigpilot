using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed class SqliteStateStore : IProfileStore, IProfileTransactionJournal, ISensorHistoryStore, IAsyncDisposable
{
    private const long MaximumDatabaseBytes = 250L * 1024 * 1024;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteStateStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA auto_vacuum = INCREMENTAL;
                CREATE TABLE IF NOT EXISTS profiles (
                    id TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_utc INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS profile_transactions (
                    id TEXT PRIMARY KEY,
                    state TEXT NOT NULL,
                    is_pending INTEGER NOT NULL,
                    json TEXT NOT NULL,
                    updated_utc INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSensorSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProfileV1>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        List<ProfileV1> profiles = [];
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM profiles ORDER BY id";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ProfileV1? profile = JsonSerializer.Deserialize<ProfileV1>(reader.GetString(0), JsonDefaults.Options);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    public async Task<ProfileV1?> GetProfileAsync(string id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM profiles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string json ? JsonSerializer.Deserialize<ProfileV1>(json, JsonDefaults.Options) : null;
    }

    public async Task SaveProfileAsync(ProfileV1 profile, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profiles(id, json, updated_utc) VALUES($id, $json, $updated)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile, JsonDefaults.Options));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(ProfileTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profile_transactions(id, state, is_pending, json, updated_utc)
            VALUES($id, $state, $pending, $json, $updated)
            ON CONFLICT(id) DO UPDATE SET state = excluded.state, is_pending = excluded.is_pending,
                json = excluded.json, updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", transaction.Id);
        command.Parameters.AddWithValue("$state", transaction.State.ToString());
        command.Parameters.AddWithValue("$pending", IsPending(transaction.State) ? 1 : 0);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(transaction, JsonDefaults.Options));
        command.Parameters.AddWithValue("$updated", transaction.UpdatedAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfileTransaction?> GetPendingAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM profile_transactions WHERE is_pending = 1 ORDER BY updated_utc DESC LIMIT 1";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string json ? JsonSerializer.Deserialize<ProfileTransaction>(json, JsonDefaults.Options) : null;
    }

    public async Task ClearPendingAsync(string transactionId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE profile_transactions SET is_pending = 0 WHERE id = $id";
        command.Parameters.AddWithValue("$id", transactionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendAsync(IReadOnlyList<SensorSample> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand definitionCommand = connection.CreateCommand();
        definitionCommand.Transaction = transaction;
        definitionCommand.CommandText = """
            INSERT INTO sensor_definitions(sensor_id, adapter_id, device_id, name, unit)
            VALUES($sensor, $adapter, $device, $name, $unit)
            ON CONFLICT(sensor_id) DO UPDATE SET
                adapter_id = excluded.adapter_id,
                device_id = excluded.device_id,
                name = excluded.name,
                unit = excluded.unit
            """;
        definitionCommand.Parameters.Add("$sensor", SqliteType.Text);
        definitionCommand.Parameters.Add("$adapter", SqliteType.Text);
        definitionCommand.Parameters.Add("$device", SqliteType.Text);
        definitionCommand.Parameters.Add("$name", SqliteType.Text);
        definitionCommand.Parameters.Add("$unit", SqliteType.Text);

        await using SqliteCommand sampleCommand = connection.CreateCommand();
        sampleCommand.Transaction = transaction;
        sampleCommand.CommandText = """
            INSERT OR REPLACE INTO sensor_samples(sensor_id, timestamp_utc, value, quality)
            VALUES($sensor, $timestamp, $value, $quality)
            """;
        sampleCommand.Parameters.Add("$sensor", SqliteType.Text);
        sampleCommand.Parameters.Add("$timestamp", SqliteType.Integer);
        sampleCommand.Parameters.Add("$value", SqliteType.Real);
        sampleCommand.Parameters.Add("$quality", SqliteType.Text);

        foreach (SensorSample sample in samples)
        {
            definitionCommand.Parameters["$sensor"].Value = sample.SensorId;
            definitionCommand.Parameters["$adapter"].Value = sample.AdapterId;
            definitionCommand.Parameters["$device"].Value = sample.DeviceId;
            definitionCommand.Parameters["$name"].Value = sample.Name;
            definitionCommand.Parameters["$unit"].Value = sample.Unit;
            await definitionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            sampleCommand.Parameters["$sensor"].Value = sample.SensorId;
            sampleCommand.Parameters["$timestamp"].Value = sample.Timestamp.ToUnixTimeMilliseconds();
            sampleCommand.Parameters["$value"].Value = sample.Value is double value && double.IsFinite(value)
                ? value
                : DBNull.Value;
            sampleCommand.Parameters["$quality"].Value = sample.Quality.ToString();
            await sampleCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SensorSample>> QueryAsync(
        string sensorId,
        DateTimeOffset lowerBoundary,
        DateTimeOffset upperBoundary,
        CancellationToken cancellationToken)
    {
        List<SensorSample> samples = [];
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT history.timestamp_utc, definitions.adapter_id, definitions.device_id,
                   definitions.name, history.value, definitions.unit, history.quality
            FROM (
                SELECT sensor_id, timestamp_utc, value, quality FROM sensor_samples
                UNION ALL
                SELECT sensor_id, minute_utc, average_value, quality FROM sensor_aggregates
            ) AS history
            JOIN sensor_definitions AS definitions ON definitions.sensor_id = history.sensor_id
            WHERE history.sensor_id = $sensor AND history.timestamp_utc BETWEEN $from AND $to
            ORDER BY history.timestamp_utc
            """;
        command.Parameters.AddWithValue("$sensor", sensorId);
        command.Parameters.AddWithValue("$from", lowerBoundary.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", upperBoundary.ToUnixTimeMilliseconds());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
            samples.Add(new SensorSample(
                sensorId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                timestamp,
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.GetString(5),
                Enum.TryParse(reader.GetString(6), out SensorQuality quality) ? quality : SensorQuality.Unavailable,
                DateTimeOffset.UtcNow - timestamp));
        }

        return samples;
    }

    public async Task EnforceRetentionAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long rawCutoff = now.AddHours(-24).ToUnixTimeMilliseconds();
        rawCutoff -= rawCutoff % 60_000;
        long aggregateCutoff = now.AddDays(-30).ToUnixTimeMilliseconds();
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using SqliteCommand aggregate = connection.CreateCommand();
            aggregate.Transaction = transaction;
            aggregate.CommandText = """
                INSERT OR REPLACE INTO sensor_aggregates(
                    sensor_id, minute_utc, average_value, minimum_value, maximum_value, sample_count, quality)
                SELECT sensor_id,
                       CAST(timestamp_utc / 60000 AS INTEGER) * 60000,
                       AVG(value), MIN(value), MAX(value), COUNT(value),
                       CASE
                           WHEN SUM(CASE WHEN quality = 'Good' THEN 1 ELSE 0 END) > 0 THEN 'Good'
                           WHEN SUM(CASE WHEN quality = 'Stale' THEN 1 ELSE 0 END) > 0 THEN 'Stale'
                           ELSE 'Unavailable'
                       END
                FROM sensor_samples
                WHERE timestamp_utc < $rawCutoff AND timestamp_utc >= $aggregateCutoff
                GROUP BY sensor_id, CAST(timestamp_utc / 60000 AS INTEGER)
                """;
            aggregate.Parameters.AddWithValue("$rawCutoff", rawCutoff);
            aggregate.Parameters.AddWithValue("$aggregateCutoff", aggregateCutoff);
            await aggregate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM sensor_samples WHERE timestamp_utc < $rawCutoff;
                DELETE FROM sensor_aggregates WHERE minute_utc < $aggregateCutoff;
                """;
            delete.Parameters.AddWithValue("$rawCutoff", rawCutoff);
            delete.Parameters.AddWithValue("$aggregateCutoff", aggregateCutoff);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnforceSizeCapAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureSensorSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        bool legacySchema = false;
        await using (SqliteCommand inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(sensor_samples)";
            await using SqliteDataReader reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                legacySchema |= string.Equals(reader.GetString(1), "adapter_id", StringComparison.OrdinalIgnoreCase);
            }
        }

        await using SqliteCommand definitions = connection.CreateCommand();
        definitions.CommandText = """
            CREATE TABLE IF NOT EXISTS sensor_definitions (
                sensor_id TEXT PRIMARY KEY,
                adapter_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                name TEXT NOT NULL,
                unit TEXT NOT NULL
            );
            """;
        await definitions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (legacySchema)
        {
            await using SqliteTransaction migration = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand migrate = connection.CreateCommand();
            migrate.Transaction = migration;
            migrate.CommandText = """
                INSERT OR REPLACE INTO sensor_definitions(sensor_id, adapter_id, device_id, name, unit)
                SELECT sensor_id, adapter_id, device_id, name, unit FROM sensor_samples GROUP BY sensor_id;
                DROP TABLE IF EXISTS sensor_samples_v2;
                CREATE TABLE sensor_samples_v2 (
                    sensor_id TEXT NOT NULL,
                    timestamp_utc INTEGER NOT NULL,
                    value REAL NULL,
                    quality TEXT NOT NULL,
                    PRIMARY KEY(sensor_id, timestamp_utc)
                );
                INSERT OR REPLACE INTO sensor_samples_v2(sensor_id, timestamp_utc, value, quality)
                SELECT sensor_id, timestamp_utc, value, quality FROM sensor_samples;
                DROP TABLE sensor_samples;
                ALTER TABLE sensor_samples_v2 RENAME TO sensor_samples;
                """;
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await migration.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS sensor_samples (
                sensor_id TEXT NOT NULL,
                timestamp_utc INTEGER NOT NULL,
                value REAL NULL,
                quality TEXT NOT NULL,
                PRIMARY KEY(sensor_id, timestamp_utc)
            );
            CREATE TABLE IF NOT EXISTS sensor_aggregates (
                sensor_id TEXT NOT NULL,
                minute_utc INTEGER NOT NULL,
                average_value REAL NULL,
                minimum_value REAL NULL,
                maximum_value REAL NULL,
                sample_count INTEGER NOT NULL,
                quality TEXT NOT NULL,
                PRIMARY KEY(sensor_id, minute_utc)
            );
            CREATE INDEX IF NOT EXISTS ix_sensor_samples_time ON sensor_samples(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_sensor_aggregates_time ON sensor_aggregates(minute_utc);
            PRAGMA user_version = 2;
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnforceSizeCapAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (int attempt = 0; attempt < 12 && GetStorageBytes() > MaximumDatabaseBytes; attempt++)
        {
            int deleted;
            await using (SqliteCommand trimRaw = connection.CreateCommand())
            {
                trimRaw.CommandText = """
                    DELETE FROM sensor_samples
                    WHERE rowid IN (SELECT rowid FROM sensor_samples ORDER BY timestamp_utc LIMIT 100000)
                    """;
                deleted = await trimRaw.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (deleted == 0)
            {
                await using SqliteCommand trimAggregates = connection.CreateCommand();
                trimAggregates.CommandText = """
                    DELETE FROM sensor_aggregates
                    WHERE rowid IN (SELECT rowid FROM sensor_aggregates ORDER BY minute_utc LIMIT 25000)
                    """;
                deleted = await trimAggregates.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (deleted == 0)
            {
                break;
            }

            await using SqliteCommand compact = connection.CreateCommand();
            compact.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            await compact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private long GetStorageBytes() => FileLength(_databasePath)
        + FileLength(_databasePath + "-wal")
        + FileLength(_databasePath + "-shm");

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool IsPending(ProfileTransactionState state) => state is
        ProfileTransactionState.Pending or
        ProfileTransactionState.Prepared or
        ProfileTransactionState.Applying or
        ProfileTransactionState.Verifying or
        ProfileTransactionState.RollingBack;
}
