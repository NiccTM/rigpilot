using System.Windows;
using Forms = System.Windows.Forms;

namespace PCHelper.App;

public partial class App : System.Windows.Application, IDisposable
{
    private const string InstanceMutexName = @"Local\PCHelper.App";
    private const string ActivationEventName = @"Local\PCHelper.App.Activate";
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _exiting;

    public bool IsExiting => _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

        _viewModel = new MainViewModel();
        _window = new MainWindow(_viewModel);
        MainWindow = _window;
        CreateTrayIcon();
        _window.Show();
        await _viewModel.InitialiseAsync();
        if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            _window.Hide();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
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

        GC.SuppressFinalize(this);
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

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "PC Helper",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quiet", null, async (_, _) => await _viewModel!.ApplyBuiltInAsync("quiet"));
        menu.Items.Add("Balanced", null, async (_, _) => await _viewModel!.ApplyBuiltInAsync("balanced"));
        menu.Items.Add("Performance", null, async (_, _) => await _viewModel!.ApplyBuiltInAsync("performance"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Reset verified controls", null, async (_, _) => await _viewModel!.ResetVerifiedControlsAsync());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _exiting = true;
            Shutdown();
        });
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
