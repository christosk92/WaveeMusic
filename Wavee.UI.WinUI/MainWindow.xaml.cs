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

        // Make titlebar buttons blend with content
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 128, 128, 128);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);
        // Let Windows auto-pick foreground based on theme (don't set = auto)
        AppWindow.TitleBar.ButtonForegroundColor = null;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = null;
    }

    private async void OnClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        var shellVm = Ioc.Default.GetService<Wavee.UI.WinUI.ViewModels.ShellViewModel>();
        shellVm?.Cleanup();

        // Force save settings on exit
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings is not null)
        {
            await settings.SaveAsync();
        }
    }

    public async Task InitializeApplicationAsync()
    {
        // Load persistent settings
        var settingsService = Ioc.Default.GetRequiredService<ISettingsService>();
        await settingsService.LoadAsync();

        // Initialize color service (before theme, so brushes are cached early)
        var colorService = Ioc.Default.GetRequiredService<ThemeColorService>();
        colorService.Initialize(RootFrame);

        // Initialize theme service
        var themeService = Ioc.Default.GetRequiredService<IThemeService>();
        themeService.Initialize(RootFrame);

        // Always navigate to ShellPage (app works without Spotify login)
        RootFrame.Navigate(typeof(ShellPage));

        // Show "What's New" dialog once the shell has rendered
        _ = ShowWhatsNewAfterDelayAsync(settingsService);

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

    private async Task ShowWhatsNewAfterDelayAsync(ISettingsService settingsService)
    {
        try
        {
            // Wait for the visual tree to fully materialize
            await Task.Delay(1500);
            if (Content?.XamlRoot is not { } xamlRoot) return;

            await Controls.WhatsNewDialog.ShowIfNeededAsync(
                xamlRoot,
                settingsService,
                Ioc.Default.GetRequiredService<IUpdateService>());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"What's New dialog failed: {ex}");
        }
    }
}
