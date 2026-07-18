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
    public async Task PendingHardwareOperationRoundTripsAndCanBeCleared()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteStateStore store = new(Path.Combine(directory, "state.db"));
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HardwareOperationStatus operation = new(
                "operation",
                HardwareOperationKind.Tuning,
                HardwareOperationState.Screening,
                "capability",
                "GPU power",
                now,
                now,
                50,
                "screening",
                null,
                null,
                null);

            await store.SaveOperationAsync(operation, CancellationToken.None);

            Assert.Equal(operation, await store.GetPendingOperationAsync(CancellationToken.None));
            Assert.Equal(operation, await store.GetLatestOperationAsync(CancellationToken.None));

            await store.ClearPendingOperationAsync(operation.Id, CancellationToken.None);

            Assert.Null(await store.GetPendingOperationAsync(CancellationToken.None));
            Assert.Equal(operation, await store.GetLatestOperationAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HistoricalHardwareOperationCanBeReadByExactIdentifier()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteStateStore store = new(Path.Combine(directory, "state.db"));
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HardwareOperationStatus older = new(
                "operation-older",
                HardwareOperationKind.Calibration,
                HardwareOperationState.Completed,
                "fan.older",
                "Older fan",
                now,
                now,
                100,
                "completed",
                null,
                null,
                null);
            HardwareOperationStatus newer = older with
            {
                Id = "operation-newer",
                CapabilityId = "fan.newer",
                CapabilityName = "Newer fan",
                UpdatedAt = now.AddMinutes(1)
            };

            await store.SaveOperationAsync(older, CancellationToken.None);
            await store.SaveOperationAsync(newer, CancellationToken.None);

            Assert.Equal(older, await store.GetOperationAsync(older.Id, CancellationToken.None));
            Assert.Equal(newer, await store.GetLatestOperationAsync(CancellationToken.None));
            Assert.Null(await store.GetOperationAsync("operation-missing", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutomationRulesCanBeSavedUpdatedAndDeleted()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteStateStore store = new(Path.Combine(directory, "state.db"));
            await store.InitializeAsync(CancellationToken.None);
            AutomationRuleV1 rule = new(
                AutomationRuleV1.CurrentSchemaVersion,
                "rule",
                "Game mode",
                true,
                AutomationTriggerKind.ForegroundApplication,
                "game.exe",
                "performance",
                100);

            await store.SaveAutomationRuleAsync(rule, CancellationToken.None);
            Assert.Equal(rule, Assert.Single(await store.GetAutomationRulesAsync(CancellationToken.None)));

            AutomationRuleV1 updated = rule with { Priority = 200 };
            await store.SaveAutomationRuleAsync(updated, CancellationToken.None);
            Assert.Equal(200, Assert.Single(await store.GetAutomationRulesAsync(CancellationToken.None)).Priority);

            await store.DeleteAutomationRuleAsync(rule.Id, CancellationToken.None);
            Assert.Empty(await store.GetAutomationRulesAsync(CancellationToken.None));
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

    [Fact]
    public async Task MigratesV1ProfilesAndPersistsTypedSuiteEntities()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "state.db");
        ProfileV1 legacy = BuiltInProfiles.Create()[0];
        try
        {
            await using (SqliteStateStore initial = new(database))
            {
                await initial.InitializeAsync(CancellationToken.None);
                await initial.SaveProfileAsync(legacy, CancellationToken.None);
            }

            await using SqliteStateStore migrated = new(database);
            await migrated.InitializeAsync(CancellationToken.None);
            ProfileV2 upgraded = Assert.Single(await migrated.GetSuiteEntitiesAsync<ProfileV2>(
                SuiteEntityKind.ProfileV2,
                CancellationToken.None));
            Assert.Equal(legacy.Id, upgraded.Id);
            Assert.Equal(legacy.Actions, upgraded.HardwareActions);

            CoolingGraphV1 graph = new(
                CoolingGraphV1.CurrentSchemaVersion,
                "cooling.test",
                "Test cooling",
                [new CoolingGraphNodeV1("flat", "Flat", CoolingNodeKind.Flat, [], null, [], new Dictionary<string, double> { ["value"] = 50 })],
                [new CoolingGraphOutputV1("fan.cpu", "flat", FanOutputMode.DutyPercent, 20, 100, 0, 100, 100, [])]);
            await migrated.SaveSuiteEntityAsync(SuiteEntityKind.CoolingGraph, graph.Id, graph, CancellationToken.None);
            CoolingGraphV1? storedGraph = await migrated.GetSuiteEntityAsync<CoolingGraphV1>(
                SuiteEntityKind.CoolingGraph,
                graph.Id,
                CancellationToken.None);
            Assert.NotNull(storedGraph);
            Assert.Equal(graph.Id, storedGraph.Id);
            Assert.Equal("flat", Assert.Single(storedGraph.Nodes).Id);
            Assert.Equal("fan.cpu", Assert.Single(storedGraph.Outputs).CapabilityId);

            CoolingOutputAssignmentV1 pumpAssignment = new(
                CoolingOutputAssignmentV1.CurrentSchemaVersion,
                "lhm.control:/lpc/nct6798d/0/control/4",
                "lhm.control:/lpc/nct6798d/0/control/4",
                "librehardwaremonitor",
                "lhm.device:/lpc/nct6798d/0",
                "lhm.sensor:/lpc/nct6798d/0/fan/4",
                "AIO_PUMP",
                CoolingOutputRole.Pump,
                DateTimeOffset.UtcNow,
                "User-confirmed pump role.");
            await migrated.SaveSuiteEntityAsync(
                SuiteEntityKind.CoolingOutputAssignment,
                pumpAssignment.Id,
                pumpAssignment,
                CancellationToken.None);
            CoolingOutputAssignmentV1? storedPump = await migrated.GetSuiteEntityAsync<CoolingOutputAssignmentV1>(
                SuiteEntityKind.CoolingOutputAssignment,
                pumpAssignment.Id,
                CancellationToken.None);
            Assert.NotNull(storedPump);
            Assert.Equal(CoolingOutputRole.Pump, storedPump.Role);
            Assert.True(storedPump.IsSafetyCritical);

            await Assert.ThrowsAsync<ArgumentException>(() => migrated.GetSuiteEntitiesAsync<ProfileV2>(
                SuiteEntityKind.CoolingGraph,
                CancellationToken.None));
            await migrated.DeleteSuiteEntityAsync(SuiteEntityKind.CoolingGraph, graph.Id, CancellationToken.None);
            Assert.Null(await migrated.GetSuiteEntityAsync<CoolingGraphV1>(
                SuiteEntityKind.CoolingGraph,
                graph.Id,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsRecoveryLeaseAndFindsLatestCommittedTransaction()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteStateStore store = new(Path.Combine(directory, "state.db"));
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ProfileAction action = new("action", "adapter", "control", ControlValue.FromNumeric(50), true, 0);
            ProfileTransaction committed = new(
                "committed",
                1,
                "profile",
                ProfileTransactionState.Committed,
                now,
                now,
                [new PreparedAction(action, ControlValue.FromNumeric(0), now, "token")],
                [],
                null);
            await store.SaveAsync(committed, CancellationToken.None);
            HardwareControlLeaseV1 lease = new(
                HardwareControlLeaseV1.CurrentSchemaVersion,
                HardwareControlLeaseV1.DefaultId,
                "instance",
                "profile",
                committed.Id,
                [new HardwareControlLeaseItemV1("adapter", "control")],
                now,
                now,
                CleanShutdown: false,
                DefaultsVerified: false,
                HardwareControlLeaseState.Active,
                "active");
            await store.SaveSuiteEntityAsync(
                SuiteEntityKind.HardwareControlLease,
                lease.Id,
                lease,
                CancellationToken.None);

            ProfileTransaction? storedTransaction = await store.GetLatestCommittedAsync(CancellationToken.None);
            Assert.NotNull(storedTransaction);
            Assert.Equal(committed.Id, storedTransaction.Id);
            Assert.Equal(committed.State, storedTransaction.State);
            Assert.Equal("control", Assert.Single(storedTransaction.PreparedActions).Action.CapabilityId);
            HardwareControlLeaseV1? storedLease = await store.GetSuiteEntityAsync<HardwareControlLeaseV1>(
                SuiteEntityKind.HardwareControlLease,
                lease.Id,
                CancellationToken.None);
            Assert.NotNull(storedLease);
            Assert.Equal(lease.Id, storedLease.Id);
            Assert.Equal(HardwareControlLeaseState.Active, storedLease.State);
            Assert.Equal("control", Assert.Single(storedLease.Controls).CapabilityId);
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
