using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Builder;
using Wavee.Core.Authentication;
using Wavee.Core.DependencyInjection;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contexts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Data;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Helpers.Application;

public static class AppLifecycleHelper
{
    public static IHost ConfigureHost()
    {
        var rxuiInstance = RxAppBuilder.CreateReactiveUIBuilder()
            .WithWinUI() // Register WinUI platform services
            .WithViewsFromAssembly(typeof(App).Assembly) // Register views and view models
            .BuildApp();

        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging
                .ClearProviders()
                .AddDebug()
                .SetMinimumLevel(LogLevel.Information))
            .ConfigureServices(services => services
                // Wavee Core services
                .AddWaveeCache()

                // Messenger (singleton - global default instance)
                .AddSingleton<IMessenger>(WeakReferenceMessenger.Default)

                // Contexts (cross-cutting state)
                .AddSingleton<IWindowContext, WindowContext>()
                .AddSingleton<IPlayerContext, PlayerContext>()

                // App state services
                .AddSingleton<INotificationService, NotificationService>()
                .AddSingleton<IPlaybackStateService, PlaybackStateService>()
                .AddSingleton<IAuthState, AuthStateService>()
                .AddSingleton<IConnectivityService, ConnectivityService>()
                .AddSingleton<IAppState, AppState>()

                // App initialization
                .AddSingleton<AppInitializationService>()

                // App services
                .AddSingleton<ISettingsService, SettingsService>()
                .AddSingleton<IThemeService, ThemeService>()
                .AddSingleton<ThemeColorService>()
                .AddSingleton<Services.HomeFeedCache>()
                .AddSingleton<Services.IHomeFeedCache>(sp => sp.GetRequiredService<Services.HomeFeedCache>())
                .AddSingleton<Services.ProfileCache>()
                .AddSingleton<Services.IProfileCache>(sp => sp.GetRequiredService<Services.ProfileCache>())
                .AddSingleton<Services.ImageCacheService>()
                .AddSingleton<Wavee.Core.Http.IColorService>(sp =>
                    new Wavee.Core.Http.ExtractedColorService(
                        sp.GetRequiredService<Wavee.Core.Session.ISession>().Pathfinder,
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<ILogger<Wavee.Core.Http.ExtractedColorService>>()))

                // Spotify session infrastructure
                .AddTransient<RetryHandler>()
                .AddHttpClient("Wavee")
                    .AddHttpMessageHandler<RetryHandler>()
                    .Services
                .AddSingleton<ICredentialsCache, CredentialsCache>()
                .AddSingleton(new SessionConfig { DeviceId = DeviceIdHelper.GetOrCreateDeviceId() })
                .AddSingleton(sp => Session.Create(
                    sp.GetRequiredService<SessionConfig>(),
                    sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                    sp.GetService<ILogger<Session>>()))
                .AddSingleton<ISession>(sp => sp.GetRequiredService<Session>())

                // Data services
                .AddSingleton<IDataServiceConfiguration>(new DataServiceConfiguration(startInDemoMode: false))
                .AddSingleton<ILibraryDataService, MockLibraryDataService>()
                .AddSingleton<ILocationService>(sp =>
                    new Data.Contexts.LocationService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetService<ILogger<Data.Contexts.LocationService>>()))
                .AddTransient<IConcertService>(sp =>
                    new Data.Contexts.ConcertService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetService<ILogger<Data.Contexts.ConcertService>>()))
                .AddTransient<ConcertViewModel>()
                .AddSingleton<IArtistService>(sp =>
                    new Data.Contexts.ArtistService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<ILocationService>(),
                        sp.GetService<ILogger<Data.Contexts.ArtistService>>()))
                .AddSingleton<IAlbumService>(sp =>
                    new Data.Contexts.AlbumService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<ILogger<Data.Contexts.AlbumService>>()))

                // ViewModels
                .AddSingleton<MainWindowViewModel>()
                .AddSingleton<ShellViewModel>()
                .AddSingleton<PlayerBarViewModel>()
                .AddTransient<HomeViewModel>()
                .AddTransient<ArtistViewModel>()
                .AddTransient<AlbumViewModel>()
                .AddTransient<AlbumsLibraryViewModel>()
                .AddTransient<ArtistsLibraryViewModel>()
                .AddTransient<LikedSongsViewModel>()
                .AddTransient<PlaylistViewModel>()
                .AddTransient<CreatePlaylistViewModel>()
                .AddTransient<ProfileViewModel>()
                .AddTransient<SpotifyConnectViewModel>()
                .AddTransient<DebugViewModel>()

                // Drag & drop
                .AddSingleton<DragStateService>()

                // Utilities
                .AddSingleton<AppModel>()

                // Image cache cleanup adapter
                .AddSingleton<ICleanableCache, ImageCacheCleanupAdapter>()
            )
            .Build();
    }

    public static void HandleAppUnhandledException(Exception? ex, bool showNotification)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {ex}");
    }
}
