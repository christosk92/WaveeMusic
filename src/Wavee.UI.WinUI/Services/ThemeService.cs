using System;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.UI;

namespace Wavee.UI.WinUI.Services;

public sealed class ThemeService : IThemeService
{
    private FrameworkElement? _rootElement;
    private readonly ISettingsService _settings;

    public ElementTheme CurrentTheme => _rootElement?.RequestedTheme ?? ElementTheme.Default;

    public ThemeService(ISettingsService settings)
    {
        _settings = settings;
    }

    public void Initialize(FrameworkElement rootElement)
    {
        _rootElement = rootElement;

        // Restore saved theme
        var savedTheme = _settings.Settings.Theme;
        if (savedTheme != "Default" && Enum.TryParse<ElementTheme>(savedTheme, out var theme))
            SetTheme(theme);
    }

    public void SetTheme(ElementTheme theme)
    {
        if (_rootElement != null)
        {
            ThemeHelper.SetRootTheme(_rootElement, theme);
            _settings.Update(s => s.Theme = theme.ToString());
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
