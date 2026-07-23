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
    // --- First-run tour and release check -------------------------------------
    // Both are user-process only: the tour is presentation plus one local flag
    // file, and the release check is a bounded anonymous HTTPS read of the
    // public GitHub releases feed. The service never touches the network.

    public const int OnboardingStepCount = 7;

    private bool _isOnboardingVisible;
    private int _onboardingStep;
    private bool _onboardingScanCompleted;
    private bool _isOnboardingWorking;
    private bool _onboardingModeApplied;
    private bool _onboardingDefaultsRestored;
    private OnboardingModeChoice? _selectedOnboardingMode;
    private OnboardingCapabilitySummary? _onboardingCapabilitySummary;
    private OnboardingBaselineSummary? _onboardingBaselineSummary;
    private string _onboardingApplySummary = "No mode has been applied.";

    public bool IsOnboardingVisible
    {
        get => _isOnboardingVisible;
        private set
        {
            _isOnboardingVisible = value;
            OnPropertyChanged();
        }
    }

    public string OnboardingStepLabel => Localization.L10n.Get("Onboarding_StepLabel")
        .Replace("{0}", (_onboardingStep + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
        .Replace("{1}", OnboardingStepCount.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);

    public string OnboardingTitle => Localization.L10n.Get($"Onboarding_Title{_onboardingStep + 1}");

    public string OnboardingBody => Localization.L10n.Get($"Onboarding_Body{_onboardingStep + 1}");

    public bool IsOnboardingFirstStep => _onboardingStep == 0;

    public bool IsOnboardingLastStep => _onboardingStep == OnboardingStepCount - 1;

    public bool IsOnboardingScanStep => _onboardingStep == 0;

    public bool IsOnboardingModeStep => _onboardingStep == 2;

    public bool IsOnboardingBaselineStep => _onboardingStep == 3;

    public bool IsOnboardingApplyStep => _onboardingStep == 5;

    public bool IsOnboardingResultStep => _onboardingStep == 6;

    public bool IsOnboardingWorking
    {
        get => _isOnboardingWorking;
        private set
        {
            if (!Set(ref _isOnboardingWorking, value))
            {
                return;
            }

            _onboardingScanCommand?.RaiseCanExecuteChanged();
            _captureOnboardingBaselineCommand?.RaiseCanExecuteChanged();
            _applyOnboardingModeCommand?.RaiseCanExecuteChanged();
            _restoreOnboardingDefaultsCommand?.RaiseCanExecuteChanged();
            _onboardingNextCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool IsOnboardingQuietSelected => _selectedOnboardingMode == OnboardingModeChoice.Quiet;

    public bool IsOnboardingEfficiencySelected => _selectedOnboardingMode == OnboardingModeChoice.Efficiency;

    public bool IsOnboardingPerformanceSelected => _selectedOnboardingMode == OnboardingModeChoice.Performance;

    public string OnboardingDetailText => _onboardingStep switch
    {
        0 => _onboardingCapabilitySummary?.ToDisplayText()
            ?? "Run the read-only scan to inventory devices, capabilities, bridge routes, and competing owners.",
        1 => _onboardingCapabilitySummary?.ToDisplayText()
            ?? "No hardware inventory is available. Go back and run the scan.",
        2 => _selectedOnboardingMode is OnboardingModeChoice mode
            ? $"{mode} selected. This is a dry selection; no hardware state has changed."
            : "Choose one mode. Blue marks the selected mode only; no change is applied on this step.",
        3 => _onboardingBaselineSummary?.ToDisplayText()
            ?? "Capture three read-only telemetry samples before applying a mode.",
        4 => BuildOnboardingEligibilitySummary(),
        5 => _onboardingModeApplied ? _onboardingApplySummary : BuildOnboardingApplyPrerequisite(),
        6 => _onboardingApplySummary,
        _ => string.Empty
    };

    public bool CanAdvanceOnboarding => !IsOnboardingWorking && _onboardingStep switch
    {
        0 => _onboardingScanCompleted,
        2 => _selectedOnboardingMode is not null,
        3 => _onboardingBaselineSummary is not null,
        5 => _onboardingModeApplied,
        _ => true
    };

    private AsyncCommand? _onboardingNextCommand;
    public ICommand OnboardingNextCommand => _onboardingNextCommand ??= new AsyncCommand(
        _ =>
        {
            if (IsOnboardingLastStep)
            {
                CompleteOnboarding();
            }
            else
            {
                _onboardingStep++;
                NotifyOnboardingStepChanged();
            }

            return Task.CompletedTask;
        },
        _ => CanAdvanceOnboarding,
        ReportError,
        _ => ShowNotice(GetOnboardingBlockedReason(), "Warning"));

    private RelayCommand? _onboardingBackCommand;
    public ICommand OnboardingBackCommand => _onboardingBackCommand ??= new RelayCommand(
        _ =>
        {
            _onboardingStep--;
            NotifyOnboardingStepChanged();
        },
        _ => !IsOnboardingFirstStep);

    private RelayCommand? _onboardingSkipCommand;
    public ICommand OnboardingSkipCommand => _onboardingSkipCommand ??= new RelayCommand(_ => CompleteOnboarding());

    private RelayCommand? _selectOnboardingModeCommand;
    public ICommand SelectOnboardingModeCommand => _selectOnboardingModeCommand ??= new RelayCommand(parameter =>
    {
        if (!Enum.TryParse(parameter?.ToString(), ignoreCase: true, out OnboardingModeChoice mode))
        {
            ShowNotice("Choose Quiet, Efficiency, or Performance.", "Warning");
            return;
        }

        _selectedOnboardingMode = mode;
        _onboardingModeApplied = false;
        _onboardingDefaultsRestored = false;
        _onboardingApplySummary = "No mode has been applied.";
        NotifyOnboardingStateChanged();
    });

    private AsyncCommand? _onboardingScanCommand;
    public ICommand OnboardingScanCommand => _onboardingScanCommand ??= new AsyncCommand(
        _ => RunOnboardingScanAsync(),
        _ => !IsOnboardingWorking,
        ReportError,
        _ => ShowNotice("The hardware scan is already running.", "Info"));

    private AsyncCommand? _captureOnboardingBaselineCommand;
    public ICommand CaptureOnboardingBaselineCommand => _captureOnboardingBaselineCommand ??= new AsyncCommand(
        _ => CaptureOnboardingBaselineAsync(),
        _ => !IsOnboardingWorking && _onboardingScanCompleted,
        ReportError,
        _ => ShowNotice(_onboardingScanCompleted
            ? "Baseline capture is already running."
            : "Run the hardware scan before capturing a baseline.", "Warning"));

    private AsyncCommand? _applyOnboardingModeCommand;
    public ICommand ApplyOnboardingModeCommand => _applyOnboardingModeCommand ??= new AsyncCommand(
        _ => ApplyOnboardingModeAsync(),
        _ => !IsOnboardingWorking && CanApplyOnboardingMode(),
        ReportError,
        _ => ShowNotice(BuildOnboardingApplyPrerequisite(), "Warning"));

    private AsyncCommand? _restoreOnboardingDefaultsCommand;
    public ICommand RestoreOnboardingDefaultsCommand => _restoreOnboardingDefaultsCommand ??= new AsyncCommand(
        _ => RestoreOnboardingDefaultsAsync(),
        _ => !IsOnboardingWorking && _onboardingModeApplied && CanUseServiceWrites && !_onboardingDefaultsRestored,
        ReportError,
        _ => ShowNotice(
            _onboardingDefaultsRestored
                ? "The Windows Balanced default was already restored and read back."
                : !_onboardingModeApplied
                    ? "Apply a mode before testing its default restoration path."
                    : GetServiceWriteBlockReason(),
            "Warning"));

    /// <summary>
    /// Shows the one-time tour when no completion flag is persisted. Only the
    /// real application startup calls this; the deterministic snapshot host
    /// never does, so renders stay unobstructed.
    /// </summary>
    public void ShowOnboardingIfFirstRun()
    {
        if (IsPortableMode || OnboardingState.IsTourCompleted())
        {
            return;
        }

        _onboardingStep = 0;
        _onboardingScanCompleted = _snapshot is not null;
        _onboardingCapabilitySummary = _snapshot is null ? null : OnboardingWorkflow.SummarizeCapabilities(_snapshot);
        NotifyOnboardingStepChanged();
        IsOnboardingVisible = true;
    }

    internal void ShowOnboardingForSnapshot(int step)
    {
        _onboardingStep = Math.Clamp(step, 0, OnboardingStepCount - 1);
        _onboardingScanCompleted = _snapshot is not null;
        _onboardingCapabilitySummary = _snapshot is null ? null : OnboardingWorkflow.SummarizeCapabilities(_snapshot);
        if (_onboardingStep >= 2)
        {
            _selectedOnboardingMode = OnboardingModeChoice.Efficiency;
        }
        NotifyOnboardingStepChanged();
        NotifyOnboardingStateChanged();
        IsOnboardingVisible = true;
    }

    private void CompleteOnboarding()
    {
        OnboardingState.MarkTourCompleted();
        IsOnboardingVisible = false;
    }

    private void NotifyOnboardingStepChanged()
    {
        OnPropertyChanged(nameof(OnboardingStepLabel));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingBody));
        OnPropertyChanged(nameof(IsOnboardingFirstStep));
        OnPropertyChanged(nameof(IsOnboardingLastStep));
        OnPropertyChanged(nameof(IsOnboardingScanStep));
        OnPropertyChanged(nameof(IsOnboardingModeStep));
        OnPropertyChanged(nameof(IsOnboardingBaselineStep));
        OnPropertyChanged(nameof(IsOnboardingApplyStep));
        OnPropertyChanged(nameof(IsOnboardingResultStep));
        OnPropertyChanged(nameof(OnboardingDetailText));
        OnPropertyChanged(nameof(CanAdvanceOnboarding));
        _onboardingBackCommand?.RaiseCanExecuteChanged();
        _onboardingNextCommand?.RaiseCanExecuteChanged();
    }

    private void NotifyOnboardingStateChanged()
    {
        OnPropertyChanged(nameof(IsOnboardingQuietSelected));
        OnPropertyChanged(nameof(IsOnboardingEfficiencySelected));
        OnPropertyChanged(nameof(IsOnboardingPerformanceSelected));
        OnPropertyChanged(nameof(OnboardingDetailText));
        OnPropertyChanged(nameof(CanAdvanceOnboarding));
        _onboardingNextCommand?.RaiseCanExecuteChanged();
        _captureOnboardingBaselineCommand?.RaiseCanExecuteChanged();
        _applyOnboardingModeCommand?.RaiseCanExecuteChanged();
        _restoreOnboardingDefaultsCommand?.RaiseCanExecuteChanged();
    }

    private async Task RunOnboardingScanAsync()
    {
        IsOnboardingWorking = true;
        try
        {
            await RefreshAsync(full: true, userInitiated: false);
            if (_snapshot is null)
            {
                _onboardingScanCompleted = false;
                ShowNotice("RigPilot could not obtain a hardware inventory. Monitoring remains available when a local probe succeeds.", "Warning");
                return;
            }

            _onboardingCapabilitySummary = OnboardingWorkflow.SummarizeCapabilities(_snapshot);
            _onboardingScanCompleted = true;
            ShowNotice("Read-only hardware and ownership scan completed.", "Success");
        }
        finally
        {
            IsOnboardingWorking = false;
            NotifyOnboardingStateChanged();
        }
    }

    private async Task CaptureOnboardingBaselineAsync()
    {
        IsOnboardingWorking = true;
        try
        {
            List<HardwareSnapshot> samples = [];
            for (int index = 0; index < 3; index++)
            {
                if (index > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), _lifetime.Token);
                }

                await RefreshAsync(full: false, userInitiated: false);
                if (_snapshot is HardwareSnapshot snapshot
                    && samples.All(existing => existing.CapturedAt != snapshot.CapturedAt))
                {
                    samples.Add(snapshot);
                }
            }

            if (samples.Count == 0)
            {
                throw new InvalidOperationException("No telemetry snapshot was available for the baseline.");
            }

            _onboardingBaselineSummary = OnboardingWorkflow.SummarizeBaseline(samples);
            ShowNotice($"Baseline captured from {samples.Count} telemetry sample{(samples.Count == 1 ? string.Empty : "s")}.", "Success");
        }
        finally
        {
            IsOnboardingWorking = false;
            NotifyOnboardingStateChanged();
        }
    }

    private bool CanApplyOnboardingMode()
    {
        if (!CanUseServiceWrites || _selectedOnboardingMode is not OnboardingModeChoice mode || _onboardingBaselineSummary is null)
        {
            return false;
        }

        return _suiteProfilesById.TryGetValue(OnboardingWorkflow.ProfileId(mode), out ProfileV2? profile)
            && profile.HardwareActions.Count > 0;
    }

    private async Task ApplyOnboardingModeAsync()
    {
        OnboardingModeChoice mode = _selectedOnboardingMode
            ?? throw new InvalidOperationException("Choose a mode before applying it.");
        if (!_suiteProfilesById.TryGetValue(OnboardingWorkflow.ProfileId(mode), out ProfileV2? profile)
            || profile.HardwareActions.Count == 0)
        {
            throw new InvalidOperationException("This system has no eligible action for the selected mode. No state was changed.");
        }

        IsOnboardingWorking = true;
        try
        {
            ApplyProfileResult result = await ApplyProfileV2Async(
                profile,
                manualSelection: true,
                applyLinkedLighting: false,
                applyLinkedOsd: false);
            bool verified = result.Transaction.State == ProfileTransactionState.Committed
                && result.Transaction.Verifications.Count == result.Transaction.PreparedActions.Count
                && result.Transaction.Verifications.All(verification => verification.Success);
            if (!verified)
            {
                throw new InvalidOperationException("The mode did not return a complete read-back proof. RigPilot did not accept it as applied.");
            }

            _onboardingModeApplied = true;
            _onboardingDefaultsRestored = false;
            _onboardingApplySummary = $"{profile.Name} committed in transaction {result.Transaction.Id[..Math.Min(8, result.Transaction.Id.Length)]}. "
                + $"{result.Transaction.Verifications.Count} requested action{(result.Transaction.Verifications.Count == 1 ? " was" : "s were")} read back successfully. "
                + "The current built-in bundle changes Windows power policy only; unqualified cooling, tuning, and lighting actions were omitted. "
                + "Use Restore Windows default below to reset Balanced and verify it independently.";
        }
        finally
        {
            IsOnboardingWorking = false;
            NotifyOnboardingStateChanged();
        }
    }

    private async Task RestoreOnboardingDefaultsAsync()
    {
        EnsureServiceWritesAvailable();
        CapabilityDescriptor capability = _snapshot?.Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, "windows.power.active-scheme", StringComparison.Ordinal)
            && item.State == CapabilityAccessState.Verified
            && item.CanResetToDefault)
            ?? throw new InvalidOperationException("The Verified Windows power default-reset path is unavailable.");

        IsOnboardingWorking = true;
        try
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.ResetHardware,
                    capability.Id,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(response);
            await RefreshAsync(full: true, userInitiated: false);
            _onboardingDefaultsRestored = true;
            _onboardingApplySummary += " Windows Balanced was then restored and its default state was read back successfully.";
            ShowNotice("Windows Balanced was restored and read back.", "Success");
        }
        finally
        {
            IsOnboardingWorking = false;
            NotifyOnboardingStateChanged();
        }
    }

    private string BuildOnboardingEligibilitySummary()
    {
        string mode = _selectedOnboardingMode?.ToString() ?? "No mode";
        string power = _selectedOnboardingMode is OnboardingModeChoice choice
            && _suiteProfilesById.TryGetValue(OnboardingWorkflow.ProfileId(choice), out ProfileV2? profile)
            && profile.HardwareActions.Count > 0
                ? $"{profile.HardwareActions.Count} Verified Windows power action ready"
                : "no eligible Windows power action";
        string cooling = CalibrationTargets.Any(target => target.IsAvailable)
            ? "fan commissioning is available but remains opt-in"
            : "fan commissioning omitted because no eligible output is available";
        string tuning = TuneTargets.Any(target => target.IsAvailable)
            ? "tuning is eligible but remains an Advanced Lab action"
            : "tuning omitted because no qualified target is available";
        string rgb = RgbReadyRouteCount > 0
            ? $"{RgbReadyRouteCount} RGB route{(RgbReadyRouteCount == 1 ? string.Empty : "s")} ready but not part of this service transaction"
            : "RGB omitted because no ready route is present";
        return $"Dry run for {mode}: {power}; {cooling}; {tuning}; {rgb}. Optional omissions do not widen write authority.";
    }

    private string BuildOnboardingApplyPrerequisite()
    {
        if (_selectedOnboardingMode is null)
        {
            return "Choose Quiet, Efficiency, or Performance before applying a mode.";
        }

        if (_onboardingBaselineSummary is null)
        {
            return "Capture the read-only baseline before applying the selected mode.";
        }

        if (!CanUseServiceWrites)
        {
            return GetServiceWriteBlockReason();
        }

        if (!_suiteProfilesById.TryGetValue(OnboardingWorkflow.ProfileId(_selectedOnboardingMode.Value), out ProfileV2? profile)
            || profile.HardwareActions.Count == 0)
        {
            return "The selected mode has no eligible hardware action on this system. No state can be changed.";
        }

        return $"Ready to apply {profile.Name} as one service-owned, read-back-verified transaction.";
    }

    private string GetOnboardingBlockedReason() => _onboardingStep switch
    {
        0 => "Complete the read-only hardware and ownership scan before continuing.",
        2 => "Choose Quiet, Efficiency, or Performance before continuing.",
        3 => "Capture the read-only telemetry baseline before continuing.",
        5 => BuildOnboardingApplyPrerequisite(),
        _ => "The current onboarding action is still running."
    };

    private string _updateCheckStatus = Localization.L10n.Get("Updates_NotCheckedYet");
    private bool _updateAvailable;
    private string _latestReleaseUrl = GitHubUpdateCheck.ReleasesPageUri;

    public string UpdateCheckStatus
    {
        get => _updateCheckStatus;
        private set
        {
            _updateCheckStatus = value;
            OnPropertyChanged();
        }
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            _updateAvailable = value;
            OnPropertyChanged();
        }
    }

    public string LatestReleaseUrl
    {
        get => _latestReleaseUrl;
        private set
        {
            _latestReleaseUrl = value;
            OnPropertyChanged();
        }
    }

    private AsyncCommand? _checkForUpdatesCommand;
    public ICommand CheckForUpdatesCommand => _checkForUpdatesCommand ??= new AsyncCommand(
        _ => CheckForUpdatesCoreAsync(noticeOnUpdate: false),
        onError: ReportError);

    private RelayCommand? _openReleasePageCommand;
    public ICommand OpenReleasePageCommand => _openReleasePageCommand ??= new RelayCommand(_ => OpenReleasePage());

    /// <summary>
    /// One release check. Non-throwing by design so the startup caller can
    /// fire-and-forget it; every failure becomes an explanatory status line.
    /// </summary>
    public async Task CheckForUpdatesCoreAsync(bool noticeOnUpdate)
    {
        UpdateCheckStatus = Localization.L10n.Get("Updates_Checking");
        try
        {
            UpdateCheckResult result = await new GitHubUpdateCheck().CheckAsync(AppVersion, CancellationToken.None);
            UpdateAvailable = result.Succeeded && result.UpdateAvailable;
            LatestReleaseUrl = result.ReleaseUrl;
            UpdateCheckStatus = result.Message;
            if (UpdateAvailable && noticeOnUpdate)
            {
                ShowNotice(result.Message, "Info");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            UpdateCheckStatus = $"The update check failed unexpectedly: {exception.GetType().Name}.";
        }
    }

    private void OpenReleasePage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LatestReleaseUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowNotice($"The release page could not be opened: {exception.Message}", "Warning");
        }
    }
}
