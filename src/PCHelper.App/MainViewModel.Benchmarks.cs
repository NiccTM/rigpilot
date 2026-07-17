using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfPointCollection = System.Windows.Media.PointCollection;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public sealed partial class MainViewModel
{
    // --- Benchmark history (local, bounded, never uploaded) -------------------

    public ObservableCollection<BenchmarkHistoryEntryV1> BenchmarkHistoryEntries { get; } = new(BenchmarkHistory.Load());

    private string _benchmarkCompareStatus = "Completed benchmark runs are recorded locally so the next run of the same game shows the change — e.g. after an undervolt preset.";

    public string BenchmarkCompareStatus
    {
        get => _benchmarkCompareStatus;
        private set => Set(ref _benchmarkCompareStatus, value);
    }

    public bool HasBenchmarkHistory => BenchmarkHistoryEntries.Count > 0;

    private void RecordCompletedBenchmark(FrametimeBenchmarkStatusV1 status, string source)
    {
        if (status.State != FrametimeBenchmarkState.Completed
            || string.IsNullOrWhiteSpace(status.ProcessName)
            || status.SampleCount <= 0)
        {
            return;
        }

        BenchmarkHistoryEntryV1 entry = new(
            BenchmarkHistoryEntryV1.CurrentSchemaVersion,
            DateTimeOffset.Now,
            source,
            Path.GetFileName(status.ProcessName),
            status.AverageFps ?? 0,
            status.MinimumFps ?? 0,
            status.MaximumFps ?? 0,
            status.OnePercentLowFps ?? 0,
            status.PointOnePercentLowFps ?? 0,
            status.SampleCount,
            status.DurationSeconds);
        BenchmarkHistoryEntryV1[] current = [.. BenchmarkHistoryEntries];
        if (BenchmarkHistory.IsDuplicateOfLatest(current, entry))
        {
            return;
        }

        BenchmarkCompareStatus = BenchmarkHistory.DescribeDelta(current, entry);
        BenchmarkHistory.Append(current, entry);
        BenchmarkHistoryEntries.Insert(0, entry);
        while (BenchmarkHistoryEntries.Count > BenchmarkHistory.MaximumEntries)
        {
            BenchmarkHistoryEntries.RemoveAt(BenchmarkHistoryEntries.Count - 1);
        }

        OnPropertyChanged(nameof(HasBenchmarkHistory));
    }

    private async Task ReadRtssFrameStatsCoreAsync()
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetRtssFrameStats),
            _lifetime.Token);
        EnsureSuccess(response);
        RtssFrameStatsV1 stats = IpcJson.FromElement<RtssFrameStatsV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty RTSS frame statistics result.");
        RtssFrameStatsStatus = stats.Applications.Count == 0
            ? stats.Message
            : string.Join("   ", stats.Applications.Select(app =>
                $"{Path.GetFileName(app.ProcessName)}: {app.FramesPerSecond:0.#} FPS ({app.FrameTimeMilliseconds:0.##} ms)"));
    }

    private async Task StartFrametimeBenchmarkCoreAsync()
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.StartFrametimeBenchmark,
                new FrametimeBenchmarkStartRequestV1(
                    FrametimeBenchmarkStartRequestV1.CurrentSchemaVersion,
                    ProcessId: 0,
                    MaxDurationSeconds: 300)),
            _lifetime.Token);
        EnsureSuccess(response);
        ApplyFrametimeBenchmarkStatus(IpcJson.FromElement<FrametimeBenchmarkStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty benchmark status."));
        ShowNotice("Benchmark started. It samples RTSS passively and stops automatically after 5 minutes.", "Info");
    }

    private async Task StopFrametimeBenchmarkCoreAsync()
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.StopFrametimeBenchmark),
            _lifetime.Token);
        EnsureSuccess(response);
        ApplyFrametimeBenchmarkStatus(IpcJson.FromElement<FrametimeBenchmarkStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty benchmark status."));
    }

    private void ApplyFrametimeBenchmarkStatus(FrametimeBenchmarkStatusV1 status)
    {
        RecordCompletedBenchmark(status, "RTSS windows");
        IsFrametimeBenchmarkRunning = status.State == FrametimeBenchmarkState.Running;
        FrametimeBenchmarkStatus = status.State switch
        {
            FrametimeBenchmarkState.Completed =>
                $"{status.ProcessName}: avg {status.AverageFps:0.#} FPS, min {status.MinimumFps:0.#}, max {status.MaximumFps:0.#}, " +
                $"1%-window low {status.OnePercentLowFps:0.#}, 0.1%-window low {status.PointOnePercentLowFps:0.#} " +
                $"({status.SampleCount} one-second windows over {status.DurationSeconds:0} s).",
            _ => status.Message
        };
    }

    private async Task StartPresentMonBenchmarkCoreAsync()
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.StartPresentMonBenchmark,
                new FrametimeBenchmarkStartRequestV1(
                    FrametimeBenchmarkStartRequestV1.CurrentSchemaVersion,
                    ProcessId: 0,
                    MaxDurationSeconds: 300)),
            _lifetime.Token);
        EnsureSuccess(response);
        FrametimeBenchmarkStatusV1 status = IpcJson.FromElement<FrametimeBenchmarkStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty benchmark status.");
        ApplyPresentMonBenchmarkStatus(status);
        if (status.State == FrametimeBenchmarkState.Running)
        {
            ShowNotice("Per-frame benchmark started via Intel PresentMon. It stops automatically after 5 minutes.", "Info");
        }
    }

    private async Task StopPresentMonBenchmarkCoreAsync()
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.StopPresentMonBenchmark),
            _lifetime.Token);
        EnsureSuccess(response);
        ApplyPresentMonBenchmarkStatus(IpcJson.FromElement<FrametimeBenchmarkStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty benchmark status."));
    }

    private void ApplyPresentMonBenchmarkStatus(FrametimeBenchmarkStatusV1 status)
    {
        RecordCompletedBenchmark(status, "PresentMon frames");
        IsPresentMonBenchmarkRunning = status.State == FrametimeBenchmarkState.Running;
        PresentMonBenchmarkStatus = status.State switch
        {
            FrametimeBenchmarkState.Completed =>
                $"{status.ProcessName}: avg {status.AverageFps:0.#} FPS, min {status.MinimumFps:0.#}, max {status.MaximumFps:0.#}, " +
                $"per-frame 1% low {status.OnePercentLowFps:0.#}, 0.1% low {status.PointOnePercentLowFps:0.#} " +
                $"({status.SampleCount} frames over {status.DurationSeconds:0} s).",
            _ => status.Message
        };
    }
}
