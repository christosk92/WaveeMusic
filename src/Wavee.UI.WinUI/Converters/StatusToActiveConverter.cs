using System;
using Microsoft.UI.Xaml.Data;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Converters;

/// <summary>
/// Returns true when ActivityStatus is InProgress (for ProgressRing.IsActive).
/// </summary>
public sealed class StatusToActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is ActivityStatus status && status == ActivityStatus.InProgress;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
