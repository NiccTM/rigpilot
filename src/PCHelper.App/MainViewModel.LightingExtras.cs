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
    // --- Screen-ambient lighting (user-session, explicit start/stop) ----------
    // Samples the primary display into a 32×18 thumbnail each tick and writes
    // edge-zone colours to every OpenRGB controller. The thumbnail never leaves
    // process memory; only LED colour values reach the local OpenRGB socket.

    private const int AmbientTickMilliseconds = 150;
    private static readonly TimeSpan RgbOperationTimeout = TimeSpan.FromSeconds(10);

    private CancellationTokenSource? _ambientCancellation;
    private Task? _ambientLoop;
    private string _ambientStatus = "Extends what is on your primary screen onto your RGB devices through the OpenRGB bridge. The screen sample stays in memory only — nothing is saved, logged, or uploaded.";
    private bool _ambientRunning;
    private bool _ambientMusicMode;

    /// <summary>
    /// When set, the ambient loop reacts to the system's output audio spectrum
    /// (a music visualiser) instead of the screen. This is the computer's own
    /// playback captured via WASAPI loopback — never a microphone — and the
    /// audio, like the screen sample, stays in process memory.
    /// </summary>
    public bool AmbientMusicMode
    {
        get => _ambientMusicMode;
        set => Set(ref _ambientMusicMode, value);
    }

    public string AmbientStatus
    {
        get => _ambientStatus;
        private set => Set(ref _ambientStatus, value);
    }

    public bool AmbientRunning
    {
        get => _ambientRunning;
        private set
        {
            Set(ref _ambientRunning, value);
            _startAmbientCommand?.RaiseCanExecuteChanged();
            _stopAmbientCommand?.RaiseCanExecuteChanged();
        }
    }

    private RelayCommand? _startAmbientCommand;
    public ICommand StartAmbientLightingCommand => _startAmbientCommand ??= new RelayCommand(
        _ => StartAmbientLighting(),
        _ => !AmbientRunning);

    private AsyncCommand? _stopAmbientCommand;
    public ICommand StopAmbientLightingCommand => _stopAmbientCommand ??= new AsyncCommand(
        _ => StopAmbientLightingAsync(),
        _ => AmbientRunning,
        ReportError);

    public void StartAmbientLighting()
    {
        if (AmbientRunning)
        {
            return;
        }

        if (!OpenRgbEnabled || !OpenRgbConnected)
        {
            ShowNotice("Connect to the local OpenRGB SDK server first (Lighting page).", "Warning");
            return;
        }

        if (HasLightingConflict)
        {
            ShowNotice(LightingConflictReason, "Warning");
            return;
        }

        if (!TryParseOpenRgbInputs(out _, out int brightness))
        {
            brightness = 100;
        }

        bool musicMode = AmbientMusicMode;
        CancellationTokenSource cancellation = new();
        _ambientCancellation = cancellation;
        AmbientRunning = true;
        AmbientStatus = musicMode
            ? "Music-reactive lighting is running. Your system's output audio spectrum drives every OpenRGB controller. Audio stays in memory; nothing is recorded."
            : "Screen-ambient lighting is running. The primary display's edge colours are mirrored onto every OpenRGB controller.";
        _ambientLoop = Task.Run(() => RunAmbientLightingLifetimeAsync(musicMode, brightness, cancellation));
    }

    public async Task StopAmbientLightingAsync()
    {
        CancellationTokenSource? cancellation = _ambientCancellation;
        Task? loop = _ambientLoop;
        if (cancellation is null || loop is null)
        {
            AmbientRunning = false;
            AmbientStatus = "Ambient lighting stopped. OpenRGB (or its own effects) owns the devices again.";
            return;
        }

        CancellationToken token = cancellation.Token;
        AmbientStatus = "Stopping ambient lighting and releasing OpenRGB…";
        cancellation.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Task.Run can surface cancellation if shutdown races task startup.
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_ambientCancellation, cancellation))
            {
                _ambientCancellation = null;
                _ambientLoop = null;
            }
        }

        AmbientRunning = false;
        AmbientStatus = "Ambient lighting stopped. OpenRGB (or its own effects) owns the devices again.";
    }

    private void CancelAmbientLightingForDisposal()
    {
        try
        {
            _ambientCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The background lifetime wrapper already released this run.
        }
    }

    private async Task RunAmbientLightingLifetimeAsync(
        bool musicMode,
        int brightness,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (musicMode)
            {
                await RunMusicLoopAsync(brightness, cancellation.Token);
            }
            else
            {
                await RunAmbientLoopAsync(brightness, cancellation.Token);
            }
        }
        finally
        {
            bool cancellationRequested = cancellation.IsCancellationRequested;
            cancellation.Dispose();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_ambientCancellation, cancellation))
                {
                    return;
                }

                _ambientCancellation = null;
                _ambientLoop = null;
                if (!cancellationRequested)
                {
                    AmbientRunning = false;
                }
            });
        }
    }

    private async Task RunAmbientLoopAsync(int brightness, CancellationToken token)
    {
        try
        {
            OpenRgbSdkClient client = new();
            await using OpenRgbSdkClient.OpenRgbAmbientSession session = await client.OpenAmbientSessionAsync(token);
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(AmbientTickMilliseconds));
            while (await timer.WaitForNextTickAsync(token))
            {
                byte[] pixels = ScreenAmbientSampler.CapturePrimaryThumbnail();
                uint[] zones = ScreenAmbientSampler.ComputeEdgeZones(
                    pixels, ScreenAmbientSampler.SampleWidth, ScreenAmbientSampler.SampleHeight, brightness);
                await session.WriteFrameAsync(ledCount => ScreenAmbientSampler.MapZonesToLeds(zones, ledCount), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ReportAmbientStopAsync(exception);
        }
    }

    /// <summary>
    /// Music-reactive loop: the base colour (the OpenRGB colour field) is tinted
    /// per LED by the audio band energy at that LED's position around the ring,
    /// so bass sits on the low LEDs and treble on the high ones. Captures the
    /// system's own output audio via WASAPI loopback — never a microphone.
    /// </summary>
    private async Task RunMusicLoopAsync(int brightness, CancellationToken token)
    {
        try
        {
            if (!TryParseOpenRgbInputs(out string hex, out _))
            {
                hex = "#0A84FF";
            }

            uint baseColour = PackOpenRgbColour(hex, brightness);

            OpenRgbSdkClient client = new();
            await using OpenRgbSdkClient.OpenRgbAmbientSession session = await client.OpenAmbientSessionAsync(token);
            using AudioAmbientSource audio = new();
            audio.Start();

            float[] samples = new float[AudioSpectrum.FftSize];
            const int bandCount = ScreenAmbientSampler.ZoneCount;
            double[] bands = new double[bandCount];
            double peak = 0;
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(AmbientTickMilliseconds));
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!audio.TryReadLatest(samples))
                {
                    continue; // nothing is playing yet
                }

                peak = AudioSpectrum.ComputeBands(samples, audio.SampleRate, bandCount, peak, bands);
                await session.WriteFrameAsync(ledCount =>
                {
                    uint[] frame = new uint[ledCount];
                    for (int index = 0; index < ledCount; index++)
                    {
                        int band = (int)((long)index * bandCount / ledCount);
                        frame[index] = AudioSpectrum.ScaleColour(baseColour, bands[band]);
                    }

                    return frame;
                }, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ReportAmbientStopAsync(exception);
        }
    }

    /// <summary>Packs an #RRGGBB colour, brightness-scaled, into OpenRGB wire order (R | G&lt;&lt;8 | B&lt;&lt;16).</summary>
    private static uint PackOpenRgbColour(string hex, int brightnessPercent)
    {
        string value = hex.Trim().TrimStart('#');
        if (value.Length != 6 || !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            rgb = 0x0A84FF;
        }

        double scale = Math.Clamp(brightnessPercent, 0, 100) / 100.0;
        uint r = (uint)Math.Round((rgb >> 16 & 0xFF) * scale);
        uint g = (uint)Math.Round((rgb >> 8 & 0xFF) * scale);
        uint b = (uint)Math.Round((rgb & 0xFF) * scale);
        return r | (g << 8) | (b << 16);
    }

    private async Task ReportAmbientStopAsync(Exception exception)
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            AmbientRunning = false;
            AmbientStatus = $"Ambient lighting stopped: {exception.Message}";
        });
    }

    // --- Native AURA lighting (RigPilot in-house adapter) ---------------------

    private AsyncCommand? _applyAuraLightingCommand;

    public ICommand ApplyAuraLightingCommand => _applyAuraLightingCommand ??= new AsyncCommand(
        parameter => ApplyAuraLightingAsync(string.Equals(parameter as string, "off", StringComparison.Ordinal)),
        _ => CanRunHardwareAction(),
        ReportError,
        _ => ShowHardwareActionBlocked());

    public async Task ApplyAuraLightingAsync(bool turnOff)
    {
        if (!CanRunHardwareAction())
        {
            ShowHardwareActionBlocked();
            return;
        }

        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetAuraLighting,
                new AuraLightingRequestV1(
                    AuraLightingRequestV1.CurrentSchemaVersion,
                    OpenRgbColour,
                    turnOff,
                    ConfirmExperimental: true,
                    AuraLightingRequestV1.ExactDeviceId)),
            _lifetime.Token);
        EnsureSuccess(response);
        AuraLightingResultV1 result = IpcJson.FromElement<AuraLightingResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty AURA lighting result.");
        ShowNotice(result.Message, result.Outcome == KrakenLightingOutcome.WriteIssued ? "Success" : "Warning");
    }

    // --- One-click sync: route the chosen colour once per ready endpoint ------

    private AsyncCommand? _syncAllRgbCommand;

    public ICommand SyncAllRgbCommand => _syncAllRgbCommand ??= new AsyncCommand(
        parameter => SyncAllRgbAsync(string.Equals(parameter as string, "off", StringComparison.Ordinal)),
        _ => CanSyncAllRgb,
        ReportError,
        _ => ShowNotice(
            IsRgbSyncRunning
                ? "A lighting apply is already running. Wait for its per-route result."
                : "Use a #RRGGBB colour and brightness from 0 to 100%.",
            "Warning"));

    public async Task SyncAllRgbAsync(bool turnOff)
    {
        if (!TryParseOpenRgbInputs(out string colour, out int brightness))
        {
            ShowNotice("Use a #RRGGBB colour and brightness from 0 to 100%.", "Warning");
            return;
        }

        if (turnOff)
        {
            brightness = 0;
        }

        await ApplyUnifiedRgbAsync(
            colour,
            brightness,
            turnOff,
            scene: null,
            useColourway: true,
            operationLabel: turnOff ? "Lighting-off apply" : "Colour sync",
            showNotice: true);
    }

    private async Task<IReadOnlyList<RgbApplyOutcome>> ApplyUnifiedRgbAsync(
        string colour,
        int brightness,
        bool turnOff,
        LightingSceneV1? scene,
        bool useColourway,
        string operationLabel,
        bool showNotice)
    {
        await _rgbMutationGate.WaitAsync(_lifetime.Token);
        IsRgbSyncRunning = true;
        BusyMessage = operationLabel;
        IsBusy = true;
        try
        {
            if (AmbientRunning)
            {
                await StopAmbientLightingAsync();
            }

            List<RgbApplyOutcome> outcomes = [];
            HashSet<string> reservedFamilies = new(StringComparer.OrdinalIgnoreCase);
            bool openRgbActive = await ApplyReadyOpenRgbRoutesAsync(
                colour,
                brightness,
                turnOff,
                useColourway,
                outcomes,
                reservedFamilies);
            if (!openRgbActive)
            {
                await ApplyReadyDynamicLightingRoutesAsync(
                    colour,
                    brightness,
                    scene,
                    outcomes,
                    reservedFamilies);
            }

            await ApplyUncoveredNativeRgbRoutesAsync(
                colour,
                brightness,
                turnOff,
                outcomes,
                reservedFamilies);
            PublishRgbApplyOutcomes(operationLabel, outcomes, showNotice);
            return outcomes;
        }
        finally
        {
            IsBusy = false;
            IsRgbSyncRunning = false;
            _rgbMutationGate.Release();
        }
    }

    private async Task<(string Message, bool Warning)> ApplySavedLightingSceneAsync(
        LightingSceneV1 scene,
        string context)
    {
        if (!TryParseOpenRgbInputs(out string colour, out _))
        {
            return ($"{context} '{scene.Name}' was not applied because the current RGB colour is invalid.", true);
        }

        try
        {
            int brightness = (int)Math.Round(Math.Clamp(scene.BrightnessPercent, 0, 100));
            IReadOnlyList<RgbApplyOutcome> outcomes = await ApplyUnifiedRgbAsync(
                colour,
                brightness,
                turnOff: brightness == 0,
                scene: scene,
                useColourway: false,
                operationLabel: $"{context} '{scene.Name}'",
                showNotice: false);
            int applied = outcomes.Count(outcome => outcome.Applied);
            int unresolved = outcomes.Count(outcome => outcome.State is
                RgbApplyState.Blocked or RgbApplyState.Failed or RgbApplyState.Unknown);
            if (applied == 0)
            {
                return ($"{context} '{scene.Name}' reached no ready endpoint. {RgbSyncStatus}", true);
            }

            return unresolved == 0
                ? ($"{context} '{scene.Name}' was issued to {applied} endpoint(s); visually confirm routes without read-back.", false)
                : ($"{context} '{scene.Name}' was partially issued to {applied} endpoint(s). {RgbSyncStatus}", true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ($"{context} '{scene.Name}' failed independently: {exception.Message}", true);
        }
    }

    private async Task<bool> ApplyReadyOpenRgbRoutesAsync(
        string colour,
        int brightness,
        bool turnOff,
        bool useColourway,
        List<RgbApplyOutcome> outcomes,
        HashSet<string> reservedFamilies)
    {
        if (!OpenRgbEnabled)
        {
            return false;
        }

        if (!OpenRgbConnected)
        {
            try
            {
                await EnsureOpenRgbServerRunningAsync(_lifetime.Token);
                OpenRgbConnectionResult probe = await new OpenRgbSdkClient().ProbeAsync(_lifetime.Token);
                SetOpenRgbControllers(probe.Controllers);
                OpenRgbConnected = true;
                OpenRgbStatus = probe.Message;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                OpenRgbConnected = false;
                SetOpenRgbControllers([]);
                OpenRgbStatus = $"Bridge discovery failed before any write: {exception.Message}";
                outcomes.Add(new RgbApplyOutcome(
                    "openrgb:discovery",
                    "Local OpenRGB bridge",
                    "openrgb",
                    RgbApplyState.Failed,
                    OpenRgbStatus));
                return false;
            }
        }

        RebuildRgbRouteAssessments();
        List<(OpenRgbController Controller, string Family)> selected = [];
        foreach (OpenRgbController controller in _openRgbControllers)
        {
            string family = RgbEndpointFamily.Resolve(controller.Name);
            reservedFamilies.Add(family);
            RgbRouteAssessment? route = RgbRouteAssessments.FirstOrDefault(
                item => string.Equals(item.Id, $"openrgb:{controller.Id}", StringComparison.Ordinal));
            if (route?.State == RgbRouteState.Ready && controller.LedCount > 0)
            {
                selected.Add((controller, family));
                continue;
            }

            outcomes.Add(new RgbApplyOutcome(
                $"openrgb:{controller.Id}",
                controller.Name,
                family,
                route?.State == RgbRouteState.Blocked ? RgbApplyState.Blocked : RgbApplyState.Skipped,
                route?.Summary ?? "The controller is not ready in the current OpenRGB session."));
        }

        if (selected.Count == 0)
        {
            return _openRgbControllers.Count > 0;
        }

        try
        {
            OpenRgbSdkClient client = new();
            uint[] selectedIds = selected.Select(item => item.Controller.Id).ToArray();
            OpenRgbConnectionResult result = !useColourway || turnOff || SelectedColourway is null or { Id: "static" }
                ? await client.SetStaticColourAsync(colour, brightness, selectedIds, _lifetime.Token)
                : await client.SetColourwayAsync(SelectedColourway.Id, colour, brightness, selectedIds, _lifetime.Token);
            SetOpenRgbControllers(result.Controllers);
            OpenRgbConnected = true;
            OpenRgbStatus = result.Message;
            outcomes.AddRange(selected.Select(item => new RgbApplyOutcome(
                $"openrgb:{item.Controller.Id}",
                item.Controller.Name,
                item.Family,
                RgbApplyState.AppliedUnverified,
                $"{result.Message} OpenRGB supplies no independent colour read-back; confirm this endpoint visually.")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            bool failedBeforeWrite = IsPreWriteRgbFailure(exception.Message);
            OpenRgbStatus = failedBeforeWrite
                ? $"OpenRGB apply stopped before any lighting write: {exception.Message}"
                : $"OpenRGB apply failed after mutation began: {exception.Message}";
            RgbApplyState state = failedBeforeWrite
                ? RgbApplyState.Failed
                : RgbApplyState.Unknown;
            outcomes.AddRange(selected.Select(item => new RgbApplyOutcome(
                $"openrgb:{item.Controller.Id}",
                item.Controller.Name,
                item.Family,
                state,
                OpenRgbStatus)));
        }

        return true;
    }

    private async Task ApplyReadyDynamicLightingRoutesAsync(
        string colour,
        int brightness,
        LightingSceneV1? scene,
        List<RgbApplyOutcome> outcomes,
        HashSet<string> reservedFamilies)
    {
        try
        {
            IReadOnlyList<DynamicLightingDevice> devices = await DynamicLightingBridge
                .ProbeAsync(_lifetime.Token)
                .WaitAsync(RgbOperationTimeout, _lifetime.Token);
            Replace(DynamicLightingDevices, devices, device => device.Id, StringComparer.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(DynamicLightingDeviceCount));
            DynamicLightingStatus = devices.Count == 0
                ? "Windows reported no LampArray-compatible devices."
                : $"Windows reported {devices.Count} Dynamic Lighting device(s).";
            RebuildRgbRouteAssessments();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outcomes.Add(new RgbApplyOutcome(
                "dynamic:discovery",
                "Windows Dynamic Lighting",
                "dynamic-lighting",
                RgbApplyState.Failed,
                $"Dynamic Lighting discovery failed before any write: {exception.Message}"));
            return;
        }

        HashSet<string>? sceneDeviceIds = scene is { Zones.Count: > 0 }
            ? scene.Zones.Select(zone => zone.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        List<(DynamicLightingDevice Device, string Family)> selected = [];
        foreach (DynamicLightingDevice device in DynamicLightingDevices)
        {
            string family = RgbEndpointFamily.Resolve(device.Name);
            reservedFamilies.Add(family);
            RgbRouteAssessment? route = RgbRouteAssessments.FirstOrDefault(
                item => string.Equals(item.Id, $"dynamic:{device.Id}", StringComparison.Ordinal));
            bool isSceneTarget = sceneDeviceIds is null || sceneDeviceIds.Contains(device.Id);
            if (route?.State == RgbRouteState.Ready && device.LampCount > 0 && isSceneTarget)
            {
                selected.Add((device, family));
                continue;
            }

            outcomes.Add(new RgbApplyOutcome(
                $"dynamic:{device.Id}",
                device.Name,
                family,
                route?.State == RgbRouteState.Blocked ? RgbApplyState.Blocked : RgbApplyState.Skipped,
                !isSceneTarget
                    ? "This endpoint is not part of the saved scene."
                    : route?.Summary ?? "Windows did not expose this LampArray endpoint as ready."));
        }

        if (sceneDeviceIds is not null)
        {
            foreach (string missingId in sceneDeviceIds.Except(
                DynamicLightingDevices.Select(device => device.Id),
                StringComparer.OrdinalIgnoreCase))
            {
                outcomes.Add(new RgbApplyOutcome(
                    $"dynamic:{missingId}",
                    missingId,
                    $"endpoint:{missingId}",
                    RgbApplyState.Failed,
                    "This saved-scene endpoint is not present in the current Windows LampArray inventory; no output was sent to it."));
            }
        }

        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            if (scene is { Zones.Count: > 0 })
            {
                HashSet<string> selectedIds = selected
                    .Select(item => item.Device.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                LightingSceneV1 routedScene = scene with
                {
                    Zones = scene.Zones.Where(zone => selectedIds.Contains(zone.DeviceId)).ToArray(),
                    DisabledDeviceIds = scene.DisabledDeviceIds.Where(selectedIds.Contains).ToArray()
                };
                await DynamicLightingBridge
                    .ApplyStaticSceneAsync(routedScene, colour, _lifetime.Token)
                    .WaitAsync(RgbOperationTimeout, _lifetime.Token);
            }
            else
            {
                await DynamicLightingBridge
                    .ApplyStaticColourAsync(
                        selected.Select(item => item.Device.Id).ToArray(),
                        colour,
                        brightness,
                        _lifetime.Token)
                    .WaitAsync(RgbOperationTimeout, _lifetime.Token);
            }
            outcomes.AddRange(selected.Select(item => new RgbApplyOutcome(
                $"dynamic:{item.Device.Id}",
                item.Device.Name,
                item.Family,
                RgbApplyState.AppliedUnverified,
                "Windows accepted the LampArray update, but no independent physical colour read-back is available.")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outcomes.AddRange(selected.Select(item => new RgbApplyOutcome(
                $"dynamic:{item.Device.Id}",
                item.Device.Name,
                item.Family,
                RgbApplyState.Unknown,
                $"Dynamic Lighting failed after mutation began: {exception.Message}")));
        }
    }

    private async Task ApplyUncoveredNativeRgbRoutesAsync(
        string colour,
        int brightness,
        bool turnOff,
        List<RgbApplyOutcome> outcomes,
        HashSet<string> reservedFamilies)
    {
        string nativeColour = ScaleRgbHex(colour, brightness);
        await ApplyNativeRgbRouteAsync(
            "native:kraken",
            "NZXT Kraken",
            "nzxt-kraken",
            IpcCommand.SetKrakenLighting,
            new KrakenLightingRequestV1(KrakenLightingRequestV1.CurrentSchemaVersion, nativeColour, turnOff, true, KrakenLightingRequestV1.ExactDeviceId),
            response =>
            {
                RgbWriteResult? result = IpcJson.FromElement<KrakenLightingResultV1>(response.Payload)?.ToRgbWriteResult();
                return (result?.WriteIssued == true, result?.Message);
            },
            outcomes,
            reservedFamilies);
        await ApplyNativeRgbRouteAsync(
            "native:aura",
            "ASUS Aura headers",
            "asus-aura",
            IpcCommand.SetAuraLighting,
            new AuraLightingRequestV1(AuraLightingRequestV1.CurrentSchemaVersion, nativeColour, turnOff, true, AuraLightingRequestV1.ExactDeviceId),
            response =>
            {
                RgbWriteResult? result = IpcJson.FromElement<AuraLightingResultV1>(response.Payload)?.ToRgbWriteResult();
                return (result?.WriteIssued == true, result?.Message);
            },
            outcomes,
            reservedFamilies);
        await ApplyNativeRgbRouteAsync(
            "native:dimm",
            "G.Skill Trident Z RAM",
            "dimm-rgb",
            IpcCommand.SetDimmRgb,
            new DimmRgbRequestV1(DimmRgbRequestV1.CurrentSchemaVersion, nativeColour, turnOff, true, DimmRgbRequestV1.ExactDeviceId),
            response =>
            {
                RgbWriteResult? result = IpcJson.FromElement<DimmRgbResultV1>(response.Payload)?.ToRgbWriteResult();
                return (result?.WriteIssued == true, result?.Message);
            },
            outcomes,
            reservedFamilies);
        await ApplyNativeRgbRouteAsync(
            "native:razer",
            "Razer Lian Li O11 case",
            "razer-lianli",
            IpcCommand.SetRazerRgb,
            new RazerRgbRequestV1(RazerRgbRequestV1.CurrentSchemaVersion, nativeColour, turnOff, true, RazerRgbRequestV1.ExactDeviceId),
            response =>
            {
                RgbWriteResult? result = IpcJson.FromElement<RazerRgbResultV1>(response.Payload)?.ToRgbWriteResult();
                return (result?.WriteIssued == true, result?.Message);
            },
            outcomes,
            reservedFamilies);
    }

    private async Task ApplyNativeRgbRouteAsync(
        string routeId,
        string name,
        string family,
        IpcCommand command,
        object payload,
        Func<IpcResponse, (bool WriteIssued, string? Message)> inspect,
        List<RgbApplyOutcome> outcomes,
        HashSet<string> reservedFamilies)
    {
        if (reservedFamilies.Contains(family))
        {
            outcomes.Add(new RgbApplyOutcome(
                routeId,
                name,
                family,
                RgbApplyState.Skipped,
                "A standard bridge already owns this endpoint family, so the native writer was not called."));
            return;
        }

        IReadOnlyList<ConflictDescriptor> owners = RgbConflictPolicy.FindBlockingOwners(
            _snapshot?.Conflicts,
            name);
        if (owners.Count > 0)
        {
            outcomes.Add(new RgbApplyOutcome(
                routeId,
                name,
                family,
                RgbApplyState.Blocked,
                $"Owned by {string.Join(", ", owners.Select(owner => owner.DisplayName))}; no native write was sent."));
            return;
        }

        if (!CanRunHardwareAction())
        {
            outcomes.Add(new RgbApplyOutcome(
                routeId,
                name,
                family,
                RgbApplyState.Skipped,
                CanUseServiceWrites
                    ? "Turn on Hardware control to allow this exact-device native fallback."
                    : GetServiceWriteBlockReason()));
            return;
        }

        try
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(command, payload),
                _lifetime.Token);
            (bool writeIssued, string? deviceMessage) = inspect(response);
            NativeRgbSyncOutcome native = BuildNativeRgbSyncOutcome(name, response, writeIssued, deviceMessage);
            RgbApplyState state = native.Succeeded
                ? RgbApplyState.AppliedUnverified
                : IsUnknownRgbOutcome(native.Message)
                    ? RgbApplyState.Unknown
                    : string.Equals(response.ErrorCode, "RECOVERY_REQUIRED", StringComparison.OrdinalIgnoreCase)
                        ? RgbApplyState.Blocked
                        : RgbApplyState.Failed;
            outcomes.Add(new RgbApplyOutcome(routeId, name, family, state, native.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outcomes.Add(new RgbApplyOutcome(
                routeId,
                name,
                family,
                RgbApplyState.Unknown,
                $"The IPC operation ended without a final device result: {exception.Message}"));
        }
    }

    private void PublishRgbApplyOutcomes(
        string operationLabel,
        IReadOnlyList<RgbApplyOutcome> outcomes,
        bool showNotice)
    {
        Replace(LastRgbApplyOutcomes, outcomes, outcome => outcome.RouteId, StringComparer.Ordinal);
        OnPropertyChanged(nameof(HasRgbApplyOutcomes));
        int applied = outcomes.Count(outcome => outcome.Applied);
        int blocked = outcomes.Count(outcome => outcome.State == RgbApplyState.Blocked);
        int failed = outcomes.Count(outcome => outcome.State == RgbApplyState.Failed);
        int unknown = outcomes.Count(outcome => outcome.State == RgbApplyState.Unknown);
        int skipped = outcomes.Count(outcome => outcome.State == RgbApplyState.Skipped);
        RgbSyncStatus = $"{operationLabel}: {applied} applied · {blocked} blocked · {failed} failed before write · {unknown} unknown · {skipped} skipped. Successful routes without hardware colour read-back still require visual confirmation.";
        if (showNotice)
        {
            ShowNotice(RgbSyncStatus, applied > 0 && blocked + failed + unknown == 0 ? "Success" : "Warning");
        }
    }

    private static bool IsUnknownRgbOutcome(string message) =>
        message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
        || message.Contains("without a final", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreWriteRgbFailure(string message) =>
        message.Contains("No lighting output was sent", StringComparison.OrdinalIgnoreCase)
        || message.Contains("before any lighting write", StringComparison.OrdinalIgnoreCase);

    internal static string ScaleRgbHex(string colour, int brightnessPercent)
    {
        string text = colour.Trim().TrimStart('#');
        if (text.Length != 6
            || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            throw new FormatException("Lighting colour must be a six-digit RGB hexadecimal value.");
        }

        double scale = Math.Clamp(brightnessPercent, 0, 100) / 100.0;
        int red = (int)Math.Round((rgb >> 16 & 0xFF) * scale);
        int green = (int)Math.Round((rgb >> 8 & 0xFF) * scale);
        int blue = (int)Math.Round((rgb & 0xFF) * scale);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    /// <summary>
    /// Uses the local OpenRGB bridge first, then Windows Dynamic Lighting, then
    /// contained native fallbacks only for endpoint families not already owned
    /// by a standard bridge. Every endpoint receives a truthful result.
    /// </summary>
    internal static NativeRgbSyncOutcome BuildNativeRgbSyncOutcome(
        string name,
        IpcResponse response,
        bool writeIssued,
        string? deviceMessage)
    {
        string detail = !response.Success && !string.IsNullOrWhiteSpace(response.Error)
            ? response.Error
            : !string.IsNullOrWhiteSpace(deviceMessage)
                ? deviceMessage
                : "The device returned no lighting result.";
        return new NativeRgbSyncOutcome(name, response.Success && writeIssued, detail);
    }

    // --- GPU sag bracket (passive ARGB on one addressable header) -------------

    private int _gpuBracketHeader = 1;

    /// <summary>1-based addressable header the Cooler Master GPU sag bracket is plugged into.</summary>
    public int GpuBracketHeader
    {
        get => _gpuBracketHeader;
        set
        {
            if (Set(ref _gpuBracketHeader, Math.Clamp(value, 1, AuraLightingRequestV1.HeaderCount)))
            {
                OnPropertyChanged(nameof(GpuBracketHeaderIndex));
            }
        }
    }

    /// <summary>0-based view of <see cref="GpuBracketHeader"/> for ComboBox SelectedIndex binding.</summary>
    public int GpuBracketHeaderIndex
    {
        get => GpuBracketHeader - 1;
        set => GpuBracketHeader = value + 1;
    }

    private AsyncCommand? _applyGpuBracketCommand;

    public ICommand ApplyGpuBracketCommand => _applyGpuBracketCommand ??= new AsyncCommand(
        parameter => ApplyGpuBracketAsync(string.Equals(parameter as string, "off", StringComparison.Ordinal)),
        _ => CanRunHardwareAction(),
        ReportError,
        _ => ShowHardwareActionBlocked());

    public async Task ApplyGpuBracketAsync(bool turnOff)
    {
        if (!CanRunHardwareAction())
        {
            ShowHardwareActionBlocked();
            return;
        }

        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetAuraLighting,
                new AuraLightingRequestV1(
                    AuraLightingRequestV1.CurrentSchemaVersion,
                    OpenRgbColour,
                    turnOff,
                    ConfirmExperimental: true,
                    AuraLightingRequestV1.ExactDeviceId,
                    GpuBracketHeader)),
            _lifetime.Token);
        EnsureSuccess(response);
        AuraLightingResultV1 result = IpcJson.FromElement<AuraLightingResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty AURA lighting result.");
        ShowNotice(result.Message, result.Outcome == KrakenLightingOutcome.WriteIssued ? "Success" : "Warning");
    }

    // --- Native DIMM RGB (Trident Z RGB over SMBus, RigPilot in-house) --------

    private AsyncCommand? _applyDimmRgbCommand;

    public ICommand ApplyDimmRgbCommand => _applyDimmRgbCommand ??= new AsyncCommand(
        parameter => ApplyDimmRgbAsync(string.Equals(parameter as string, "off", StringComparison.Ordinal)),
        _ => CanRunHardwareAction(),
        ReportError,
        _ => ShowHardwareActionBlocked());

    public async Task ApplyDimmRgbAsync(bool turnOff)
    {
        if (!CanRunHardwareAction())
        {
            ShowHardwareActionBlocked();
            return;
        }

        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetDimmRgb,
                new DimmRgbRequestV1(
                    DimmRgbRequestV1.CurrentSchemaVersion,
                    OpenRgbColour,
                    turnOff,
                    ConfirmExperimental: true,
                    DimmRgbRequestV1.ExactDeviceId)),
            _lifetime.Token);
        EnsureSuccess(response);
        DimmRgbResultV1 result = IpcJson.FromElement<DimmRgbResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty DIMM RGB result.");
        ShowNotice(result.Message, result.WriteIssued ? "Success" : "Warning");
    }

    // --- Native Razer USB lighting (RigPilot in-house, no Synapse) ------------

    private AsyncCommand? _applyRazerUsbCommand;

    public ICommand ApplyRazerUsbCommand => _applyRazerUsbCommand ??= new AsyncCommand(
        parameter => ApplyRazerUsbAsync(string.Equals(parameter as string, "off", StringComparison.Ordinal)),
        _ => CanRunHardwareAction(),
        ReportError,
        _ => ShowHardwareActionBlocked());

    public async Task ApplyRazerUsbAsync(bool turnOff)
    {
        if (!CanRunHardwareAction())
        {
            ShowHardwareActionBlocked();
            return;
        }

        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetRazerRgb,
                new RazerRgbRequestV1(
                    RazerRgbRequestV1.CurrentSchemaVersion,
                    OpenRgbColour,
                    turnOff,
                    ConfirmExperimental: true,
                    RazerRgbRequestV1.ExactDeviceId)),
            _lifetime.Token);
        EnsureSuccess(response);
        RazerRgbResultV1 result = IpcJson.FromElement<RazerRgbResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty Razer lighting result.");
        ShowNotice(result.Message, result.Outcome == KrakenLightingOutcome.WriteIssued ? "Success" : "Warning");
    }

    // --- Native Razer Chroma lighting (official REST SDK) ---------------------

    private string _razerChromaStatus = "Applies a static colour to every Razer Chroma device — the Lian Li O11 Razer Edition, keyboards, mice, and headsets — through Razer's official Chroma SDK. Needs Razer Synapse running.";

    public System.Windows.Input.ICommand ApplyRazerChromaCommand { get; }

    public string RazerChromaStatus
    {
        get => _razerChromaStatus;
        private set => Set(ref _razerChromaStatus, value);
    }

    private async Task ApplyRazerChromaAsync()
    {
        if (!TryParseOpenRgbInputs(out string colour, out int brightness))
        {
            RazerChromaStatus = "Use a #RRGGBB colour and brightness from 0 to 100%.";
            return;
        }

        RazerChromaStatus = "Applying via the Razer Chroma SDK…";
        ChromaRestClient client = new();
        ChromaConnectionResult result = await client.SetStaticColourAsync(colour, brightness, _lifetime.Token);
        RazerChromaStatus = result.Message;
        ShowNotice(result.Message, result.Connected ? "Success" : "Warning");
    }
}

internal sealed record NativeRgbSyncOutcome(string Name, bool Succeeded, string Message);
