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
    private async Task RefreshMonitorBrightnessCoreAsync(bool showNotice)
    {
        string? selectedId = SelectedMonitorBrightnessDevice?.Id;
        IReadOnlyList<MonitorBrightnessDeviceV1> devices = await GetUserEntitiesAsync<MonitorBrightnessDeviceV1>(IpcCommand.GetMonitorBrightnesses);
        Replace(MonitorBrightnessDevices, devices);
        SelectedMonitorBrightnessDevice = MonitorBrightnessDevices.FirstOrDefault(device =>
                string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? MonitorBrightnessDevices.FirstOrDefault(device => device.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified)
            ?? MonitorBrightnessDevices.FirstOrDefault();
        // A display may be unplugged, remapped, or replaced while retaining a
        // Windows logical display name. Every discovery pass therefore requires
        // a fresh acknowledgement before another brightness write.
        MonitorBrightnessDeviceConfirmed = false;

        int controllable = MonitorBrightnessDevices.Count(device => device.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified);
        MonitorBrightnessStatus = MonitorBrightnessDevices.Count == 0
            ? "Windows did not return any displays for this signed-in session."
            : controllable == 0
                ? "Displays were recognized, but none expose a verified writable DDC/CI or Windows-panel brightness range."
                : $"Detected {MonitorBrightnessDevices.Count} display{(MonitorBrightnessDevices.Count == 1 ? "" : "s")}; {controllable} expose{(controllable == 1 ? "s" : "")} a bounded DDC/CI or Windows-panel brightness path.";
        OnPropertyChanged(nameof(CanSetMonitorBrightness));
        OnPropertyChanged(nameof(IsSelectedMonitorBrightnessWritable));
        _setMonitorBrightnessCommand.RaiseCanExecuteChanged();
        if (showNotice)
        {
            ShowNotice(MonitorBrightnessStatus, controllable > 0 ? "Info" : "Warning");
        }
    }

    private async Task SetMonitorBrightnessCoreAsync()
    {
        MonitorBrightnessDeviceV1 device = SelectedMonitorBrightnessDevice
            ?? throw new InvalidOperationException("Select a monitor before changing brightness.");
        if (!int.TryParse(MonitorBrightnessPercentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int brightness)
            || brightness is < 0 or > 100)
        {
            throw new InvalidOperationException("Brightness must be a whole percentage from 0 through 100.");
        }

        BusyMessage = $"Applying {brightness}% brightness to {device.DisplayName}";
        IsBusy = true;
        try
        {
            IpcResponse response = await _userAgentClient.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SetMonitorBrightness,
                    new SetMonitorBrightnessRequestV1(
                        SetMonitorBrightnessRequestV1.CurrentSchemaVersion,
                        device.Id,
                        brightness,
                        ConfirmDevice: true),
                    idempotencyKey: Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(response);
            MonitorBrightnessApplyResultV1 result = IpcJson.FromElement<MonitorBrightnessApplyResultV1>(response.Payload)
                ?? throw new InvalidDataException("User agent returned an empty monitor brightness result.");
            if (!result.Applied || !result.ReadBackVerified)
            {
                throw new InvalidOperationException(result.Message);
            }

            await RefreshMonitorBrightnessCoreAsync(showNotice: false);
            MonitorBrightnessStatus = $"{device.DisplayName}: requested {result.RequestedPercent}%, read back {result.ObservedPercent ?? result.RequestedPercent}% through the selected local transport.";
            ShowNotice("Monitor brightness was applied and read back successfully.", "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryBuildOsdPresentationSettings(out OsdPresentationSettingsV1 settings)
    {
        settings = _osdPresentationSettings;
        if (!TryParseDouble(OsdOpacityText, out double opacityPercent)
            || opacityPercent is < 20 or > 100
            || !TryParseDouble(OsdScaleText, out double scalePercent)
            || scalePercent is < 60 or > 250)
        {
            return false;
        }

        string hotkey = OsdHotkeyText.Trim();
        if (hotkey.Length is 0 or > 32 || !hotkey.Contains('+', StringComparison.Ordinal))
        {
            return false;
        }

        settings = new OsdPresentationSettingsV1(
            OsdPresentationSettingsV1.CurrentSchemaVersion,
            OsdPresentationSettingsV1.DefaultId,
            SelectedOsdMonitor?.StableId,
            SelectedOsdAnchor,
            opacityPercent / 100d,
            scalePercent / 100d,
            hotkey,
            Enabled: true);
        return true;
    }
}
