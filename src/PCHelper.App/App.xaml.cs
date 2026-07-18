using System.Windows;
using PCHelper.Contracts;
using PCHelper.Ipc;
using Forms = System.Windows.Forms;

namespace PCHelper.App;

public partial class App : System.Windows.Application, IDisposable, IAsyncDisposable
{
    private const string InstanceMutexName = @"Local\PCHelper.App";
    private const string ActivationEventName = @"Local\PCHelper.App.Activate";
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly object _disposeSync = new();
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private UserAgentRuntime? _userAgent;
    private CancellationTokenSource? _userAgentCancellation;
    private Task? _userAgentTask;
    private Task? _disposeTask;
    private int _shutdownRequested;
    private bool _exiting;

    public bool IsExiting => _exiting;

    /// <summary>
    /// Allows the repository's deterministic UI snapshot host to run the WPF
    /// lifecycle without creating the production tray agent or single-instance
    /// mutex. Normal application startup never sets this property.
    /// </summary>
    public bool SuppressProductStartup { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (InteractiveFanPreflightHost.IsInvocation(e.Args))
        {
            // This one-shot mode is launched only by the user-agent after an
            // explicit UAC request. It must bypass the tray mutex and never
            // construct a dashboard, service client, or user-agent server.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = await InteractiveFanPreflightHost.RunAsync(e.Args, CancellationToken.None);
            Shutdown(exitCode);
            return;
        }
        if (SuppressProductStartup)
        {
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowDashboard),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        // Optional explicit UI language, e.g. "--culture de". Without it the OS
        // language applies; missing translations fall back to English per key.
        int cultureIndex = Array.FindIndex(e.Args, argument => argument.Equals("--culture", StringComparison.OrdinalIgnoreCase));
        if (cultureIndex >= 0 && cultureIndex + 1 < e.Args.Length)
        {
            Localization.L10n.ApplyCulture(e.Args[cultureIndex + 1]);
        }

        bool portable = e.Args.Contains("--portable", StringComparer.OrdinalIgnoreCase);
        _viewModel = new MainViewModel { IsPortableMode = portable };
        _window = new MainWindow(_viewModel);
        MainWindow = _window;
        CreateTrayIcon();
        _window.Show();
        if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            _window.Hide();
        }

        if (portable)
        {
            // Portable mode hosts no pipe server and starts no user-agent
            // features: nothing listens, nothing writes hardware, and the
            // process leaves no machine configuration behind.
            await _viewModel.InitialiseAsync();
            return;
        }

        try
        {
            IReadOnlyList<System.Security.Principal.SecurityIdentifier> gameBarPackageSids =
                GameBarPackageTrust.ResolveInstalledPackageSids();
            _userAgent = new UserAgentRuntime(gameBarPackageSids: gameBarPackageSids);
            _userAgentCancellation = new CancellationTokenSource();
            await _userAgent.InitializeAsync(_userAgentCancellation.Token);
            NamedPipeRequestServer userAgentServer = new(
                ProtocolConstants.UserAgentPipeName,
                _userAgent.HandleRequestAsync,
                gameBarPackageSids);
            _userAgentTask = userAgentServer.RunAsync(_userAgentCancellation.Token);
        }
        catch (Exception exception)
        {
            _viewModel.ShowNotice($"User-agent features are unavailable: {exception.Message}", "Warning");
        }

        await _viewModel.InitialiseAsync();
        _viewModel.ShowOnboardingIfFirstRun();

        // Network access is user-initiated. The Diagnostics page exposes an
        // explicit bounded update check; startup remains fully offline.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (SuppressProductStartup)
        {
            base.OnExit(e);
            return;
        }

        _exiting = true;
        bool requiresFallback;
        lock (_disposeSync)
        {
            requiresFallback = _disposeTask is null;
        }
        if (requiresFallback)
        {
            // Normal tray exit awaits cleanup before calling Shutdown. This is
            // the bounded fallback for OS-initiated or otherwise direct exits.
            Dispose();
        }
        base.OnExit(e);
    }

    public void Dispose()
    {
        try
        {
            EnsureDisposeAsync()
                .WaitAsync(ShutdownTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            System.Diagnostics.Debug.WriteLine("RigPilot shutdown cleanup exceeded 5 seconds; process exit will release remaining resources.");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"RigPilot shutdown cleanup failed: {exception}");
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await EnsureDisposeAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task RequestShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        _exiting = true;
        try
        {
            await DisposeAsync();
        }
        catch (TimeoutException)
        {
            System.Diagnostics.Debug.WriteLine("RigPilot shutdown cleanup exceeded 5 seconds; continuing with application exit.");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"RigPilot shutdown cleanup failed: {exception}");
        }
        finally
        {
            Shutdown();
        }
    }

    private Task EnsureDisposeAsync()
    {
        lock (_disposeSync)
        {
            return _disposeTask ??= DisposeAsyncCore();
        }
    }

    private async Task DisposeAsyncCore()
    {
        CancellationTokenSource? userAgentCancellation = _userAgentCancellation;
        Task? userAgentTask = _userAgentTask;
        UserAgentRuntime? userAgent = _userAgent;
        _userAgentCancellation = null;
        _userAgentTask = null;
        _userAgent = null;

        userAgentCancellation?.Cancel();
        DisposeUiResources();

        if (userAgentTask is not null)
        {
            try
            {
                await userAgentTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (userAgentCancellation?.IsCancellationRequested == true)
            {
                // Expected after stopping the user-agent pipe.
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"RigPilot user-agent pipe shutdown failed: {exception}");
            }
        }

        if (userAgent is not null)
        {
            try
            {
                await userAgent.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"RigPilot user-agent resource cleanup failed: {exception}");
            }
        }

        userAgentCancellation?.Dispose();
    }

    private void DisposeUiResources()
    {
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        _viewModel?.Dispose();
        _viewModel = null;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    public void ShowDashboard()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void ShowTrayNotification(string title, string message)
    {
        if (_trayIcon is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        try
        {
            _trayIcon.ShowBalloonTip(
                timeout: 5000,
                tipTitle: title.Length > 63 ? title[..63] : title,
                tipText: message.Length > 255 ? message[..255] : message,
                tipIcon: Forms.ToolTipIcon.Warning);
        }
        catch (InvalidOperationException)
        {
            // Explorer can recreate the notification area during a session;
            // health logging remains durable even when a transient balloon is
            // unavailable.
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = ProductBrand.Name,
            Icon = BrandIconFactory.CreateTrayIcon(),
            Visible = true
        };
        Forms.ContextMenuStrip menu = new()
        {
            BackColor = System.Drawing.Color.FromArgb(20, 27, 37),
            ForeColor = System.Drawing.Color.FromArgb(243, 246, 250),
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColorTable()),
            ShowImageMargin = false,
            ShowCheckMargin = true
        };
        Forms.ToolStripMenuItem openItem = new("Open dashboard", null, (_, _) => ShowDashboard())
        {
            Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold)
        };
        Forms.ToolStripMenuItem statusItem = new("Connecting to service\u2026") { Enabled = false };
        Forms.ToolStripMenuItem telemetryItem = new("No telemetry yet") { Enabled = false };
        Forms.ToolStripMenuItem profileMenu = new("Profiles");
        Forms.ToolStripMenuItem quietItem = new("Quiet", null, async (_, _) => await _viewModel!.ApplyBuiltInAsync("quiet"));
        Forms.ToolStripMenuItem balancedItem = new("Balanced", null, async (_, _) => await _viewModel!.ApplyBuiltInAsync("balanced"));
        Forms.ToolStripMenuItem performanceItem = new("Performance", null, async (_, _) => await _viewModel!.ApplyBuiltInAsync("performance"));
        profileMenu.DropDownItems.AddRange([quietItem, balancedItem, performanceItem]);
        profileMenu.DropDown.BackColor = menu.BackColor;
        profileMenu.DropDown.ForeColor = menu.ForeColor;

        // G-Helper-style quick access: the master switch and efficiency power
        // presets are one click from the tray. Both run the exact same
        // acknowledged, transactional paths as the dashboard controls.
        Forms.ToolStripMenuItem hardwareControlItem = new("Hardware control", null, (_, _) =>
        {
            if (_viewModel is not null)
            {
                bool requested = !_viewModel.HardwareControlEnabled;
                if (_viewModel.ToggleHardwareControlCommand.CanExecute(requested))
                {
                    _viewModel.ToggleHardwareControlCommand.Execute(requested);
                }
            }
        });
        Forms.ToolStripMenuItem undervoltMenu = new("GPU efficiency power target");
        Forms.ToolStripMenuItem undervoltQuiet = new("Quiet (\u221225% power)", null, async (_, _) => await _viewModel!.ApplyUndervoltPresetAsync(UndervoltPresets.Quiet));
        Forms.ToolStripMenuItem undervoltEfficient = new("Efficient (\u221215% power)", null, async (_, _) => await _viewModel!.ApplyUndervoltPresetAsync(UndervoltPresets.Efficient));
        Forms.ToolStripMenuItem undervoltStock = new("Back to stock", null, async (_, _) => await _viewModel!.ApplyUndervoltPresetAsync(UndervoltPresets.Stock));
        undervoltMenu.DropDownItems.AddRange([undervoltQuiet, undervoltEfficient, undervoltStock]);
        undervoltMenu.DropDown.BackColor = menu.BackColor;
        undervoltMenu.DropDown.ForeColor = menu.ForeColor;

        menu.Items.Add(openItem);
        menu.Items.Add(statusItem);
        menu.Items.Add(telemetryItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(hardwareControlItem);
        menu.Items.Add(profileMenu);
        menu.Items.Add(undervoltMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Forms.ToolStripMenuItem resetItem = new("Reset verified controls", null, async (_, _) => await _viewModel!.ResetVerifiedControlsAsync());
        menu.Items.Add(resetItem);
        menu.Items.Add("Exit", null, async (_, _) => await RequestShutdownAsync());
        menu.Opening += (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            statusItem.Text = _viewModel.IsServiceOnline
                ? $"Service connected \u00B7 {_viewModel.ActiveProfileName}"
                : "Local read-only mode";
            string[] readings = _viewModel.ImportantSensors
                .Take(3)
                .Select(sensor => $"{sensor.Name} {sensor.DisplayValue}")
                .ToArray();
            telemetryItem.Text = readings.Length > 0 ? string.Join("  \u00B7  ", readings) : "No telemetry yet";
            hardwareControlItem.Checked = _viewModel.HardwareControlEnabled;
            hardwareControlItem.Enabled = _viewModel.IsServiceOnline;
            undervoltMenu.Enabled = _viewModel.IsServiceOnline && _viewModel.HardwareControlEnabled;
            profileMenu.Enabled = _viewModel.IsServiceOnline;
            resetItem.Enabled = _viewModel.IsServiceOnline && _viewModel.ResettableVerifiedControlCount > 0;
            quietItem.Checked = _viewModel.ActiveProfileName.Equals("Quiet", StringComparison.OrdinalIgnoreCase);
            balancedItem.Checked = _viewModel.ActiveProfileName.Equals("Balanced", StringComparison.OrdinalIgnoreCase);
            performanceItem.Checked = _viewModel.ActiveProfileName.Equals("Performance", StringComparison.OrdinalIgnoreCase);
        };
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowDashboard();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting. Exiting avoids duplicate tray agents.
        }
    }
}

internal sealed class DarkMenuColorTable : Forms.ProfessionalColorTable
{
    private static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(20, 27, 37);
    private static readonly System.Drawing.Color Raised = System.Drawing.Color.FromArgb(32, 44, 60);
    private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(38, 50, 68);

    public override System.Drawing.Color ToolStripDropDownBackground => Surface;

    public override System.Drawing.Color ImageMarginGradientBegin => Surface;

    public override System.Drawing.Color ImageMarginGradientMiddle => Surface;

    public override System.Drawing.Color ImageMarginGradientEnd => Surface;

    public override System.Drawing.Color MenuBorder => Border;

    public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(105, 173, 255);

    public override System.Drawing.Color MenuItemSelected => Raised;

    public override System.Drawing.Color MenuItemSelectedGradientBegin => Raised;

    public override System.Drawing.Color MenuItemSelectedGradientEnd => Raised;

    public override System.Drawing.Color MenuItemPressedGradientBegin => Raised;

    public override System.Drawing.Color MenuItemPressedGradientMiddle => Raised;

    public override System.Drawing.Color MenuItemPressedGradientEnd => Raised;

    public override System.Drawing.Color SeparatorDark => Border;

    public override System.Drawing.Color SeparatorLight => Border;
}
