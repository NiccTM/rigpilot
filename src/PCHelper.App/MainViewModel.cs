using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows;
using System.IO;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly NamedPipeRequestClient _client = new(ProtocolConstants.ServicePipeName, TimeSpan.FromSeconds(3));
    private readonly System.Threading.Timer _refreshTimer;
    private AdapterCoordinator? _localCoordinator;
    private bool _serviceOnline;
    private HardwareSnapshot? _snapshot;
    private ServiceStatus? _status;
    private string _serviceStatusText = "Connecting…";
    private string _activeProfileName = "None";
    private string _currentPageTitle = "Overview";
    private string _currentPageSubtitle = "Live health, ownership, and safety state";
    private string _safetySummary = "Waiting for service data.";
    private bool _refreshing;

    public MainViewModel()
    {
        RefreshCommand = new AsyncCommand(_ => RefreshAsync(full: true));
        ApplyProfileCommand = new AsyncCommand(parameter => ApplyProfileAsync((ProfileV1)parameter!));
        ResetVerifiedCommand = new AsyncCommand(_ => ResetVerifiedControlsAsync());
        _refreshTimer = new System.Threading.Timer(
            _ => System.Windows.Application.Current.Dispatcher.BeginInvoke(async () => await RefreshAsync(full: false)),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SensorDisplay> ImportantSensors { get; } = [];

    public ObservableCollection<ProfileV1> Profiles { get; } = [];

    public ObservableCollection<CapabilityDisplay> CoolingCapabilities { get; } = [];

    public ObservableCollection<CapabilityDisplay> PerformanceCapabilities { get; } = [];

    public ObservableCollection<DeviceDisplay> Devices { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand ApplyProfileCommand { get; }

    public ICommand ResetVerifiedCommand { get; }

    public string ServiceStatusText
    {
        get => _serviceStatusText;
        private set => Set(ref _serviceStatusText, value);
    }

    public string ActiveProfileName
    {
        get => _activeProfileName;
        private set => Set(ref _activeProfileName, value);
    }

    public string CurrentPageTitle
    {
        get => _currentPageTitle;
        private set => Set(ref _currentPageTitle, value);
    }

    public string CurrentPageSubtitle
    {
        get => _currentPageSubtitle;
        private set => Set(ref _currentPageSubtitle, value);
    }

    public string SafetySummary
    {
        get => _safetySummary;
        private set => Set(ref _safetySummary, value);
    }

    public int DeviceCount => _snapshot?.Devices.Count ?? 0;

    public int SensorCount => _snapshot?.Sensors.Count ?? 0;

    public int VerifiedControlCount => _snapshot?.Capabilities.Count(capability => capability.State == CapabilityAccessState.Verified) ?? 0;

    public async Task InitialiseAsync()
    {
        await RefreshAsync(full: true);
        _refreshTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void SetPage(string title, string subtitle)
    {
        CurrentPageTitle = title;
        CurrentPageSubtitle = subtitle;
    }

    public async Task ApplyBuiltInAsync(string profileId)
    {
        ProfileV1? profile = Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is not null)
        {
            await ApplyProfileAsync(profile);
        }
    }

    public async Task ResetVerifiedControlsAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        foreach (CapabilityDescriptor capability in _snapshot.Capabilities.Where(
                     item => item.State == CapabilityAccessState.Verified && item.CanResetToDefault))
        {
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.ResetHardware,
                capability.Id,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            await _client.SendAsync(request, CancellationToken.None);
        }

        await RefreshAsync(full: true);
    }

    public async Task<CompatibilityReportV1> GetReportPreviewAsync()
    {
        if (_serviceOnline)
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.ExportReport),
                CancellationToken.None);
            EnsureSuccess(response);
            return IpcJson.FromElement<CompatibilityReportV1>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty report.");
        }

        HardwareSnapshot snapshot = _snapshot
            ?? throw new InvalidOperationException("No hardware data is available yet.");
        return CompatibilityReportBuilder.Build(
            snapshot,
            typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            new Dictionary<string, string>
            {
                ["framework"] = Environment.Version.ToString(),
                ["osVersion"] = Environment.OSVersion.VersionString
            },
            [],
            userApproved: false);
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
        DisposeLocalCoordinator();
    }

    private async Task RefreshAsync(bool full)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            IpcResponse statusResponse = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
                CancellationToken.None);
            EnsureSuccess(statusResponse);
            _status = IpcJson.FromElement<ServiceStatus>(statusResponse.Payload);
            ServiceStatusText = _status?.Message ?? "Service response was empty.";

            if (full || _snapshot is null)
            {
                IpcResponse snapshotResponse = await _client.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetInventory),
                    CancellationToken.None);
                EnsureSuccess(snapshotResponse);
                _snapshot = IpcJson.FromElement<HardwareSnapshot>(snapshotResponse.Payload);

                IpcResponse profilesResponse = await _client.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetProfiles),
                    CancellationToken.None);
                EnsureSuccess(profilesResponse);
                IReadOnlyList<ProfileV1> profiles = IpcJson.FromElement<IReadOnlyList<ProfileV1>>(profilesResponse.Payload) ?? [];
                Replace(Profiles, profiles);
            }
            else
            {
                IpcResponse sensorsResponse = await _client.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.SubscribeSensors),
                    CancellationToken.None);
                EnsureSuccess(sensorsResponse);
                IReadOnlyList<SensorSample> sensors = IpcJson.FromElement<IReadOnlyList<SensorSample>>(sensorsResponse.Payload) ?? [];
                _snapshot = _snapshot with { Sensors = sensors, CapturedAt = DateTimeOffset.UtcNow };
            }

            _serviceOnline = true;
            DisposeLocalCoordinator();
            UpdateDisplays();
        }
        catch (Exception exception)
        {
            _serviceOnline = false;
            ServiceStatusText = DescribeServiceFailure(exception);
            await RefreshFromLocalAdaptersAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshFromLocalAdaptersAsync()
    {
        try
        {
            _localCoordinator ??= new AdapterCoordinator(
            [
                new SystemInventoryAdapter(),
                new WindowsPowerAdapter(),
                new LibreHardwareMonitorAdapter()
            ]);
            _snapshot = await _localCoordinator.CaptureAsync(CancellationToken.None);
            _status = null;
            if (Profiles.Count == 0)
            {
                Replace(Profiles, BuiltInProfiles.Create());
            }

            UpdateDisplays();
            SafetySummary = "The service is unavailable, so this data comes from a local read-only probe. No hardware writes are possible.";
        }
        catch (Exception exception)
        {
            SafetySummary = $"The service is unavailable and the local probe failed: {exception.Message}";
        }
    }

    private static string DescribeServiceFailure(Exception exception) => exception switch
    {
        TimeoutException or FileNotFoundException => "Offline: the PC Helper service is not running. Showing local read-only data.",
        UnauthorizedAccessException => "Offline: access to the service was denied. Sign out and back in so your PC Helper Operators membership takes effect, then restart the app.",
        IOException => "Offline: the PC Helper service connection was interrupted. Showing local read-only data.",
        _ => $"Offline: {exception.Message}"
    };

    private void DisposeLocalCoordinator()
    {
        if (_localCoordinator is AdapterCoordinator coordinator)
        {
            _localCoordinator = null;
            _ = coordinator.DisposeAsync().AsTask();
        }
    }

    private async Task ApplyProfileAsync(ProfileV1 profile)
    {
        if (!_serviceOnline)
        {
            throw new InvalidOperationException(
                "The PC Helper service is not reachable, so profiles cannot be applied. The dashboard is showing local read-only data.");
        }

        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.ApplyProfile,
            new ApplyProfileRequest(profile, ConfirmExperimental: false),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, CancellationToken.None);
        EnsureSuccess(response);
        await RefreshAsync(full: true);
    }

    private void UpdateDisplays()
    {
        if (_snapshot is null)
        {
            return;
        }

        ActiveProfileName = Profiles.FirstOrDefault(profile => profile.Id == _status?.ActiveProfileId)?.Name ?? "None";
        Replace(ImportantSensors, SelectImportantSensors(_snapshot).Select(sensor => new SensorDisplay(
            sensor.Name,
            FindDevice(sensor.DeviceId),
            $"{sensor.Value:0.#} {sensor.Unit}")));

        Replace(Devices, _snapshot.Devices.Select(device => new DeviceDisplay(
            device.Name,
            $"{device.Kind} · {device.Manufacturer ?? "Unknown manufacturer"} · {device.Model ?? "No model"}")));

        Replace(CoolingCapabilities, _snapshot.Capabilities
            .Where(capability => capability.Name.Contains("fan", StringComparison.OrdinalIgnoreCase)
                || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)
                || capability.Id.Contains("control", StringComparison.OrdinalIgnoreCase))
            .Select(CapabilityDisplay.From));
        Replace(PerformanceCapabilities, _snapshot.Capabilities
            .Where(capability => !capability.Id.Contains("control", StringComparison.OrdinalIgnoreCase))
            .Select(CapabilityDisplay.From));

        List<string> diagnostics = _snapshot.Warnings.Select(warning => $"{warning.Code}: {warning.Message}").ToList();
        diagnostics.AddRange(_snapshot.Conflicts.Where(conflict => conflict.IsRunning).Select(
            conflict => $"{conflict.DisplayName} is running ({string.Join(", ", conflict.ResourceFamilies)}). {conflict.Guidance}"));
        if (diagnostics.Count == 0)
        {
            diagnostics.Add("No warnings or competing controllers were detected.");
        }

        Replace(Diagnostics, diagnostics);
        SafetySummary = _snapshot.Capabilities.Any(capability => capability.Risk is RiskLevel.Experimental or RiskLevel.Critical)
            ? "Experimental capabilities are present but remain locked until explicit confirmation and qualification."
            : "Unqualified hardware controls are locked. Monitoring and Verified Windows controls remain available.";
        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(SensorCount));
        OnPropertyChanged(nameof(VerifiedControlCount));
    }

    private string FindDevice(string id) => _snapshot?.Devices.FirstOrDefault(device => device.Id == id)?.Name ?? "Hardware";

    /// <summary>
    /// Picks a curated, deduplicated set of live readings: primary temperatures ordered by
    /// device importance, then fan speeds, then power draw. Static limits (for example NVMe
    /// "Critical Temperature") and unattributed or implausible readings are excluded.
    /// </summary>
    private static IEnumerable<SensorSample> SelectImportantSensors(HardwareSnapshot snapshot)
    {
        Dictionary<string, HardwareDevice> devices = snapshot.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .ToDictionary(device => device.Id);

        List<SensorSample> candidates = snapshot.Sensors
            .Where(sensor => sensor.Quality == SensorQuality.Good && sensor.Value is double value && double.IsFinite(value))
            .Where(sensor => devices.ContainsKey(sensor.DeviceId))
            .Where(sensor => !sensor.Name.Contains("critical", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains("max", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains("limit", StringComparison.OrdinalIgnoreCase))
            .GroupBy(sensor => (sensor.DeviceId, sensor.Name))
            .Select(group => group.First())
            .ToList();

        int DeviceRank(SensorSample sensor) => devices[sensor.DeviceId].Kind switch
        {
            DeviceKind.Cpu => 0,
            DeviceKind.Gpu => 1,
            DeviceKind.Motherboard => 2,
            DeviceKind.Memory => 3,
            DeviceKind.Storage => 4,
            DeviceKind.Cooling => 5,
            _ => 6
        };

        IEnumerable<SensorSample> temperatures = candidates
            .Where(sensor => sensor.Unit == "°C" && sensor.Value > 1 && sensor.Value < 130)
            .OrderBy(DeviceRank)
            .ThenBy(sensor => SensorNameRank(sensor.Name))
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10);
        IEnumerable<SensorSample> fans = candidates
            .Where(sensor => sensor.Unit == "RPM" && sensor.Value > 0)
            .OrderBy(DeviceRank)
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5);
        IEnumerable<SensorSample> power = candidates
            .Where(sensor => sensor.Unit == "W" && sensor.Value > 0)
            .OrderBy(DeviceRank)
            .ThenBy(sensor => SensorNameRank(sensor.Name))
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3);
        return temperatures.Concat(fans).Concat(power);
    }

    /// <summary>Prefers the reading that summarises a device over its per-part siblings.</summary>
    private static int SensorNameRank(string name)
    {
        if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("package", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)
            || name.Contains("composite", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return name.Contains("hot spot", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static void EnsureSuccess(IpcResponse response)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException($"{response.ErrorCode}: {response.Error}");
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record SensorDisplay(string Name, string Device, string DisplayValue);

public sealed record DeviceDisplay(string Name, string Details);

public sealed record CapabilityDisplay(string Name, string State, string Reason)
{
    public static CapabilityDisplay From(CapabilityDescriptor capability) => new(
        capability.Name,
        capability.State.ToString(),
        capability.Reason);
}

internal sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _executing;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_executing && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _executing = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute(parameter);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "PC Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _executing = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
