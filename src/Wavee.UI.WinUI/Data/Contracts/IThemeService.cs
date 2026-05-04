using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface IThemeService
{
    ElementTheme CurrentTheme { get; }

    void Initialize(FrameworkElement rootElement);
    void SetTheme(ElementTheme theme);
    void ToggleTheme();
}
