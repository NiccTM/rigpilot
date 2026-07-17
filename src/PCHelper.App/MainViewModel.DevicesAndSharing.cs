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
    // --- Drive health (read-only Windows Storage provider snapshot) -----------

    public ObservableCollection<StorageDeviceHealthV1> StorageHealthDevices { get; } = [];

    private string _storageHealthStatus = "Read-only drive identity, OS health status, and reliability counters (temperature, wear, power-on hours) where the drive reports them. RigPilot has no storage write path.";

    public string StorageHealthStatus
    {
        get => _storageHealthStatus;
        private set => Set(ref _storageHealthStatus, value);
    }

    public bool HasStorageHealthDevices => StorageHealthDevices.Count > 0;

    private AsyncCommand? _readStorageHealthCommand;
    public ICommand ReadStorageHealthCommand => _readStorageHealthCommand ??= new AsyncCommand(
        _ => ReadStorageHealthCoreAsync(),
        _ => IsServiceOnline,
        ReportError);

    private async Task ReadStorageHealthCoreAsync()
    {
        if (!IsServiceOnline)
        {
            StorageHealthStatus = "Drive health requires the RigPilot service.";
            return;
        }

        StorageHealthStatus = "Reading the Windows Storage provider…";
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetStorageHealth),
            _lifetime.Token);
        EnsureSuccess(response);
        StorageHealthReportV1 report = IpcJson.FromElement<StorageHealthReportV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty storage health report.");
        StorageHealthDevices.Clear();
        foreach (StorageDeviceHealthV1 device in report.Devices)
        {
            StorageHealthDevices.Add(device);
        }

        StorageHealthStatus = report.Message;
        OnPropertyChanged(nameof(HasStorageHealthDevices));
    }

    // --- Profile sharing (export/import as JSON) ------------------------------

    private ProfileCardDisplay? _selectedProfileForExport;

    public ProfileCardDisplay? SelectedProfileForExport
    {
        get => _selectedProfileForExport;
        set => Set(ref _selectedProfileForExport, value);
    }

    /// <summary>
    /// Serializes the selected profile's V2 record for sharing, or returns
    /// null with an explanatory notice when the selection has no typed V2
    /// content to share.
    /// </summary>
    public string? BuildSelectedProfileExport()
    {
        if (SelectedProfileForExport is not ProfileCardDisplay card)
        {
            ShowNotice("Select a profile to export first.", "Warning");
            return null;
        }

        if (!_suiteProfilesById.TryGetValue(card.Profile.Id, out ProfileV2? profile)
            || profile.HardwareActions.Count == 0)
        {
            ShowNotice("That profile has no typed hardware actions to share. Generated power presets are machine-specific.", "Warning");
            return null;
        }

        return ProfileShareFile.Export(profile, AppVersion);
    }

    /// <summary>
    /// Imports a shared profile file: renamed, re-identified, stripped of
    /// machine-local references, forced Experimental, then saved through the
    /// normal service path. Applying it later still requires the Experimental
    /// acknowledgement, exact-device confirmation, and per-action bounds
    /// clamping against this machine's discovered capabilities.
    /// </summary>
    public async Task ImportSharedProfileAsync(string json)
    {
        ProfileV2 profile = ProfileShareFile.Import(json);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveProfileV2,
                profile,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshAsync(full: true, userInitiated: false);
        ShowNotice($"Imported '{profile.Name}' as an Experimental profile. Review it before applying; every action is clamped to this machine's bounds.", "Success");
    }
}
