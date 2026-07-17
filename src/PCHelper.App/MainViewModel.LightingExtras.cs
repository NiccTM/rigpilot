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
    // Samples the primary display into a 32Ã—18 thumbnail each tick and writes
    // edge-zone colours to every OpenRGB controller. The thumbnail never leaves
    // process memory; only LED colour values reach the local OpenRGB socket.

    private const int AmbientTickMilliseconds = 150;

    private CancellationTokenSource? _ambientCancellation;
    private Task? _ambientLoop;
    private string _ambientStatus = "Extends what is on your primary screen onto your RGB devices through the OpenRGB bridge. The screen sample stays in memory only â€” nothing is saved, logged, or uploaded.";
    private bool _ambientRunning;

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

    private RelayCommand? _stopAmbientCommand;
    public ICommand StopAmbientLightingCommand => _stopAmbientCommand ??= new RelayCommand(
        _ => StopAmbientLighting(),
        _ => AmbientRunning);

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

        _ambientCancellation = new CancellationTokenSource();
        CancellationToken token = _ambientCancellation.Token;
        AmbientRunning = true;
        AmbientStatus = "Screen-ambient lighting is running. The primary display's edge colours are mirrored onto every OpenRGB controller.";
        _ambientLoop = Task.Run(() => RunAmbientLoopAsync(brightness, token), token);
    }

    public void StopAmbientLighting()
    {
        _ambientCancellation?.Cancel();
        _ambientCancellation = null;
        AmbientRunning = false;
        AmbientStatus = "Screen-ambient lighting stopped. OpenRGB (or its own effects) owns the devices again.";
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
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AmbientRunning = false;
                AmbientStatus = $"Screen-ambient lighting stopped: {exception.Message}";
            });
        }
    }

    // --- Native AURA lighting (RigPilot in-house adapter) ---------------------

    private AsyncCommand? _applyAuraLightingCommand;

    public ICommand ApplyAuraLightingCommand => _applyAuraLightingCommand ??= new AsyncCommand(
        parameter => ApplyAuraLightingAsync(string.Equals(parameter as string, "off", StringComparison.Ordinal)),
        _ => IsServiceOnline && HardwareControlEnabled,
        ReportError);

    public async Task ApplyAuraLightingAsync(bool turnOff)
    {
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header first.", "Warning");
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

    // --- Native Razer Chroma lighting (official REST SDK) ---------------------

    private string _razerChromaStatus = "Applies a static colour to every Razer Chroma device â€” the Lian Li O11 Razer Edition, keyboards, mice, and headsets â€” through Razer's official Chroma SDK. Needs Razer Synapse running.";

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

        RazerChromaStatus = "Applying via the Razer Chroma SDKâ€¦";
        ChromaRestClient client = new();
        ChromaConnectionResult result = await client.SetStaticColourAsync(colour, brightness, _lifetime.Token);
        RazerChromaStatus = result.Message;
        ShowNotice(result.Message, result.Connected ? "Success" : "Warning");
    }
}
