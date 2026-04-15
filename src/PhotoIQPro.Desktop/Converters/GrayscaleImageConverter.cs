using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoIQPro.Desktop.Converters;

/// <summary>
/// Multi-value converter: (loadedBitmap, isOffline) → BitmapSource.
/// Accepts a pre-loaded BitmapSource (set by the background thumbnail loader).
/// Returns null (blank placeholder) until the bitmap is ready.
/// Applies grayscale when isOffline = true.
/// </summary>
public class GrayscaleImageConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 1 || values[0] is not BitmapSource bmp)
            return null;   // bitmap not yet loaded — show blank placeholder

        bool isOffline = values.Length > 1 && values[1] is bool b && b;
        if (!isOffline) return bmp;

        try
        {
            var gray = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            return gray;
        }
        catch { return bmp; }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
