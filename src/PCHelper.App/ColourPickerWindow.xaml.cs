using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// The App project enables WinForms interop, so these type names are ambiguous
// with their System.Windows.Forms / System.Drawing counterparts. Bind them to
// the WPF types this window actually uses.
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace PCHelper.App;

/// <summary>
/// A themed HSV colour picker matching the RigPilot visual language, used in
/// place of the native Windows <c>ColorDialog</c> (which renders in the system
/// light theme and clashes with the app's dark surfaces). Presents a
/// saturation/value field, a hue strip, and live hex + R/G/B fields that stay
/// in sync. Callers use <see cref="TryPick"/>; the chosen colour is exposed as
/// an upper-case <c>#RRGGBB</c> string.
/// </summary>
public partial class ColourPickerWindow : Window
{
    // Hue in degrees [0,360), saturation and value in [0,1]. Hue and saturation
    // are retained across achromatic colours (grey/black/white) so dragging
    // value or saturation back up returns to the same hue the user last chose.
    private double _hue;
    private double _saturation = 1;
    private double _value = 1;

    // Guards the field/visual writers from re-entering each other while a single
    // logical change propagates (e.g. an SV drag rewriting the hex + R/G/B text).
    private bool _syncing;
    private bool _draggingSv;
    private bool _draggingHue;

    /// <summary>The selected colour as <c>#RRGGBB</c>, valid after the dialog returns true.</summary>
    public string SelectedHex { get; private set; } = "#FFFFFF";

    public ColourPickerWindow(string? initialHex)
    {
        InitializeComponent();

        (byte r, byte g, byte b) = TryParseHex(initialHex, out byte pr, out byte pg, out byte pb)
            ? (pr, pg, pb)
            : ((byte)0x4E, (byte)0xA1, (byte)0xFF);
        SetFromRgb(r, g, b);

        // The SV/hue thumbs are positioned from pixel dimensions, so seat them
        // once the layout has produced real sizes.
        Loaded += (_, _) => SyncVisuals();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Shows the picker modally, seeded with <paramref name="initialHex"/>.
    /// Returns true and sets <paramref name="resultHex"/> to <c>#RRGGBB</c> when
    /// the user confirms; false when cancelled.
    /// </summary>
    public static bool TryPick(Window owner, string? initialHex, out string resultHex)
    {
        ColourPickerWindow picker = new(initialHex) { Owner = owner };
        bool confirmed = picker.ShowDialog() == true;
        resultHex = picker.SelectedHex;
        return confirmed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter confirms unless focus is in a field the user is still editing;
        // IsDefault/IsCancel already cover the common path, this makes Esc close
        // even while a text field holds focus.
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    // ----- State transitions -------------------------------------------------

    private void SetFromHsv(double hue, double saturation, double value)
    {
        _hue = ((hue % 360) + 360) % 360;
        _saturation = Math.Clamp(saturation, 0, 1);
        _value = Math.Clamp(value, 0, 1);
        SyncFieldsFromState();
        SyncVisuals();
    }

    private void SetFromRgb(byte r, byte g, byte b)
    {
        RgbToHsv(r, g, b, out double h, out double s, out double v);

        // Preserve the prior hue/saturation when the colour carries no chroma,
        // so the hue strip and SV thumb do not snap to an arbitrary position.
        _hue = s <= 0 ? _hue : h;
        _saturation = v <= 0 ? _saturation : s;
        _value = v;

        SyncFieldsFromState();
        SyncVisuals();
    }

    /// <summary>Rewrites the hex and R/G/B text fields from the current HSV state.</summary>
    private void SyncFieldsFromState()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            (byte r, byte g, byte b) = CurrentRgb();
            if (PreviewBrush is not null)
            {
                PreviewBrush.Color = Color.FromRgb(r, g, b);
            }

            HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
            RedBox.Text = r.ToString(CultureInfo.InvariantCulture);
            GreenBox.Text = g.ToString(CultureInfo.InvariantCulture);
            BlueBox.Text = b.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Repaints the SV base hue and repositions both thumbs. The thumb
    /// coordinates come from the canvases' rendered sizes, so they stay correct
    /// whatever width the SV field lays out to; before the first layout pass the
    /// sizes are zero and only the hue paint runs (the Loaded handler re-syncs).</summary>
    private void SyncVisuals()
    {
        if (SvHueLayer is null)
        {
            return;
        }

        (byte hr, byte hg, byte hb) = HsvToRgb(_hue, 1, 1);
        SvHueLayer.Background = new SolidColorBrush(Color.FromRgb(hr, hg, hb));

        double svWidth = SvCanvas.ActualWidth;
        double svHeight = SvCanvas.ActualHeight;
        if (svWidth > 0 && svHeight > 0)
        {
            Canvas.SetLeft(SvThumb, (_saturation * svWidth) - (SvThumb.Width / 2));
            Canvas.SetTop(SvThumb, ((1 - _value) * svHeight) - (SvThumb.Height / 2));
        }

        double hueHeight = HueCanvas.ActualHeight;
        if (hueHeight > 0)
        {
            Canvas.SetTop(HueThumb, ((_hue / 360) * hueHeight) - (HueThumb.Height / 2));
        }
    }

    private (byte R, byte G, byte B) CurrentRgb() => HsvToRgb(_hue, _saturation, _value);

    // ----- Field editing -----------------------------------------------------

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (TryParseHex(HexBox.Text, out byte r, out byte g, out byte b))
        {
            _syncing = true;
            try
            {
                RgbToHsv(r, g, b, out double h, out double s, out double v);
                _hue = s <= 0 ? _hue : h;
                _saturation = v <= 0 ? _saturation : s;
                _value = v;
                if (PreviewBrush is not null)
                {
                    PreviewBrush.Color = Color.FromRgb(r, g, b);
                }

                RedBox.Text = r.ToString(CultureInfo.InvariantCulture);
                GreenBox.Text = g.ToString(CultureInfo.InvariantCulture);
                BlueBox.Text = b.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _syncing = false;
            }

            SyncVisuals();
        }
    }

    private void RgbBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        byte r = ParseChannel(RedBox.Text);
        byte g = ParseChannel(GreenBox.Text);
        byte b = ParseChannel(BlueBox.Text);

        _syncing = true;
        try
        {
            RgbToHsv(r, g, b, out double h, out double s, out double v);
            _hue = s <= 0 ? _hue : h;
            _saturation = v <= 0 ? _saturation : s;
            _value = v;
            if (PreviewBrush is not null)
            {
                PreviewBrush.Color = Color.FromRgb(r, g, b);
            }

            HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
        }
        finally
        {
            _syncing = false;
        }

        SyncVisuals();
    }

    // ----- SV field drag -----------------------------------------------------

    private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        SvCanvas.CaptureMouse();
        UpdateSvFromPoint(e.GetPosition(SvCanvas));
    }

    private void SvCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSv)
        {
            UpdateSvFromPoint(e.GetPosition(SvCanvas));
        }
    }

    private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false;
        SvCanvas.ReleaseMouseCapture();
    }

    private void UpdateSvFromPoint(Point p)
    {
        double width = SvCanvas.ActualWidth;
        double height = SvCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double s = Math.Clamp(p.X / width, 0, 1);
        double v = Math.Clamp(1 - (p.Y / height), 0, 1);
        SetFromHsv(_hue, s, v);
    }

    // ----- Hue strip drag ----------------------------------------------------

    private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueCanvas.CaptureMouse();
        UpdateHueFromPoint(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue)
        {
            UpdateHueFromPoint(e.GetPosition(HueCanvas));
        }
    }

    private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = false;
        HueCanvas.ReleaseMouseCapture();
    }

    private void UpdateHueFromPoint(Point p)
    {
        double height = HueCanvas.ActualHeight;
        if (height <= 0)
        {
            return;
        }

        double h = Math.Clamp(p.Y / height, 0, 1) * 360;
        SetFromHsv(h, _saturation, _value);
    }

    // ----- Chrome ------------------------------------------------------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        (byte r, byte g, byte b) = CurrentRgb();
        SelectedHex = $"#{r:X2}{g:X2}{b:X2}";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ----- Colour maths ------------------------------------------------------

    private static byte ParseChannel(string? text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return (byte)Math.Clamp(value, 0, 255);
        }

        return 0;
    }

    private static bool TryParseHex(string? value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> hex = value.Trim().TrimStart('#');
        const NumberStyles Hex = NumberStyles.HexNumber;
        CultureInfo invariant = CultureInfo.InvariantCulture;
        return hex.Length == 6
            && byte.TryParse(hex[..2], Hex, invariant, out red)
            && byte.TryParse(hex[2..4], Hex, invariant, out green)
            && byte.TryParse(hex[4..6], Hex, invariant, out blue);
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        h = 0;
        if (delta > 0)
        {
            if (max == rd)
            {
                h = 60 * (((gd - bd) / delta) % 6);
            }
            else if (max == gd)
            {
                h = 60 * (((bd - rd) / delta) + 2);
            }
            else
            {
                h = 60 * (((rd - gd) / delta) + 4);
            }
        }

        if (h < 0)
        {
            h += 360;
        }

        s = max <= 0 ? 0 : delta / max;
        v = max;
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return (
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
