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
    private async Task StartMacroRecordingCoreAsync()
    {
        BeginMacroRecordingRequest payload = new(MacroRecordingName.Trim(), TimeSpan.FromSeconds(MacroRecordingDurationSeconds));
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.BeginMacroRecording, payload, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        ActiveMacroRecording = IpcJson.FromElement<MacroRecordingSessionV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty recording session.");
        MacroRecordingStatus = "Recording is active. Keyboard, clicks, and pointer movement are captured only until Stop or Cancel.";
        ShowNotice("Macro recording started. Stop to save, or cancel to discard all captured input.", "Warning");
    }

    private async Task StopMacroRecordingCoreAsync()
    {
        MacroRecordingSessionV1 session = ActiveMacroRecording
            ?? throw new InvalidOperationException("No macro recording is active.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.StopMacroRecording, new StopMacroRecordingRequest(session.Id), idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        MacroRecordingResultV1 result = IpcJson.FromElement<MacroRecordingResultV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty recording result.");
        ActiveMacroRecording = null;
        MacroRecordingStatus = $"Saved {result.Session.StepCount} recorded input step(s) as '{result.Macro?.Name ?? result.Session.Name}'.";
        await RefreshUserFeaturesAsync();
        SelectedMacro = Macros.FirstOrDefault(macro => macro.Id == result.Macro?.Id) ?? SelectedMacro;
        ShowNotice(MacroRecordingStatus, "Success");
    }

    private async Task CancelMacroRecordingCoreAsync()
    {
        MacroRecordingSessionV1 session = ActiveMacroRecording
            ?? throw new InvalidOperationException("No macro recording is active.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.CancelMacroRecording, new CancelMacroRecordingRequest(session.Id), idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        ActiveMacroRecording = null;
        MacroRecordingStatus = "Recording cancelled. Captured input was discarded and no macro was saved.";
        await RefreshUserFeaturesAsync();
        ShowNotice(MacroRecordingStatus, "Info");
    }

    private async Task TestMacroCoreAsync()
    {
        MacroV1 macro = SelectedMacro ?? throw new InvalidOperationException("Select a saved macro to test.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.ExecuteMacro, new ExecuteMacroRequest(macro.Id, ConfirmedVisibleSession: true)),
            _lifetime.Token);
        EnsureSuccess(response);
        MacroExecutionResultV1 result = IpcJson.FromElement<MacroExecutionResultV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty macro execution result.");
        ShowNotice($"Macro test completed: {result.ExecutedSteps} input step(s).", "Success");
    }

    private Task AddMacroKeyPressCoreAsync()
    {
        if (!TryGetMacroEditorKeyPress(out int keyCode, out int delayMilliseconds))
        {
            throw new InvalidOperationException("Enter a virtual-key code from 1 to 255 and a release delay from 0 to 5000 ms.");
        }

        _macroEditorSteps.Add(new MacroStepV1(MacroStepKind.KeyDown, keyCode, 0, 0, 0, TimeSpan.Zero));
        _macroEditorSteps.Add(new MacroStepV1(MacroStepKind.KeyUp, keyCode, 0, 0, 0, TimeSpan.FromMilliseconds(delayMilliseconds)));
        RefreshMacroEditorSummary();
        NotifyMacroEditorProperties();
        return Task.CompletedTask;
    }

    private Task RemoveMacroKeyPressCoreAsync()
    {
        if (!HasTrailingEditableKeyPress())
        {
            throw new InvalidOperationException("Only the last key press added by this editor can be removed safely. Re-record a macro to remove recorded pointer or media input.");
        }

        _macroEditorSteps.RemoveRange(_macroEditorSteps.Count - 2, 2);
        RefreshMacroEditorSummary();
        NotifyMacroEditorProperties();
        return Task.CompletedTask;
    }

    private async Task SaveMacroEditCoreAsync()
    {
        MacroV1 current = SelectedMacro ?? throw new InvalidOperationException("Select a saved macro before saving edits.");
        MacroV1 edited = current with
        {
            Name = MacroEditorName.Trim(),
            Steps = _macroEditorSteps.ToArray()
        };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.SaveMacro, edited, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        await RefreshUserFeaturesAsync();
        SelectedMacro = Macros.FirstOrDefault(macro => macro.Id == edited.Id) ?? edited;
        ShowNotice($"Saved typed edits to '{edited.Name}'.", "Success");
    }

    private void LoadMacroEditor(MacroV1? macro)
    {
        _macroEditorSteps.Clear();
        if (macro is null)
        {
            MacroEditorName = string.Empty;
            MacroEditorSummary = "Select a saved macro to review or edit its typed key presses.";
        }
        else
        {
            _macroEditorSteps.AddRange(macro.Steps);
            MacroEditorName = macro.Name;
            RefreshMacroEditorSummary();
        }
        NotifyMacroEditorProperties();
    }

    private bool TryGetMacroEditorKeyPress(out int keyCode, out int delayMilliseconds)
    {
        keyCode = 0;
        delayMilliseconds = 0;
        if (!int.TryParse(MacroEditorKeyCodeText, out keyCode)
            || keyCode is < 1 or > 255
            || !int.TryParse(MacroEditorDelayMillisecondsText, out delayMilliseconds)
            || delayMilliseconds is < 0 or > 5000)
        {
            keyCode = 0;
            delayMilliseconds = 0;
            return false;
        }
        return true;
    }

    private bool HasTrailingEditableKeyPress() => _macroEditorSteps.Count >= 2
        && _macroEditorSteps[^2] is { Kind: MacroStepKind.KeyDown } down
        && _macroEditorSteps[^1] is { Kind: MacroStepKind.KeyUp } up
        && down.Code == up.Code;

    private void RefreshMacroEditorSummary()
    {
        if (_macroEditorSteps.Count == 0)
        {
            MacroEditorSummary = "No typed steps remain. Add a key press or re-record this macro before saving.";
            return;
        }

        string preview = string.Join(
            " Ã‚Â· ",
            _macroEditorSteps.Take(6).Select(DescribeMacroStep));
        string suffix = _macroEditorSteps.Count > 6 ? " Ã‚Â· Ã¢â‚¬Â¦" : string.Empty;
        MacroEditorSummary = $"{_macroEditorSteps.Count} typed step(s): {preview}{suffix}";
    }

    private static string DescribeMacroStep(MacroStepV1 step) => step.Kind switch
    {
        MacroStepKind.KeyDown => $"Key {step.Code} down",
        MacroStepKind.KeyUp => $"Key {step.Code} up",
        MacroStepKind.MouseButtonDown => $"Mouse {step.Code} down",
        MacroStepKind.MouseButtonUp => $"Mouse {step.Code} up",
        MacroStepKind.MouseMove => $"Move {step.X},{step.Y}",
        MacroStepKind.MouseWheel => $"Wheel {step.Delta}",
        MacroStepKind.MediaKey => $"Media {step.Code}",
        _ => $"Delay {step.Delay.TotalMilliseconds:0} ms"
    };


    private async Task SaveGameBundleCoreAsync()
    {
        GameEntryV1 game = SelectedGame ?? throw new InvalidOperationException("Select a game first.");
        GameEntryV1 updated = game with
        {
            ProfileId = SelectedGameProfile?.Id,
            LightingSceneId = SelectedGameLightingScene?.Id,
            OsdLayoutId = SelectedGameOsdLayout?.Id,
            CapturePresetId = SelectedGameCapturePreset?.Id,
            MacroIds = SelectedGameMacro is null ? [] : [SelectedGameMacro.Id]
        };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.SaveGame, updated, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        GameEntryV1 saved = IpcJson.FromElement<GameEntryV1>(response.Payload) ?? updated;
        Replace(Games, Games.Where(item => item.Id != saved.Id).Append(saved).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        SelectedGame = saved;
        NotifyUserFeatureProperties();
        ShowNotice($"Saved the per-game bundle for '{saved.Name}'.", "Success");
    }

    private Task ShowDesktopOsdCoreAsync()
    {
        if (_snapshot is null || _snapshot.Sensors.Count == 0)
        {
            throw new InvalidOperationException("Live sensor data is required before showing a desktop OSD.");
        }

        OsdLayoutV1 layout = ResolveDesktopOsdLayout();
        OsdPresentationSettingsV1 presentation = TryBuildOsdPresentationSettings(out OsdPresentationSettingsV1 settings)
            ? settings
            : _osdPresentationSettings;
        if (!presentation.Enabled)
        {
            throw new InvalidOperationException("The local OSD is disabled in its presentation settings.");
        }
        _desktopOsd.Show(layout, _snapshot.Sensors, presentation);
        DesktopOsdStatus = $"Showing '{layout.Name}' as a local non-activating desktop overlay. It does not inject into games or write RTSS shared memory.";
        NotifyDesktopOsdProperties();
        ShowNotice("Desktop OSD is visible. Use Hide OSD to remove it.", "Success");
        return Task.CompletedTask;
    }

    private Task HideDesktopOsdCoreAsync()
    {
        _desktopOsd.Close();
        DesktopOsdStatus = "Desktop OSD is hidden. It uses a local non-activating window, not RTSS or injection.";
        NotifyDesktopOsdProperties();
        ShowNotice("Desktop OSD hidden.", "Info");
        return Task.CompletedTask;
    }

    private async Task CaptureDesktopSnapshotCoreAsync()
    {
        CaptureTargetV1 target = SelectedCaptureTarget
            ?? throw new InvalidOperationException("Select a display or visible window before capturing a snapshot.");
        BusyMessage = "Saving explicit desktop snapshot";
        IsBusy = true;
        try
        {
            IpcResponse response = await _userAgentClient.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.CaptureDesktopSnapshot,
                    new CaptureSnapshotRequestV1(
                        CaptureSnapshotRequestV1.CurrentSchemaVersion,
                        target,
                        ConfirmedVisibleCapture: true),
                    idempotencyKey: Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(response);
            CaptureSnapshotResultV1 result = IpcJson.FromElement<CaptureSnapshotResultV1>(response.Payload)
                ?? throw new InvalidDataException("User agent returned an empty desktop snapshot result.");
            string warning = string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : $" {result.Warning}";
            DesktopSnapshotStatus = $"Saved {result.Width} x {result.Height} PNG as {Path.GetFileName(result.OutputPath)} ({Math.Max(1, result.BytesWritten / 1024):N0} KB).{warning}";
            ShowNotice("Desktop snapshot saved only to your Pictures\\RigPilot\\Snapshots folder.", "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartVideoRecordingCoreAsync()
    {
        CaptureTargetV1 target = SelectedCaptureTarget
            ?? throw new InvalidOperationException("Select a display or visible window before recording.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.StartVideoRecording,
                new VideoRecordingStartRequestV1(
                    VideoRecordingStartRequestV1.CurrentSchemaVersion,
                    target,
                    ConfirmedVisibleCapture: true,
                    IdempotencyKey: Guid.NewGuid().ToString("N"),
                    MaxDurationSeconds: 300,
                    CaptureSystemAudio: true)),
            _lifetime.Token);
        EnsureSuccess(response);
        VideoRecordingStatusV1 status = IpcJson.FromElement<VideoRecordingStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty recording status.");
        ApplyVideoRecordingStatus(status);
        ShowNotice("Recording started. Output stays in your Videos\\RigPilot\\Recordings folder and stops automatically after 5 minutes.", "Info");
    }

    private async Task StopVideoRecordingCoreAsync()
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.StopVideoRecording),
            _lifetime.Token);
        EnsureSuccess(response);
        VideoRecordingStatusV1 status = IpcJson.FromElement<VideoRecordingStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty recording status.");
        // Stop is acknowledged asynchronously by the encoder; poll briefly so the user
        // sees the final file path instead of a transient "Recording" state.
        for (int attempt = 0; attempt < 10 && status.State == VideoRecordingState.Recording; attempt++)
        {
            await Task.Delay(300, _lifetime.Token);
            IpcResponse poll = await _userAgentClient.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetVideoRecordingStatus),
                _lifetime.Token);
            EnsureSuccess(poll);
            status = IpcJson.FromElement<VideoRecordingStatusV1>(poll.Payload) ?? status;
        }

        ApplyVideoRecordingStatus(status);
        if (status.State == VideoRecordingState.Completed && status.OutputPath is string path)
        {
            ShowNotice($"Recording saved as {Path.GetFileName(path)} in your Videos\\RigPilot\\Recordings folder.", "Success");
        }
    }

    private async Task PublishRtssOsdCoreAsync()
    {
        string line = BuildRtssOsdLine()
            ?? throw new InvalidOperationException("Live sensor data is required before publishing a sensor line to RTSS.");
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.PublishRtssOsdText,
                new RtssOsdPublishRequestV1(
                    RtssOsdPublishRequestV1.CurrentSchemaVersion,
                    line,
                    ConfirmedThirdPartyOsdWrite: true),
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        RtssOsdBridgeStatusV1 status = IpcJson.FromElement<RtssOsdBridgeStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty RTSS OSD status.");
        IsRtssOsdPublishing = response.Success && status.Publishing;
        RtssOsdPublishStatus = status.Message;
        if (!response.Success)
        {
            throw new InvalidOperationException(status.Message);
        }
        ShowNotice("RigPilot's sensor line is now published to the RTSS on-screen display. It refreshes with live sensors and uses only RigPilot's own OSD slot.", "Success");
    }

    private async Task ReleaseRtssOsdCoreAsync()
    {
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.ReleaseRtssOsd, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        RtssOsdBridgeStatusV1 status = IpcJson.FromElement<RtssOsdBridgeStatusV1>(response.Payload)
            ?? throw new InvalidDataException("User agent returned an empty RTSS OSD status.");
        IsRtssOsdPublishing = status.Publishing;
        RtssOsdPublishStatus = status.Message;
        ShowNotice("RigPilot's RTSS OSD slot is released.", "Info");
    }

    /// <summary>
    /// Builds the single OSD text line published to RTSS from the same sensor
    /// selection the desktop OSD uses, so both overlays always agree.
    /// </summary>
    private string? BuildRtssOsdLine()
    {
        if (_snapshot is null || _snapshot.Sensors.Count == 0)
        {
            return null;
        }
        OsdLayoutV1 layout = ResolveDesktopOsdLayout();
        Dictionary<string, SensorSample> samples = _snapshot.Sensors
            .Where(sample => sample.Value is double value && double.IsFinite(value))
            .GroupBy(sample => sample.SensorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        List<string> parts = [];
        foreach (OsdWidgetV1 widget in layout.Widgets)
        {
            if (samples.TryGetValue(widget.SensorId, out SensorSample? sample) && sample.Value is double value)
            {
                parts.Add($"{widget.Label} {value:0.#}{sample.Unit}");
            }
        }
        return parts.Count == 0 ? null : $"RigPilot: {string.Join("  ", parts)}";
    }

    private void RefreshRtssOsdPublish()
    {
        if (!IsRtssOsdPublishing || BuildRtssOsdLine() is not string line)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _rtssOsdRefreshBusy, 1, 0) != 0)
        {
            return;
        }
        _ = RefreshRtssOsdPublishAsync(line);
    }

    private async Task RefreshRtssOsdPublishAsync(string line)
    {
        try
        {
            IpcResponse response = await _userAgentClient.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.PublishRtssOsdText,
                    new RtssOsdPublishRequestV1(
                        RtssOsdPublishRequestV1.CurrentSchemaVersion,
                        line,
                        ConfirmedThirdPartyOsdWrite: true),
                    idempotencyKey: Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            RtssOsdBridgeStatusV1? status = IpcJson.FromElement<RtssOsdBridgeStatusV1>(response.Payload);
            if (status is not null)
            {
                IsRtssOsdPublishing = response.Success && status.Publishing;
                RtssOsdPublishStatus = status.Message;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A vanished RTSS instance stops publishing without surfacing an error
            // dialog; the card message explains how to resume.
            IsRtssOsdPublishing = false;
            RtssOsdPublishStatus = $"RTSS OSD publishing stopped safely: {exception.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _rtssOsdRefreshBusy, 0);
        }
    }

    private void ApplyVideoRecordingStatus(VideoRecordingStatusV1 status)
    {
        IsVideoRecording = status.State == VideoRecordingState.Recording;
        VideoRecordingStatus = status.State switch
        {
            VideoRecordingState.Recording => $"Recording {status.Target?.DisplayName}: {status.DurationSeconds:0} s elapsed. {status.Message}",
            VideoRecordingState.Completed => $"Saved {Path.GetFileName(status.OutputPath ?? string.Empty)} ({Math.Max(1, status.BytesWritten / 1024):N0} KB, {status.DurationSeconds:0} s).",
            VideoRecordingState.Failed => status.Message,
            _ => status.Message
        };
    }


    private void ApplyOsdPresentationSettings(OsdPresentationSettingsV1 settings)
    {
        _osdPresentationSettings = settings;
        _selectedOsdAnchor = settings.Anchor;
        _osdHotkeyText = settings.Hotkey;
        _osdOpacityText = ((settings.OpacityOverride ?? 0.92d) * 100d).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        _osdScaleText = ((settings.ScaleOverride ?? 1d) * 100d).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        _selectedOsdMonitor = ResolveOsdMonitor(settings.MonitorStableId);
        OnPropertyChanged(nameof(SelectedOsdAnchor));
        OnPropertyChanged(nameof(SelectedOsdMonitor));
        OnPropertyChanged(nameof(OsdHotkeyText));
        OnPropertyChanged(nameof(OsdOpacityText));
        OnPropertyChanged(nameof(OsdScaleText));
        NotifyOsdPresentationProperties();
        OsdHotkeyChanged?.Invoke(settings.Hotkey);
        RefreshDesktopOsd();
    }

    private async Task SaveOsdPresentationCoreAsync()
    {
        if (!TryBuildOsdPresentationSettings(out OsdPresentationSettingsV1 settings))
        {
            throw new InvalidOperationException("Use opacity from 20 to 100%, scale from 60 to 250%, and a short modifier hotkey such as Ctrl+Alt+O.");
        }

        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveOsdPresentationSettings,
                settings,
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        ApplyOsdPresentationSettings(IpcJson.FromElement<OsdPresentationSettingsV1>(response.Payload) ?? settings);
        ShowNotice("Saved local OSD placement and hotkey. The overlay remains non-injecting.", "Success");
    }

    private CaptureTargetV1? ResolveOsdMonitor(string? stableId)
    {
        IReadOnlyList<CaptureTargetV1> monitors = OsdMonitors;
        foreach (CaptureTargetV1 monitor in monitors)
        {
            if (string.Equals(monitor.StableId, stableId, StringComparison.OrdinalIgnoreCase))
            {
                return monitor;
            }
        }
        return monitors.Count > 0 ? monitors[0] : null;
    }
}
