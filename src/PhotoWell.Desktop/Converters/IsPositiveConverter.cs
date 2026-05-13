using System.Globalization;
using System.Windows.Data;

namespace PhotoWell.Desktop.Converters;

/// <summary>Returns true when the bound double value is >= 0 (i.e. determinate progress is available).</summary>
public sealed class IsPositiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d && d >= 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
