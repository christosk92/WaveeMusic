using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Helpers.UI;

public static class ThemeHelper
{
    public static ElementTheme GetActualTheme(FrameworkElement element)
    {
        if (element.ActualTheme == ElementTheme.Default)
        {
            return Microsoft.UI.Xaml.Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
        return element.ActualTheme;
    }

    public static void SetRootTheme(FrameworkElement root, ElementTheme theme)
    {
        root.RequestedTheme = theme;
    }

    public static ElementTheme ToggleTheme(ElementTheme currentTheme)
    {
        return currentTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
    }
}
