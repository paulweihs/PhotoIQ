using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoWell.Desktop.Converters;

/// <summary>
/// Converts a double (ThumbnailSize) to a square Size for VirtualizingWrapPanel.ItemSize.
/// The optional ConverterParameter adds a margin offset (default 8 to match the 4px margin each side).
/// </summary>
public class DoubleToSquareSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double d = value is double dv ? dv : value is int iv ? iv : 100;
        double offset = parameter is string s && double.TryParse(s, out double p) ? p : 8;
        return new Size(d + offset, d + offset);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
