using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wavee.WinUI.Converters;

/// <summary>
/// Converts true to Visibility.Visible and false to Visibility.Collapsed
/// </summary>
public partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}
