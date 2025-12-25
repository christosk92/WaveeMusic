using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Views;
using WinUIEx;

namespace Wavee.UI.WinUI;

public sealed partial class MainWindow : WindowEx
{
    private static MainWindow? _instance;
    public static MainWindow Instance => _instance ??= new();

    public nint WindowHandle { get; }

    private MainWindow()
    {
        InitializeComponent();

        WindowHandle = this.GetWindowHandle();

        // Extend content into titlebar
        ExtendsContentIntoTitleBar = true;

        // Make titlebar buttons transparent
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Colors.Transparent;
    }

    public async Task InitializeApplicationAsync()
    {
        // Initialize theme service
        var themeService = Ioc.Default.GetRequiredService<IThemeService>();
        themeService.Initialize(RootFrame);

        // Navigate to shell page (ShellPage will open the initial tab on Loaded)
        RootFrame.Navigate(typeof(ShellPage));

        await Task.CompletedTask;
    }
}
