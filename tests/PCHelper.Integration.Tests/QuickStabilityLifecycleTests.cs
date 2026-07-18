using System.Reflection;
using PCHelper.App;

namespace PCHelper.Integration.Tests;

public sealed class QuickStabilityLifecycleTests
{
    [Fact]
    public void HardwareActionsStayClickableAndExplainBlockedPrerequisites()
    {
        using MainViewModel viewModel = new() { IsPortableMode = true };
        System.Windows.Input.ICommand[] hardwareActions =
        [
            viewModel.ApplyGpuControlCommand,
            viewModel.StartGpuAutoOcCommand,
            viewModel.StartGpuMemoryAutoOcCommand,
            viewModel.StartFullAutoOcCommand,
            viewModel.SetKrakenPumpCommand,
            viewModel.EnableCaseFansAutoModeCommand,
            viewModel.EnableGpuFanAutoModeCommand,
            viewModel.StartAdaptiveCoolingCommand,
            viewModel.ApplyUndervoltPresetCommand,
            viewModel.StartCalibrationCommand,
            viewModel.StartTuneCommand,
            viewModel.AbortOperationCommand,
            viewModel.SyncAllRgbCommand,
            viewModel.ApplyKrakenLightingCommand,
            viewModel.ApplyAuraLightingCommand,
            viewModel.ApplyGpuBracketCommand,
            viewModel.ApplyDimmRgbCommand,
            viewModel.ApplyRazerUsbCommand
        ];

        Assert.All(hardwareActions, command => Assert.True(command.CanExecute(null)));

        viewModel.StartGpuMemoryAutoOcCommand.Execute(null);

        Assert.True(viewModel.HasNotice);
        Assert.Equal("Warning", viewModel.NoticeTone);
        Assert.Contains("service", viewModel.NoticeText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.HasActiveOperation);
    }

    [Fact]
    public async Task StopAmbientLightingAwaitsLoopAndDisposesCancellation()
    {
        using MainViewModel viewModel = new();
        CancellationTokenSource cancellation = new();
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task loop = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                cancellationObserved.SetResult();
                await allowCompletion.Task;
            }
        });

        SetField(viewModel, "_ambientCancellation", cancellation);
        SetField(viewModel, "_ambientLoop", loop);
        SetField(viewModel, "_ambientRunning", true);

        Task stop = viewModel.StopAmbientLightingAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(stop.IsCompleted);

        allowCompletion.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(loop.IsCompletedSuccessfully);
        Assert.False(viewModel.AmbientRunning);
        Assert.Null(GetField<CancellationTokenSource>(viewModel, "_ambientCancellation"));
        Assert.Null(GetField<Task>(viewModel, "_ambientLoop"));
        Assert.Throws<ObjectDisposedException>(cancellation.Cancel);
    }

    private static void SetField<T>(MainViewModel viewModel, string name, T value) =>
        ResolveField(name).SetValue(viewModel, value);

    private static T? GetField<T>(MainViewModel viewModel, string name) where T : class =>
        ResolveField(name).GetValue(viewModel) as T;

    private static FieldInfo ResolveField(string name) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(MainViewModel).FullName, name);
}
