using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Track;

public sealed partial class TrackItem
{
    private Brush ResolveTrackBrush(string resourceKey)
    {
        if (TryResolveTrackBrush(Resources, resourceKey, out var brush))
            return brush;

        if (Application.Current?.Resources is { } appResources
            && TryResolveTrackBrush(appResources, resourceKey, out brush))
        {
            return brush;
        }

        if (_themeColors is not null)
            return _themeColors.GetBrush(resourceKey);

        return new SolidColorBrush(Colors.Magenta);
    }

    private bool TryResolveTrackBrush(ResourceDictionary resources, string resourceKey, out Brush brush)
    {
        var themeKey = ActualTheme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => "Default"
        };

        if (TryResolveTrackBrush(resources, themeKey, resourceKey, out brush))
            return true;

        if (themeKey != "Default" && TryResolveTrackBrush(resources, "Default", resourceKey, out brush))
            return true;

        return TryResolveFlatBrush(resources, resourceKey, out brush);
    }

    private static bool TryResolveTrackBrush(
        ResourceDictionary resources,
        string themeKey,
        string resourceKey,
        out Brush brush)
    {
        if (resources.ThemeDictionaries.TryGetValue(themeKey, out var themeResource)
            && themeResource is ResourceDictionary themeDictionary
            && TryResolveFlatBrush(themeDictionary, resourceKey, out brush))
        {
            return true;
        }

        foreach (var merged in resources.MergedDictionaries)
        {
            if (TryResolveTrackBrush(merged, themeKey, resourceKey, out brush))
                return true;
        }

        brush = null!;
        return false;
    }

    private static bool TryResolveFlatBrush(ResourceDictionary resources, string resourceKey, out Brush brush)
    {
        if (resources.TryGetValue(resourceKey, out var value) && value is Brush resolvedBrush)
        {
            brush = resolvedBrush;
            return true;
        }

        foreach (var merged in resources.MergedDictionaries)
        {
            if (TryResolveFlatBrush(merged, resourceKey, out brush))
                return true;
        }

        brush = null!;
        return false;
    }
}
