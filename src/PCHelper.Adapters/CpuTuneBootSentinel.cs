using System.IO;
using System.Text.Json;

namespace PCHelper.Adapters;

/// <summary>
/// One persisted pending-CPU-tune journal entry. An entry exists from just
/// before a tune is applied until the tune is verified settled; if it is still
/// present at the next service start, the machine did not settle cleanly and
/// the sentinel reverts to stock.
/// </summary>
public sealed record CpuTuneJournalEntry(
    string ParameterName,
    int RequestedValue,
    DateTimeOffset StartedAt);

/// <summary>
/// The boot-recovery sentinel the qualification gate requires before any live
/// CPU tune (docs/qualification/cpu-tuning-and-intel-arc.md, step 3): a bad
/// curve or limit can hang or crash the machine, so every tune is journalled to
/// disk before it is applied and cleared only after it verifies. On service
/// start, a surviving journal entry means the previous tune never settled —
/// recovery commands the transport back to vendor stock and clears the journal.
///
/// The sentinel is transport-agnostic and holds no privileged code itself; with
/// no transport registered (the only production configuration today) recovery
/// can only observe and clear, never write.
/// </summary>
public sealed class CpuTuneBootSentinel(string journalPath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _journalPath = !string.IsNullOrWhiteSpace(journalPath)
        ? journalPath
        : throw new ArgumentException("A journal path is required.", nameof(journalPath));
    private readonly object _gate = new();

    /// <summary>Reads the surviving journal entry, or null when the last tune settled cleanly.</summary>
    public CpuTuneJournalEntry? ReadPending()
    {
        lock (_gate)
        {
            if (!File.Exists(_journalPath))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<CpuTuneJournalEntry>(File.ReadAllText(_journalPath));
            }
            catch (JsonException)
            {
                // A corrupt journal is treated as an unclean tune: recovery still
                // reverts to stock rather than trusting an unreadable record.
                return new CpuTuneJournalEntry("unreadable", 0, DateTimeOffset.MinValue);
            }
        }
    }

    /// <summary>
    /// Persists the pending-tune record. This MUST complete (durably, on disk)
    /// before the corresponding SMU write is issued.
    /// </summary>
    public void BeginPendingTune(CpuTuneJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (File.Exists(_journalPath))
            {
                throw new SmuTuningSafetyException(
                    "A previous CPU tune has not settled; refusing to journal a second concurrent tune.");
            }

            string? directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream stream = new(_journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, entry, SerializerOptions);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Marks the pending tune as settled by removing the journal entry.</summary>
    public void MarkSettled()
    {
        lock (_gate)
        {
            if (File.Exists(_journalPath))
            {
                File.Delete(_journalPath);
            }
        }
    }

    /// <summary>
    /// Service-start recovery. If an entry survived the restart, the previous
    /// tune did not settle: command stock values through the transport (when one
    /// exists) and clear the journal. Returns a human-readable outcome line for
    /// the service log. With a null transport a surviving entry is reported but
    /// the journal is retained, so the evidence is not destroyed.
    /// </summary>
    public async Task<string> RecoverAsync(ISmuTuningTransport? transport, CancellationToken cancellationToken)
    {
        CpuTuneJournalEntry? pending = ReadPending();
        if (pending is null)
        {
            return "CPU tune journal is clean; no recovery needed.";
        }

        if (transport is null)
        {
            return $"CPU tune journal has an unsettled entry ('{pending.ParameterName}') but no tuning transport exists; " +
                "journal retained as evidence. No SMU write path is present, so no hardware state can be outstanding.";
        }

        await transport.RestoreStockAsync(cancellationToken).ConfigureAwait(false);
        MarkSettled();
        return $"Unsettled CPU tune ('{pending.ParameterName}' → {pending.RequestedValue}) found at start; vendor stock values were restored.";
    }
}
