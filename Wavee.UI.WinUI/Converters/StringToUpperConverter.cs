using System;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Uppercases a string for display. Used by the Zune-style genre sidebar so the
/// VM keeps human-cased titles ("Religion &amp; Spirituality") while the UI renders
/// "RELIGION &amp; SPIRITUALITY".
/// </summary>
public sealed class StringToUpperConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s ? s.ToUpperInvariant() : (value ?? string.Empty);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
