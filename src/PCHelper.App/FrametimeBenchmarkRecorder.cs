using System.IO;
using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>
/// Pure benchmark statistics over RTSS one-second-window frame-rate samples.
/// Deliberately window-based, not per-frame: the published figures are the
/// session mean, extremes, and the means of the worst 1% / 0.1% of windows.
/// </summary>
public static class FrametimeBenchmarkStatistics
{
    public static (double AverageFps, double MinimumFps, double MaximumFps, double OnePercentLow, double PointOnePercentLow, double AverageFrameTimeMs)
        Compute(IReadOnlyList<double> windowFps, IReadOnlyList<double> frameTimesMs)
    {
        if (windowFps.Count == 0)
        {
            throw new InvalidOperationException("Benchmark statistics need at least one sample.");
        }

        double[] sorted = [.. windowFps.Order()];
        return (
            Math.Round(windowFps.Average(), 1),
            Math.Round(sorted[0], 1),
            Math.Round(sorted[^1], 1),
            Math.Round(WorstFractionMean(sorted, 0.01), 1),
            Math.Round(WorstFractionMean(sorted, 0.001), 1),
            Math.Round(frameTimesMs.Count == 0 ? 0 : frameTimesMs.Average(), 2));
    }

    /// <summary>Mean of the slowest <paramref name="fraction"/> of ascending-sorted samples (at least one).</summary>
    private static double WorstFractionMean(double[] ascending, double fraction)
    {
        int count = Math.Max(1, (int)Math.Floor(ascending.Length * fraction));
        return ascending.Take(count).Average();
    }
}

public interface IFrametimeBenchmarkRecorder : IDisposable
{
    FrametimeBenchmarkStatusV1 Status { get; }

    FrametimeBenchmarkStatusV1 Start(FrametimeBenchmarkStartRequestV1 request);

    FrametimeBenchmarkStatusV1 StopBenchmark();
}

/// <summary>
/// Samples RTSS frame statistics on a timer while a benchmark session runs.
/// Consecutive identical raw readings are dropped (RTSS refreshes its window
/// roughly once a second while RigPilot polls faster), so each accepted sample
/// is a distinct RTSS measurement window. A session ends on Stop, on the
/// requested duration ceiling, or with a Failed state when RTSS disappears
/// mid-run. Reading is passive shared-memory access only.
/// </summary>
public sealed class FrametimeBenchmarkRecorder : IFrametimeBenchmarkRecorder
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private const int MaximumMissedReads = 20; // ~5 s without RTSS data fails the run.

    private readonly Func<RtssFrameStatsV1> _readFrameStats;
    private readonly object _sync = new();
    private readonly List<double> _windowFps = [];
    private readonly List<double> _frameTimes = [];
    private System.Threading.Timer? _timer;
    private int _targetProcessId;
    private string? _processName;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _deadline;
    private (double Fps, double FrameTime)? _lastRaw;
    private int _missedReads;
    private FrametimeBenchmarkStatusV1 _status = FrametimeBenchmarkStatusV1.Idle(
        "No benchmark has run. Start one while RTSS is monitoring a game; sampling is passive and injection-free.");

    public FrametimeBenchmarkRecorder(Func<RtssFrameStatsV1> readFrameStats)
    {
        _readFrameStats = readFrameStats ?? throw new ArgumentNullException(nameof(readFrameStats));
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
                throw new InvalidOperationException("A frame-rate benchmark is already running; stop it first.");
            }

            _windowFps.Clear();
            _frameTimes.Clear();
            _lastRaw = null;
            _missedReads = 0;
            _targetProcessId = request.ProcessId;
            _processName = null;
            _startedAt = DateTimeOffset.UtcNow;
            _deadline = _startedAt.AddSeconds(request.MaxDurationSeconds);
            _status = new FrametimeBenchmarkStatusV1(
                FrametimeBenchmarkStatusV1.CurrentSchemaVersion,
                FrametimeBenchmarkState.Running,
                null,
                _startedAt,
                0,
                0,
                null, null, null, null, null, null,
                "Benchmark running: sampling RTSS one-second measurement windows.");
            _timer = new System.Threading.Timer(_ => SampleOnce(), null, SampleInterval, SampleInterval);
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

            return SettleLocked();
        }
    }

    /// <summary>One sampling tick; public so tests can drive the state machine without the timer.</summary>
    public void SampleOnce()
    {
        lock (_sync)
        {
            if (_status.State != FrametimeBenchmarkState.Running)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            RtssFrameStatsV1 stats;
            try
            {
                stats = _readFrameStats();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                FailLocked($"Benchmark stopped: RTSS frame statistics became unreadable ({exception.GetType().Name}).");
                return;
            }

            RtssAppFrameStatsV1? app = _targetProcessId > 0
                ? stats.Applications.FirstOrDefault(candidate => candidate.ProcessId == _targetProcessId)
                : stats.Applications.OrderByDescending(candidate => candidate.FramesPerSecond).FirstOrDefault();
            if (app is null || app.FramesPerSecond <= 0)
            {
                if (++_missedReads >= MaximumMissedReads)
                {
                    FailLocked(_windowFps.Count == 0
                        ? "Benchmark stopped: RTSS reported no presenting application. Start the game first, then the benchmark."
                        : "Benchmark stopped: the monitored application stopped presenting frames.");
                }
                return;
            }

            _missedReads = 0;
            _processName ??= app.ProcessName;
            (double Fps, double FrameTime) raw = (app.FramesPerSecond, app.FrameTimeMilliseconds);
            if (_lastRaw != raw)
            {
                // A changed reading means RTSS advanced to a new measurement window.
                _lastRaw = raw;
                _windowFps.Add(app.FramesPerSecond);
                if (app.FrameTimeMilliseconds > 0)
                {
                    _frameTimes.Add(app.FrameTimeMilliseconds);
                }
            }

            if (now >= _deadline)
            {
                SettleLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private FrametimeBenchmarkStatusV1 SettleLocked()
    {
        _timer?.Dispose();
        _timer = null;
        double duration = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
        if (_windowFps.Count == 0)
        {
            _status = _status with
            {
                State = FrametimeBenchmarkState.Failed,
                DurationSeconds = Math.Round(duration, 1),
                Message = "Benchmark collected no samples: RTSS reported no presenting application during the run."
            };
            return _status;
        }

        (double average, double minimum, double maximum, double onePercent, double pointOnePercent, double frameTime) =
            FrametimeBenchmarkStatistics.Compute(_windowFps, _frameTimes);
        _status = new FrametimeBenchmarkStatusV1(
            FrametimeBenchmarkStatusV1.CurrentSchemaVersion,
            FrametimeBenchmarkState.Completed,
            _processName,
            _startedAt,
            Math.Round(duration, 1),
            _windowFps.Count,
            average,
            minimum,
            maximum,
            onePercent,
            pointOnePercent,
            frameTime,
            $"Completed over {_windowFps.Count} RTSS one-second windows. Low figures are the means of the worst 1% / 0.1% of windows, not per-frame lows.");
        return _status;
    }

    private void FailLocked(string message)
    {
        _timer?.Dispose();
        _timer = null;
        _status = _status with
        {
            State = FrametimeBenchmarkState.Failed,
            DurationSeconds = Math.Round((DateTimeOffset.UtcNow - _startedAt).TotalSeconds, 1),
            SampleCount = _windowFps.Count,
            Message = message
        };
    }
}
