using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Services;
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

        Closed += OnClosed;

        // Extend content into titlebar
        ExtendsContentIntoTitleBar = true;

        // Make titlebar buttons transparent
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Colors.Transparent;
    }

    private void OnClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        var shellVm = Ioc.Default.GetService<Wavee.UI.WinUI.ViewModels.ShellViewModel>();
        shellVm?.Cleanup();
    }

    public async Task InitializeApplicationAsync()
    {
        // Initialize theme service
        var themeService = Ioc.Default.GetRequiredService<IThemeService>();
        themeService.Initialize(RootFrame);

        // Always navigate to ShellPage (app works without Spotify login)
        RootFrame.Navigate(typeof(ShellPage));

        // Try to restore cached Spotify session in background (non-blocking)
        var authState = Ioc.Default.GetRequiredService<IAuthState>();
        _ = Task.Run(async () =>
        {
            try
            {
                await authState.TryRestoreSessionAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Session restore failed: {ex}");
                // Not critical -- user can connect manually via sidebar button
            }
        });
    }
}
