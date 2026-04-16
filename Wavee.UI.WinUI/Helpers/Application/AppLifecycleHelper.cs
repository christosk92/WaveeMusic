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
using Wavee.Audio;
using Wavee.Core.Authentication;
using Wavee.Core.DependencyInjection;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contexts;
// Processors now live in AudioHost — EQ config goes via IPC
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Data;
using Wavee.UI.WinUI.ViewModels;
using System.Collections.Generic;
using Serilog;
using Serilog.Extensions.Logging;
using Wavee.Controls.Lyrics.Services.LocalizationService;
using Wavee.UI.Contracts;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Services.Data;

namespace Wavee.UI.WinUI.Helpers.Application;

public static class AppLifecycleHelper
{
    private static readonly List<IDisposable> _appSubscriptions = [];
    private static TrackMetadataEnricher? _trackMetadataEnricher;
    private static readonly SemaphoreSlim _playbackTeardownGate = new(1, 1);
    // Captured on UI thread during ConfigureHost, used by background init
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    // Out-of-process audio mode
    private static Wavee.AudioIpc.AudioProcessManager? _audioProcessManager;

    // Held so we can -= on teardown. Without these the lambdas would still be
    // collectible once _audioProcessManager is nulled, but explicit unsubscribe
    // also stops handlers from firing during the dispose race window.
    private static Action<Wavee.AudioIpc.AudioProcessState, string>? _audioStateChangedHandler;
    private static Action<Wavee.AudioIpc.AudioPipelineProxy>? _audioProxyRestartedHandler;

    /// <summary>
    /// The active audio process manager (null if using in-process audio).
    /// Exposed for diagnostics UI.
    /// </summary>
    public static Wavee.AudioIpc.AudioProcessManager? AudioProcessManager => _audioProcessManager;

    /// <summary>
    /// Set to true to use a separate audio process for GC isolation.
    /// When false (default), the AudioPipeline runs in-process as before.
    /// </summary>
    public static bool UseOutOfProcessAudio { get; set; } = true;

    public static IHost ConfigureHost()
    {
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
#if DEBUG
        const Serilog.Events.LogEventLevel appMinimumLogLevel = Serilog.Events.LogEventLevel.Debug;
        const Serilog.Events.LogEventLevel inMemoryMinimumLogLevel = Serilog.Events.LogEventLevel.Debug;
        const LogLevel hostMinimumLogLevel = LogLevel.Debug;
#else
        const Serilog.Events.LogEventLevel appMinimumLogLevel = Serilog.Events.LogEventLevel.Information;
        const Serilog.Events.LogEventLevel inMemoryMinimumLogLevel = Serilog.Events.LogEventLevel.Warning;
        const LogLevel hostMinimumLogLevel = LogLevel.Information;
#endif

        var rxuiInstance = RxAppBuilder.CreateReactiveUIBuilder()
            .WithWinUI() // Register WinUI platform services
            .WithViewsFromAssembly(typeof(App).Assembly) // Register views and view models
            .BuildApp();

        // Create the InMemorySink early so Serilog can write to it from the start
        var inMemorySink = new InMemorySink(_uiDispatcher);
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(appMinimumLogLevel)
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
                restrictedToMinimumLevel: appMinimumLogLevel)
            .WriteTo.Sink(inMemorySink, restrictedToMinimumLevel: inMemoryMinimumLogLevel)
            .Enrich.FromLogContext()
            .CreateLogger();

        // Read the caching profile BEFORE the DI container is built. Cache services
        // are singletons constructed at container build time, so we need their capacities
        // available now — we can't resolve ISettingsService yet because it's itself
        // about to be registered. PeekCachingProfile() does a minimal JSON file read
        // and falls back to Medium on any failure.
        var cachingProfile = SettingsService.PeekCachingProfile();
        var cacheCapacities = CachingProfilePresets.Get(cachingProfile);
        Log.Information(
            "Caching profile: {Profile} (estimated ~{EstMb} MB in caches)",
            cachingProfile, CachingProfilePresets.EstimateMegabytes(cacheCapacities));

        var spotifyMetadataLocale = SpotifyMetadataLanguageSettings.ResolveEffectiveLocale(
            SettingsService.PeekSpotifyMetadataLanguage());

        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging
                .ClearProviders()
                .AddSerilog(Log.Logger, dispose: false)
                .SetMinimumLevel(hostMinimumLogLevel))
            .ConfigureServices(services => services
                // Wavee Core services — capacities driven by the caching profile
                .AddWaveeCache(opts =>
                {
                    opts.TrackHotCacheSize = cacheCapacities.TrackHotCacheSize;
                    opts.AlbumHotCacheSize = cacheCapacities.AlbumHotCacheSize;
                    opts.ArtistHotCacheSize = cacheCapacities.ArtistHotCacheSize;
                    opts.PlaylistHotCacheSize = cacheCapacities.PlaylistHotCacheSize;
                    opts.ShowHotCacheSize = cacheCapacities.ShowHotCacheSize;
                    opts.EpisodeHotCacheSize = cacheCapacities.EpisodeHotCacheSize;
                    opts.UserHotCacheSize = cacheCapacities.UserHotCacheSize;
                    opts.ContextCacheSize = cacheCapacities.ContextCacheSize;
                    opts.DatabaseHotCacheSize = cacheCapacities.DatabaseHotCacheSize;
                    opts.AudioAuxCacheSize = cacheCapacities.AudioAuxCacheSize;
                    opts.SpotifyMetadataLocale = spotifyMetadataLocale;
                })

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
                        sp.GetService<IHomeFeedCache>()))
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

                // EQ processor now lives in AudioHost — settings sent via IPC

                // Dispatcher abstraction
                .AddSingleton<IDispatcherService>(sp =>
                    new DispatcherService(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))

                // App services
                .AddSingleton<ILocalizationService, LocalizationService>()
                .AddSingleton<IAppLocalizationService, AppLocalizationService>()
                .AddSingleton<ISettingsService, SettingsService>()
                .AddSingleton<IShellSessionService, ShellSessionService>()
                .AddSingleton<IMediaOverrideService, MediaOverrideService>()
                .AddSingleton<IThemeService, ThemeService>()
                .AddSingleton<ThemeColorService>()
                .AddSingleton<HomeResponseParserFactory>()
                .AddSingleton<HomeFeedCache>()
                .AddSingleton<IHomeFeedCache>(sp => sp.GetRequiredService<HomeFeedCache>())
                .AddSingleton<RecentlyPlayedService>(sp =>
                    new RecentlyPlayedService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetRequiredService<IMessenger>(),
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                        sp.GetService<ILogger<RecentlyPlayedService>>()))
                .AddSingleton<ProfileCache>()
                .AddSingleton<IProfileCache>(sp => sp.GetRequiredService<ProfileCache>())
                .AddSingleton(sp => new ImageCacheService(cacheCapacities.ImageCacheMaxSize))
                .AddSingleton<IPreviewAudioPlaybackEngine, PreviewAudioGraphService>()
                .AddSingleton<PreviewAudioGraphService>(sp => (PreviewAudioGraphService)sp.GetRequiredService<IPreviewAudioPlaybackEngine>())
                // IUiDispatcher abstraction: lets services in the plain-C# Wavee.UI library marshal
                // callbacks onto the UI thread without depending on Microsoft.UI.Dispatching.
                .AddSingleton<Wavee.UI.Threading.IUiDispatcher>(sp =>
                    new DispatcherQueueUiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))
                .AddSingleton<ICardPreviewPlaybackCoordinator, CardPreviewPlaybackCoordinator>()
                .AddSingleton<ISharedCardCanvasPreviewService, SharedCardCanvasPreviewService>()
                // Shared now-playing highlight observer. Subscribes to NowPlayingChangedMessage
                // once; ContentCard instances subscribe to its C# event instead of registering
                // individually with WeakReferenceMessenger. Big savings during HomePage realization.
                .AddSingleton(sp => new NowPlayingHighlightService(sp.GetRequiredService<IMessenger>()))
                .AddSingleton(sp =>
                {
                    var profiler = new UiOperationProfiler(
                        sp.GetService<ILogger<UiOperationProfiler>>());
                    UiOperationProfiler.Instance = profiler;
                    return profiler;
                })
                .AddSingleton(inMemorySink)
                .AddSingleton<Wavee.Core.Http.IColorService>(sp =>
                    new Wavee.Core.Http.ExtractedColorService(
                        sp.GetRequiredService<Wavee.Core.Session.ISession>().Pathfinder,
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<ILogger<Wavee.Core.Http.ExtractedColorService>>()))
                // UI-oriented batched color-hint service for virtualized track rows.
                // Wraps IColorService with request dedupe + debounce-window batching so
                // scroll bursts across hundreds of tracks coalesce into a few backend calls.
                .AddSingleton<Wavee.UI.Services.ITrackColorHintService>(sp =>
                    new Wavee.UI.Services.TrackColorHintService(
                        sp.GetRequiredService<Wavee.Core.Http.IColorService>(),
                        logger: sp.GetService<ILogger<Wavee.UI.Services.TrackColorHintService>>()))

                // Spotify session infrastructure
                .AddTransient<RetryHandler>()
                .AddHttpClient("Wavee")
                    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
                    {
                        // Enables Accept-Encoding: gzip, deflate, br on all outgoing requests
                        // and transparent decompression of responses.
                        AutomaticDecompression = System.Net.DecompressionMethods.All,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    })
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
                .AddSingleton(new SessionConfig
                {
                    DeviceId = DeviceIdHelper.GetOrCreateDeviceId(),
                    PreferredLocale = spotifyMetadataLocale
                })
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
                .AddTransient<TrackMetadataEnricher>(sp =>
                    new TrackMetadataEnricher(
                        sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        sp.GetRequiredService<Wavee.Core.Storage.ICacheService>(),
                        sp.GetRequiredService<ISession>().SpClient,
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetService<ILogger<TrackMetadataEnricher>>()))
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
                        sp.GetRequiredService<ISession>(),
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
                        sp.GetRequiredService<Wavee.Core.Http.IColorService>(),
                        sp.GetRequiredService<ILocationService>(),
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetService<ILogger<Data.Contexts.ArtistService>>()))
                .AddSingleton<IAlbumService>(sp =>
                    new Data.Contexts.AlbumService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<ILogger<Data.Contexts.AlbumService>>(),
                        cacheCapacities.AlbumTracksHotCacheCapacity))
                .AddSingleton<ISearchService>(sp =>
                    new Data.Contexts.SearchService(
                        sp.GetRequiredService<ISession>().Pathfinder))
                .AddSingleton<ITrackDescriptorFetcher>(sp =>
                    new Data.Contexts.TrackDescriptorFetcher(
                        sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<ILogger<Data.Contexts.TrackDescriptorFetcher>>()))

                // Lyrics
                .AddSingleton<ILyricsService>(sp =>
                    new LyricsService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetService<IMetadataDatabase>(),
                        sp.GetService<ISettingsService>(),
                        sp.GetService<ILogger<LyricsService>>(),
                        cacheCapacities.LyricsMemoryCacheCapacity))
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
                        sp.GetRequiredService<IMediaOverrideService>(),
                        sp.GetService<ILogger<TrackDetailsViewModel>>()))

                // ViewModels
                .AddSingleton<MainWindowViewModel>()
                .AddSingleton<ShellViewModel>()
                .AddSingleton<PlayerBarViewModel>(sp =>
                    new PlayerBarViewModel(
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetService<IConnectivityService>(),
                        sp.GetService<ILoggerFactory>()))
                .AddTransient<HomeViewModel>(sp =>
                    new HomeViewModel(
                        sp.GetService<ISession>(),
                        sp.GetService<ISettingsService>(),
                        sp.GetService<HomeFeedCache>(),
                        sp.GetService<RecentlyPlayedService>(),
                        sp.GetService<HomeResponseParserFactory>(),
                        sp.GetService<ILogger<HomeViewModel>>()))
                .AddTransient<ArtistViewModel>()
                .AddTransient<AlbumViewModel>()
                .AddTransient<LibraryPageViewModel>()
                .AddTransient<AlbumsLibraryViewModel>()
                .AddTransient<ArtistsLibraryViewModel>()
                .AddTransient<LikedSongsViewModel>()
                .AddTransient<PlaylistViewModel>()
                .AddTransient<CreatePlaylistViewModel>()
                .AddTransient<ProfileViewModel>(sp =>
                    new ProfileViewModel(
                        sp.GetService<ProfileCache>(),
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
                        sp.GetRequiredService<InMemorySink>(),
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
                        sp.GetRequiredService<InMemorySink>(),
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
    // In-process playback was removed — all audio goes through AudioHost process via IPC.
    // See InitializeOutOfProcessAudioAsync.

    /// <summary>
    /// Initializes playback using a separate audio host process for GC isolation.
    /// The audio process owns the AudioPipeline, PortAudioSink, and its own Session.
    /// UI communicates via Named Pipes IPC.
    /// </summary>
    public static async Task InitializeOutOfProcessAudioAsync(
        Session session,
        Microsoft.Extensions.Logging.ILogger? logger)
    {
        try
        {
            InitializeTrackMetadataEnricher(session, logger);

            _audioProcessManager = new Wavee.AudioIpc.AudioProcessManager(logger);

            // Load stored credentials to pass to the audio process
            var username = session.GetUserData()?.Username;
            var credCache = Ioc.Default.GetService<ICredentialsCache>();
            var creds = credCache != null && username != null
                ? await credCache.LoadCredentialsAsync(username, CancellationToken.None)
                : null;
            if (creds == null || creds.AuthData.Length == 0 || username == null)
            {
                logger?.LogError("No cached credentials for user {User} — cannot start audio process", username);
                return;
            }

            // Wire up state change notifications BEFORE starting (so failures are visible)
            var profiler = UiOperationProfiler.Instance;
            var notifDispatcher = _uiDispatcher;
            Guid? audioActivityId = null;

            _audioStateChangedHandler = (state, message) =>
            {
                logger?.LogInformation("Audio process: {State} — {Message}", state, message);
                notifDispatcher?.TryEnqueue(() =>
                {
                    var notifService = Ioc.Default.GetService<INotificationService>();
                    var actSvc = Ioc.Default.GetService<Data.Contracts.IActivityService>();

                    switch (state)
                    {
                        case Wavee.AudioIpc.AudioProcessState.Connected:
                            notifService?.Dismiss();
                            if (audioActivityId != null)
                                actSvc?.Complete(audioActivityId.Value, AppLocalization.GetString("AudioHost_Connected"));
                            else
                                actSvc?.Post("playback", AppLocalization.GetString("AudioHost_Connected"),
                                    "\uE768", Data.Models.ActivityStatus.Completed,
                                    $"PID {_audioProcessManager?.ProcessId}", silent: true);
                            audioActivityId = null;
                            break;

                        case Wavee.AudioIpc.AudioProcessState.Reconnecting:
                            notifService?.Show(new Data.Models.NotificationInfo
                            {
                                Message = message,
                                Severity = Data.Models.NotificationSeverity.Warning,
                            });
                            audioActivityId ??= actSvc?.Start("playback", "Audio engine reconnecting", "\uE9CE");
                            actSvc?.Update(audioActivityId ?? Guid.Empty, message);
                            break;

                        case Wavee.AudioIpc.AudioProcessState.Failed:
                            notifService?.Show(new Data.Models.NotificationInfo
                            {
                                Message = message,
                                Severity = Data.Models.NotificationSeverity.Error,
                                ActionLabel = AppLocalization.GetString("Retry"),
                                Action = async () =>
                                {
                                    if (_audioProcessManager != null)
                                    {
                                        await _audioProcessManager.StopAsync();
                                        await InitializeOutOfProcessAudioAsync(session, logger);
                                    }
                                }
                            });
                            if (audioActivityId != null)
                                actSvc?.Fail(audioActivityId.Value, message);
                            else
                                actSvc?.Post("playback", AppLocalization.GetString("AudioHost_Failed"), "\uE783",
                                    Data.Models.ActivityStatus.Failed, message);
                            audioActivityId = null;
                            break;
                    }
                });
            };
            _audioProcessManager.StateChanged += _audioStateChangedHandler;

            // Audio cache directory: shared between this process (to check HasCompleteFile)
            // and AudioHost (to write new downloads and read cached files).
            var audioCacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wavee", "AudioCache");

            // Now start the audio process (state/error events are already wired above)
            var clusterVol = (int)(session.PlaybackState?.CurrentState?.Volume ?? 0);
            logger?.LogInformation("Starting audio process for user {User}, initialVolume={Vol}%", username, clusterVol);
            var proxy = await _audioProcessManager.StartAsync(
                username,
                creds.AuthData,
                session.Config.DeviceId,
                initialVolumePercent: clusterVol,
                audioCacheDirectory: audioCacheDirectory,
                CancellationToken.None);

            // Create PlaybackOrchestrator — owns queue, track resolution, remote commands
            var spClient = (SpClient)session.SpClient;
            var httpClient = Ioc.Default.GetService<System.Net.Http.IHttpClientFactory>()?.CreateClient("Wavee")
                             ?? new System.Net.Http.HttpClient();
            var extMetadataClient = Ioc.Default.GetService<Wavee.Core.Http.IExtendedMetadataClient>();
            var metadataDb = Ioc.Default.GetService<IMetadataDatabase>();
            var cacheService = Ioc.Default.GetService<Wavee.Core.Storage.ICacheService>();

            var headFileClient = new Wavee.Core.Audio.HeadFileClient(httpClient, logger);
            var trackResolver = new Wavee.Audio.TrackResolver(
                session, spClient, headFileClient, httpClient,
                Wavee.Core.Audio.AudioQuality.VeryHigh,
                extMetadataClient, cacheService, logger,
                audioCacheDirectory: audioCacheDirectory);

            Wavee.Audio.ContextResolver? contextResolver = null;
            if (metadataDb != null && extMetadataClient != null && cacheService != null)
            {
                var contextCache = new Wavee.Core.Storage.HotCache<Wavee.Core.Storage.ContextCacheEntry>(256);
                contextResolver = new Wavee.Audio.ContextResolver(
                    spClient, extMetadataClient, cacheService, contextCache, logger);
            }

            var orchestrator = new Wavee.Audio.PlaybackOrchestrator(
                proxy, trackResolver, contextResolver!, session.CommandHandler, logger);

            // Wire up orchestrator (not raw proxy) as the local engine
            var executor = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
            executor?.EnableLocalPlayback(orchestrator);

            // Bidirectional mode uses orchestrator's queue-enriched state stream
            session.PlaybackState?.EnableBidirectionalMode(
                orchestrator,
                spClient,
                session,
                suppressClusterUpdates: false);

            profiler?.SetAudioUnderrunProvider(() => proxy.UnderrunCount);

            // Re-wire on auto-restart (variables are now in scope for the closure)
            _audioProxyRestartedHandler = newProxy =>
            {
                var notifDisp = _uiDispatcher;
                notifDisp?.TryEnqueue(() =>
                {
                    var newOrch = new Wavee.Audio.PlaybackOrchestrator(
                        newProxy, trackResolver, contextResolver!, session.CommandHandler, logger);
                    var exec = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
                    exec?.EnableLocalPlayback(newOrch);
                    session.PlaybackState?.EnableBidirectionalMode(
                        newOrch, spClient, session, suppressClusterUpdates: false);
                    profiler?.SetAudioUnderrunProvider(() => newProxy.UnderrunCount);
                });
            };
            _audioProcessManager.ProxyRestarted += _audioProxyRestartedHandler;

            // Surface errors via notifications and activity feed
            var notificationService = Ioc.Default.GetService<INotificationService>();
            var activityService = Ioc.Default.GetService<IActivityService>();
            {
                var dispatcher = _uiDispatcher;
                var errorSub = proxy.Errors.Subscribe(error =>
                {
                    var message = error.ErrorType switch
                    {
                        PlaybackErrorType.AudioDeviceUnavailable => error.Message,
                        PlaybackErrorType.TrackUnavailable => "Track unavailable (Premium required?).",
                        PlaybackErrorType.NetworkError => "Network error during playback.",
                        _ => error.Message
                    };
                    var (title, iconGlyph) = error.ErrorType switch
                    {
                        PlaybackErrorType.AudioDeviceUnavailable => ("Audio device error", "\uE7F3"),
                        PlaybackErrorType.TrackUnavailable => ("Track unavailable", "\uE774"),
                        PlaybackErrorType.NetworkError => ("Network error", "\uE774"),
                        _ => ("Playback error", "\uE783")
                    };
                    dispatcher?.TryEnqueue(() =>
                    {
                        notificationService?.Show(new Data.Models.NotificationInfo
                        {
                            Message = message,
                            Severity = Data.Models.NotificationSeverity.Error,
                            AutoDismissAfter = TimeSpan.FromSeconds(5)
                        });
                        activityService?.Post(
                            category: "playback",
                            title: title,
                            iconGlyph: iconGlyph,
                            status: Data.Models.ActivityStatus.Failed,
                            message: message);
                    });
                });
                _appSubscriptions.Add(errorSub);
            }

            logger?.LogInformation("Out-of-process audio initialized — PID isolation active");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to initialize out-of-process audio — falling back to in-process");
            // Clean up
            if (_audioProcessManager != null)
            {
                await _audioProcessManager.DisposeAsync();
                _audioProcessManager = null;
            }

            // In-process fallback was removed — all audio goes through AudioHost
            logger?.LogError("Out-of-process audio failed. No fallback available.");
        }
    }

    /// <summary>
    /// Tears down the playback engine and associated resources.
    /// Call on logout to avoid leaking AudioPipeline and subscriptions on re-login.
    /// </summary>
    public static void TeardownPlaybackEngine()
        => _ = TeardownPlaybackEngineAsync();

    /// <summary>
    /// Tears down the playback engine and associated resources.
    /// Await this on app shutdown so background audio/process work finishes before XAML teardown.
    /// </summary>
    public static Task TeardownPlaybackEngineAsync()
        => TeardownPlaybackEngineCoreAsync();

    private static async Task TeardownPlaybackEngineCoreAsync()
    {
        await _playbackTeardownGate.WaitAsync().ConfigureAwait(false);
        try
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

            // Stop audio host process if running
            if (_audioProcessManager != null)
            {
                if (_audioStateChangedHandler != null)
                {
                    _audioProcessManager.StateChanged -= _audioStateChangedHandler;
                    _audioStateChangedHandler = null;
                }
                if (_audioProxyRestartedHandler != null)
                {
                    _audioProcessManager.ProxyRestarted -= _audioProxyRestartedHandler;
                    _audioProxyRestartedHandler = null;
                }
                await _audioProcessManager.DisposeAsync().ConfigureAwait(false);
                _audioProcessManager = null;
            }
        }
        finally
        {
            _playbackTeardownGate.Release();
        }
    }

    public static void HandleAppUnhandledException(Exception? ex, bool showNotification)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {ex}");
    }

    private static void InitializeTrackMetadataEnricher(Session session, Microsoft.Extensions.Logging.ILogger? logger)
    {
        try
        {
            // Wire metadata client into PlaybackStateManager for enriching incomplete cluster metadata.
            var extMetadataClient = Ioc.Default.GetService<Wavee.Core.Http.IExtendedMetadataClient>();
            if (extMetadataClient != null)
                session.PlaybackState?.SetMetadataClient(extMetadataClient);

            // Resolve a fresh enricher instance from DI (transient) and keep it for this session.
            _trackMetadataEnricher?.Dispose();
            _trackMetadataEnricher = Ioc.Default.GetService<TrackMetadataEnricher>();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to initialize track metadata enricher");
        }
    }
}
