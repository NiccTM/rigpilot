using System.IO;
using System.Text.Json;

namespace PCHelper.App;

/// <summary>One completed benchmark run, as recorded in the local history.</summary>
public sealed record BenchmarkHistoryEntryV1(
    int SchemaVersion,
    DateTimeOffset RecordedAt,
    string Source,
    string ProcessName,
    double AverageFps,
    double MinimumFps,
    double MaximumFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    int SampleCount,
    double DurationSeconds)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Presentation label, e.g. "17 Jul 14:32 · vrchat.exe · RTSS windows".</summary>
    public string Title => $"{RecordedAt.LocalDateTime:dd MMM HH:mm} · {ProcessName} · {Source}";

    public string Summary =>
        $"avg {AverageFps:0.#} FPS · 1% low {OnePercentLowFps:0.#} · 0.1% low {PointOnePercentLowFps:0.#} · {DurationSeconds:0} s";
}

/// <summary>
/// Local, per-user benchmark history beside the other dashboard preferences.
/// Purpose: make tuning measurable — after an undervolt or clock change, the
/// next run of the same game shows the delta against the previous run. Purely
/// local (never uploaded, no machine identity beyond the process name the
/// benchmark already displayed), bounded to the newest
/// <see cref="MaximumEntries"/> runs, and a corrupt file degrades to an empty
/// history rather than an error.
/// </summary>
public static class BenchmarkHistory
{
    public const int MaximumEntries = 200;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RigPilot",
        "benchmark-history.json");

    public static IReadOnlyList<BenchmarkHistoryEntryV1> Load(string? path = null)
    {
        try
        {
            string historyPath = path ?? DefaultPath;
            if (!File.Exists(historyPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<BenchmarkHistoryEntryV1>>(File.ReadAllText(historyPath))
                ?.Where(entry => entry is { SchemaVersion: BenchmarkHistoryEntryV1.CurrentSchemaVersion })
                .ToArray() ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The completion status polls repeat while a finished benchmark stays on
    /// screen; a run is a duplicate when its statistics match the newest
    /// recorded run of the same source and process exactly.
    /// </summary>
    public static bool IsDuplicateOfLatest(IReadOnlyList<BenchmarkHistoryEntryV1> entries, BenchmarkHistoryEntryV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(candidate);
        BenchmarkHistoryEntryV1? latest = entries.FirstOrDefault(entry =>
            string.Equals(entry.Source, candidate.Source, StringComparison.Ordinal)
            && string.Equals(entry.ProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase));
        return latest is not null
            && latest.SampleCount == candidate.SampleCount
            && latest.AverageFps.Equals(candidate.AverageFps)
            && latest.OnePercentLowFps.Equals(candidate.OnePercentLowFps)
            && latest.DurationSeconds.Equals(candidate.DurationSeconds);
    }

    /// <summary>
    /// Compares a new run against the previous recorded run of the same game
    /// and source. Percentages are relative to the previous run.
    /// </summary>
    public static string DescribeDelta(IReadOnlyList<BenchmarkHistoryEntryV1> entries, BenchmarkHistoryEntryV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(candidate);
        BenchmarkHistoryEntryV1? previous = entries.FirstOrDefault(entry =>
            string.Equals(entry.Source, candidate.Source, StringComparison.Ordinal)
            && string.Equals(entry.ProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (previous is null || previous.AverageFps <= 0 || previous.OnePercentLowFps <= 0)
        {
            return $"First recorded {candidate.Source} run for {candidate.ProcessName}.";
        }

        double averageDelta = (candidate.AverageFps - previous.AverageFps) / previous.AverageFps * 100;
        double lowDelta = (candidate.OnePercentLowFps - previous.OnePercentLowFps) / previous.OnePercentLowFps * 100;
        return $"vs previous {candidate.ProcessName} run ({previous.RecordedAt.LocalDateTime:dd MMM HH:mm}): " +
               $"avg {FormatDelta(averageDelta)}, 1% low {FormatDelta(lowDelta)}.";
    }

    /// <summary>Prepends the run (newest first), trims, persists, and returns the new list.</summary>
    public static IReadOnlyList<BenchmarkHistoryEntryV1> Append(
        IReadOnlyList<BenchmarkHistoryEntryV1> entries,
        BenchmarkHistoryEntryV1 candidate,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(candidate);
        List<BenchmarkHistoryEntryV1> updated = [candidate, .. entries];
        if (updated.Count > MaximumEntries)
        {
            updated.RemoveRange(MaximumEntries, updated.Count - MaximumEntries);
        }

        try
        {
            string historyPath = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            File.WriteAllText(historyPath, JsonSerializer.Serialize(updated));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort; the in-memory list still updates.
        }

        return updated;
    }

    private static string FormatDelta(double percent) =>
        $"{(percent >= 0 ? "+" : string.Empty)}{percent:0.#}%";
}
