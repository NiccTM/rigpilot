using System.Globalization;
using System.Windows.Data;

namespace PCHelper.App;

/// <summary>
/// Formats a control-slider value with its unit. Wattage values additionally
/// show an Afterburner-style percentage where 100% is the vendor DEFAULT limit
/// (e.g. 385 W on a 350 W-default board reads "110 %"); when the adapter could
/// not discover a default, the honest fallback is percent of the board maximum.
/// Inputs: [0] slider value, [1] unit string, [2] range maximum, [3] default (nullable).
/// </summary>
public sealed class SliderValueLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double value, string unit, double maximum, ..])
        {
            return string.Empty;
        }

        if (!string.Equals(unit, "W", StringComparison.OrdinalIgnoreCase))
        {
            return $"{value:0} {unit}";
        }

        double? defaultValue = values.Length > 3 && values[3] is double known and > 0 ? known : null;
        return defaultValue is double reference
            ? $"{value:0} W · {value / reference * 100:0} %"
            : maximum > 0
                ? $"{value:0} W · {value / maximum * 100:0} % of max"
                : $"{value:0} W";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
