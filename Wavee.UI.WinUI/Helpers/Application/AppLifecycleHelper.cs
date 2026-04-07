using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Wavee.Connect.Playback.Processors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.Data;
using Wavee.UI.WinUI.ViewModels;
using System.Collections.Generic;
using Serilog;
using Serilog.Extensions.Logging;
namespace Wavee.UI.WinUI.Helpers.Application;

public static class AppLifecycleHelper
{
    private static readonly List<IDisposable> _appSubscriptions = [];
    private static Services.TrackMetadataEnricher? _trackMetadataEnricher;
    // Captured on UI thread during ConfigureHost, used by background init
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    public static IHost ConfigureHost()
    {
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var rxuiInstance = RxAppBuilder.CreateReactiveUIBuilder()
            .WithWinUI() // Register WinUI platform services
            .WithViewsFromAssembly(typeof(App).Assembly) // Register views and view models
            .BuildApp();

        // Create the InMemorySink early so Serilog can write to it from the start
        var inMemorySink = new Services.InMemorySink(_uiDispatcher);
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Information)
            .WriteTo.Debug()
            .WriteTo.File(
                path: AppPaths.RollingLogFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
            .WriteTo.Sink(inMemorySink)
            .Enrich.FromLogContext()
            .CreateLogger();

        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging
                .ClearProviders()
                .AddSerilog(Log.Logger, dispose: false)
                .SetMinimumLevel(LogLevel.Debug))
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
                .AddSingleton<IAudioPipelineControl>(sp =>
                    (IAudioPipelineControl)sp.GetRequiredService<IPlaybackCommandExecutor>())
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
                .AddSingleton<IUpdateService, UpdateService>()
                .AddSingleton<IPlaybackStateService>(sp =>
                    new PlaybackStateService(
                        sp.GetRequiredService<Session>(),
                        sp.GetRequiredService<IPlaybackService>(),
                        sp.GetRequiredService<Wavee.Core.Http.IColorService>(),
                        sp.GetRequiredService<IMessenger>(),
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                        sp.GetService<ILogger<PlaybackStateService>>(),
                        sp.GetService<Services.IHomeFeedCache>()))
                .AddSingleton<IAuthState, AuthStateService>()
                .AddSingleton<IConnectivityService>(sp =>
                    new ConnectivityService(
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetRequiredService<Session>()))
                .AddSingleton<IAppState, AppState>()

                // App initialization
                .AddSingleton<AppInitializationService>()
                .AddSingleton(sp =>
                    new Data.Contexts.LibrarySyncOrchestrator(
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetService<Wavee.Core.Library.Spotify.ISpotifyLibraryService>(),
                        sp.GetService<ITrackLikeService>(),
                        sp.GetService<INotificationService>(),
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                        sp.GetService<ILogger<Data.Contexts.LibrarySyncOrchestrator>>()))
                .AddSingleton<IActivityService, Data.Contexts.ActivityService>()

                // Audio processors (shared with UI for real-time control)
                .AddSingleton<EqualizerProcessor>()

                // Dispatcher abstraction
                .AddSingleton<IDispatcherService>(sp =>
                    new Services.DispatcherService(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))

                // App services
                .AddSingleton<Wavee.Controls.Lyrics.Services.LocalizationService.ILocalizationService, Wavee.Controls.Lyrics.Services.LocalizationService.LocalizationService>()
                .AddSingleton<ISettingsService, SettingsService>()
                .AddSingleton<IThemeService, ThemeService>()
                .AddSingleton<ThemeColorService>()
                .AddSingleton<Services.HomeFeedCache>()
                .AddSingleton<Services.IHomeFeedCache>(sp => sp.GetRequiredService<Services.HomeFeedCache>())
                .AddSingleton<Services.RecentlyPlayedService>(sp =>
                    new Services.RecentlyPlayedService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetRequiredService<IMessenger>(),
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                        sp.GetService<ILogger<Services.RecentlyPlayedService>>()))
                .AddSingleton<Services.ProfileCache>()
                .AddSingleton<Services.IProfileCache>(sp => sp.GetRequiredService<Services.ProfileCache>())
                .AddSingleton<Services.ImageCacheService>()
                .AddSingleton(inMemorySink)
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
                .AddHttpClient("WaveeAudio")
                    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
                    {
                        MaxConnectionsPerServer = 10,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    })
                    .Services
                .AddSingleton<ICredentialsCache, CredentialsCache>()
                .AddSingleton(new SessionConfig { DeviceId = DeviceIdHelper.GetOrCreateDeviceId() })
                .AddSingleton(sp => Session.Create(
                    sp.GetRequiredService<SessionConfig>(),
                    sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                    sp.GetService<ILogger<Session>>()))
                .AddSingleton<ISession>(sp => sp.GetRequiredService<Session>())
                .AddSingleton<Wavee.Core.Http.IExtendedMetadataClient>(sp =>
                    new Wavee.Core.Http.ExtendedMetadataClient(
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("Wavee"),
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetService<ILogger<Wavee.Core.Http.ExtendedMetadataClient>>()))
                .AddSingleton<Wavee.Core.Library.Spotify.ISpotifyLibraryService>(sp =>
                {
                    var session = sp.GetRequiredService<ISession>();
                    return new Wavee.Core.Library.Spotify.SpotifyLibraryService(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        (Wavee.Core.Http.SpClient)session.SpClient,
                        session,
                        metadataClient: sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        logger: sp.GetService<ILogger<Wavee.Core.Library.Spotify.SpotifyLibraryService>>());
                })

                // Data services
                .AddSingleton<IDataServiceConfiguration>(new DataServiceConfiguration(startInDemoMode: false))
                .AddSingleton<ITrackLikeService>(sp =>
                    new Data.Contexts.TrackLikeService(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetService<Wavee.Core.Library.Spotify.ISpotifyLibraryService>(),
                        sp.GetService<ILogger<Data.Contexts.TrackLikeService>>()))
                .AddSingleton<ILibraryDataService>(sp =>
                    new Data.Contexts.LibraryDataService(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetRequiredService<ITrackLikeService>(),
                        sp.GetService<ILogger<Data.Contexts.LibraryDataService>>()))
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
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetService<ILogger<Data.Contexts.ArtistService>>()))
                .AddSingleton<IAlbumService>(sp =>
                    new Data.Contexts.AlbumService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<ILogger<Data.Contexts.AlbumService>>()))
                .AddSingleton<ISearchService>(sp =>
                    new Data.Contexts.SearchService(
                        sp.GetRequiredService<ISession>().Pathfinder))

                // Lyrics
                .AddSingleton<ILyricsService>(sp =>
                    new Services.LyricsService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetService<IMetadataDatabase>(),
                        sp.GetService<ISettingsService>(),
                        sp.GetService<ILogger<Services.LyricsService>>()))
                .AddSingleton<LyricsViewModel>(sp =>
                    new LyricsViewModel(
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetRequiredService<ILyricsService>(),
                        sp.GetService<ILogger<LyricsViewModel>>()))
                .AddSingleton<ITrackCreditsService>(sp =>
                    new Data.Contexts.TrackCreditsService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<IExtendedMetadataClient>(),
                        sp.GetService<ILogger<Data.Contexts.TrackCreditsService>>()))
                .AddSingleton<TrackDetailsViewModel>(sp =>
                    new TrackDetailsViewModel(
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<ITrackCreditsService>(),
                        sp.GetService<ILogger<TrackDetailsViewModel>>()))

                // ViewModels
                .AddSingleton<MainWindowViewModel>()
                .AddSingleton<ShellViewModel>()
                .AddSingleton<PlayerBarViewModel>()
                .AddTransient<HomeViewModel>(sp =>
                    new HomeViewModel(
                        sp.GetService<ISession>(),
                        sp.GetService<ISettingsService>(),
                        sp.GetService<Services.HomeFeedCache>(),
                        sp.GetService<Services.RecentlyPlayedService>(),
                        sp.GetService<ILogger<HomeViewModel>>()))
                .AddTransient<ArtistViewModel>()
                .AddTransient<AlbumViewModel>()
                .AddTransient<AlbumsLibraryViewModel>()
                .AddTransient<ArtistsLibraryViewModel>()
                .AddTransient<LikedSongsViewModel>()
                .AddTransient<PlaylistViewModel>()
                .AddTransient<CreatePlaylistViewModel>()
                .AddTransient<ProfileViewModel>(sp =>
                    new ProfileViewModel(
                        sp.GetService<Services.ProfileCache>(),
                        sp.GetService<Session>(),
                        sp.GetService<IAuthState>(),
                        sp.GetService<ILogger<ProfileViewModel>>()))
                .AddTransient<SpotifyConnectViewModel>()
                .AddTransient<SearchViewModel>(sp =>
                    new SearchViewModel(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetService<ILogger<SearchViewModel>>()))
                .AddTransient<DebugViewModel>()
                .AddTransient<FeedbackViewModel>(sp =>
                    new FeedbackViewModel(
                        sp.GetRequiredService<IFeedbackService>(),
                        sp.GetRequiredService<ISettingsService>(),
                        sp.GetRequiredService<Services.InMemorySink>(),
                        sp.GetService<ILogger<FeedbackViewModel>>()))
                .AddHttpClient<IFeedbackService, FeedbackService>(client =>
                {
                    // Cloudflare Worker proxy → creates GitHub Issues
                    client.BaseAddress = new Uri("https://wavee-feedback-proxy.christosk92.workers.dev");
                    client.DefaultRequestHeaders.Add("X-Api-Key", "CHANGE_ME_AFTER_DEPLOY");
                })
                    .Services
                .AddTransient<SettingsViewModel>(sp =>
                    new SettingsViewModel(
                        sp.GetRequiredService<ISettingsService>(),
                        sp.GetRequiredService<IThemeService>(),
                        sp.GetRequiredService<Services.InMemorySink>(),
                        sp.GetService<IAudioPipelineControl>(),
                        sp.GetService<ISession>(),
                        sp.GetRequiredService<IUpdateService>(),
                        sp.GetService<ILogger<SettingsViewModel>>()))

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
    public static void InitializePlaybackEngine(Session session, System.Net.Http.HttpClient httpClient, System.Net.Http.HttpClient audioHttpClient, Microsoft.Extensions.Logging.ILogger? logger)
    {
        try
        {
            var metadataDb = Ioc.Default.GetService<IMetadataDatabase>();
            var cacheService = Ioc.Default.GetService<Wavee.Core.Storage.ICacheService>();

            // Resolve from DI — registered in ConfigureHost, base URL resolves lazily from session
            var extMetadataClient = Ioc.Default.GetService<Wavee.Core.Http.IExtendedMetadataClient>();

            if (metadataDb != null)
                cacheService ??= new Wavee.Core.Storage.CacheService(metadataDb, logger: logger);

            // Wire metadata client into PlaybackStateManager for enriching incomplete cluster metadata
            if (extMetadataClient != null)
                session.PlaybackState?.SetMetadataClient(extMetadataClient);

            // Create TrackMetadataEnricher — communicates via IMessenger,
            // handles both track enrichment and ArtistService extended top tracks
            if (cacheService != null && extMetadataClient != null)
            {
                _trackMetadataEnricher = new Services.TrackMetadataEnricher(
                    extMetadataClient,
                    cacheService,
                    (Wavee.Core.Http.SpClient)session.SpClient,
                    Ioc.Default.GetRequiredService<IMessenger>(),
                    logger);
            }

            // Create a shared EqualizerProcessor — registered in DI during ConfigureHost,
            // resolved here to pass into the audio pipeline
            var sharedEqualizer = Ioc.Default.GetRequiredService<EqualizerProcessor>();

            // Apply persisted EQ settings so they take effect immediately (not only when Settings page opens)
            var settingsService = Ioc.Default.GetService<ISettingsService>();
            if (settingsService?.Settings is { } eqSettings)
            {
                sharedEqualizer.IsEnabled = eqSettings.EqualizerEnabled;
                sharedEqualizer.ClearBands();
                sharedEqualizer.CreateGraphicEq10Band();
                for (var i = 0; i < sharedEqualizer.Bands.Count && i < eqSettings.EqualizerBandGains.Length; i++)
                    sharedEqualizer.Bands[i].GainDb = eqSettings.EqualizerBandGains[i];
                sharedEqualizer.RefreshFilters();
            }

            var audioPipeline = AudioPipelineFactory.CreateSpotifyPipeline(
                session,
                (SpClient)session.SpClient,
                httpClient,
                options: new AudioPipelineOptions { UserEqualizer = sharedEqualizer },
                metadataDatabase: metadataDb,
                cacheService: cacheService,
                deviceId: session.Config.DeviceId,
                eventService: session.Events,
                commandHandler: session.CommandHandler,
                deviceStateManager: session.DeviceState,
                logger: logger,
                audioHttpClient: audioHttpClient);

            // Enable bidirectional mode — local state changes publish to Spotify
            session.PlaybackState?.EnableBidirectionalMode(
                audioPipeline,
                (SpClient)session.SpClient,
                session);

            // Wire up the local engine on the executor (legitimate late dep — it's the routing layer)
            var executor = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
            executor?.EnableLocalPlayback(audioPipeline);

            // Sync initial volume on the local engine
            var volumePercent = session.GetVolumePercentage() ?? 50;
            audioPipeline.SetVolumeAsync((float)(volumePercent / 100.0));

            // Sync UI volume slider (without triggering a remote command)
            var playbackState = Ioc.Default.GetService<IPlaybackStateService>();
            if (playbackState is Data.Contexts.PlaybackStateService pssVolume)
            {
                var dispatcher = _uiDispatcher;
                if (dispatcher != null)
                    dispatcher.TryEnqueue(() =>
                    {
                        pssVolume.SetVolumeWithoutCommand(volumePercent);
                    });
            }

            // Surface playback engine errors to the user via notifications
            var notificationService = Ioc.Default.GetService<INotificationService>();
            if (notificationService != null)
            {
                var dispatcher = _uiDispatcher;
                var errorSub = audioPipeline.Errors.Subscribe(error =>
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
                _appSubscriptions.Add(errorSub);
            }

            // Subscribe AudioPipeline to session connection state for buffering indicator
            audioPipeline.SubscribeToConnectionState(session.ConnectionState);

            // Show persistent notification during AP reconnection/disconnection
            if (notificationService != null)
            {
                var dispatcher2 = _uiDispatcher;
                var sessionRef = session;
                var connSub = session.ConnectionState.Subscribe(state =>
                {
                    dispatcher2?.TryEnqueue(() =>
                    {
                        if (state == Wavee.Core.Session.SessionConnectionState.Reconnecting)
                        {
                            notificationService.Show(new Data.Models.NotificationInfo
                            {
                                Message = "Reconnecting to Spotify...",
                                Severity = Data.Models.NotificationSeverity.Warning
                            });
                        }
                        else if (state == Wavee.Core.Session.SessionConnectionState.Disconnected)
                        {
                            notificationService.Show(new Data.Models.NotificationInfo
                            {
                                Message = "Unable to reach Spotify. Check your network connection.",
                                Severity = Data.Models.NotificationSeverity.Error,
                                ActionLabel = "Retry",
                                Action = async () =>
                                {
                                    try
                                    {
                                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                        await sessionRef.ReconnectApAsync(cts.Token);
                                    }
                                    catch (Exception ex)
                                    {
                                        logger?.LogWarning(ex, "Manual reconnection attempt failed");
                                    }
                                }
                            });
                        }
                        else
                        {
                            // Connected — dismiss any reconnection notification
                            notificationService.Dismiss();
                        }
                    });
                });
                _appSubscriptions.Add(connSub);
            }

            logger?.LogInformation("AudioPipeline initialized — local playback enabled");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to initialize AudioPipeline — local playback unavailable");
        }
    }

    /// <summary>
    /// Tears down the playback engine and associated resources.
    /// Call on logout to avoid leaking AudioPipeline and subscriptions on re-login.
    /// </summary>
    public static void TeardownPlaybackEngine()
    {
        // Dispose app-level subscriptions (error stream, connection state notifications)
        foreach (var sub in _appSubscriptions) sub.Dispose();
        _appSubscriptions.Clear();

        // Clear engine from executor
        var executor = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
        executor?.DisableLocalPlayback();

        // Dispose the enricher (unregisters from messenger)
        _trackMetadataEnricher?.Dispose();
        _trackMetadataEnricher = null;
    }

    public static void HandleAppUnhandledException(Exception? ex, bool showNotification)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {ex}");
    }
}
