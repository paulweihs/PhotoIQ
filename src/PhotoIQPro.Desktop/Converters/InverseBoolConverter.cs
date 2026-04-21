using System.Globalization;
using System.Windows.Data;

namespace PhotoIQPro.Desktop.Converters;

/// <summary>WPF value converter that inverts a boolean value (true → false, false → true).</summary>
/// <remarks>
/// Used in XAML bindings to negate boolean properties without code-behind.
/// Example: <code>&lt;IsEnabled="{Binding IsLoading, Converter={StaticResource InverseBoolConverter}}"&gt;</code>
/// to disable a button while loading is in progress.
/// </remarks>
public class InverseBoolConverter : IValueConverter
{
    /// <summary>Converts a boolean to its inverse value.</summary>
    /// <remarks>Returns false if value is true, false if value is false, or false if value is not a bool.</remarks>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    /// <summary>Converts a boolean back to its inverse value (bidirectional binding support).</summary>
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}
