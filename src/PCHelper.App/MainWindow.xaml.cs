using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using PCHelper.Core;

namespace PCHelper.App;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x5043;
    private static readonly string[] PageTitles = ["Overview", "Profiles", "Cooling", "Performance", "Lighting", "Automation", "Devices", "Diagnostics"];
    private static readonly string[] PageSubtitles =
    [
        "Live health, ownership, and safety state",
        "Transactional stock-safe and generated policies",
        "Fan curves, calibration, and emergency behaviour",
        "Capability-gated CPU and GPU controls",
        "Optional external OpenRGB integration",
        "Deterministic profile switching rules",
        "Exact hardware identifiers and evidence",
        "Conflicts, logs, privacy, and reports"
    ];

    private FrameworkElement[] _pages = [];
    private HwndSource? _source;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _pages = [OverviewPage, ProfilesPage, CoolingPage, PerformancePage, LightingPage, AutomationPage, DevicesPage, DiagnosticsPage];
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialised;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WindowHook);
        }

        base.OnClosed(e);
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages.Length == 0)
        {
            return;
        }

        int index = Math.Max(0, Navigation.SelectedIndex);
        for (int pageIndex = 0; pageIndex < _pages.Length; pageIndex++)
        {
            _pages[pageIndex].Visibility = pageIndex == index ? Visibility.Visible : Visibility.Collapsed;
        }

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SetPage(PageTitles[index], PageSubtitles[index]);
        }
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Save redacted PC Helper report preview",
            Filter = "JSON report (*.json)|*.json",
            FileName = $"pchelper-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            object report = await viewModel.GetReportPreviewAsync();
            await System.IO.File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, JsonDefaults.Options));
            System.Windows.MessageBox.Show(this, "The redacted preview was saved locally. Nothing was uploaded.", "PC Helper", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(this, exception.Message, "PC Helper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnSourceInitialised(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowHook);
        if (_source is not null)
        {
            RegisterHotKey(_source.Handle, HotkeyId, 0x0002 | 0x0004, 0x7B); // Ctrl+Shift+F12
        }
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            ((App)System.Windows.Application.Current).ShowDashboard();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
