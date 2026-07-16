using System.IO;
using System.Security.Principal;
using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Integration.Tests;

public sealed class FrametimeBenchmarkTests
{
    [Fact]
    public void StatisticsComputeSessionMeanExtremesAndWorstWindowMeans()
    {
        // 200 windows: mostly 100 FPS with a few slow ones at the bottom.
        List<double> windows = [.. Enumerable.Repeat(100.0, 196), 40, 50, 60, 70];

        (double average, double minimum, double maximum, double onePercent, double pointOnePercent, double frameTime) =
            FrametimeBenchmarkStatistics.Compute(windows, [10.0, 10.5]);

        Assert.Equal(99.1, average); // (196*100 + 40+50+60+70) / 200
        Assert.Equal(40, minimum);
        Assert.Equal(100, maximum);
        // Worst 1% of 200 windows = worst 2 windows = mean(40, 50).
        Assert.Equal(45, onePercent);
        // Worst 0.1% floors to at least one window.
        Assert.Equal(40, pointOnePercent);
        Assert.Equal(10.25, frameTime);
    }

    [Fact]
    public void RecorderDeduplicatesUnchangedRtssWindowsAndCompletesWithStats()
    {
        Queue<RtssFrameStatsV1> readings = new([
            Stats(120.0, 8.33), Stats(120.0, 8.33), // same RTSS window polled twice
            Stats(90.0, 11.11),
            Stats(110.0, 9.09),
        ]);
        using FrametimeBenchmarkRecorder recorder = new(() => readings.Count > 1 ? readings.Dequeue() : readings.Peek());

        recorder.Start(Request());
        for (int tick = 0; tick < 4; tick++)
        {
            recorder.SampleOnce();
        }
        FrametimeBenchmarkStatusV1 result = recorder.StopBenchmark();

        Assert.Equal(FrametimeBenchmarkState.Completed, result.State);
        Assert.Equal(3, result.SampleCount); // the duplicate window was dropped
        Assert.Equal("game.exe", result.ProcessName);
        Assert.Equal(90, result.MinimumFps);
        Assert.Equal(120, result.MaximumFps);
        Assert.Contains("worst 1% / 0.1% of windows, not per-frame lows", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecorderFailsAfterSustainedAbsenceOfAPresentingApplication()
    {
        using FrametimeBenchmarkRecorder recorder = new(() => new RtssFrameStatsV1(
            RtssFrameStatsV1.CurrentSchemaVersion, false, [], "RTSS is not running."));

        recorder.Start(Request());
        for (int tick = 0; tick < 25 && recorder.Status.State == FrametimeBenchmarkState.Running; tick++)
        {
            recorder.SampleOnce();
        }

        Assert.Equal(FrametimeBenchmarkState.Failed, recorder.Status.State);
        Assert.Contains("no presenting application", recorder.Status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecorderRejectsInvalidRequestsAndConcurrentSessions()
    {
        using FrametimeBenchmarkRecorder recorder = new(() => Stats(100.0, 10.0));

        Assert.Throws<InvalidDataException>(() => recorder.Start(Request() with { MaxDurationSeconds = 5 }));
        Assert.Throws<InvalidDataException>(() => recorder.Start(Request() with { ProcessId = -1 }));

        recorder.Start(Request());
        Assert.Throws<InvalidOperationException>(() => recorder.Start(Request()));
        recorder.StopBenchmark();
    }

    [Fact]
    public void RecorderTargetsTheRequestedProcessIdWhenGiven()
    {
        RtssFrameStatsV1 stats = new(
            RtssFrameStatsV1.CurrentSchemaVersion,
            true,
            [
                new RtssAppFrameStatsV1(100, "fast.exe", 240.0, 4.17),
                new RtssAppFrameStatsV1(200, "target.exe", 60.0, 16.67),
            ],
            "Two applications.");
        using FrametimeBenchmarkRecorder recorder = new(() => stats);

        recorder.Start(Request() with { ProcessId = 200 });
        recorder.SampleOnce();
        FrametimeBenchmarkStatusV1 result = recorder.StopBenchmark();

        Assert.Equal("target.exe", result.ProcessName);
        Assert.Equal(60, result.AverageFps);
    }

    [Fact]
    public async Task UserAgentRoutesBenchmarkCommandsAndRejectsDoubleStart()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using FrametimeBenchmarkRecorder recorder = new(() => Stats(144.0, 6.94));
            await using UserAgentRuntime runtime = new(directory, frametimeBenchmark: recorder);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);

            IpcResponse started = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.StartFrametimeBenchmark, Request()),
                current, CancellationToken.None);
            IpcResponse duplicate = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.StartFrametimeBenchmark, Request()),
                current, CancellationToken.None);
            IpcResponse status = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetFrametimeBenchmarkStatus),
                current, CancellationToken.None);
            IpcResponse stopped = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.StopFrametimeBenchmark),
                current, CancellationToken.None);

            Assert.True(started.Success);
            Assert.False(duplicate.Success);
            Assert.Equal("BENCHMARK_IN_PROGRESS", duplicate.ErrorCode);
            Assert.Equal(FrametimeBenchmarkState.Running, IpcJson.FromElement<FrametimeBenchmarkStatusV1>(status.Payload)!.State);
            Assert.True(stopped.Success);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FrametimeBenchmarkStartRequestV1 Request() => new(
        FrametimeBenchmarkStartRequestV1.CurrentSchemaVersion,
        ProcessId: 0,
        MaxDurationSeconds: 60);

    private static RtssFrameStatsV1 Stats(double fps, double frameTimeMs) => new(
        RtssFrameStatsV1.CurrentSchemaVersion,
        true,
        [new RtssAppFrameStatsV1(4242, "game.exe", fps, frameTimeMs)],
        "One application.");
}
