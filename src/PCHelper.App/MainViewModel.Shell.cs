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

    public const int OnboardingStepCount = 3;

    private bool _isOnboardingVisible;
    private int _onboardingStep;

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

    private RelayCommand? _onboardingNextCommand;
    public ICommand OnboardingNextCommand => _onboardingNextCommand ??= new RelayCommand(_ =>
    {
        if (IsOnboardingLastStep)
        {
            CompleteOnboarding();
            return;
        }

        _onboardingStep++;
        NotifyOnboardingStepChanged();
    });

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
        NotifyOnboardingStepChanged();
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
        _onboardingBackCommand?.RaiseCanExecuteChanged();
    }

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
