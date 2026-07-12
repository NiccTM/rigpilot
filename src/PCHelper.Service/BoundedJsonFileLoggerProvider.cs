using System.Text;
using System.Text.Json;
using PCHelper.Core;

namespace PCHelper.Service;

public sealed class BoundedJsonFileLoggerProvider : ILoggerProvider
{
    private const long MaximumTotalBytes = 50L * 1024 * 1024;
    private const long MaximumSegmentBytes = 10L * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonDefaults.Options)
    {
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    private DateOnly _activeDate;
    private int _segment;
    private int _writesSinceCleanup;
    private bool _disabled;

    public BoundedJsonFileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        EnforceRetentionAndCap();
    }

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        lock (_sync)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                EnsureWriter();
                string line = JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    level = level.ToString(),
                    category,
                    eventId = eventId.Id,
                    eventName = eventId.Name,
                    message,
                    exception = exception?.ToString()
                }, LogJsonOptions);
                _writer!.WriteLine(line);
                _writer.Flush();

                if (++_writesSinceCleanup >= 100)
                {
                    _writesSinceCleanup = 0;
                    EnforceRetentionAndCap();
                }
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                _writer?.Dispose();
                _writer = null;
                _disabled = true;
            }
        }
    }

    private void EnsureWriter()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_writer is not null
            && _activeDate == today
            && _writer.BaseStream.Length < MaximumSegmentBytes)
        {
            return;
        }

        _writer?.Dispose();
        if (_activeDate != today)
        {
            _activeDate = today;
            _segment = 0;
        }
        else
        {
            _segment++;
        }

        string path;
        do
        {
            path = Path.Combine(_directory, $"service-{today:yyyyMMdd}-{_segment:D2}.jsonl");
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumSegmentBytes)
            {
                break;
            }

            _segment++;
        }
        while (true);

        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void EnforceRetentionAndCap()
    {
        DateTime cutoff = DateTime.UtcNow - Retention;
        FileInfo[] files = new DirectoryInfo(_directory)
            .EnumerateFiles("service-*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        foreach (FileInfo file in files.Where(file => file.LastWriteTimeUtc < cutoff))
        {
            TryDelete(file);
        }

        files = new DirectoryInfo(_directory)
            .EnumerateFiles("service-*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        long total = files.Sum(file => file.Length);
        foreach (FileInfo file in files)
        {
            if (total <= MaximumTotalBytes || _writer?.BaseStream is FileStream active && active.Name == file.FullName)
            {
                continue;
            }

            long length = file.Length;
            if (TryDelete(file))
            {
                total -= length;
            }
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class JsonFileLogger(BoundedJsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, eventId, formatter(state, exception), exception);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
