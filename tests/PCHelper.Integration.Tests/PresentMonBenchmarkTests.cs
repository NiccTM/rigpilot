using System.IO;
using System.Security.Principal;
using System.Threading.Channels;
using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the Intel PresentMon per-frame benchmark against scripted CSV
/// sessions. No real PresentMon binary, ETW session, or game is touched.
/// </summary>
public sealed class PresentMonBenchmarkTests
{
    private const string V1Header = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msBetweenPresents";
    private const string V2Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameTime";

    [Fact]
    public void PerFrameStatisticsComputeFromAKnownDistribution()
    {
        // 100 frames: 99 at 10 ms plus one 30 ms hitch. Total 1020 ms.
        List<double> frames = [.. Enumerable.Repeat(10.0, 99), 30.0];

        (double average, double minimum, double maximum, double onePercent, double pointOnePercent, double frameTime) =
            PresentMonPerFrameStatistics.Compute(frames);

        Assert.Equal(98.0, average);          // 100 frames / 1.020 s
        Assert.Equal(33.3, minimum);          // the 30 ms hitch
        Assert.Equal(100.0, maximum);
        Assert.Equal(33.3, onePercent);       // worst 1% of 100 frames = the single hitch
        Assert.Equal(33.3, pointOnePercent);  // floors to at least one frame
        Assert.Equal(10.2, frameTime);
    }

    [Theory]
    [InlineData(V1Header)]
    [InlineData(V2Header)]
    public void CsvParserHandlesBothHeaderGenerationsAndSkipsBadRows(string header)
    {
        PresentMonCsvParser parser = new();

        Assert.True(parser.TryParseHeader(header));
        Assert.True(parser.TryParseRow(Row(header, "game.exe", 4242, "16.67"), out double frameTime, out int processId, out string? application));
        Assert.Equal(16.67, frameTime);
        Assert.Equal(4242, processId);
        Assert.Equal("game.exe", application);

        Assert.False(parser.TryParseRow(Row(header, "game.exe", 4242, "not-a-number"), out _, out _, out _));
        Assert.False(parser.TryParseRow(Row(header, "game.exe", 4242, "-5"), out _, out _, out _));
        Assert.False(parser.TryParseRow(Row(header, "game.exe", 4242, "0"), out _, out _, out _));
        Assert.False(parser.TryParseRow("short,row", out _, out _, out _));
    }

    [Fact]
    public void CsvParserRefusesRowsBeforeAUsableHeader()
    {
        PresentMonCsvParser parser = new();

        Assert.False(parser.TryParseHeader("Application,ProcessID,NothingUseful"));
        Assert.False(parser.TryParseRow(Row(V2Header, "game.exe", 1, "10"), out _, out _, out _));
    }

    [Fact]
    public async Task RecorderCompletesWithPerFrameStatsAndPicksTheDominantProcess()
    {
        // Two processes; pid 0 in the request must select the one with more frames.
        List<string> lines = [V2Header];
        lines.AddRange(Enumerable.Repeat(Row(V2Header, "game.exe", 100, "10"), 30));
        lines.AddRange(Enumerable.Repeat(Row(V2Header, "overlay.exe", 200, "50"), 5));
        using PresentMonBenchmarkRecorder recorder = new(new ScriptedFactory(new ScriptedSession(lines)));

        recorder.Start(Request());
        FrametimeBenchmarkStatusV1 result = await WaitForSettleAsync(recorder);

        Assert.Equal(FrametimeBenchmarkState.Completed, result.State);
        Assert.Equal("game.exe", result.ProcessName);
        Assert.Equal(30, result.SampleCount);
        Assert.Equal(100.0, result.AverageFps);
        Assert.Contains("per-frame lows", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecorderTargetsTheRequestedProcessId()
    {
        List<string> lines = [V2Header];
        lines.AddRange(Enumerable.Repeat(Row(V2Header, "fast.exe", 100, "5"), 40));
        lines.AddRange(Enumerable.Repeat(Row(V2Header, "target.exe", 200, "20"), 10));
        using PresentMonBenchmarkRecorder recorder = new(new ScriptedFactory(new ScriptedSession(lines)));

        recorder.Start(Request() with { ProcessId = 200 });
        FrametimeBenchmarkStatusV1 result = await WaitForSettleAsync(recorder);

        Assert.Equal("target.exe", result.ProcessName);
        Assert.Equal(10, result.SampleCount);
        Assert.Equal(50.0, result.AverageFps);
    }

    [Fact]
    public async Task RecorderFailsWhenNoFramesArrive()
    {
        using PresentMonBenchmarkRecorder recorder = new(new ScriptedFactory(new ScriptedSession([V2Header])));

        recorder.Start(Request());
        FrametimeBenchmarkStatusV1 result = await WaitForSettleAsync(recorder);

        Assert.Equal(FrametimeBenchmarkState.Failed, result.State);
        Assert.Contains("collected no frames", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecorderReportsMissingPresentMonWithoutEnteringARunningState()
    {
        using PresentMonBenchmarkRecorder recorder = new(new ScriptedFactory(null));

        FrametimeBenchmarkStatusV1 status = recorder.Start(Request());

        Assert.Equal(FrametimeBenchmarkState.Idle, status.State);
        Assert.Contains("was not found", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecorderRejectsInvalidRequestsAndConcurrentSessions()
    {
        using ScriptedSession openSession = new([V2Header], stayOpen: true);
        using PresentMonBenchmarkRecorder recorder = new(new ScriptedFactory(openSession));

        Assert.Throws<InvalidDataException>(() => recorder.Start(Request() with { MaxDurationSeconds = 5 }));
        Assert.Throws<InvalidDataException>(() => recorder.Start(Request() with { ProcessId = -1 }));

        recorder.Start(Request());
        Assert.Throws<InvalidOperationException>(() => recorder.Start(Request()));
        Assert.NotEqual(FrametimeBenchmarkState.Running, recorder.StopBenchmark().State);
    }

    [Fact]
    public async Task UserAgentRoutesPresentMonBenchmarkCommandsAndRejectsDoubleStart()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using ScriptedSession openSession = new([V2Header], stayOpen: true);
            using PresentMonBenchmarkRecorder recorder = new(new ScriptedFactory(openSession));
            await using UserAgentRuntime runtime = new(directory, presentMonBenchmark: recorder);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);

            IpcResponse started = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.StartPresentMonBenchmark, Request()),
                current, CancellationToken.None);
            IpcResponse duplicate = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.StartPresentMonBenchmark, Request()),
                current, CancellationToken.None);
            IpcResponse status = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetPresentMonBenchmarkStatus),
                current, CancellationToken.None);
            IpcResponse stopped = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.StopPresentMonBenchmark),
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

    /// <summary>Builds a data row whose cells line up with the given header's columns.</summary>
    private static string Row(string header, string application, int processId, string frameTime)
    {
        string[] columns = header.Split(',');
        string[] cells = new string[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            cells[index] = columns[index] switch
            {
                "Application" => application,
                "ProcessID" => processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "msBetweenPresents" or "FrameTime" => frameTime,
                _ => "0",
            };
        }

        return string.Join(',', cells);
    }

    private static async Task<FrametimeBenchmarkStatusV1> WaitForSettleAsync(PresentMonBenchmarkRecorder recorder)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (recorder.Status.State != FrametimeBenchmarkState.Running)
            {
                return recorder.Status;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The benchmark session did not settle.");
    }

    private sealed class ScriptedFactory(ScriptedSession? session) : IPresentMonSessionFactory
    {
        public string? DiscoveredPath => session is null ? null : "scripted";

        public string DiscoveryMessage => session is null
            ? "The Intel PresentMon console binary was not found. Install PresentMon, add it to PATH, or set PCHELPER_PRESENTMON_PATH."
            : "PresentMon console found: scripted.";

        public IPresentMonSession Start(int processId, int durationSeconds) =>
            session ?? throw new InvalidOperationException(DiscoveryMessage);
    }

    /// <summary>Feeds scripted CSV lines; optionally stays open until terminated like a live capture.</summary>
    private sealed class ScriptedSession : IPresentMonSession
    {
        private readonly ChannelReader<string> _lines;
        private readonly Channel<string> _channel;

        public ScriptedSession(IEnumerable<string> lines, bool stayOpen = false)
        {
            _channel = Channel.CreateUnbounded<string>();
            foreach (string line in lines)
            {
                _channel.Writer.TryWrite(line);
            }

            if (!stayOpen)
            {
                _channel.Writer.TryComplete();
            }

            _lines = _channel.Reader;
            StandardOutput = new ChannelTextReader(_lines);
        }

        public TextReader StandardOutput { get; }

        public void Terminate() => _channel.Writer.TryComplete();

        public void Dispose() => Terminate();

        private sealed class ChannelTextReader(ChannelReader<string> reader) : TextReader
        {
            public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                try
                {
                    return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }
        }
    }
}
