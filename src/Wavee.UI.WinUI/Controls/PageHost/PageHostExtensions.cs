using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.PageHost;

public static class PageHostExtensions
{
    /// <summary>
    /// Walks the visual tree upward looking for the nearest <see cref="PageHost"/>
    /// that hosts <paramref name="element"/>. Used by pages (and their nested
    /// controls) that need to call back into their host for navigation —
    /// the replacement for <c>Page.Frame</c>, which doesn't exist under PageHost.
    /// </summary>
    public static PageHost? FindHostingPageHost(this DependencyObject element)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is PageHost host) return host;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
