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

        bool musicMode = AmbientMusicMode;
        _ambientCancellation = new CancellationTokenSource();
        CancellationToken token = _ambientCancellation.Token;
        AmbientRunning = true;
        AmbientStatus = musicMode
            ? "Music-reactive lighting is running. Your system's output audio spectrum drives every OpenRGB controller. Audio stays in memory; nothing is recorded."
            : "Screen-ambient lighting is running. The primary display's edge colours are mirrored onto every OpenRGB controller.";
        _ambientLoop = Task.Run(() => musicMode
            ? RunMusicLoopAsync(brightness, token)
            : RunAmbientLoopAsync(brightness, token), token);
    }

    public void StopAmbientLighting()
    {
        _ambientCancellation?.Cancel();
        _ambientCancellation = null;
        AmbientRunning = false;
        AmbientStatus = "Ambient lighting stopped. OpenRGB (or its own effects) owns the devices again.";
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
        _ => IsServiceOnline && HardwareControlEnabled,
        ReportError);

    public async Task ApplyGpuBracketAsync(bool turnOff)
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
        _ => IsServiceOnline && HardwareControlEnabled,
        ReportError);

    public async Task ApplyDimmRgbAsync(bool turnOff)
    {
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header first.", "Warning");
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
