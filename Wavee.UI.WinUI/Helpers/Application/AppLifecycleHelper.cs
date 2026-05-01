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
using Wavee.UI.WinUI.Services.Docking;
using System.Collections.Generic;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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
    /// Serilog level switch driving the file + in-memory sinks. Flipped at runtime by the
    /// "Verbose logging" toggle in the Diagnostics settings — no app restart required.
    /// Initialised in <see cref="ConfigureHost"/>.
    /// </summary>
    public static LoggingLevelSwitch LogLevelSwitch { get; } = new(Serilog.Events.LogEventLevel.Information);

    private const string DrmLogChannel = "DRM";

    /// <summary>
    /// Apply a verbose-logging change at runtime. Flips the Serilog level switch and tells
    /// the audio process to do the same on its next restart (live forwarding could be added
    /// over IPC later).
    /// </summary>
    public static void SetVerboseLogging(bool enabled)
    {
        LogLevelSwitch.MinimumLevel = enabled
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
        Wavee.AudioIpc.AudioProcessManager.UseVerboseLogging = enabled;
        Log.Information("Verbose logging {State}", enabled ? "ENABLED" : "DISABLED");
    }

    // ── Memory diagnostics periodic logger ────────────────────────────────
    //
    // While the user has the in-app Memory diagnostics panel turned on, we also
    // emit a structured log line every 30 s so a leak hunt can be done from logs
    // alone (without keeping the panel visible). Enable/disable is driven by
    // SettingsViewModel.MemoryDiagnosticsEnabled.

    private static bool IsMainLogEvent(LogEvent logEvent, LogEventLevel noisyOverride)
    {
        if (IsDrmLogEvent(logEvent))
            return false;

        if (IsNoisyFrameworkEvent(logEvent) && logEvent.Level < noisyOverride)
            return false;

        return logEvent.Level >= LogLevelSwitch.MinimumLevel;
    }

    private static bool IsDrmLogEvent(LogEvent logEvent)
    {
        if (TryGetStringProperty(logEvent, "LogChannel", out var channel)
            && string.Equals(channel, DrmLogChannel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetStringProperty(logEvent, "SourceContext", out var sourceContext)
            && sourceContext.Contains("SpotifyVideoProvider", StringComparison.Ordinal))
        {
            return true;
        }

        return logEvent.MessageTemplate.Text.StartsWith("[DRM]", StringComparison.Ordinal);
    }

    private static bool IsNoisyFrameworkEvent(LogEvent logEvent)
    {
        if (!TryGetStringProperty(logEvent, "SourceContext", out var sourceContext))
            return false;

        return sourceContext == "Microsoft"
               || sourceContext == "System"
               || sourceContext.StartsWith("Microsoft.", StringComparison.Ordinal)
               || sourceContext.StartsWith("System.", StringComparison.Ordinal);
    }

    private static bool TryGetStringProperty(LogEvent logEvent, string propertyName, out string value)
    {
        value = "";
        if (!logEvent.Properties.TryGetValue(propertyName, out var property))
            return false;

        if (property is ScalarValue { Value: string scalarValue })
        {
            value = scalarValue;
            return !string.IsNullOrWhiteSpace(value);
        }

        value = property.ToString().Trim('"');
        return !string.IsNullOrWhiteSpace(value);
    }

    private static CancellationTokenSource? _memoryDiagCts;
    private static Task? _memoryDiagTask;
    private static readonly TimeSpan MemoryDiagnosticsInterval = TimeSpan.FromSeconds(30);

    public static void SetMemoryDiagnostics(bool enabled)
    {
        if (enabled)
        {
            if (_memoryDiagTask != null) return;
            _memoryDiagCts = new CancellationTokenSource();
            _memoryDiagTask = RunMemoryDiagnosticsLoopAsync(_memoryDiagCts.Token);
            Log.Information("Memory diagnostics ENABLED — sampling every {Interval}", MemoryDiagnosticsInterval);
        }
        else
        {
            var cts = _memoryDiagCts;
            _memoryDiagCts = null;
            _memoryDiagTask = null;
            try { cts?.Cancel(); cts?.Dispose(); } catch { /* best-effort */ }
            Log.Information("Memory diagnostics DISABLED");
        }
    }

    private static async Task RunMemoryDiagnosticsLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(MemoryDiagnosticsInterval);
            // Resolve lazily — Ioc.Default may not be configured when the loop starts
            // very early in launch. The first iteration tolerates a null service.
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var svc = Ioc.Default.GetService<Diagnostics.MemoryDiagnosticsService>();
                    if (svc == null) continue;
                    await svc.WriteSnapshotCsvAsync("periodic", ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Memory diagnostics periodic sample failed");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    /// <summary>
    /// Set to true to use a separate audio process for GC isolation.
    /// When false (default), the AudioPipeline runs in-process as before.
    /// </summary>
    public static bool UseOutOfProcessAudio { get; set; } = true;

    public static IHost ConfigureHost()
    {
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Initial switch level: Verbose if user opted in (or DEBUG build), otherwise Information.
        // The switch is mutable at runtime — see SetVerboseLogging.
        var verboseEnabled = SettingsService.PeekVerboseLogging();
#if DEBUG
        LogLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
#else
        LogLevelSwitch.MinimumLevel = verboseEnabled
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
#endif
        // Microsoft/System are noisy at Information; keep them at Warning unless verbose mode is on.
        var noisyOverride = verboseEnabled
            ? Serilog.Events.LogEventLevel.Information
            : Serilog.Events.LogEventLevel.Warning;
        // Microsoft.Extensions.Logging passes EVERYTHING to Serilog; Serilog's own switch is the gate.
        const LogLevel hostMinimumLogLevel = LogLevel.Trace;

        // Propagate to the audio process so its CLI flag matches at first launch.
        Wavee.AudioIpc.AudioProcessManager.UseVerboseLogging = verboseEnabled;

        var rxuiInstance = RxAppBuilder.CreateReactiveUIBuilder()
            .WithWinUI() // Register WinUI platform services
            .WithViewsFromAssembly(typeof(App).Assembly) // Register views and view models
            .BuildApp();

        // Create the InMemorySink early so Serilog can write to it from the start
        var inMemorySink = new InMemorySink(_uiDispatcher);
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        // Output template includes SourceContext so log files identify the originating class —
        // a small but huge readability win for production debugging.
        const string fileOutputTemplate =
            "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
        const string drmOutputTemplate =
            "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Logger(main => main
                .Filter.ByIncludingOnly(logEvent => IsMainLogEvent(logEvent, noisyOverride))
                .WriteTo.Debug()
                .WriteTo.File(
                    path: AppPaths.RollingLogFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: fileOutputTemplate)
                .WriteTo.Sink(inMemorySink))
            .WriteTo.Logger(drm => drm
                .Filter.ByIncludingOnly(IsDrmLogEvent)
                .Filter.ByIncludingOnly(logEvent => logEvent.Level >= LogEventLevel.Debug)
                .WriteTo.File(
                    path: AppPaths.DrmRollingLogFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: drmOutputTemplate))
            .Enrich.FromLogContext()
            .CreateLogger();

        Log.Information("Logger initialised — minLevel={Level}, verbose={Verbose}",
            LogLevelSwitch.MinimumLevel, verboseEnabled);

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
                // Per-session in-memory cache for music-video metadata. Fed
                // by GraphQL response handlers on artist / album / search
                // surfaces; consumed by the discovery service to avoid
                // redundant NPV roundtrips.
                .AddSingleton<Services.IMusicVideoCatalogCache, Services.MusicVideoCatalogCache>()
                .AddSingleton<Services.IMusicVideoMetadataService>(sp =>
                    new Services.MusicVideoMetadataService(
                        sp.GetRequiredService<Data.Stores.ExtendedMetadataStore>(),
                        sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetRequiredService<Services.IMusicVideoCatalogCache>(),
                        sp.GetService<ILogger<Services.MusicVideoMetadataService>>()))
                // Music-video discovery for the linked-URI catalog pattern
                // (audio URI ≠ video URI; e.g. drunk text). Invoked directly
                // by PlaybackStateService.OnCurrentTrackIdChanged when the
                // catalog cache returns no hint; runs NPV in background and
                // publishes MusicVideoAvailabilityMessage on completion.
                // Lazily resolves IPlaybackStateService via Ioc.Default to
                // break the construction cycle.
                .AddSingleton<Services.IMusicVideoDiscoveryService>(sp =>
                    new Services.MusicVideoDiscoveryService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetRequiredService<Services.IMusicVideoMetadataService>(),
                        sp.GetService<ILogger<Services.MusicVideoDiscoveryService>>()))
                // UI-process MediaPlayer used as the engine for video tracks.
                // Must be resolved on the UI thread (its ctor captures the
                // dispatcher); the orchestrator hands it Play/Pause/Seek calls
                // when a wavee:local:track:* URI is flagged as IsVideo.
                //
                // Registered as a concrete singleton so the SAME instance is
                // forwarded to ILocalMediaPlayer (for the orchestrator's
                // playback routing) and IVideoSurfaceProvider (for the UI's
                // active-surface arbitration). A future Spotify video engine
                // is registered the same way — concrete singleton + a
                // forwarded IVideoSurfaceProvider — and the rest of the UI
                // keeps working without code changes.
                .AddSingleton<Services.LocalMediaPlayer>(sp =>
                    new Services.LocalMediaPlayer(
                        sp.GetService<ILogger<Services.LocalMediaPlayer>>()))
                .AddSingleton<Wavee.Audio.ILocalMediaPlayer>(sp =>
                    sp.GetRequiredService<Services.LocalMediaPlayer>())
                .AddSingleton<Services.IVideoSurfaceProvider>(sp =>
                    sp.GetRequiredService<Services.LocalMediaPlayer>())
                // Spotify music-video engine — registered as a concrete singleton
                // then forwarded to ISpotifyVideoPlayback (for the orchestrator)
                // and as a second IVideoSurfaceProvider (for the surface service).
                .AddSingleton<Services.SpotifyVideoProvider>(sp =>
                    new Services.SpotifyVideoProvider(
                        sp.GetRequiredService<ISession>().SpClient,
                        sp.GetService<ILogger<Services.SpotifyVideoProvider>>()))
                .AddSingleton<Wavee.Audio.ISpotifyVideoPlayback>(sp =>
                    sp.GetRequiredService<Services.SpotifyVideoProvider>())
                .AddSingleton<Services.ISpotifyVideoPlaybackDetails>(sp =>
                    sp.GetRequiredService<Services.SpotifyVideoProvider>())
                .AddSingleton<Services.IVideoSurfaceProvider>(sp =>
                    sp.GetRequiredService<Services.SpotifyVideoProvider>())
                .AddSingleton<Services.IActiveVideoSurfaceService>(sp =>
                {
                    var svc = new Services.ActiveVideoSurfaceService(
                        sp.GetService<ILogger<Services.ActiveVideoSurfaceService>>());
                    foreach (var provider in sp.GetServices<Services.IVideoSurfaceProvider>())
                    {
                        svc.RegisterProvider(provider);
                    }
                    return svc;
                })
                // Windows-shell video frame thumbnail provider for the local
                // scanner. Registered as IVideoThumbnailExtractor so the
                // scanner DI in Wavee.Core picks it up without needing a
                // direct reference to Windows.Storage in the core project.
                .AddSingleton<Wavee.Core.Library.Local.IVideoThumbnailExtractor>(sp =>
                    new Services.WindowsVideoThumbnailExtractor(
                        sp.GetService<ILogger<Services.WindowsVideoThumbnailExtractor>>()))
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
                        sp.GetService<IAuthState>(),
                        sp.GetService<ILogger<Data.Contexts.LibrarySyncOrchestrator>>()))
                .AddSingleton<IActivityService, Data.Contexts.ActivityService>()
                .AddSingleton<IFriendsFeedService, Data.Contexts.FriendsFeedService>()

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
                .AddSingleton<Services.Docking.IPanelDockingService, Services.Docking.PanelDockingService>()
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
                .AddSingleton<LibraryRecentsService>(sp =>
                    new LibraryRecentsService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<IMessenger>(),
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                        sp.GetService<ILogger<LibraryRecentsService>>()))
                .AddSingleton<ProfileCache>()
                .AddSingleton<IProfileCache>(sp => sp.GetRequiredService<ProfileCache>())
                .AddSingleton<ProfileService>()
                .AddSingleton<IUserProfileResolver, UserProfileResolver>()
                .AddSingleton(sp => new ImageCacheService(cacheCapacities.ImageCacheMaxSize))
                .AddSingleton(sp => new PlaylistMosaicService(
                    sp.GetRequiredService<ILibraryDataService>(),
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                    sp.GetService<ILogger<PlaylistMosaicService>>()))
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
                        sp.GetService<Wavee.Core.Library.Local.ILocalLikeService>(),
                        sp.GetService<ILogger<Data.Contexts.TrackLikeService>>()))
                .AddSingleton<Wavee.Core.Playlists.IPlaylistCacheService>(sp =>
                    new Wavee.Core.Playlists.PlaylistCacheService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetService<ILogger<Wavee.Core.Playlists.PlaylistCacheService>>()))
                .AddSingleton<IUserScopeGuard>(sp =>
                    new UserScopeGuard(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetRequiredService<Wavee.Core.Playlists.IPlaylistCacheService>(),
                        sp.GetRequiredService<ITrackLikeService>(),
                        sp.GetRequiredService<IProfileCache>(),
                        sp.GetService<ILogger<UserScopeGuard>>()))
                .AddSingleton<ILibraryDataService>(sp =>
                    new Data.Contexts.LibraryDataService(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetRequiredService<Wavee.Core.Playlists.IPlaylistCacheService>(),
                        sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        sp.GetRequiredService<IMessenger>(),
                        sp.GetRequiredService<ITrackLikeService>(),
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>(),
                        sp.GetRequiredService<Data.Stores.ExtendedMetadataStore>(),
                        sp.GetRequiredService<Services.IMusicVideoMetadataService>(),
                        sp.GetService<ILogger<Data.Contexts.LibraryDataService>>()))
                .AddSingleton(sp =>
                    new Data.Stores.PlaylistStore(
                        sp.GetRequiredService<ILibraryDataService>(),
                        sp.GetRequiredService<Wavee.Core.Playlists.IPlaylistCacheService>(),
                        sp.GetService<ILogger<Data.Stores.PlaylistStore>>()))
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
                .AddSingleton(sp =>
                    new Data.Stores.ArtistStore(
                        sp.GetRequiredService<IArtistService>(),
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Data.Contracts.ArtistOverviewResult>>(),
                        sp.GetService<ILogger<Data.Stores.ArtistStore>>()))
                .AddSingleton(sp =>
                    new Data.Stores.AlbumStore(
                        sp.GetRequiredService<IAlbumService>(),
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Data.Contracts.AlbumDetailResult>>(),
                        sp.GetService<ILogger<Data.Stores.AlbumStore>>()))
                .AddSingleton(sp =>
                    new Data.Stores.ExtendedMetadataStore(
                        sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        sp.GetService<ILogger<Data.Stores.ExtendedMetadataStore>>()))
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
                        sp.GetService<INotificationService>(),
                        sp.GetService<IPanelDockingService>(),
                        sp.GetService<ILoggerFactory>()))
                .AddTransient<HomeViewModel>(sp =>
                    new HomeViewModel(
                        sp.GetService<ISession>(),
                        sp.GetService<ISettingsService>(),
                        sp.GetService<HomeFeedCache>(),
                        sp.GetService<RecentlyPlayedService>(),
                        sp.GetService<HomeResponseParserFactory>(),
                        sp.GetService<IAuthState>(),
                        sp.GetService<ILogger<HomeViewModel>>(),
                        sp.GetService<Wavee.Core.Library.Local.ILocalLibraryService>()))
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
                        sp.GetService<ProfileService>(),
                        sp.GetService<Session>(),
                        sp.GetService<IAuthState>(),
                        sp.GetService<ILogger<ProfileViewModel>>()))
                .AddTransient<SpotifyConnectViewModel>()
                // Video page + mini-player VMs. Singletons so the mini-player
                // (mounted at shell level) keeps its subscriptions stable
                // across page navigation; same for the page VM since the
                // page can be re-navigated.
                .AddSingleton<VideoPlayerPageViewModel>(sp =>
                    new VideoPlayerPageViewModel(
                        sp.GetRequiredService<Services.IActiveVideoSurfaceService>(),
                        sp.GetService<Wavee.UI.Contracts.IPlaybackStateService>(),
                        sp.GetService<Wavee.Core.Library.Local.ILocalLibraryService>(),
                        sp.GetService<IPathfinderClient>(),
                        sp.GetService<IExtendedMetadataClient>(),
                        sp.GetService<ILogger<VideoPlayerPageViewModel>>()))
                .AddSingleton<MiniVideoPlayerViewModel>(sp =>
                    new MiniVideoPlayerViewModel(
                        sp.GetRequiredService<Services.IActiveVideoSurfaceService>(),
                        sp.GetService<Wavee.UI.Contracts.IPlaybackStateService>(),
                        sp.GetService<ILogger<MiniVideoPlayerViewModel>>()))
                .AddTransient<SearchViewModel>(sp =>
                    new SearchViewModel(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetService<ILogger<SearchViewModel>>(),
                        sp.GetService<Wavee.Core.Library.Local.ILocalLibraryService>()))
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
                        sp.GetService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<CommunityToolkit.Mvvm.Messaging.IMessenger>(),
                        sp.GetService<INotificationService>(),
                        sp.GetService<ILogger<SettingsViewModel>>(),
                        sp.GetService<LocalFilesViewModel>()))
                .AddTransient<LocalFilesViewModel>(sp =>
                    new LocalFilesViewModel(
                        sp.GetRequiredService<Wavee.Core.Library.Local.ILocalLibraryService>(),
                        sp.GetService<ILogger<LocalFilesViewModel>>()))
                .AddTransient<LocalLibraryViewModel>(sp =>
                    new LocalLibraryViewModel(
                        sp.GetService<Wavee.Core.Library.Local.ILocalLibraryService>(),
                        sp.GetService<ILogger<LocalLibraryViewModel>>()))

                // Drag & drop
                .AddSingleton<DragStateService>()

                // Utilities
                .AddSingleton<AppModel>()

                // Image cache cleanup adapter
                .AddSingleton<ICleanableCache, ImageCacheCleanupAdapter>()
                .AddSingleton<MemoryBudgetService>()

                // Rich-type detail-page hot caches. Wired into ArtistStore /
                // AlbumStore / PlaylistStore's ReadHotAsync/WriteHot so a
                // re-navigation after the EntityStore Slot evicts (MaxSlots=64
                // LRU) hits memory instead of re-fetching from Pathfinder. The
                // lean *CacheEntry HotCaches registered upstream in Wavee.Core
                // are a separate concern (search results / sidebar tiles).
                // Sizes reuse WaveeCacheOptions to keep one tuning surface.
                .AddSingleton<Wavee.Core.Storage.Abstractions.IHotCache<Data.Contracts.ArtistOverviewResult>>(sp =>
                {
                    var opts = sp.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
                    return new Wavee.Core.Storage.HotCache<Data.Contracts.ArtistOverviewResult>(
                        opts.ArtistHotCacheSize,
                        sp.GetService<ILogger<Wavee.Core.Storage.HotCache<Data.Contracts.ArtistOverviewResult>>>());
                })
                .AddSingleton<ICleanableCache>(sp =>
                    (ICleanableCache)sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Data.Contracts.ArtistOverviewResult>>())

                .AddSingleton<Wavee.Core.Storage.Abstractions.IHotCache<Data.Contracts.AlbumDetailResult>>(sp =>
                {
                    var opts = sp.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
                    return new Wavee.Core.Storage.HotCache<Data.Contracts.AlbumDetailResult>(
                        opts.AlbumHotCacheSize,
                        sp.GetService<ILogger<Wavee.Core.Storage.HotCache<Data.Contracts.AlbumDetailResult>>>());
                })
                .AddSingleton<ICleanableCache>(sp =>
                    (ICleanableCache)sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Data.Contracts.AlbumDetailResult>>())

                .AddSingleton<Wavee.Core.Storage.Abstractions.IHotCache<Data.DTOs.PlaylistDetailDto>>(sp =>
                {
                    var opts = sp.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
                    return new Wavee.Core.Storage.HotCache<Data.DTOs.PlaylistDetailDto>(
                        opts.PlaylistHotCacheSize,
                        sp.GetService<ILogger<Wavee.Core.Storage.HotCache<Data.DTOs.PlaylistDetailDto>>>());
                })
                .AddSingleton<ICleanableCache>(sp =>
                    (ICleanableCache)sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Data.DTOs.PlaylistDetailDto>>())

                // Memory diagnostics (in-app panel under Settings → Diagnostics).
                // Off the hot path; resolved lazily when the user opens the panel
                // and only samples while it's visible.
                .AddSingleton<Diagnostics.MemoryDiagnosticsService>(sp =>
                    new Diagnostics.MemoryDiagnosticsService(
                        sp.GetService<ILogger<Diagnostics.MemoryDiagnosticsService>>()))
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

            // Wire the disk-backed cache into the session so AudioKeyManager persists
            // keys to SQLite. Must run before the first RequestAudioKeyAsync (the
            // session lazily constructs AudioKeyManager on first access) — doing it
            // here, before TrackResolver / PlaybackOrchestrator touch session.AudioKeys,
            // gets us in under the wire.
            if (cacheService != null)
            {
                session.SetCacheService(cacheService);
            }

            // PlayPlay fallback: AudioHost (x86_64, runs under WoA x64 emulation
            // on ARM64 hosts) loads Spotify.dll directly and exposes a
            // DerivePlayPlayKey RPC over the same named pipe used for playback.
            // Register the deriver on the session before the first
            // RequestAudioKeyAsync — same lifecycle constraint as the cache.
            try
            {
                var dll = Wavee.Core.Audio.SpotifyDllLocator.Locate(logger: logger);
                var manager = _audioProcessManager;
                if (dll is not null && manager is not null)
                {
                    var deriver = new Wavee.Core.Audio.AudioHostPlayPlayKeyDeriver(
                        spClient,
                        proxyResolver: () => manager.Proxy,
                        spotifyDllPath: dll,
                        cacheService: cacheService,
                        logger: logger);
                    session.SetPlayPlayKeyDeriver(deriver);
                    logger?.LogInformation(
                        "PlayPlay fallback enabled via AudioHost (Spotify.dll v{Version}, dll={Dll})",
                        Wavee.Core.Audio.PlayPlayConstants.SpotifyClientVersion, dll);
                }
                else
                {
                    logger?.LogInformation(
                        "PlayPlay fallback disabled: Spotify.dll v{Version} not found or AudioHost manager not initialised",
                        Wavee.Core.Audio.PlayPlayConstants.SpotifyClientVersion);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "PlayPlay fallback failed to initialise; AP-only mode");
            }

            // Resolve the head-files URL template lazily from the session so we pick
            // up the CDN host Spotify hands us in ProductInfo (e.g.
            // heads-fa-tls13.spotifycdn.com) instead of the legacy hardcoded host.
            var headFileClient = new Wavee.Core.Audio.HeadFileClient(
                httpClient,
                logger,
                urlTemplateResolver: () => session.UserData?.HeadFilesUrl);
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
                proxy, trackResolver, contextResolver!, session.CommandHandler, logger,
                events: session.Events,
                localDeviceId: session.Config.DeviceId,
                localLibrary: Ioc.Default.GetService<Wavee.Core.Library.Local.ILocalLibraryService>(),
                localMediaPlayer: Ioc.Default.GetService<Wavee.Audio.ILocalMediaPlayer>(),
                spotifyVideoPlayback: Ioc.Default.GetService<Wavee.Audio.ISpotifyVideoPlayback>());

            // Honor the user's autoplay preference. Read fresh on each check so
            // a toggle in the Settings page takes effect immediately — no event
            // plumbing / debounce needed.
            var settingsForAutoplay = Ioc.Default.GetService<ISettingsService>();
            if (settingsForAutoplay is not null)
                orchestrator.AutoplayEnabledProvider = () => settingsForAutoplay.Settings.AutoplayEnabled;

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

            // Surface errors via notifications and activity feed. Shared between
            // proxy errors (decode / device faults from the audio host) and
            // orchestrator errors (track-resolve / AudioKey timeout failures) —
            // both ultimately reach the user, so route them through one path.
            var notificationService = Ioc.Default.GetService<INotificationService>();
            var activityService = Ioc.Default.GetService<IActivityService>();
            var errorDispatcher = _uiDispatcher;
            Action<Wavee.Connect.PlaybackError> showError = error =>
            {
                var message = error.ErrorType switch
                {
                    PlaybackErrorType.AudioDeviceUnavailable => error.Message,
                    PlaybackErrorType.TrackUnavailable => "Track unavailable (Premium required?).",
                    PlaybackErrorType.NetworkError => "Network error during playback.",
                    PlaybackErrorType.DecodeError => "Couldn't decode this track. Try another.",
                    _ => string.IsNullOrEmpty(error.Message)
                        ? "Playback failed. Please try again."
                        : error.Message
                };
                var (title, iconGlyph) = error.ErrorType switch
                {
                    PlaybackErrorType.AudioDeviceUnavailable => ("Audio device error", "\uE7F3"),
                    PlaybackErrorType.TrackUnavailable => ("Track unavailable", "\uE774"),
                    PlaybackErrorType.NetworkError => ("Network error", "\uE774"),
                    PlaybackErrorType.DecodeError => ("Decode error", "\uE783"),
                    _ => ("Playback error", "\uE783")
                };
                errorDispatcher?.TryEnqueue(() =>
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
            };

            _appSubscriptions.Add(proxy.Errors.Subscribe(showError));
            // Orchestrator surfaces its own errors from the resolve pipeline
            // (e.g. AudioKey timed out after 5 attempts, CDN resolve failed).
            // Without this subscription the track just silently fails — which is
            // what the user was hitting on the stuck-audiokey channel.
            _appSubscriptions.Add(orchestrator.Errors.Subscribe(showError));

            // End-of-context: autoplay cascade exhausted. Without user-visible
            // feedback, playback just silently stopped at the last track and
            // the user had no idea why / what to do. Show an informational
            // notification and keep the activity trail.
            // One toast per session — the PlayerBar's inline "You've reached
            // the end" hint handles subsequent end-of-context transitions.
            // Toast exists for discovery; once the user has seen it, the
            // inline hint is enough.
            var endOfContextToastShown = false;
            Action<Wavee.Audio.EndOfContextEvent> showEndOfContext = evt =>
            {
                var isAutoplayOn = settingsForAutoplay?.Settings.AutoplayEnabled ?? true;
                var canNudgeAutoplay = evt.ContextSupportsAutoplay && !isAutoplayOn;

                var (title, message) = evt switch
                {
                    _ when canNudgeAutoplay => (
                        "Reached the end",
                        "Turn on Autoplay to keep listening with similar songs."),
                    { AutoplayAttempted: true } => (
                        "Reached the end",
                        "Couldn't find related songs to continue with. Click Play to restart."),
                    _ => (
                        "Reached the end",
                        "Click Play to restart the queue.")
                };
                errorDispatcher?.TryEnqueue(() =>
                {
                    // Inline hint in the PlayerBar — fires every time.
                    var playbackState = Ioc.Default.GetService<IPlaybackStateService>();
                    playbackState?.NotifyEndOfContext();

                    if (!endOfContextToastShown)
                    {
                        endOfContextToastShown = true;
                        notificationService?.Show(new Data.Models.NotificationInfo
                        {
                            Message = message,
                            Severity = Data.Models.NotificationSeverity.Informational,
                            AutoDismissAfter = TimeSpan.FromSeconds(10),
                            ActionLabel = canNudgeAutoplay ? "Turn on Autoplay" : null,
                            Action = canNudgeAutoplay && settingsForAutoplay is not null
                                ? () =>
                                {
                                    settingsForAutoplay.Update(s => s.AutoplayEnabled = true);
                                    return Task.CompletedTask;
                                }
                                : null
                        });
                    }
                    activityService?.Post(
                        category: "playback",
                        title: title,
                        iconGlyph: "",
                        status: Data.Models.ActivityStatus.Completed,
                        message: message);
                });
            };
            _appSubscriptions.Add(orchestrator.EndOfContext.Subscribe(showEndOfContext));

            // AudioKey channel sometimes goes silent for a specific FileId while the
            // rest of the AP socket works fine. AudioKeyManager recovers by
            // reconnecting after 2 consecutive timeouts (~5 s). That recovery is NOT
            // an error — playback hasn't failed yet — but the user sees a ~5 s freeze
            // and should know the app is actively dealing with it. Show a Warning
            // InfoBar that auto-dismisses well past the reconnect window.
            EventHandler<Wavee.Core.Audio.AudioKeyRecoveryEventArgs> recoveryStartedHandler = (_, _) =>
                errorDispatcher?.TryEnqueue(() =>
                {
                    notificationService?.Show(new Data.Models.NotificationInfo
                    {
                        Message = "Having trouble reaching Spotify — reconnecting, one moment…",
                        Severity = Data.Models.NotificationSeverity.Warning,
                        AutoDismissAfter = TimeSpan.FromSeconds(8)
                    });
                });
            session.AudioKeys.RecoveryStarted += recoveryStartedHandler;
            _appSubscriptions.Add(System.Reactive.Disposables.Disposable.Create(() =>
                session.AudioKeys.RecoveryStarted -= recoveryStartedHandler));

            // Re-wire on auto-restart (variables are now in scope for the closure)
            _audioProxyRestartedHandler = newProxy =>
            {
                var notifDisp = _uiDispatcher;
                notifDisp?.TryEnqueue(() =>
                {
                    var newOrch = new Wavee.Audio.PlaybackOrchestrator(
                        newProxy, trackResolver, contextResolver!, session.CommandHandler, logger,
                        events: session.Events,
                        localDeviceId: session.Config.DeviceId,
                        localLibrary: Ioc.Default.GetService<Wavee.Core.Library.Local.ILocalLibraryService>(),
                        localMediaPlayer: Ioc.Default.GetService<Wavee.Audio.ILocalMediaPlayer>(),
                        spotifyVideoPlayback: Ioc.Default.GetService<Wavee.Audio.ISpotifyVideoPlayback>());
                    if (settingsForAutoplay is not null)
                        newOrch.AutoplayEnabledProvider = () => settingsForAutoplay.Settings.AutoplayEnabled;
                    var exec = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
                    exec?.EnableLocalPlayback(newOrch);
                    session.PlaybackState?.EnableBidirectionalMode(
                        newOrch, spClient, session, suppressClusterUpdates: false);
                    profiler?.SetAudioUnderrunProvider(() => newProxy.UnderrunCount);
                    // New proxy/orchestrator pair → re-subscribe error streams so
                    // failures after a restart still reach the user.
                    _appSubscriptions.Add(newProxy.Errors.Subscribe(showError));
                    _appSubscriptions.Add(newOrch.Errors.Subscribe(showError));
                    _appSubscriptions.Add(newOrch.EndOfContext.Subscribe(showEndOfContext));
                });
            };
            _audioProcessManager.ProxyRestarted += _audioProxyRestartedHandler;

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

            // Force-construct the music-video metadata and discovery
            // singletons so they're alive BEFORE the first TrackChangedMessage
            // fires. WeakReferenceMessenger only delivers to recipients that
            // were registered when Send is called, so lazy DI construction
            // would let the very first track change slip past the discovery
            // service's subscription.
            var videoMetadata = Ioc.Default.GetService<Services.IMusicVideoMetadataService>();
            var videoDiscovery = Ioc.Default.GetService<Services.IMusicVideoDiscoveryService>();
            logger?.LogInformation("[VideoDiscovery] eager construction at sign-in: metadata={MetadataAlive} discovery={DiscoveryAlive}",
                videoMetadata is null ? "<null>" : "alive",
                videoDiscovery is null ? "<null>" : "alive");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to initialize track metadata enricher");
        }
    }
}
