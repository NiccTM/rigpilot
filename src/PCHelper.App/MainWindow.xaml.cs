using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.App;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x5043;
    private const int AutomationHotkey1Id = 0x5044;
    private const int AutomationHotkey2Id = 0x5045;
    private const int AutomationHotkey3Id = 0x5046;
    private const int DesktopOsdHotkeyId = 0x5048;
    private static readonly string[] PageTitles = ["Overview", "Profiles", "Cooling", "Performance", "Lighting", "Automation", "Games & tools", "Devices", "Diagnostics"];
    private static readonly string[] PageSubtitles =
    [
        "Live health, ownership, and safety state",
        "Transactional stock-safe and generated policies",
        "Fan curves, calibration, and emergency behaviour",
        "Capability-gated CPU and GPU controls",
        "Optional external OpenRGB integration",
        "Deterministic profile switching rules",
        "Local games, effects, macros, OSD, capture, and trusted scripts",
        "Exact hardware identifiers and evidence",
        "Conflicts, logs, privacy, and reports"
    ];

    private FrameworkElement[] _pages = [];
    private HwndSource? _source;
    private string? _registeredDesktopOsdHotkey;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.OsdHotkeyChanged += OnOsdHotkeyChanged;
        _pages = [OverviewPage, ProfilesPage, CoolingPage, PerformancePage, LightingPage, AutomationPage, EcosystemPage, DevicesPage, DiagnosticsPage];
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialised;
        StyleComparisonPlot();
        viewModel.MonitoringComparisonSeries.CollectionChanged += (_, _) => RefreshComparisonPlot(viewModel);
        RefreshComparisonPlot(viewModel);
        NavigateTo(0);
    }

    private void StyleComparisonPlot()
    {
        ScottPlot.Plot plot = ComparisonPlot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#171A1F");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#171A1F");
        plot.Axes.Color(ScottPlot.Color.FromHex("#8A93A0"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#262B33");
        plot.Axes.Left.IsVisible = false;
        plot.Axes.Bottom.TickLabelStyle.IsVisible = false;
    }

    private void RefreshComparisonPlot(MainViewModel viewModel)
    {
        ScottPlot.Plot plot = ComparisonPlot.Plot;
        plot.Clear();
        foreach (SensorComparisonSeriesDisplay series in viewModel.MonitoringComparisonSeries)
        {
            if (series.Values.Count < 2)
            {
                continue;
            }

            // Normalise each series to [0,1] so mixed units overlay comparably,
            // matching the dashboard's existing normalized-overlay semantics.
            double minimum = series.Values.Min();
            double maximum = series.Values.Max();
            double span = maximum - minimum;
            double[] normalised = series.Values
                .Select(value => span > 0 ? (value - minimum) / span : 0.5)
                .ToArray();
            ScottPlot.Plottables.Signal signal = plot.Add.Signal(normalised);
            signal.LegendText = series.DisplayName;
            signal.LineWidth = 2;
            if (series.Stroke is System.Windows.Media.SolidColorBrush brush)
            {
                signal.Color = new ScottPlot.Color(brush.Color.R, brush.Color.G, brush.Color.B);
            }
        }

        plot.Axes.AutoScale();
        ComparisonPlot.Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            UnregisterHotKey(_source.Handle, AutomationHotkey1Id);
            UnregisterHotKey(_source.Handle, AutomationHotkey2Id);
            UnregisterHotKey(_source.Handle, AutomationHotkey3Id);
            UnregisterHotKey(_source.Handle, DesktopOsdHotkeyId);
            _ = WTSUnRegisterSessionNotification(_source.Handle);
            _source.RemoveHook(WindowHook);
        }

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OsdHotkeyChanged -= OnOsdHotkeyChanged;
        }

        base.OnClosed(e);
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages.Length == 0)
        {
            return;
        }

        int index = Math.Clamp(Navigation.SelectedIndex, 0, _pages.Length - 1);
        NavigateTo(index);
    }

    private void NavigateTo(int index)
    {
        for (int pageIndex = 0; pageIndex < _pages.Length; pageIndex++)
        {
            _pages[pageIndex].Visibility = pageIndex == index ? Visibility.Visible : Visibility.Collapsed;
        }

        Title = $"{ProductBrand.Name} \u2014 {PageTitles[index]}";
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SetPage(PageTitles[index], PageSubtitles[index]);
        }
    }

    private void OpenExperimentalCooling_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is FrameworkElement { DataContext: ExperimentalControlDisplay control })
        {
            viewModel.SelectExperimentalCoolingControl(control);
        }

        Navigation.SelectedIndex = 2;
        Navigation.ScrollIntoView(Navigation.Items[2]);
        _ = Dispatcher.BeginInvoke(new Action(CoolingCommissioningWizard.BringIntoView));
        e.Handled = true;
    }

    private void OpenExperimentalDevices_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedIndex = 7;
        Navigation.ScrollIntoView(Navigation.Items[7]);
        _ = Dispatcher.BeginInvoke(new Action(AdvancedDeviceDecisionMatrix.BringIntoView));
        e.Handled = true;
    }

    private async void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        string? json = viewModel.BuildSelectedProfileExport();
        if (json is null)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = $"Export {ProductBrand.Name} profile",
            Filter = "RigPilot profile (*.rigpilot-profile.json)|*.rigpilot-profile.json",
            FileName = $"{viewModel.SelectedProfileForExport?.Name ?? "profile"}{ProfileShareFile.FileExtension}",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, json);
            viewModel.ShowNotice("Profile exported. The file carries typed actions and safety limits only — no scripts, no machine identity.", "Success");
        }
        catch (Exception exception)
        {
            viewModel.ShowNotice(exception.Message, "Error");
        }
    }

    private async void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = $"Import a shared {ProductBrand.Name} profile",
            Filter = "RigPilot profile (*.rigpilot-profile.json;*.json)|*.rigpilot-profile.json;*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string json = await System.IO.File.ReadAllTextAsync(dialog.FileName);
            await viewModel.ImportSharedProfileAsync(json);
        }
        catch (Exception exception)
        {
            viewModel.ShowNotice(exception.Message, "Error");
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
            Title = $"Save redacted {ProductBrand.Name} report preview",
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
            viewModel.ShowNotice("The redacted preview was saved locally. Nothing was uploaded.", "Success");
        }
        catch (Exception exception)
        {
            viewModel.ShowNotice(exception.Message, "Error");
        }
    }

    private async void SaveEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = $"Save local {ProductBrand.Name} hardware evidence",
            Filter = "JSON evidence (*.json)|*.json",
            FileName = $"rigpilot-evidence-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            HardwareEvidenceReportV1 evidence = await viewModel.GetHardwareEvidenceAsync();
            await System.IO.File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(evidence, JsonDefaults.Options));
            viewModel.ShowNotice("Saved bounded local hardware evidence. Nothing was uploaded.", "Success");
        }
        catch (Exception exception)
        {
            viewModel.ShowNotice(exception.Message, "Error");
        }
    }

    private async void SaveMonitoringExport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = $"Save {ProductBrand.Name} monitoring history",
            Filter = "CSV data (*.csv)|*.csv",
            FileName = $"rigpilot-monitoring-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, viewModel.BuildMonitoringCsv());
            viewModel.ShowNotice("Saved the local dashboard trend buffer as CSV.", "Success");
        }
        catch (Exception exception)
        {
            viewModel.ShowNotice(exception.Message, "Error");
        }
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(viewModel.BuildDiagnosticSummary());
            viewModel.ShowNotice("Diagnostic summary copied to the clipboard.", "Success");
        }
        catch (Exception exception)
        {
            viewModel.ShowNotice($"The summary could not be copied: {exception.Message}", "Error");
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        string dataDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PCHelper");
        if (!System.IO.Directory.Exists(dataDirectory))
        {
            viewModel.ShowNotice($"The {ProductBrand.Name} data folder does not exist yet.", "Info");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataDirectory}\"")
            {
                UseShellExecute = true
            });
            viewModel.ShowNotice($"Opened the local {ProductBrand.Name} data folder.", "Success");
        }
        catch (Exception exception)
        {
            viewModel.ShowNotice($"The data folder could not be opened: {exception.Message}", "Error");
        }
    }

    private void PickOpenRgbColour_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // Native colour mixer, opened with the custom-colour panel already expanded so
        // the user can dial in any RGB value, seeded with the current #RRGGBB.
        using System.Windows.Forms.ColorDialog dialog = new()
        {
            FullOpen = true,
            AnyColor = true,
        };
        if (TryParseHexColour(viewModel.OpenRgbColour, out byte red, out byte green, out byte blue))
        {
            dialog.Color = System.Drawing.Color.FromArgb(red, green, blue);
        }

        System.Windows.Forms.IWin32Window owner = new Win32WindowHandle(new WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
        {
            System.Drawing.Color chosen = dialog.Color;
            viewModel.OpenRgbColour = $"#{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}";
        }
    }

    private static bool TryParseHexColour(string? value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> hex = value.Trim().TrimStart('#');
        const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        return hex.Length == 6
            && byte.TryParse(hex[..2], Hex, invariant, out red)
            && byte.TryParse(hex[2..4], Hex, invariant, out green)
            && byte.TryParse(hex[4..6], Hex, invariant, out blue);
    }

    /// <summary>Minimal <see cref="System.Windows.Forms.IWin32Window"/> wrapper so a WinForms
    /// dialog can be parented to this WPF window's HWND.</summary>
    private sealed class Win32WindowHandle(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        int index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            _ => -1
        };
        if (index >= 0)
        {
            Navigation.SelectedIndex = index;
            Navigation.ScrollIntoView(Navigation.Items[index]);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F)
        {
            Navigation.SelectedIndex = 7;
            DeviceSearchBox.Focus();
            DeviceSearchBox.SelectAll();
            e.Handled = true;
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
            bool isSnapshotHost = System.Windows.Application.Current is App { SuppressProductStartup: true };
            if (!isSnapshotHost)
            {
                bool hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, 0x0002 | 0x0004, 0x7B); // Ctrl+Shift+F12
                bool automationHotkeysRegistered = RegisterHotKey(_source.Handle, AutomationHotkey1Id, 0x0002 | 0x0001, 0x31)
                    && RegisterHotKey(_source.Handle, AutomationHotkey2Id, 0x0002 | 0x0001, 0x32)
                    && RegisterHotKey(_source.Handle, AutomationHotkey3Id, 0x0002 | 0x0001, 0x33);
                if (!hotkeyRegistered && DataContext is MainViewModel viewModel)
                {
                    viewModel.ShowNotice("The Ctrl+Shift+F12 global shortcut is already used by another application.", "Warning");
                }

                if (!automationHotkeysRegistered && DataContext is MainViewModel automationViewModel)
                {
                    automationViewModel.ShowNotice("One or more Ctrl+Alt+1/2/3 automation hotkeys are already in use.", "Warning");
                }

                if (DataContext is MainViewModel osdViewModel)
                {
                    ConfigureDesktopOsdHotkey(osdViewModel.OsdHotkeyText);
                }

                _ = WTSRegisterSessionNotification(_source.Handle, 0);
            }

            int darkMode = 1;
            _ = DwmSetWindowAttribute(_source.Handle, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int));
        }
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            ((App)System.Windows.Application.Current).ShowDashboard();
            handled = true;
        }
        else if (message == 0x0312 && wParam.ToInt32() == DesktopOsdHotkeyId && DataContext is MainViewModel osdViewModel)
        {
            _ = osdViewModel.ToggleDesktopOsdFromHotkeyAsync();
            handled = true;
        }
        else if (message == 0x0312 && DataContext is MainViewModel viewModel)
        {
            string? hotkey = wParam.ToInt32() switch
            {
                AutomationHotkey1Id => "Ctrl+Alt+1",
                AutomationHotkey2Id => "Ctrl+Alt+2",
                AutomationHotkey3Id => "Ctrl+Alt+3",
                _ => null
            };
            if (hotkey is not null)
            {
                viewModel.NotifyAutomationHotkey(hotkey);
                handled = true;
            }
        }
        else if (message == 0x02B1 && DataContext is MainViewModel sessionViewModel)
        {
            if (wParam.ToInt32() == 0x7)
            {
                sessionViewModel.SetSessionLocked(true);
            }
            else if (wParam.ToInt32() == 0x8)
            {
                sessionViewModel.SetSessionLocked(false);
            }
        }

        return IntPtr.Zero;
    }

    private void OnOsdHotkeyChanged(string hotkey) => ConfigureDesktopOsdHotkey(hotkey);

    private void ConfigureDesktopOsdHotkey(string hotkey)
    {
        if (_source is null || System.Windows.Application.Current is App { SuppressProductStartup: true })
        {
            return;
        }
        if (string.Equals(_registeredDesktopOsdHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = UnregisterHotKey(_source.Handle, DesktopOsdHotkeyId);
        _registeredDesktopOsdHotkey = null;
        if (!TryParseGlobalHotkey(hotkey, out uint modifiers, out uint virtualKey))
        {
            if (DataContext is MainViewModel invalidViewModel)
            {
                invalidViewModel.ShowNotice("OSD hotkey must use Ctrl, Alt, or Shift plus one letter, number, or function key.", "Warning");
            }
            return;
        }
        if (!RegisterHotKey(_source.Handle, DesktopOsdHotkeyId, modifiers, virtualKey))
        {
            if (DataContext is MainViewModel unavailableViewModel)
            {
                unavailableViewModel.ShowNotice($"The OSD hotkey {hotkey} is already used by another application.", "Warning");
            }
            return;
        }
        _registeredDesktopOsdHotkey = hotkey;
    }

    private static bool TryParseGlobalHotkey(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        string keyName = parts[^1];
        foreach (string part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= 0x0002;
                    break;
                case "ALT":
                    modifiers |= 0x0001;
                    break;
                case "SHIFT":
                    modifiers |= 0x0004;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= 0x0008;
                    break;
                default:
                    return false;
            }
        }
        if (modifiers == 0 || !Enum.TryParse(keyName, ignoreCase: true, out Key key) || key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            return false;
        }

        int candidate = KeyInterop.VirtualKeyFromKey(key);
        if (candidate is < 0x30 or > 0x7B || candidate is > 0x39 and < 0x41 || candidate is > 0x5A and < 0x70)
        {
            return false;
        }
        virtualKey = (uint)candidate;
        return true;
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
