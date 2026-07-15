using System.Diagnostics.Eventing.Reader;
using PCHelper.Core;

namespace PCHelper.Service;

/// <summary>
/// Read-only, privacy-minimised System-log probe for WHEA and display-driver
/// reset signals. It deliberately retains no event message, machine name, user
/// name, or process path. Failure to read the log is non-fatal: sensor health
/// rules continue to operate.
/// </summary>
public interface ISystemHealthSignalProbe
{
    IReadOnlyList<HealthSystemSignal> ReadSince(DateTimeOffset since, DateTimeOffset now);
}

public sealed class WindowsSystemHealthSignalProbe : ISystemHealthSignalProbe
{
    private const int MaximumEvents = 64;

    public IReadOnlyList<HealthSystemSignal> ReadSince(DateTimeOffset since, DateTimeOffset now)
    {
        try
        {
            EventLogQuery query = new("System", PathType.LogName)
            {
                ReverseDirection = true
            };
            using EventLogReader reader = new(query);
            List<HealthSystemSignal> signals = [];
            for (EventRecord? record = reader.ReadEvent(); record is not null && signals.Count < MaximumEvents; record = reader.ReadEvent())
            {
                using (record)
                {
                    DateTimeOffset timestamp = record.TimeCreated is DateTime created
                        ? new DateTimeOffset(created)
                        : now;
                    if (timestamp <= since)
                    {
                        break;
                    }
                    if (TryClassify(record.ProviderName, record.Id, timestamp, out HealthSystemSignal? signal)
                        && signal is not null)
                    {
                        signals.Add(signal);
                    }
                }
            }
            return signals;
        }
        catch (EventLogException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool TryClassify(
        string? provider,
        int? eventId,
        DateTimeOffset timestamp,
        out HealthSystemSignal? signal)
    {
        string normalized = provider?.Trim() ?? string.Empty;
        if (normalized.Contains("WHEA", StringComparison.OrdinalIgnoreCase)
            && eventId is 1 or 17 or 18 or 19 or 20 or 46)
        {
            signal = new HealthSystemSignal(
                HealthSystemSignalKind.Whea,
                timestamp,
                $"A WHEA event ({eventId}) was observed in the Windows System log.");
            return true;
        }
        if (eventId == 4101
            && (normalized.Equals("Display", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("amdkmdag", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("igfx", StringComparison.OrdinalIgnoreCase)))
        {
            signal = new HealthSystemSignal(
                HealthSystemSignalKind.DisplayDriverReset,
                timestamp,
                $"A display-driver reset event ({eventId}) was observed in the Windows System log.");
            return true;
        }
        signal = null;
        return false;
    }
}
