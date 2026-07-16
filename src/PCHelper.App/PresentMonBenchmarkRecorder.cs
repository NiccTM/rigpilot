using System.Globalization;
using System.IO;
using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>
/// Pure per-frame benchmark statistics over Intel PresentMon frame times.
/// Unlike <see cref="FrametimeBenchmarkStatistics"/> (RTSS one-second windows),
/// these are true per-frame figures: the low metrics are the mean rates of the
/// slowest 1% / 0.1% of individual frames.
/// </summary>
public static class PresentMonPerFrameStatistics
{
    public static (double AverageFps, double MinimumFps, double MaximumFps, double OnePercentLow, double PointOnePercentLow, double AverageFrameTimeMs)
        Compute(IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0)
        {
            throw new InvalidOperationException("Per-frame statistics need at least one frame.");
        }

        double totalMs = frameTimesMs.Sum();
        double[] descending = [.. frameTimesMs.OrderDescending()];
        return (
            Math.Round(1000.0 * frameTimesMs.Count / totalMs, 1),
            Math.Round(1000.0 / descending[0], 1),
            Math.Round(1000.0 / descending[^1], 1),
            Math.Round(1000.0 / WorstFractionMeanMs(descending, 0.01), 1),
            Math.Round(1000.0 / WorstFractionMeanMs(descending, 0.001), 1),
            Math.Round(totalMs / frameTimesMs.Count, 2));
    }

    /// <summary>Mean frame time of the slowest <paramref name="fraction"/> of frames (at least one frame).</summary>
    private static double WorstFractionMeanMs(double[] descendingFrameTimes, double fraction)
    {
        int count = Math.Max(1, (int)Math.Floor(descendingFrameTimes.Length * fraction));
        return descendingFrameTimes.Take(count).Average();
    }
}

/// <summary>
/// Parses PresentMon console CSV output. Header-driven so both the 1.x
/// (`msBetweenPresents`) and 2.x (`FrameTime`) column names work; rows whose
/// frame-time cell is missing, non-numeric, or non-positive are skipped rather
/// than invented.
/// </summary>
public sealed class PresentMonCsvParser
{
    private static readonly string[] FrameTimeColumns = ["msBetweenPresents", "FrameTime"];

    private int _frameTimeIndex = -1;
    private int _processIdIndex = -1;
    private int _applicationIndex = -1;

    public bool HeaderParsed => _frameTimeIndex >= 0;

    /// <summary>Attempts to read a line as the CSV header. Returns true when a usable frame-time column was found.</summary>
    public bool TryParseHeader(string line)
    {
        string[] columns = line.Split(',');
        for (int index = 0; index < columns.Length; index++)
        {
            string name = columns[index].Trim();
            if (FrameTimeColumns.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
            {
                _frameTimeIndex = index;
            }
            else if (string.Equals(name, "ProcessID", StringComparison.OrdinalIgnoreCase))
            {
                _processIdIndex = index;
            }
            else if (string.Equals(name, "Application", StringComparison.OrdinalIgnoreCase))
            {
                _applicationIndex = index;
            }
        }

        return HeaderParsed;
    }

    /// <summary>Parses one data row. Returns false for malformed or non-positive frame-time rows.</summary>
    public bool TryParseRow(string line, out double frameTimeMs, out int processId, out string? application)
    {
        frameTimeMs = 0;
        processId = 0;
        application = null;
        string[] cells = line.Split(',');
        if (!HeaderParsed
            || _frameTimeIndex >= cells.Length
            || !double.TryParse(cells[_frameTimeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out frameTimeMs)
            || !double.IsFinite(frameTimeMs)
            || frameTimeMs <= 0)
        {
            return false;
        }

        if (_processIdIndex >= 0 && _processIdIndex < cells.Length)
        {
            _ = int.TryParse(cells[_processIdIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);
        }

        if (_applicationIndex >= 0 && _applicationIndex < cells.Length)
        {
            string name = cells[_applicationIndex].Trim();
            application = name.Length == 0 ? null : name;
        }

        return true;
    }
}

/// <summary>One running PresentMon console capture: its stdout stream plus termination.</summary>
public interface IPresentMonSession : IDisposable
{
    TextReader StandardOutput { get; }

    void Terminate();
}

/// <summary>Creates a capture session for a request, or throws with a reasoned message.</summary>
public interface IPresentMonSessionFactory
{
    /// <summary>Null when no PresentMon console binary was found; the message explains where RigPilot looked.</summary>
    string? DiscoveredPath { get; }

    string DiscoveryMessage { get; }

    IPresentMonSession Start(int processId, int durationSeconds);
}

/// <summary>
/// Discovers and launches the separately-installed Intel PresentMon console
/// binary (MIT-licensed, https://github.com/GameTechDev/PresentMon). RigPilot
/// never downloads or bundles it; like RTSS it is an optional external tool.
/// The capture is passive ETW consumption — PresentMon injects nothing into
/// the monitored application.
/// </summary>
public sealed class PresentMonSessionFactory : IPresentMonSessionFactory
{
    private readonly Lazy<(string? Path, string Message)> _discovery = new(Discover);

    public string? DiscoveredPath => _discovery.Value.Path;

    public string DiscoveryMessage => _discovery.Value.Message;

    public IPresentMonSession Start(int processId, int durationSeconds)
    {
        string path = DiscoveredPath ?? throw new InvalidOperationException(DiscoveryMessage);
        List<string> arguments =
        [
            "--output_stdout",
            "--stop_existing_session",
            "--timed", durationSeconds.ToString(CultureInfo.InvariantCulture),
            "--terminate_after_timed",
        ];
        if (processId > 0)
        {
            arguments.Add("--process_id");
            arguments.Add(processId.ToString(CultureInfo.InvariantCulture));
        }

        System.Diagnostics.Process process = new()
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The PresentMon console process failed to start.");
        }

        return new ProcessSession(process);
    }

    private static (string? Path, string Message) Discover()
    {
        List<string> candidates = [];
        string? overridePath = Environment.GetEnvironmentVariable("PCHELPER_PRESENTMON_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            candidates.Add(overridePath);
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            string intelDirectory = Path.Combine(programFiles, "Intel", "PresentMon");
            if (Directory.Exists(intelDirectory))
            {
                candidates.AddRange(Directory.EnumerateFiles(intelDirectory, "PresentMon*.exe", SearchOption.TopDirectoryOnly));
            }
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), "PresentMon.exe");
                if (File.Exists(candidate))
                {
                    candidates.Add(candidate);
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; skip it.
            }
        }

        string? found = candidates.FirstOrDefault(File.Exists);
        return found is not null
            ? (found, $"PresentMon console found: {Path.GetFileName(found)}.")
            : (null, "The Intel PresentMon console binary was not found. Install PresentMon (github.com/GameTechDev/PresentMon), add it to PATH, or set PCHELPER_PRESENTMON_PATH.");
    }

    private sealed class ProcessSession(System.Diagnostics.Process process) : IPresentMonSession
    {
        public TextReader StandardOutput => process.StandardOutput;

        public void Terminate()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        public void Dispose()
        {
            Terminate();
            process.Dispose();
        }
    }
}

/// <summary>
/// Per-frame frame-rate benchmark over the Intel PresentMon console. A session
/// launches one bounded PresentMon capture (duration enforced both by
/// PresentMon's own --timed flag and a local watchdog), consumes its CSV
/// stdout, and computes true per-frame statistics. When the request selects no
/// process (id 0), the process that presented the most frames during the run
/// is reported. Reading is passive ETW consumption; no injection or hardware
/// command is involved.
/// </summary>
public sealed class PresentMonBenchmarkRecorder : IFrametimeBenchmarkRecorder
{
    private const int MaximumRetainedFrames = 2_000_000; // ~9 hours at 60 fps; bounds memory.

    private readonly IPresentMonSessionFactory _factory;
    private readonly object _sync = new();
    private readonly Dictionary<int, (string? Application, List<double> FrameTimes)> _frames = [];
    private IPresentMonSession? _session;
    private CancellationTokenSource? _cancellation;
    private System.Threading.Timer? _watchdog;
    private int _targetProcessId;
    private int _retainedFrames;
    private DateTimeOffset _startedAt;
    private FrametimeBenchmarkStatusV1 _status = FrametimeBenchmarkStatusV1.Idle(
        "No per-frame benchmark has run. Requires the Intel PresentMon console; sampling is passive and injection-free.");

    public PresentMonBenchmarkRecorder(IPresentMonSessionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public FrametimeBenchmarkStatusV1 Status
    {
        get { lock (_sync) { return _status; } }
    }

    public FrametimeBenchmarkStatusV1 Start(FrametimeBenchmarkStartRequestV1 request)
    {
        if (request.SchemaVersion != FrametimeBenchmarkStartRequestV1.CurrentSchemaVersion
            || request.MaxDurationSeconds is < FrametimeBenchmarkStartRequestV1.MinimumDurationSeconds
                or > FrametimeBenchmarkStartRequestV1.MaximumDurationSeconds
            || request.ProcessId < 0)
        {
            throw new InvalidDataException(
                $"A benchmark needs the current schema version, a non-negative process id, and a duration between {FrametimeBenchmarkStartRequestV1.MinimumDurationSeconds} and {FrametimeBenchmarkStartRequestV1.MaximumDurationSeconds} seconds.");
        }

        lock (_sync)
        {
            if (_status.State == FrametimeBenchmarkState.Running)
            {
                throw new InvalidOperationException("A per-frame benchmark is already running; stop it first.");
            }

            IPresentMonSession session;
            try
            {
                session = _factory.Start(request.ProcessId, request.MaxDurationSeconds);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _status = FrametimeBenchmarkStatusV1.Idle(exception.Message);
                return _status;
            }

            _frames.Clear();
            _retainedFrames = 0;
            _session = session;
            _targetProcessId = request.ProcessId;
            _startedAt = DateTimeOffset.UtcNow;
            _cancellation = new CancellationTokenSource();
            _status = new FrametimeBenchmarkStatusV1(
                FrametimeBenchmarkStatusV1.CurrentSchemaVersion,
                FrametimeBenchmarkState.Running,
                null,
                _startedAt,
                0,
                0,
                null, null, null, null, null, null,
                "Per-frame benchmark running: capturing individual frame times via Intel PresentMon.");
            CancellationToken token = _cancellation.Token;
            _ = Task.Run(() => ConsumeAsync(session, token), CancellationToken.None);
            // PresentMon terminates itself via --timed; this local watchdog is the backstop.
            _watchdog = new System.Threading.Timer(
                _ => StopBenchmark(), null, TimeSpan.FromSeconds(request.MaxDurationSeconds + 15), Timeout.InfiniteTimeSpan);
            return _status;
        }
    }

    public FrametimeBenchmarkStatusV1 StopBenchmark()
    {
        lock (_sync)
        {
            if (_status.State != FrametimeBenchmarkState.Running)
            {
                return _status;
            }

            _session?.Terminate();
            return SettleLocked();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _cancellation?.Cancel();
            _watchdog?.Dispose();
            _watchdog = null;
            _session?.Dispose();
            _session = null;
        }
    }

    private async Task ConsumeAsync(IPresentMonSession session, CancellationToken token)
    {
        PresentMonCsvParser parser = new();
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await session.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break; // PresentMon exited (its --timed deadline, process exit, or termination).
                }

                if (!parser.HeaderParsed)
                {
                    parser.TryParseHeader(line);
                    continue;
                }

                if (parser.TryParseRow(line, out double frameTimeMs, out int processId, out string? application))
                {
                    RecordFrame(processId, application, frameTimeMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                if (_status.State == FrametimeBenchmarkState.Running)
                {
                    _session?.Terminate();
                    _status = _status with
                    {
                        State = FrametimeBenchmarkState.Failed,
                        DurationSeconds = Math.Round((DateTimeOffset.UtcNow - _startedAt).TotalSeconds, 1),
                        Message = $"Benchmark stopped: the PresentMon output stream became unreadable ({exception.GetType().Name})."
                    };
                }
                return;
            }
        }

        lock (_sync)
        {
            if (_status.State == FrametimeBenchmarkState.Running)
            {
                SettleLocked();
            }
        }
    }

    private void RecordFrame(int processId, string? application, double frameTimeMs)
    {
        lock (_sync)
        {
            if (_status.State != FrametimeBenchmarkState.Running || _retainedFrames >= MaximumRetainedFrames)
            {
                return;
            }

            if (!_frames.TryGetValue(processId, out (string? Application, List<double> FrameTimes) entry))
            {
                entry = (application, []);
                _frames[processId] = entry;
            }

            entry.FrameTimes.Add(frameTimeMs);
            _retainedFrames++;
        }
    }

    private FrametimeBenchmarkStatusV1 SettleLocked()
    {
        _cancellation?.Cancel();
        _watchdog?.Dispose();
        _watchdog = null;
        _session?.Dispose();
        _session = null;
        double duration = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;

        (string? Application, List<double> FrameTimes) selected = _targetProcessId > 0
            ? _frames.GetValueOrDefault(_targetProcessId, (null, []))
            : _frames.Values.OrderByDescending(entry => entry.FrameTimes.Count).FirstOrDefault((null, []));
        if (selected.FrameTimes.Count == 0)
        {
            _status = _status with
            {
                State = FrametimeBenchmarkState.Failed,
                DurationSeconds = Math.Round(duration, 1),
                Message = "Benchmark collected no frames. Check that PresentMon can run (ETW access), the application was presenting, and the process id was correct."
            };
            return _status;
        }

        (double average, double minimum, double maximum, double onePercent, double pointOnePercent, double frameTime) =
            PresentMonPerFrameStatistics.Compute(selected.FrameTimes);
        _status = new FrametimeBenchmarkStatusV1(
            FrametimeBenchmarkStatusV1.CurrentSchemaVersion,
            FrametimeBenchmarkState.Completed,
            selected.Application,
            _startedAt,
            Math.Round(duration, 1),
            selected.FrameTimes.Count,
            average,
            minimum,
            maximum,
            onePercent,
            pointOnePercent,
            frameTime,
            $"Completed over {selected.FrameTimes.Count} individual frames via Intel PresentMon. Low figures are true per-frame lows: the mean rates of the slowest 1% / 0.1% of frames.");
        return _status;
    }
}
