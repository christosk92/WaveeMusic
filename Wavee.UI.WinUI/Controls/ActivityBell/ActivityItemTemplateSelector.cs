using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Controls.ActivityBell;

/// <summary>
/// Selects the DataTemplate based on the concrete IActivityItem type.
/// Each activity source type gets its own visual treatment.
/// </summary>
public sealed class ActivityItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ProgressTemplate { get; set; }
    public DataTemplate? NotificationTemplate { get; set; }
    public DataTemplate? SpotifyTemplate { get; set; }
    public DataTemplate? DefaultTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return item switch
        {
            ProgressActivityItem => ProgressTemplate,
            SpotifyActivityItem => SpotifyTemplate,
            NotificationActivityItem => NotificationTemplate,
            _ => DefaultTemplate
        };
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
