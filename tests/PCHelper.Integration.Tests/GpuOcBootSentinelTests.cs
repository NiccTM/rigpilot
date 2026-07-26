using System.IO;
using System.Text.Json;
using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Persisting a GPU overclock across restarts is the most dangerous thing the suite can do —
/// a bad clock offset can hang a boot before the user can remove it — so its safety rests
/// entirely on the boot-recovery sentinel. These tests pin the contract that makes it safe:
/// a trial that verifies is forgotten, a trial that survives a restart reverts every output
/// to stock and reports that persistence must be disabled, and an unreadable journal is
/// treated as a failed boot rather than trusted.
/// </summary>
public sealed class GpuOcBootSentinelTests : IDisposable
{
    private const string DeviceId = "nvidia:gpu-0";
    private readonly string _journalPath = Path.Combine(
        Path.GetTempPath(), $"pchelper-gpu-oc-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_journalPath))
        {
            File.Delete(_journalPath);
        }
    }

    private static IReadOnlyList<GpuOcStartupOutputV1> Outputs =>
    [
        new("gpuclock.core:0", 150),
        new("gpuclock.memory:0", 800),
        new("gpupower.limit:0", 350_000),
    ];

    [Fact]
    public async Task ACleanJournalAllowsReapplyAndCallsNoRestore()
    {
        GpuOcBootSentinel sentinel = new(_journalPath);
        RecordingRestore restore = new();

        (bool recovered, string message) = await sentinel.RecoverAsync(restore, default);

        Assert.False(recovered);
        Assert.Empty(restore.Calls);
        Assert.Contains("clean", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASettledTrialLeavesNoJournalBehind()
    {
        GpuOcBootSentinel sentinel = new(_journalPath);
        sentinel.BeginTrial(new GpuOcTrialEntryV1(DeviceId, DateTimeOffset.UnixEpoch, Outputs));
        Assert.True(File.Exists(_journalPath));

        sentinel.MarkSettled();

        Assert.False(File.Exists(_journalPath));
        Assert.Null(sentinel.ReadPending());
    }

    [Fact]
    public async Task ATrialThatSurvivedARestartRevertsEveryOutputAndDisablesPersistence()
    {
        // First process: journal the trial, then "crash" before MarkSettled.
        new GpuOcBootSentinel(_journalPath).BeginTrial(new GpuOcTrialEntryV1(DeviceId, DateTimeOffset.UnixEpoch, Outputs));

        // Next service start sees the surviving entry.
        GpuOcBootSentinel restarted = new(_journalPath);
        RecordingRestore restore = new();
        (bool recovered, string message) = await restarted.RecoverAsync(restore, default);

        Assert.True(recovered);
        Assert.Equal(Outputs, restore.Calls.Single());
        Assert.False(File.Exists(_journalPath)); // Journal cleared after recovery.
        Assert.Contains("did not survive", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disabled", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnreadableJournalIsTreatedAsAFailedBoot()
    {
        File.WriteAllText(_journalPath, "{ this is not valid json");
        GpuOcBootSentinel sentinel = new(_journalPath);
        RecordingRestore restore = new();

        (bool recovered, _) = await sentinel.RecoverAsync(restore, default);

        Assert.True(recovered);              // Fails safe: unreadable == did not settle.
        Assert.False(File.Exists(_journalPath));
    }

    [Fact]
    public void BeginTrialOverwritesAStaleEntryRatherThanBlockingAFreshReapply()
    {
        GpuOcBootSentinel sentinel = new(_journalPath);
        sentinel.BeginTrial(new GpuOcTrialEntryV1(DeviceId, DateTimeOffset.UnixEpoch, [new GpuOcStartupOutputV1("gpuclock.core:0", 50)]));
        sentinel.BeginTrial(new GpuOcTrialEntryV1(DeviceId, DateTimeOffset.UnixEpoch, Outputs));

        GpuOcTrialEntryV1? pending = sentinel.ReadPending();

        Assert.NotNull(pending);
        Assert.Equal(3, pending!.Outputs.Count);
    }

    private sealed class RecordingRestore : IGpuOcStockRestore
    {
        public List<IReadOnlyList<GpuOcStartupOutputV1>> Calls { get; } = [];

        public Task RestoreStockAsync(IReadOnlyList<GpuOcStartupOutputV1> outputs, CancellationToken cancellationToken)
        {
            Calls.Add(outputs);
            return Task.CompletedTask;
        }
    }
}
