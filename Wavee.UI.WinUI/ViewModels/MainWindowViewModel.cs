using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IThemeService _themeService;

    [ObservableProperty]
    private string _title = "Wavee";

    [ObservableProperty]
    private bool _isFullScreen;

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
