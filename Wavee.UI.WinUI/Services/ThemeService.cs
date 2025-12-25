using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.UI;

namespace Wavee.UI.WinUI.Services;

public sealed class ThemeService : IThemeService
{
    private FrameworkElement? _rootElement;

    public ElementTheme CurrentTheme => _rootElement?.RequestedTheme ?? ElementTheme.Default;

    public void Initialize(FrameworkElement rootElement)
    {
        _rootElement = rootElement;
    }

    public void SetTheme(ElementTheme theme)
    {
        if (_rootElement != null)
        {
            ThemeHelper.SetRootTheme(_rootElement, theme);
        }
    }

    public void ToggleTheme()
    {
        if (_rootElement != null)
        {
            var newTheme = ThemeHelper.ToggleTheme(ThemeHelper.GetActualTheme(_rootElement));
            SetTheme(newTheme);
        }
    }
}
