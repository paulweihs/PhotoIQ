using System.Globalization;
using System.Windows.Data;

namespace PhotoIQPro.Desktop.Converters;

/// <summary>
/// Adds a numeric constant (default 8) to the bound value.
/// Used to account for item margins when setting VirtualizingWrapPanel ItemWidth/ItemHeight.
/// </summary>
public class AddConverter : IValueConverter
{
    public double Amount { get; set; } = 8;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return d + Amount;
        if (value is int i) return (double)(i + Amount);
        return value;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
