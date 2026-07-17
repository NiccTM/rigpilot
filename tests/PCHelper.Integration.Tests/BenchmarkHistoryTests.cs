using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the local benchmark-history semantics: newest-first append with the
/// bounded cap, duplicate suppression of repeated completion polls, the
/// per-game/per-source delta text, and fail-safe load of corrupt files.
/// </summary>
public sealed class BenchmarkHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rigpilot-bench-{Guid.NewGuid():N}");

    private string HistoryPath => Path.Combine(_directory, "benchmark-history.json");

    private static BenchmarkHistoryEntryV1 Entry(
        string process = "game.exe",
        string source = "RTSS windows",
        double average = 120,
        double onePercent = 90,
        int samples = 300,
        double duration = 300,
        int minutesAgo = 0) => new(
        BenchmarkHistoryEntryV1.CurrentSchemaVersion,
        DateTimeOffset.Now.AddMinutes(-minutesAgo),
        source,
        process,
        average,
        60,
        160,
        onePercent,
        70,
        samples,
        duration);

    [Fact]
    public void AppendPersistsNewestFirstAndReloads()
    {
        IReadOnlyList<BenchmarkHistoryEntryV1> updated = BenchmarkHistory.Append([], Entry(average: 100), HistoryPath);
        updated = BenchmarkHistory.Append(updated, Entry(average: 110), HistoryPath);

        IReadOnlyList<BenchmarkHistoryEntryV1> loaded = BenchmarkHistory.Load(HistoryPath);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(110, loaded[0].AverageFps); // newest first
    }

    [Fact]
    public void HistoryIsBounded()
    {
        IReadOnlyList<BenchmarkHistoryEntryV1> entries = [];
        for (int index = 0; index < BenchmarkHistory.MaximumEntries + 5; index++)
        {
            entries = BenchmarkHistory.Append(entries, Entry(average: index), HistoryPath);
        }

        Assert.Equal(BenchmarkHistory.MaximumEntries, entries.Count);
        Assert.Equal(BenchmarkHistory.MaximumEntries + 4, entries[0].AverageFps); // newest kept, oldest dropped
    }

    [Fact]
    public void RepeatedCompletionPollsAreDuplicates()
    {
        IReadOnlyList<BenchmarkHistoryEntryV1> entries = [Entry()];

        Assert.True(BenchmarkHistory.IsDuplicateOfLatest(entries, Entry()));
        Assert.False(BenchmarkHistory.IsDuplicateOfLatest(entries, Entry(average: 121))); // a genuinely new run
        Assert.False(BenchmarkHistory.IsDuplicateOfLatest(entries, Entry(source: "PresentMon frames")));
        Assert.False(BenchmarkHistory.IsDuplicateOfLatest(entries, Entry(process: "other.exe")));
    }

    [Fact]
    public void DeltaComparesAgainstThePreviousRunOfTheSameGameAndSource()
    {
        IReadOnlyList<BenchmarkHistoryEntryV1> entries =
        [
            Entry(average: 100, onePercent: 80, minutesAgo: 30),
            Entry(process: "other.exe", average: 500, minutesAgo: 10),
        ];

        string delta = BenchmarkHistory.DescribeDelta(entries, Entry(average: 105, onePercent: 76));

        Assert.Contains("avg +5%", delta, StringComparison.Ordinal);
        Assert.Contains("1% low -5%", delta, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRunOfAGameSaysSo()
    {
        string delta = BenchmarkHistory.DescribeDelta([Entry(process: "other.exe")], Entry());

        Assert.StartsWith("First recorded", delta, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptHistoryLoadsAsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(HistoryPath, "{ not json");

        Assert.Empty(BenchmarkHistory.Load(HistoryPath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
