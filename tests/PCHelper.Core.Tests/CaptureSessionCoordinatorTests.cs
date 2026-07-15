using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class CaptureSessionCoordinatorTests
{
    [Fact]
    public async Task StartsAndStopsWithFinalMetrics()
    {
        FakeBackend backend = new();
        FakeJournal journal = new();
        CaptureSessionCoordinator coordinator = new(backend, journal);

        CaptureSessionV1 recording = await coordinator.StartAsync(Preset(), Target(), Output(), CancellationToken.None);
        CaptureSessionV1 completed = await coordinator.StopAsync(recording, CancellationToken.None);

        Assert.Equal(CaptureSessionState.Recording, recording.State);
        Assert.Equal(CaptureSessionState.Completed, completed.State);
        Assert.Equal(300, completed.Metrics.FramesEncoded);
        Assert.Equal(
            [CaptureSessionState.Planned, CaptureSessionState.Starting, CaptureSessionState.Recording, CaptureSessionState.Stopping, CaptureSessionState.Completed],
            journal.States);
    }

    [Fact]
    public async Task EncoderFailureAbortsAndPersistsFailedState()
    {
        FakeBackend backend = new() { StopError = new IOException("encoder failed") };
        FakeJournal journal = new();
        CaptureSessionCoordinator coordinator = new(backend, journal);

        CaptureSessionV1 recording = await coordinator.StartAsync(Preset(), Target(), Output(), CancellationToken.None);
        CaptureSessionV1 failed = await coordinator.StopAsync(recording, CancellationToken.None);

        Assert.Equal(CaptureSessionState.Failed, failed.State);
        Assert.True(backend.Aborted);
        Assert.Contains("encoder", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisplayRemovalAbortsActiveCapture()
    {
        FakeBackend backend = new();
        CaptureSessionCoordinator coordinator = new(backend, new FakeJournal());
        CaptureSessionV1 recording = await coordinator.StartAsync(Preset(), Target(), Output(), CancellationToken.None);

        CaptureSessionV1 failed = await coordinator.FailAsync(recording, "Capture display was removed.", CancellationToken.None);

        Assert.Equal(CaptureSessionState.Failed, failed.State);
        Assert.True(backend.Aborted);
    }

    [Fact]
    public async Task CancelledStartAbortsBackendAndJournalsFailure()
    {
        FakeBackend backend = new() { StartError = new OperationCanceledException("cancelled") };
        FakeJournal journal = new();
        CaptureSessionCoordinator coordinator = new(backend, journal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.StartAsync(Preset(), Target(), Output(), CancellationToken.None));

        Assert.True(backend.Aborted);
        Assert.Equal(CaptureSessionState.Failed, journal.States[^1]);
    }

    private static CapturePresetV1 Preset() => new(
        CapturePresetV1.CurrentSchemaVersion,
        "capture.test",
        "Test",
        CaptureTargetKind.Display,
        60,
        20_000,
        "h264",
        true,
        false,
        true,
        "mp4");

    private static CaptureTargetV1 Target() => new(CaptureTargetKind.Display, "display.1", "Display 1");

    private static string Output() => Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.mp4");

    private sealed class FakeBackend : ICaptureBackend
    {
        public Exception? StopError { get; set; }
        public Exception? StartError { get; set; }
        public bool Aborted { get; private set; }

        public Task StartAsync(CaptureSessionV1 session, CancellationToken cancellationToken) =>
            StartError is null ? Task.CompletedTask : Task.FromException(StartError);

        public Task<CaptureMetricsV1> StopAsync(string sessionId, CancellationToken cancellationToken) =>
            StopError is null
                ? Task.FromResult(new CaptureMetricsV1(300, 2, TimeSpan.FromSeconds(5), 1_000_000))
                : Task.FromException<CaptureMetricsV1>(StopError);

        public Task AbortAsync(string sessionId, CancellationToken cancellationToken)
        {
            Aborted = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJournal : ICaptureSessionJournal
    {
        public List<CaptureSessionState> States { get; } = [];

        public Task SaveAsync(CaptureSessionV1 session, CancellationToken cancellationToken)
        {
            States.Add(session.State);
            return Task.CompletedTask;
        }
    }
}
