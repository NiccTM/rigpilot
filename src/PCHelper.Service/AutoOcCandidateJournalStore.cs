using System.IO;
using System.Text.Json;
using PCHelper.Core;

namespace PCHelper.Service;

/// <summary>
/// File-backed <see cref="IAutoOcCandidateJournal"/> plus the crash history it feeds.
///
/// Two files, deliberately separate:
///  * the journal holds at most one entry — the offset applied to the hardware right now —
///    and is written before the value reaches the card and deleted once the candidate has
///    been screened. Its whole purpose is to survive a hard hang.
///  * the history accumulates the offsets that DID hang the machine, so later searches can
///    stay below them. Converting a surviving journal entry into a history record happens
///    once at service start, which is the only moment we can distinguish "the machine died
///    holding this value" from "a candidate is legitimately under test right now".
///
/// Both are best-effort: a journal that cannot be written must not stop a tuning run, since
/// the alternative is refusing to tune at all over a missing safety nicety.
/// </summary>
internal sealed class AutoOcCandidateJournalStore(string journalPath, string historyPath) : IAutoOcCandidateJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public void BeginCandidate(string capabilityId, double value)
    {
        lock (_gate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(journalPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using FileStream stream = new(journalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(
                    stream,
                    new AutoOcCrashRecordV1(AutoOcCrashRecordV1.CurrentSchemaVersion, capabilityId, value, DateTimeOffset.UtcNow),
                    SerializerOptions);
                // Flushed to disk: a hang moments later must still leave the entry behind.
                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort; a missing journal costs crash memory, not correctness.
            }
        }
    }

    public void EndCandidate()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(journalPath))
                {
                    File.Delete(journalPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Service-start conversion. A surviving journal entry means the machine went down while
    /// that offset was applied, so it is promoted into the crash history and the journal is
    /// cleared. Returns the promoted record, or null when the last run ended cleanly.
    /// </summary>
    public AutoOcCrashRecordV1? PromoteSurvivingEntry()
    {
        lock (_gate)
        {
            AutoOcCrashRecordV1? pending = ReadJournal();
            if (pending is null)
            {
                return null;
            }

            List<AutoOcCrashRecordV1> history = ReadHistoryCore();
            history.Add(pending);
            // Bounded: only the recent past informs a search, and the ceiling takes the most
            // conservative record anyway.
            if (history.Count > 32)
            {
                history = [.. history.OrderByDescending(record => record.ObservedAt).Take(32)];
            }

            WriteHistory(history);
            EndCandidateCore();
            return pending;
        }
    }

    public IReadOnlyList<AutoOcCrashRecordV1> ReadHistory()
    {
        lock (_gate)
        {
            return ReadHistoryCore();
        }
    }

    private AutoOcCrashRecordV1? ReadJournal()
    {
        try
        {
            if (!File.Exists(journalPath))
            {
                return null;
            }

            AutoOcCrashRecordV1? record = JsonSerializer.Deserialize<AutoOcCrashRecordV1>(File.ReadAllText(journalPath));
            return record is { SchemaVersion: AutoOcCrashRecordV1.CurrentSchemaVersion } ? record : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<AutoOcCrashRecordV1> ReadHistoryCore()
    {
        try
        {
            if (!File.Exists(historyPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<AutoOcCrashRecordV1>>(File.ReadAllText(historyPath)) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void WriteHistory(List<AutoOcCrashRecordV1> history)
    {
        try
        {
            string? directory = Path.GetDirectoryName(historyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(historyPath, JsonSerializer.Serialize(history, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void EndCandidateCore()
    {
        try
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
