using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoIQPro.Desktop.Converters;

/// <summary>WPF value converter that inverts a boolean and converts it to a Visibility value.</summary>
/// <remarks>
/// Maps false → Visible and true → Collapsed. Used to hide UI elements when a condition is active.
/// Example: <code>&lt;Visibility="{Binding IsLoading, Converter={StaticResource InverseBoolToVisibilityConverter}}"&gt;</code>
/// shows content only when loading is false.
/// </remarks>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <summary>Converts an inverted boolean to a Visibility value: false/!true → Visible, true/!false → Collapsed.</summary>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Converts a Visibility value back to a boolean: Visible → false, other → true.</summary>
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}
