using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PCHelper.App;

/// <summary>
/// Collapses a secondary header element once the header has too little room for
/// the page title beside it.
///
/// <para>The header's action strip is fixed-width, so the title column gets
/// whatever is left. At the 960px minimum window width that leaves roughly 90px
/// — enough to trim "Overview" down to "Ove…". The "Updated HH:MM:SS / F5 to
/// refresh" block is the one part of the strip that carries no control and is
/// already conveyed by the adjacent Refresh button, so it is what gives way and
/// the title stays readable.</para>
/// </summary>
public sealed class HeaderHintVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Header width, in device-independent pixels, below which the hint yields
    /// its space. At the 1240px default window the header is about 958px wide;
    /// at the 960px minimum it is about 662px.
    /// </summary>
    public const double DefaultMinimumHeaderWidth = 760;

    /// <summary>
    /// True when the header is wide enough to show the hint. A width that is not
    /// yet measured (zero, negative, or non-finite during the first layout pass)
    /// keeps the hint, so nothing flickers away on a normal-sized window.
    /// </summary>
    public static bool IsHintVisible(double headerWidth, double minimumWidth = DefaultMinimumHeaderWidth) =>
        !double.IsFinite(headerWidth) || headerWidth <= 0 || headerWidth >= minimumWidth;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double minimum = parameter is string text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : DefaultMinimumHeaderWidth;
        double width = value is double actual ? actual : double.NaN;
        return IsHintVisible(width, minimum) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Header hint visibility is one-way.");
}
