using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaScaleTransform = System.Windows.Media.ScaleTransform;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;
using PCHelper.Contracts;
using PCHelper.Core;
using Forms = System.Windows.Forms;

namespace PCHelper.App;

/// <summary>
/// Converts an already-validated OSD frame into presentation-only cells. This
/// stays independent of the WPF window so layout and missing-sensor behavior
/// can be tested without opening a desktop overlay.
/// </summary>
public static class DesktopOsdPresentation
{
    public static IReadOnlyList<DesktopOsdCell> Create(OsdFrameV1 frame) => frame.Widgets
        .OrderBy(widget => widget.Row)
        .ThenBy(widget => widget.Column)
        .Select(widget => new DesktopOsdCell(
            widget.Label,
            widget.Text,
            widget.Row,
            widget.Column,
            widget.Colour,
            widget.Quality))
        .ToArray();
}

public sealed record DesktopOsdCell(
    string Label,
    string Text,
    int Row,
    int Column,
    string Colour,
    SensorQuality Quality);

/// <summary>
/// A transparent, non-activating WPF desktop overlay. It is a local window,
/// not RTSS shared-memory output or graphics-process injection.
/// </summary>
public sealed class DesktopOsdController : IDisposable
{
    private DesktopOsdWindow? _window;

    public bool IsVisible => _window?.IsVisible == true;

    public void Show(
        OsdLayoutV1 layout,
        IReadOnlyList<SensorSample> sensors,
        OsdPresentationSettingsV1? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sensors);
        Update(layout, sensors, presentation);
        _window!.Show();
    }

    public void Update(
        OsdLayoutV1 layout,
        IReadOnlyList<SensorSample> sensors,
        OsdPresentationSettingsV1? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sensors);
        OsdFrameV1 frame = OsdFrameRenderer.Render(layout, sensors, DateTimeOffset.UtcNow);
        _window ??= new DesktopOsdWindow();
        _window.Update(layout, DesktopOsdPresentation.Create(frame), presentation);
    }

    public void Close()
    {
        if (_window is null)
        {
            return;
        }
        _window.Close();
        _window = null;
    }

    public void Dispose() => Close();
}

internal sealed class DesktopOsdWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;
    private readonly Border _surface;
    private readonly Grid _grid;

    public DesktopOsdWindow()
    {
        _grid = new Grid();
        _surface = new Border
        {
            Background = new MediaSolidColorBrush(MediaColor.FromArgb(218, 12, 16, 23)),
            BorderBrush = new MediaSolidColorBrush(MediaColor.FromArgb(230, 54, 67, 86)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = _grid
        };
        Content = _surface;
        AllowsTransparency = true;
        Background = MediaBrushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = 24;
        Top = 24;
    }

    public void Update(
        OsdLayoutV1 layout,
        IReadOnlyList<DesktopOsdCell> cells,
        OsdPresentationSettingsV1? presentation)
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        int rowCount = Math.Max(1, cells.Count == 0 ? 1 : cells.Max(cell => cell.Row) + 1);
        int columnCount = Math.Max(1, cells.Count == 0 ? 1 : cells.Max(cell => cell.Column) + 1);
        for (int row = 0; row < rowCount; row++)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (int column = 0; column < columnCount; column++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 86 });
        }

        foreach (DesktopOsdCell cell in cells)
        {
            Border card = new()
            {
                Background = new MediaSolidColorBrush(MediaColor.FromArgb(72, 255, 255, 255)),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(2),
                Padding = new Thickness(8, 5, 10, 6),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = cell.Label,
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new MediaSolidColorBrush(MediaColor.FromArgb(210, 222, 231, 243))
                        },
                        new TextBlock
                        {
                            Text = cell.Text,
                            FontSize = 16,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = ToBrush(cell.Colour, cell.Quality),
                            Margin = new Thickness(0, 1, 0, 0)
                        }
                    }
                }
            };
            Grid.SetRow(card, cell.Row);
            Grid.SetColumn(card, cell.Column);
            _grid.Children.Add(card);
        }

        double opacity = presentation?.OpacityOverride ?? layout.Opacity;
        double scale = presentation?.ScaleOverride ?? layout.Scale;
        _surface.Opacity = Math.Clamp(opacity, 0.2, 1);
        _surface.LayoutTransform = new MediaScaleTransform(Math.Clamp(scale, 0.6, 2.5), Math.Clamp(scale, 0.6, 2.5));
        ApplyPlacement(presentation, scale);
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        nint current = GetWindowLongPtr(handle, GwlExStyle);
        _ = SetWindowLongPtr(handle, GwlExStyle, current | WsExNoActivate | WsExToolWindow);
    }

    private static MediaSolidColorBrush ToBrush(string colour, SensorQuality quality)
    {
        if (quality is SensorQuality.Unavailable or SensorQuality.Invalid)
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(155, 166, 184));
        }
        try
        {
            MediaColor value = (MediaColor)MediaColorConverter.ConvertFromString(colour)!;
            MediaSolidColorBrush brush = new(value);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return new MediaSolidColorBrush(MediaColor.FromRgb(234, 241, 250));
        }
    }

    private void ApplyPlacement(OsdPresentationSettingsV1? presentation, double scale)
    {
        string? requested = presentation?.MonitorStableId;
        string? deviceName = requested?.StartsWith("display:", StringComparison.OrdinalIgnoreCase) == true
            ? requested["display:".Length..]
            : null;
        Forms.Screen screen = Forms.Screen.AllScreens.FirstOrDefault(candidate =>
                deviceName is not null && string.Equals(candidate.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen
            ?? Forms.Screen.AllScreens.First();
        _surface.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Max(1, _surface.DesiredSize.Width * Math.Clamp(scale, 0.6, 2.5));
        double height = Math.Max(1, _surface.DesiredSize.Height * Math.Clamp(scale, 0.6, 2.5));
        const double margin = 24;
        OsdScreenAnchor anchor = presentation?.Anchor ?? OsdScreenAnchor.TopLeft;
        Left = anchor is OsdScreenAnchor.TopRight or OsdScreenAnchor.BottomRight
            ? screen.WorkingArea.Right - width - margin
            : screen.WorkingArea.Left + margin;
        Top = anchor is OsdScreenAnchor.BottomLeft or OsdScreenAnchor.BottomRight
            ? screen.WorkingArea.Bottom - height - margin
            : screen.WorkingArea.Top + margin;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr window, int index, nint value);
}
