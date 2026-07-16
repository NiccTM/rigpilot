using System.IO;
using PCHelper.Contracts;
using ScreenRecorderLib;

namespace PCHelper.App;

/// <summary>
/// Same-user, opt-in, bounded video recording. Deliberately parallel to the PNG
/// snapshot path: recording starts only after an explicit visible-session
/// confirmation, accepts only a currently discovered display or window target,
/// is capped to a bounded duration, writes only below the user's
/// Videos\RigPilot\Recordings directory, and never runs through the service.
/// The encoder is ScreenRecorderLib (MIT): Windows Graphics Capture frames into
/// a Media Foundation H.264 stream, optionally with system loopback audio.
/// </summary>
public interface IDesktopVideoRecorder
{
    VideoRecordingStatusV1 Status { get; }

    Task<VideoRecordingStatusV1> StartAsync(VideoRecordingStartRequestV1 request, CancellationToken cancellationToken);

    Task<VideoRecordingStatusV1> StopAsync(CancellationToken cancellationToken);
}

public sealed class WindowsDesktopVideoRecorder : IDesktopVideoRecorder, IDisposable
{
    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<CaptureTargetV1>> _discoverTargets;
    private readonly string _outputDirectory;
    private readonly Dictionary<string, VideoRecordingStatusV1> _idempotency = new(StringComparer.Ordinal);
    private Recorder? _recorder;
    private CancellationTokenSource? _durationLimit;
    private VideoRecordingStatusV1 _status = Idle("No recording has been started in this session.");
    private string? _activeIdempotencyKey;
    private DateTimeOffset _startedAt;

    public WindowsDesktopVideoRecorder(
        Func<IReadOnlyList<CaptureTargetV1>> discoverTargets,
        string? outputDirectory = null)
    {
        _discoverTargets = discoverTargets ?? throw new ArgumentNullException(nameof(discoverTargets));
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        _outputDirectory = Path.GetFullPath(outputDirectory
            ?? Path.Combine(videos, "RigPilot", "Recordings"));
    }

    public VideoRecordingStatusV1 Status
    {
        get
        {
            lock (_sync)
            {
                return ComposeStatus();
            }
        }
    }

    public Task<VideoRecordingStatusV1> StartAsync(VideoRecordingStartRequestV1 request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);

        lock (_sync)
        {
            if (_idempotency.TryGetValue(request.IdempotencyKey, out VideoRecordingStatusV1? prior))
            {
                return Task.FromResult(prior);
            }

            if (_status.State == VideoRecordingState.Recording)
            {
                throw new InvalidOperationException("A recording is already in progress. Stop it before starting another.");
            }

            // The requested target must be re-validated against a fresh discovery so a
            // stale or fabricated identifier can never select a hidden capture source.
            CaptureTargetV1? resolved = _discoverTargets()
                .FirstOrDefault(target => target.Kind == request.Target.Kind
                    && string.Equals(target.StableId, request.Target.StableId, StringComparison.Ordinal));
            if (resolved is null)
            {
                throw new InvalidDataException(
                    $"Capture target '{request.Target.StableId}' is not currently discoverable. Refresh targets and retry.");
            }

            Directory.CreateDirectory(_outputDirectory);
            string output = CreateOutputPath(resolved);
            Recorder recorder = Recorder.CreateRecorder(BuildOptions(resolved, request.CaptureSystemAudio));
            recorder.OnRecordingComplete += OnComplete;
            recorder.OnRecordingFailed += OnFailed;

            _recorder = recorder;
            _startedAt = DateTimeOffset.UtcNow;
            _activeIdempotencyKey = request.IdempotencyKey;
            _status = new VideoRecordingStatusV1(
                VideoRecordingStatusV1.CurrentSchemaVersion,
                VideoRecordingState.Recording,
                resolved,
                output,
                _startedAt,
                0,
                0,
                $"Recording {resolved.DisplayName} for at most {request.MaxDurationSeconds} s.");

            recorder.Record(output);

            // The duration ceiling is enforced locally: the recording is stopped even if
            // the requesting client disappears, so an unbounded capture cannot persist.
            _durationLimit = new CancellationTokenSource(TimeSpan.FromSeconds(request.MaxDurationSeconds));
            _durationLimit.Token.Register(() =>
            {
                try
                {
                    Recorder? active;
                    lock (_sync)
                    {
                        active = _status.State == VideoRecordingState.Recording ? _recorder : null;
                    }
                    active?.Stop();
                }
                catch (Exception)
                {
                    // The recorder may already be completing; the completion event settles state.
                }
            });

            return Task.FromResult(ComposeStatus());
        }
    }

    public Task<VideoRecordingStatusV1> StopAsync(CancellationToken cancellationToken)
    {
        Recorder? recorder;
        lock (_sync)
        {
            if (_status.State != VideoRecordingState.Recording)
            {
                return Task.FromResult(ComposeStatus());
            }

            recorder = _recorder;
        }

        try
        {
            recorder?.Stop();
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                SettleFailure($"Stopping the recording failed: {exception.Message}");
            }
        }

        lock (_sync)
        {
            return Task.FromResult(ComposeStatus());
        }
    }

    private static void Validate(VideoRecordingStartRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != VideoRecordingStartRequestV1.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported video recording request schema {request.SchemaVersion}.");
        }

        if (!request.ConfirmedVisibleCapture)
        {
            throw new InvalidDataException("Video recording requires explicit visible-session confirmation.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new InvalidDataException("Video recording requires an idempotency key.");
        }

        if (request.MaxDurationSeconds is < VideoRecordingStartRequestV1.MinimumDurationSeconds
            or > VideoRecordingStartRequestV1.MaximumDurationSeconds)
        {
            throw new InvalidDataException(
                $"Video recording duration must be between {VideoRecordingStartRequestV1.MinimumDurationSeconds} and {VideoRecordingStartRequestV1.MaximumDurationSeconds} seconds.");
        }

        if (request.Target.Kind is not (CaptureTargetKind.Display or CaptureTargetKind.Window))
        {
            throw new InvalidDataException("Only desktop display and window targets can be recorded.");
        }
    }

    private static RecorderOptions BuildOptions(CaptureTargetV1 target, bool captureSystemAudio)
    {
        RecordingSourceBase source = target.Kind == CaptureTargetKind.Display
            ? new DisplayRecordingSource(target.StableId["display:".Length..])
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture
            }
            : new WindowRecordingSource(ParseWindowHandle(target.StableId));

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [source] },
            OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = captureSystemAudio,
                IsOutputDeviceEnabled = captureSystemAudio,
                // Microphone capture is deliberately never enabled by this path.
                IsInputDeviceEnabled = false
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder(),
                IsHardwareEncodingEnabled = true,
                Framerate = 60
            }
        };
    }

    private static nint ParseWindowHandle(string stableId)
    {
        // Window ids are "window:0x<hex>" as produced by target discovery.
        string hex = stableId["window:0x".Length..];
        return long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long handle)
            ? (nint)handle
            : throw new InvalidDataException($"Capture target '{stableId}' is not a valid window identifier.");
    }

    private string CreateOutputPath(CaptureTargetV1 target)
    {
        string kind = target.Kind == CaptureTargetKind.Display ? "display" : "window";
        return Path.Combine(
            _outputDirectory,
            $"rigpilot-{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4");
    }

    private void OnComplete(object? sender, RecordingCompleteEventArgs e)
    {
        lock (_sync)
        {
            long bytes = 0;
            try
            {
                bytes = string.IsNullOrWhiteSpace(e.FilePath) ? 0 : new FileInfo(e.FilePath).Length;
            }
            catch (Exception)
            {
                // Metadata only; a missing length does not invalidate the recording.
            }

            _status = _status with
            {
                State = VideoRecordingState.Completed,
                OutputPath = string.IsNullOrWhiteSpace(e.FilePath) ? _status.OutputPath : e.FilePath,
                DurationSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                BytesWritten = bytes,
                Message = "Recording completed."
            };
            SettleCommon();
        }
    }

    private void OnFailed(object? sender, RecordingFailedEventArgs e)
    {
        lock (_sync)
        {
            SettleFailure($"Recording failed: {e.Error}");
        }
    }

    private void SettleFailure(string message)
    {
        _status = _status with
        {
            State = VideoRecordingState.Failed,
            DurationSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            Message = message
        };
        SettleCommon();
    }

    private void SettleCommon()
    {
        if (_activeIdempotencyKey is string key)
        {
            if (_idempotency.Count > 64)
            {
                _idempotency.Clear();
            }

            _idempotency[key] = _status;
            _activeIdempotencyKey = null;
        }

        _durationLimit?.Dispose();
        _durationLimit = null;
        Recorder? recorder = _recorder;
        _recorder = null;
        recorder?.Dispose();
    }

    private VideoRecordingStatusV1 ComposeStatus() => _status.State == VideoRecordingState.Recording
        ? _status with { DurationSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds }
        : _status;

    private static VideoRecordingStatusV1 Idle(string message) => new(
        VideoRecordingStatusV1.CurrentSchemaVersion,
        VideoRecordingState.Idle,
        null,
        null,
        null,
        0,
        0,
        message);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_status.State == VideoRecordingState.Recording)
            {
                try
                {
                    _recorder?.Stop();
                }
                catch (Exception)
                {
                    // Disposal must not throw; the process is exiting.
                }
            }

            _durationLimit?.Dispose();
            _durationLimit = null;
            _recorder?.Dispose();
            _recorder = null;
        }
    }
}
