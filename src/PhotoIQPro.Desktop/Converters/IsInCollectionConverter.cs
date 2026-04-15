using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoIQPro.Desktop.Converters;

/// <summary>
/// Returns Visibility.Visible when the first value (an item) is contained
/// in the second value (an IEnumerable collection), Collapsed otherwise.
/// Used to show the multi-select checkbox overlay on gallery thumbnails.
/// </summary>
public class IsInCollectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue || values[1] is not IEnumerable collection)
            return Visibility.Collapsed;

        foreach (var item in collection)
            if (Equals(item, values[0]))
                return Visibility.Visible;

        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
