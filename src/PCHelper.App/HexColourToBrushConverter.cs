using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PCHelper.App;

/// <summary>
/// Converts a "#RRGGBB" text value into a solid brush for colour previews.
/// Anything unparsable renders transparent instead of throwing, so a
/// half-typed value never breaks the UI.
/// </summary>
public sealed class HexColourToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = (value as string)?.Trim() ?? string.Empty;
        if (text.Length == 7
            && text[0] == '#'
            && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            SolidColorBrush brush = new(System.Windows.Media.Color.FromRgb(
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF)));
            brush.Freeze();
            return brush;
        }

        return System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Colour preview is one-way.");
}
