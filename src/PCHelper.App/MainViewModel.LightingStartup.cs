using System.Windows.Input;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public sealed partial class MainViewModel
{
    // --- Static lighting restored at every service start ---------------------
    //
    // RGB controllers keep their colour in volatile lighting registers, so a
    // power cycle leaves them at whatever the firmware defaults to. Ticking this
    // records the colour currently in the box and has the service re-drive the
    // native routes at start, before anyone signs in.
    //
    // Only a flat colour is saved. Animated colourways are rendered by the
    // OpenRGB bridge and the signed-in user agent, neither of which exists at the
    // login screen, so persisting one here would promise something this path
    // cannot deliver.

    private bool _lightingStartupSaved;
    private string _lightingStartupStatus =
        "Lighting is applied for this session only. Tick to save the colour above and have the service restore it at every start.";

    private AsyncCommand? _toggleLightingStartupCommand;

    public bool LightingStartupSaved
    {
        get => _lightingStartupSaved;
        private set => Set(ref _lightingStartupSaved, value);
    }

    /// <summary>The service's own account of the saved lighting. Never inferred locally.</summary>
    public string LightingStartupStatus
    {
        get => _lightingStartupStatus;
        private set => Set(ref _lightingStartupStatus, value);
    }

    /// <summary>
    /// Bound <c>Mode=OneWay</c> plus this command, matching the other persistence switches:
    /// the box shows what the service holds, so a refused save cannot leave it ticked.
    /// </summary>
    public ICommand ToggleLightingStartupCommand => _toggleLightingStartupCommand ??= new AsyncCommand(
        _ => ToggleLightingStartupSafelyAsync(),
        _ => CanUseServiceWrites,
        ReportError,
        _ => ShowNotice(GetServiceWriteBlockReason(), "Warning"));

    private async Task ToggleLightingStartupSafelyAsync()
    {
        try
        {
            await ToggleLightingStartupAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowNotice($"Saving the startup lighting failed: {exception.Message}", "Warning");
        }
        finally
        {
            OnPropertyChanged(nameof(LightingStartupSaved));
        }
    }

    private async Task ToggleLightingStartupAsync()
    {
        if (LightingStartupSaved)
        {
            await SetLightingStartupAsync(new SetLightingStartupPersistenceRequest(
                Enable: false,
                Colour: string.Empty,
                RouteIds: []));
            return;
        }

        if (!TryParseOpenRgbInputs(out string colour, out int brightness))
        {
            ShowNotice("Use a #RRGGBB colour and brightness from 0 to 100% before saving it for startup.", "Warning");
            return;
        }

        // The saved colour is the one the native writers receive, brightness already
        // folded in, so what comes back at boot is what the routes were driven with
        // rather than an unscaled value that would return brighter than the operator set.
        string nativeColour = ScaleRgbHex(colour, brightness);
        if (LightingStartupPolicy.NormaliseColour(nativeColour) is not string normalised)
        {
            ShowNotice($"'{nativeColour}' is not a six-digit RGB colour.", "Warning");
            return;
        }

        // Brightness is folded into the saved colour, so a brightness of zero — or a
        // black colour — stores "restore my lighting to off at every boot". That is a
        // real thing to want and a very easy thing to do by accident, and the saved
        // value looks identical either way, so it is refused rather than guessed at.
        if (normalised == "000000")
        {
            ShowNotice(
                brightness == 0
                    ? "Brightness is 0%, so the saved colour would be black — every start would turn the lighting off. Raise the brightness first."
                    : "The colour is black, so every start would turn the lighting off. Pick a colour to restore instead.",
                "Warning");
            return;
        }

        // Every known route is offered; the service keeps only the ones that actually
        // accept the write, so an absent controller narrows the saved set instead of
        // failing the save or requiring the dashboard to track what is plugged in.
        await SetLightingStartupAsync(new SetLightingStartupPersistenceRequest(
            Enable: true,
            Colour: normalised,
            RouteIds: [.. LightingStartupPolicy.KnownRouteIds]));
    }

    private async Task SetLightingStartupAsync(SetLightingStartupPersistenceRequest payload)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetLightingStartupPersistence,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);

        if (!response.Success)
        {
            LightingStartupStatus = response.Error ?? "The service refused to save the lighting.";
            ShowNotice(LightingStartupStatus, "Warning");
            await RefreshLightingStartupAsync(_lifetime.Token);
            return;
        }

        UpdateStateRevision(response);
        ApplyLightingStartupStatus(IpcJson.FromElement<LightingStartupPersistenceStatus>(response.Payload));
        // The service's message names how many routes actually lit, which is the part
        // worth surfacing: "saved" without that count would hide a partial result.
        ShowNotice(LightingStartupStatus, payload.Enable ? "Success" : "Success");
    }

    /// <summary>
    /// Syncs the displayed state from the service. A service one version behind has no such
    /// command, so a failure clears the flag rather than throwing: it holds no saved lighting,
    /// and claiming otherwise would be worse than showing nothing.
    /// </summary>
    private async Task RefreshLightingStartupAsync(CancellationToken cancellationToken)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetLightingStartupPersistence),
            cancellationToken);
        if (!response.Success)
        {
            LightingStartupSaved = false;
            return;
        }

        ApplyLightingStartupStatus(IpcJson.FromElement<LightingStartupPersistenceStatus>(response.Payload));
    }

    private void ApplyLightingStartupStatus(LightingStartupPersistenceStatus? status)
    {
        if (status is null)
        {
            LightingStartupSaved = false;
            return;
        }

        LightingStartupSaved = status.Enabled;
        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            LightingStartupStatus = status.Message;
        }
    }
}
