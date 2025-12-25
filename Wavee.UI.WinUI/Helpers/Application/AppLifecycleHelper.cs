using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wavee.Core.DependencyInjection;
using Wavee.UI.WinUI.Data.Contexts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Helpers.Application;

public static class AppLifecycleHelper
{
    public static IHost ConfigureHost()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging
                .ClearProviders()
                .AddDebug()
                .SetMinimumLevel(LogLevel.Information))
            .ConfigureServices(services => services
                // Wavee Core services
                .AddWaveeCache()

                // Contexts (cross-cutting state)
                .AddSingleton<IWindowContext, WindowContext>()
                .AddSingleton<IPlayerContext, PlayerContext>()

                // App services
                .AddSingleton<INavigationService, NavigationService>()
                .AddSingleton<IThemeService, ThemeService>()

                // ViewModels
                .AddSingleton<MainWindowViewModel>()
                .AddSingleton<ShellViewModel>()
                .AddSingleton<PlayerBarViewModel>()
                .AddTransient<HomeViewModel>()
                .AddTransient<ArtistViewModel>()
                .AddTransient<AlbumViewModel>()
                .AddTransient<CreatePlaylistViewModel>()
                .AddTransient<ProfileViewModel>()

                // Utilities
                .AddSingleton<AppModel>()
            )
            .Build();
    }

    public static void HandleAppUnhandledException(Exception? ex, bool showNotification)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {ex}");
    }
}
