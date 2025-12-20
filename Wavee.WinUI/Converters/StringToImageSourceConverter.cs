using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wavee.WinUI.Converters;

/// <summary>
/// Converts a string URL to an ImageSource (BitmapImage).
/// Returns null for null or empty strings, which prevents binding errors.
/// </summary>
public partial class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null; // Image.Source handles null gracefully
        }

        try
        {
            return new BitmapImage(new Uri(url));
        }
        catch
        {
            // Invalid URL - return null to avoid crashes
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
