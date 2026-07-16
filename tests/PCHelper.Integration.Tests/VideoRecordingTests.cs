using System.IO;
using System.Security.Principal;
using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Integration.Tests;

public sealed class VideoRecordingTests
{
    [Fact]
    public async Task VideoRecordingRequiresVisibleConfirmationAndAnIdempotencyKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeDesktopVideoRecorder recorder = new();
            await using UserAgentRuntime runtime = new(directory, videoRecorder: recorder);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            CaptureTargetV1 target = new(CaptureTargetKind.Display, "display:\\\\.\\DISPLAY1", "Primary display");

            VideoRecordingStartRequestV1 unconfirmed = new(
                VideoRecordingStartRequestV1.CurrentSchemaVersion,
                target,
                ConfirmedVisibleCapture: false,
                IdempotencyKey: "video.unconfirmed",
                MaxDurationSeconds: 60,
                CaptureSystemAudio: false);
            IpcResponse rejected = await runtime.HandleRequestAsync(
                Request(IpcCommand.StartVideoRecording, unconfirmed),
                current,
                CancellationToken.None);

            VideoRecordingStartRequestV1 missingKey = unconfirmed with
            {
                ConfirmedVisibleCapture = true,
                IdempotencyKey = " "
            };
            IpcResponse keyRejected = await runtime.HandleRequestAsync(
                Request(IpcCommand.StartVideoRecording, missingKey),
                current,
                CancellationToken.None);

            Assert.False(rejected.Success);
            Assert.Equal("CAPTURE_CONFIRMATION_REQUIRED", rejected.ErrorCode);
            Assert.False(keyRejected.Success);
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", keyRejected.ErrorCode);
            Assert.Equal(0, recorder.StartCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfirmedRecordingStartsStopsAndReportsStatusThroughTheUserAgent()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeDesktopVideoRecorder recorder = new();
            await using UserAgentRuntime runtime = new(directory, videoRecorder: recorder);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            CaptureTargetV1 target = new(CaptureTargetKind.Display, "display:\\\\.\\DISPLAY1", "Primary display");

            VideoRecordingStartRequestV1 request = new(
                VideoRecordingStartRequestV1.CurrentSchemaVersion,
                target,
                ConfirmedVisibleCapture: true,
                IdempotencyKey: "video.confirmed",
                MaxDurationSeconds: 60,
                CaptureSystemAudio: true);
            IpcResponse started = await runtime.HandleRequestAsync(
                Request(IpcCommand.StartVideoRecording, request),
                current,
                CancellationToken.None);
            IpcResponse status = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetVideoRecordingStatus),
                current,
                CancellationToken.None);
            IpcResponse stopped = await runtime.HandleRequestAsync(
                Request(IpcCommand.StopVideoRecording),
                current,
                CancellationToken.None);

            Assert.True(started.Success);
            Assert.Equal(VideoRecordingState.Recording, IpcJson.FromElement<VideoRecordingStatusV1>(started.Payload)!.State);
            Assert.True(status.Success);
            Assert.Equal(VideoRecordingState.Recording, IpcJson.FromElement<VideoRecordingStatusV1>(status.Payload)!.State);
            Assert.True(stopped.Success);
            Assert.Equal(VideoRecordingState.Completed, IpcJson.FromElement<VideoRecordingStatusV1>(stopped.Payload)!.State);
            Assert.Equal(1, recorder.StartCount);
            Assert.Equal(1, recorder.StopCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsRecorderRejectsAnOutOfRangeDurationBeforeCreatingAnyEncoder()
    {
        WindowsDesktopVideoRecorder recorder = new(
            () => [new CaptureTargetV1(CaptureTargetKind.Display, "display:\\\\.\\DISPLAY1", "Primary display")],
            Path.Combine(Path.GetTempPath(), $"pchelper-video-{Guid.NewGuid():N}"));

        VideoRecordingStartRequestV1 tooLong = new(
            VideoRecordingStartRequestV1.CurrentSchemaVersion,
            new CaptureTargetV1(CaptureTargetKind.Display, "display:\\\\.\\DISPLAY1", "Primary display"),
            ConfirmedVisibleCapture: true,
            IdempotencyKey: "video.too-long",
            MaxDurationSeconds: VideoRecordingStartRequestV1.MaximumDurationSeconds + 1,
            CaptureSystemAudio: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => recorder.StartAsync(tooLong, CancellationToken.None));
        Assert.Equal(VideoRecordingState.Idle, recorder.Status.State);
    }

    [Fact]
    public async Task WindowsRecorderRejectsATargetThatIsNotCurrentlyDiscoverable()
    {
        WindowsDesktopVideoRecorder recorder = new(
            () => [new CaptureTargetV1(CaptureTargetKind.Display, "display:\\\\.\\DISPLAY1", "Primary display")],
            Path.Combine(Path.GetTempPath(), $"pchelper-video-{Guid.NewGuid():N}"));

        VideoRecordingStartRequestV1 forged = new(
            VideoRecordingStartRequestV1.CurrentSchemaVersion,
            new CaptureTargetV1(CaptureTargetKind.Window, "window:0xDEAD", "Forged window"),
            ConfirmedVisibleCapture: true,
            IdempotencyKey: "video.forged-target",
            MaxDurationSeconds: 60,
            CaptureSystemAudio: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => recorder.StartAsync(forged, CancellationToken.None));
        Assert.Equal(VideoRecordingState.Idle, recorder.Status.State);
    }

    private static IpcRequest Request(IpcCommand command, object? payload = null, string? idempotencyKey = null) =>
        NamedPipeRequestClient.CreateRequest(command, payload, idempotencyKey: idempotencyKey);

    private sealed class FakeDesktopVideoRecorder : IDesktopVideoRecorder
    {
        private VideoRecordingStatusV1 _status = new(
            VideoRecordingStatusV1.CurrentSchemaVersion,
            VideoRecordingState.Idle,
            null,
            null,
            null,
            0,
            0,
            "Idle.");

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public VideoRecordingStatusV1 Status => _status;

        public Task<VideoRecordingStatusV1> StartAsync(VideoRecordingStartRequestV1 request, CancellationToken cancellationToken)
        {
            StartCount++;
            _status = _status with
            {
                State = VideoRecordingState.Recording,
                Target = request.Target,
                OutputPath = @"C:\fake\video.mp4",
                StartedAt = DateTimeOffset.UtcNow,
                Message = "Recording."
            };
            return Task.FromResult(_status);
        }

        public Task<VideoRecordingStatusV1> StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            _status = _status with { State = VideoRecordingState.Completed, Message = "Completed." };
            return Task.FromResult(_status);
        }
    }
}
