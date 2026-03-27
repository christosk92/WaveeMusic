using System;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Builder;
using Wavee.Connect;
using Wavee.Connect.Playback;
using Wavee.Core.Authentication;
using Wavee.Core.DependencyInjection;
using Wavee.Core.Http;
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
    // Captured on UI thread during ConfigureHost, used by background init
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    public static IHost ConfigureHost()
    {
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

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

                // Playback service layer
                .AddSingleton<Wavee.Connect.ConnectCommandClient>(sp =>
                    new Wavee.Connect.ConnectCommandClient(
                        sp.GetRequiredService<Session>(),
                        sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("Wavee"),
                        sp.GetService<ILogger<Wavee.Connect.ConnectCommandClient>>()))
                .AddSingleton<IPlaybackCommandExecutor>(sp =>
                    new ConnectCommandExecutor(
                        sp.GetRequiredService<Wavee.Connect.ConnectCommandClient>(),
                        sp.GetRequiredService<Session>(),
                        sp.GetService<ILogger<ConnectCommandExecutor>>()))
                .AddSingleton<IPlaybackPromptService>(sp =>
                    new Data.Contexts.PlaybackPromptService(
                        sp.GetRequiredService<ISettingsService>()))
                .AddSingleton<IPlaybackService>(sp =>
                    new Data.Contexts.PlaybackService(
                        sp.GetRequiredService<IPlaybackCommandExecutor>(),
                        sp.GetRequiredService<Session>(),
                        sp.GetRequiredService<INotificationService>(),
                        sp.GetRequiredService<IPlaybackPromptService>(),
                        sp.GetService<ILogger<Data.Contexts.PlaybackService>>()))

                // App state services
                .AddSingleton<INotificationService, NotificationService>()
                .AddSingleton<IPlaybackStateService>(sp =>
                    new PlaybackStateService(
                        sp.GetRequiredService<Session>(),
                        sp.GetRequiredService<IPlaybackService>(),
                        sp.GetRequiredService<Wavee.Core.Http.IColorService>(),
                        sp.GetRequiredService<IMessenger>(),
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                        sp.GetService<ILogger<PlaybackStateService>>()))
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

    /// <summary>
    /// Initializes the AudioPipeline for local playback after session connects.
    /// Call once after Session.ConnectAsync succeeds.
    /// </summary>
    public static void InitializePlaybackEngine(Session session, System.Net.Http.HttpClient httpClient, ILogger? logger)
    {
        try
        {
            var metadataDb = Ioc.Default.GetService<IMetadataDatabase>();
            var cacheService = Ioc.Default.GetService<Wavee.Core.Storage.ICacheService>();

            var audioPipeline = AudioPipelineFactory.CreateSpotifyPipeline(
                session,
                (SpClient)session.SpClient,
                httpClient,
                options: AudioPipelineOptions.Default,
                metadataDatabase: metadataDb,
                cacheService: cacheService,
                deviceId: session.Config.DeviceId,
                eventService: session.Events,
                commandHandler: session.CommandHandler,
                deviceStateManager: session.DeviceState,
                logger: logger);

            // Enable bidirectional mode — local state changes publish to Spotify
            session.PlaybackState?.EnableBidirectionalMode(
                audioPipeline,
                (SpClient)session.SpClient,
                session);

            // Wire up the local engine on the executor
            var executor = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
            if (executor != null)
                executor.LocalEngine = audioPipeline;

            // Sync initial volume from DeviceStateManager to UI
            var volumePercent = session.GetVolumePercentage() ?? 50;
            var playbackState = Ioc.Default.GetService<IPlaybackStateService>();
            if (playbackState != null)
                playbackState.Volume = volumePercent;

            // Surface playback engine errors to the user via notifications
            var notificationService = Ioc.Default.GetService<INotificationService>();
            if (notificationService != null)
            {
                var dispatcher = _uiDispatcher;
                audioPipeline.Errors.Subscribe(error =>
                {
                    var message = error.ErrorType switch
                    {
                        PlaybackErrorType.AudioDeviceUnavailable => "Audio device unavailable. Check your audio output.",
                        PlaybackErrorType.TrackUnavailable => "This track is not available. It may require a Premium account.",
                        PlaybackErrorType.NetworkError => "Network error during playback. Check your connection.",
                        PlaybackErrorType.DecodeError => "Failed to decode audio stream.",
                        _ => error.Message
                    };

                    logger?.LogWarning("Playback engine error: {Type} — {Message}", error.ErrorType, error.Message);
                    dispatcher?.TryEnqueue(() =>
                        notificationService.Show(new Data.Models.NotificationInfo
                        {
                            Message = message,
                            Severity = Data.Models.NotificationSeverity.Error,
                            AutoDismissAfter = TimeSpan.FromSeconds(5)
                        }));
                });
            }

            logger?.LogInformation("AudioPipeline initialized — local playback enabled");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to initialize AudioPipeline — local playback unavailable");
        }
    }

    public static void HandleAppUnhandledException(Exception? ex, bool showNotification)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {ex}");
    }
}
