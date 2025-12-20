using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wavee.WinUI.Converters;

/// <summary>
/// Converts true to Visibility.Collapsed and false to Visibility.Visible
/// </summary>
public partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Collapsed;
        }
        return false;
    }
}
