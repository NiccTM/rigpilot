using System.IO;
using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Restores GPU overclock outputs to their vendor-stock default. The service supplies the
/// real implementation (which drives the arm-gated adapters); keeping it a seam lets the
/// sentinel's recovery logic be tested without any hardware.
/// </summary>
public interface IGpuOcStockRestore
{
    Task RestoreStockAsync(IReadOnlyList<GpuOcStartupOutputV1> outputs, CancellationToken cancellationToken);
}

/// <summary>
/// The boot-recovery sentinel for persisted GPU overclocks, mirroring the proven
/// <see cref="CpuTuneBootSentinel"/> shape: a bad clock offset can hang or crash the machine
/// during boot, so a trial entry is journalled to disk BEFORE the OC is reapplied and cleared
/// only after it verifies. If a trial entry survives to the next service start, the previous
/// reapply never settled — recovery commands every output back to stock and reports it, so the
/// caller can disable persistence rather than reapply a boot-breaking OC forever.
///
/// The sentinel holds no privileged code itself and fails safe: a corrupt journal is treated
/// as an unsettled trial, and any error path leaves persistence disabled rather than trusting
/// an OC that may have caused the reboot.
/// </summary>
public sealed class GpuOcBootSentinel(string journalPath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _journalPath = !string.IsNullOrWhiteSpace(journalPath)
        ? journalPath
        : throw new ArgumentException("A journal path is required.", nameof(journalPath));
    private readonly object _gate = new();

    /// <summary>The surviving trial entry, or null when the last reapply settled cleanly.</summary>
    public GpuOcTrialEntryV1? ReadPending()
    {
        lock (_gate)
        {
            if (!File.Exists(_journalPath))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<GpuOcTrialEntryV1>(File.ReadAllText(_journalPath));
            }
            catch (JsonException)
            {
                // A corrupt journal is treated as an unsettled trial: recovery reverts to
                // stock rather than trusting an unreadable record.
                return new GpuOcTrialEntryV1("unreadable", DateTimeOffset.MinValue, []);
            }
        }
    }

    /// <summary>
    /// Persists the trial record. This MUST complete durably on disk BEFORE the first OC
    /// write of the reapply is issued, so a crash mid-reapply still leaves the entry behind.
    /// </summary>
    public void BeginTrial(GpuOcTrialEntryV1 entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Overwrite rather than refuse: the previous trial has, by definition, already
            // been recovered before a new reapply is attempted, and a stale entry must never
            // block a fresh, deliberate reapply.
            using FileStream stream = new(_journalPath, FileMode.Create, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, entry, SerializerOptions);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Marks the reapply settled by removing the journal entry, once it has verified.</summary>
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
    /// Service-start recovery. If a trial entry survived the restart the previous reapply did
    /// not settle: restore every listed output to stock through <paramref name="restore"/> and
    /// clear the journal. Returns (Recovered, Message): Recovered is true when an unsettled
    /// trial was found, signalling the caller to disable persistence. A clean journal returns
    /// (false, ...) and reapplication may proceed.
    /// </summary>
    public async Task<(bool Recovered, string Message)> RecoverAsync(
        IGpuOcStockRestore restore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restore);
        GpuOcTrialEntryV1? pending = ReadPending();
        if (pending is null)
        {
            return (false, "GPU OC startup journal is clean; safe to reapply a saved overclock.");
        }

        try
        {
            if (pending.Outputs.Count > 0)
            {
                await restore.RestoreStockAsync(pending.Outputs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            MarkSettled();
        }

        return (true,
            $"A saved GPU overclock on '{pending.DeviceId}' did not survive the last restart; it was reverted to stock and " +
            "startup persistence was disabled. Reapply and re-accept the restart risk to enable it again.");
    }
}
