using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IThemeService _themeService;

    [ObservableProperty]
    public partial string Title { get; set; } = "Wavee";

    [ObservableProperty]
    public partial bool IsFullScreen { get; set; }

    public MainWindowViewModel(IThemeService themeService)
    {
        _themeService = themeService;
    }

    public ElementTheme CurrentTheme => _themeService.CurrentTheme;

    public void ToggleTheme()
    {
        _themeService.ToggleTheme();
        OnPropertyChanged(nameof(CurrentTheme));
    }
}