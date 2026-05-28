using System;
using Wavee.Audio;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Core.Authentication;
using Wavee.Core.DependencyInjection;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contexts;
// Processors now live in AudioHost — EQ config goes via IPC
using Wavee.UI.Contracts;
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
using Wavee.UI.Services;
using Wavee.UI.Services.Actions;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Handlers;
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

    private static Wavee.Local.ILocalLibraryService? GetLocalLibraryService(IServiceProvider services)
        => AppFeatureFlags.LocalFilesEnabled
            ? services.GetService<Wavee.Local.ILocalLibraryService>()
            : null;

    private static Wavee.UI.Library.Local.ILocalLibraryFacade? GetLocalLibraryFacade(IServiceProvider services)
        => AppFeatureFlags.LocalFilesEnabled
            ? services.GetService<Wavee.UI.Library.Local.ILocalLibraryFacade>()
            : null;

    private static Wavee.Local.ILocalLibraryService? GetLocalLibraryService()
        => AppFeatureFlags.LocalFilesEnabled
            ? Ioc.Default.GetService<Wavee.Local.ILocalLibraryService>()
            : null;

    private static Wavee.Audio.ILocalMediaPlayer? GetLocalMediaPlayer()
        => AppFeatureFlags.LocalFilesEnabled
            ? Ioc.Default.GetService<Wavee.Audio.ILocalMediaPlayer>()
            : null;

    /// <summary>
    /// The active audio process manager (null if using in-process audio).
    /// Exposed for diagnostics UI.
    /// </summary>
    public static Wavee.AudioIpc.AudioProcessManager? AudioProcessManager => _audioProcessManager;

    internal static UiHandoffLaunchOptions? PendingUiHandoff { get; set; }

    /// <summary>
    /// Serilog level switch driving the file + in-memory sinks. Flipped at runtime by the
    /// "Verbose logging" toggle in the Diagnostics settings — no app restart required.
    /// Initialised in <see cref="ConfigureHost"/>.
    /// </summary>
    public static LoggingLevelSwitch LogLevelSwitch { get; } = new(Serilog.Events.LogEventLevel.Information);

    private static IServiceCollection AddRemoteStateRecorderIfDiagnosticsEnabled(IServiceCollection services)
    {
        if (!AppFeatureFlags.DiagnosticsEnabled)
            return services;

        return services
            .AddSingleton<Wavee.Connect.Diagnostics.IRemoteStateRecorder>(sp =>
                new Wavee.UI.WinUI.Services.RemoteStateRecorder(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()))
            .AddSingleton<Wavee.UI.WinUI.Services.RemoteStateRecorder>(sp =>
                (Wavee.UI.WinUI.Services.RemoteStateRecorder)sp.GetRequiredService<Wavee.Connect.Diagnostics.IRemoteStateRecorder>());
    }

    private const string DrmLogChannel = "DRM";

    /// <summary>
    /// Apply a verbose-logging change at runtime. Flips the Serilog level switch and tells
    /// the audio process to do the same on its next restart (live forwarding could be added
    /// over IPC later).
    /// </summary>
    public static void SetVerboseLogging(bool enabled)
    {
        if (!AppFeatureFlags.DiagnosticsEnabled)
            enabled = false;

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
        if (!AppFeatureFlags.DiagnosticsEnabled)
            enabled = false;

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

        // Wire the framework-neutral LocalizationHook so Wavee.UI DTOs / formatters
        // can resolve user-facing strings through resw. Must happen before any DTO
        // metadata is composed.
        AppLocalization.InstallHook();

        // Initial switch level: Verbose if user opted in (or DEBUG build), otherwise Information.
        // The switch is mutable at runtime — see SetVerboseLogging.
        var verboseEnabled = AppFeatureFlags.DiagnosticsEnabled && SettingsService.PeekVerboseLogging();
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
            .WriteTo.Logger(main =>
            {
                var mainLog = main.Filter.ByIncludingOnly(logEvent => IsMainLogEvent(logEvent, noisyOverride))
                    .WriteTo.Debug()
                    .WriteTo.File(
                        path: AppPaths.RollingLogFilePath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        rollOnFileSizeLimit: true,
                        fileSizeLimitBytes: 10 * 1024 * 1024,
                        shared: true,
                        flushToDiskInterval: TimeSpan.FromSeconds(1),
                        outputTemplate: fileOutputTemplate);

                if (AppFeatureFlags.DiagnosticsEnabled)
                    mainLog.WriteTo.Sink(inMemorySink);
            })
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

        // HostApplicationBuilder (not the older Host.CreateDefaultBuilder).
        // CreateDefaultBuilder() pulls in the reflection-based configuration
        // binder (IL2026/IL3050) even when no IConfiguration is consumed.
        // HostApplicationBuilder exposes the same Services + Logging surface
        // but uses the lighter, AOT-clean path. We don't bind appsettings.json
        // to options anywhere — every config value comes from direct
        // service registrations below — so dropping the default builder is
        // a no-op behaviourally.
        var builder = Host.CreateApplicationBuilder();

        builder.Logging
            .ClearProviders()
            .AddSerilog(Log.Logger, dispose: false)
            .SetMinimumLevel(hostMinimumLogLevel);

        AddRemoteStateRecorderIfDiagnosticsEnabled(builder.Services
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
                        sp.GetService<ILogger<Wavee.Connect.ConnectCommandClient>>(),
                        sp.GetService<Wavee.Connect.Diagnostics.IRemoteStateRecorder>()))
                .AddSingleton<IPlaybackCommandExecutor, ConnectCommandExecutor>()
                .AddSingleton<IAudioPipelineControl>(sp =>
                    (IAudioPipelineControl)sp.GetRequiredService<IPlaybackCommandExecutor>())
                .AddSingleton<IPlaybackPromptService, Data.Contexts.PlaybackPromptService>()
                .AddSingleton<IPlaybackService, Data.Contexts.PlaybackService>()

                // UI-thread DispatcherQueue captured once at DI setup so service
                // ctors can take it as a regular DI parameter (no need for the
                // inline `DispatcherQueue.GetForCurrentThread()` call every
                // factory used to do). `ConfigureHost` runs on the UI thread,
                // which is where every consumer needs to dispatch back to.
                .AddSingleton(_ => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread())

                // App state services
                .AddSingleton<Services.InPageFilterController>()
                .AddSingleton<INotificationService, NotificationService>()
                .AddSingleton<Services.UiRestartCoordinator>()
                .AddSingleton<IUpdateService, UpdateService>()
                .AddSingleton<IPlaybackStateService, PlaybackStateService>()
                .AddSingleton<Services.SystemMediaTransportControlsService>()
                .AddSingleton<Wavee.UI.Services.Playback.SleepTimerService>()
                // Register the concrete ContentFilterService once and expose
                // it under both interfaces so the orchestrator (Wavee.Audio)
                // and UI consumers (Wavee.UI) share the same in-memory cache.
                .AddSingleton<Wavee.UI.Services.Library.ContentFilterService>()
                .AddSingleton<Wavee.UI.Contracts.IContentFilterService>(sp => sp.GetRequiredService<Wavee.UI.Services.Library.ContentFilterService>())
                .AddSingleton<Wavee.Audio.IPlaybackContentFilter>(sp => sp.GetRequiredService<Wavee.UI.Services.Library.ContentFilterService>())
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
                        GetLocalLibraryService(sp),
                        sp.GetService<ILogger<Services.MusicVideoMetadataService>>()))
                // Music-video discovery for the linked-URI catalog pattern
                // (audio URI ≠ video URI; e.g. drunk text). Invoked directly
                // by PlaybackStateService.OnCurrentTrackIdChanged when the
                // catalog cache returns no hint; runs NPV in background and
                // publishes MusicVideoAvailabilityMessage on completion.
                // Lazily resolves IPlaybackStateService via Ioc.Default to
                // break the construction cycle.
                .AddSingleton<Services.IMusicVideoDiscoveryService, Services.MusicVideoDiscoveryService>()
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
                .AddSingleton<Services.LocalMediaPlayer>()
                .AddSingleton<Wavee.Audio.ILocalMediaPlayer>(sp =>
                    sp.GetRequiredService<Services.LocalMediaPlayer>())
                // Per-session cache of MKV/MP4 chapter cues read from
                // local TV episodes — feeds the Up-Next overlay's
                // credits-detection. Singleton so the same scan result is
                // reused across overlay instances when a user re-opens
                // the same episode.
                .AddSingleton<Services.LocalEpisodeChapterScanner>()
                .AddSingleton<Services.IVideoSurfaceProvider>(sp =>
                    sp.GetRequiredService<Services.LocalMediaPlayer>())
                // Shared video manifest cache — prefetch (TrackResolver) writes,
                // play path (SpotifyVideoProvider) reads. Eliminates the manifest
                // HTTP roundtrip on prefetch hits.
                .AddSingleton<Wavee.Core.Video.IVideoManifestCache>(_ =>
                    new Wavee.Core.Video.InMemoryVideoManifestCache())
                // Spotify music-video engine — registered as a concrete singleton
                // then forwarded to ISpotifyVideoPlayback (for the orchestrator)
                // and as a second IVideoSurfaceProvider (for the surface service).
                .AddSingleton<Services.SpotifyVideoProvider>()
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
                .AddSingleton<Services.INowPlayingPresentationService, Services.NowPlayingPresentationService>()
                // Logged fire-and-forget helper for background tasks the UI
                // starts but doesn't await — replaces naked _ = SomeAsync(...)
                // sites so any thrown exception lands in the structured log
                // instead of the unobserved-task scheduler.
                .AddSingleton<Wavee.UI.Services.Infra.IBackgroundWorkRunner, Wavee.UI.Services.Infra.BackgroundWorkRunner>()
                // Single sink for "something changed" notifications — coalesces
                // bursts (150 ms window) and replaces the four-way fan-out
                // (DataChanged / PlaylistsChanged events + LibraryDataChangedMessage
                // / PlaylistsChangedMessage messages) that existed before Phase 1.
                .AddSingleton<Wavee.UI.Services.Infra.IChangeBus, Wavee.UI.Services.Infra.ChangeBus>()
                // Drains the "subscribe → cancel CTS → reload → marshal" pattern
                // from VMs; backed by IChangeBus.
                .AddSingleton<Wavee.UI.Services.Infra.IReloadCoordinator, Wavee.UI.Services.Infra.ReloadCoordinator>()
                // Composes IPlaylistCacheService with IChangeBus so call sites
                // can express "I just mutated playlist X" in one call.
                .AddSingleton<Wavee.UI.Services.Infra.ICacheInvalidator, Wavee.UI.Services.Infra.CacheInvalidator>()
                // Windows-shell video frame thumbnail provider for the local
                // scanner. Registered as IVideoThumbnailExtractor so the
                // scanner DI in Wavee.Core picks it up without needing a
                // direct reference to Windows.Storage in the core project.
                .AddSingleton<Wavee.Local.IVideoThumbnailExtractor, Services.WindowsVideoThumbnailExtractor>()
                .AddSingleton<IAuthState, AuthStateService>()
                .AddSingleton<IConnectivityService, ConnectivityService>()
                .AddSingleton<IAppState, AppState>()

                // App initialization
                .AddSingleton<AppInitializationService>()
                .AddSingleton<IPlaylistPrefetcher, PlaylistPrefetchService>()
                .AddSingleton<Data.Contexts.LibrarySyncOrchestrator>()
                .AddSingleton<Data.Contexts.ActivityService>()
                .AddSingleton<IActivityService>(sp => sp.GetRequiredService<Data.Contexts.ActivityService>())
                .AddSingleton<IUserActionActivitySink>(sp => sp.GetRequiredService<Data.Contexts.ActivityService>())
                .AddSingleton<IUserActionRunner>(sp =>
                    new UserActionRunner(
                        sp.GetRequiredService<IUserActionActivitySink>(),
                        () => sp.GetRequiredService<IUserActionFactory>(),
                        sp.GetService<ILogger<UserActionRunner>>()))
                .AddSingleton<IUserActionFactory, LibraryUserActionFactory>()
                .AddSingleton<IFriendsFeedService, Data.Contexts.FriendsFeedService>()

                // EQ processor now lives in AudioHost — settings sent via IPC

                // Dispatcher abstraction
                .AddSingleton<IDispatcherService, DispatcherService>()

                // App services
                .AddSingleton<ILocalizationService, LocalizationService>()
                .AddSingleton<IAppLocalizationService, AppLocalizationService>()
                .AddSingleton<ISettingsService, SettingsService>()

                // On-device AI (Copilot+ PC; opt-in via Settings → On-device AI)
                .AddSingleton<Wavee.AI.IAiFeatureSettings, Services.Ai.WinUiAiFeatureSettings>()
                .AddSingleton<Wavee.AI.Generation.ILanguageModelClient, Wavee.AI.Generation.PhiSilicaLanguageModelClient>()
                .AddSingleton<Wavee.AI.Artists.IArtistAiToolProvider, Services.Ai.WinUiArtistAiToolProvider>()
                .AddSingleton<Wavee.AI.Artists.IMusicCatalogSearchProvider, Services.Ai.WinUiMusicCatalogSearchProvider>()
                // Web grounding: a shared LRU cache underlies a composite provider that
                // routes to the user's custom endpoint when configured, else to the
                // baked-in DuckDuckGo lite scrape so grounding works on a fresh install.
                .AddSingleton<Services.Ai.WebSearchCache>()
                .AddSingleton<Services.Ai.ConfigurableWebSearchToolProvider>()
                .AddSingleton<Services.Ai.DuckDuckGoLiteWebSearchProvider>()
                .AddSingleton<Wavee.AI.Tools.IWebSearchToolProvider>(sp => new Services.Ai.CompositeWebSearchToolProvider(
                    sp.GetRequiredService<Services.Ai.ConfigurableWebSearchToolProvider>(),
                    sp.GetRequiredService<Services.Ai.DuckDuckGoLiteWebSearchProvider>()))
                .AddSingleton<Wavee.AI.Tools.IWikipediaLookup, Services.Ai.WikipediaArticleLookup>()
                .AddSingleton<Wavee.AI.Tools.IMusicGroundingProvider, Services.Ai.MusicWebGroundingProvider>()
                .AddSingleton<Wavee.AI.Artists.ArtistAiQuestionService>()
                .AddSingleton<AiCapabilities>()
                .AddSingleton<LyricsAiService>()
                .AddSingleton<ArtistBioSummarizer>()
                .AddSingleton<AlbumBioSummarizer>()
                .AddSingleton<AiNotificationService>()

                .AddSingleton<IShellSessionService, ShellSessionService>()
                .AddSingleton<Services.Docking.IPanelDockingService, Services.Docking.PanelDockingService>()
                .AddSingleton<IMediaOverrideService, MediaOverrideService>()
                .AddSingleton<IThemeService, ThemeService>()
                .AddSingleton<ThemeColorService>()
                .AddSingleton<HomeResponseParserFactory>()
                .AddSingleton<HomeFeedCache>()
                .AddSingleton<IHomeFeedCache>(sp => sp.GetRequiredService<HomeFeedCache>())
                .AddSingleton<IHomeFeedService, Data.Contexts.HomeFeedService>()
                .AddSingleton<IUserFollowService, Data.Contexts.UserFollowService>()
                .AddSingleton<RecentlyPlayedService>()
                .AddSingleton<LibraryRecentsService>()
                .AddSingleton<ProfileCache>()
                .AddSingleton<IProfileCache>(sp => sp.GetRequiredService<ProfileCache>())
                .AddSingleton<ProfileService>()
                .AddSingleton<IUserProfileService>(sp => sp.GetRequiredService<ProfileService>())
                .AddSingleton<IUserProfileResolver, UserProfileResolver>()
                .AddSingleton(sp => new ImageCacheService(cacheCapacities.ImageCacheMaxSize))
                .AddSingleton<PlaylistMosaicService>()
                // WinUI palette → brush composer. Stateless, reusable by
                // ArtistHeaderViewModel (and future header VMs that share the
                // same palette→accent/hero-gradient/pill-brush mapping).
                .AddSingleton<PaletteGradientCompositor>()
                .AddSingleton<IPreviewAudioPlaybackEngine, PreviewAudioGraphService>()
                .AddSingleton<PreviewAudioGraphService>(sp => (PreviewAudioGraphService)sp.GetRequiredService<IPreviewAudioPlaybackEngine>())
                // IUiDispatcher abstraction: lets services in the plain-C# Wavee.UI library marshal
                // callbacks onto the UI thread without depending on Microsoft.UI.Dispatching.
                .AddSingleton<Wavee.UI.Threading.IUiDispatcher, DispatcherQueueUiDispatcher>()
                .AddSingleton<ICardPreviewPlaybackCoordinator, CardPreviewPlaybackCoordinator>()
                .AddSingleton<ISharedCardCanvasPreviewService, SharedCardCanvasPreviewService>()
                // Shared now-playing highlight observer. Subscribes to NowPlayingChangedMessage
                // once; ContentCard instances subscribe to its C# event instead of registering
                // individually with WeakReferenceMessenger. Big savings during HomePage realization.
                .AddSingleton<NowPlayingHighlightService>()
                .AddSingleton(sp =>
                {
                    var profiler = new UiOperationProfiler(
                        sp.GetService<ILogger<UiOperationProfiler>>());
                    UiOperationProfiler.Instance = profiler;
                    return profiler;
                })
                .AddSingleton(sp =>
                {
                    var diag = new Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics(
                        sp.GetService<ILogger<Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics>>());
                    Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance = diag;
                    return diag;
                })
                .AddSingleton(inMemorySink))
                .AddSingleton<Wavee.Core.Http.IColorService, Wavee.Core.Http.ExtractedColorService>()
                // UI-oriented batched color-hint service for virtualized track rows.
                // Wraps IColorService with request dedupe + debounce-window batching so
                // scroll bursts across hundreds of tracks coalesce into a few backend calls.
                .AddSingleton<Wavee.UI.Services.ITrackColorHintService, Wavee.UI.Services.TrackColorHintService>()

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
                    PreferredLocale = spotifyMetadataLocale,
                    LocalSpotifyPlaybackEnabled = Wavee.Core.Audio.SpotifyPlaybackCapabilities.DefaultLocalSpotifyPlaybackEnabled
                })
                .AddSingleton(sp => Session.Create(
                    sp.GetRequiredService<SessionConfig>(),
                    sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                    sp.GetService<ILogger<Session>>(),
                    sp.GetService<Wavee.Connect.Diagnostics.IRemoteStateRecorder>()))
                .AddSingleton<ISession>(sp => sp.GetRequiredService<Session>())
                // Surface the two protocol entry points that hang off ISession
                // (Pathfinder GraphQL + SpClient REST) as first-class DI types so
                // service ctors can take them directly and the registrations stay
                // flat (`AddSingleton<TIface, TImpl>()`) instead of every factory
                // doing `sp.GetRequiredService<ISession>().Pathfinder` by hand.
                .AddSingleton<Wavee.Core.Http.IPathfinderClient>(sp => sp.GetRequiredService<ISession>().Pathfinder)
                .AddSingleton<Wavee.Core.Http.ISpClient>(sp => sp.GetRequiredService<ISession>().SpClient)
                .AddSingleton<Wavee.Core.Http.IExtendedMetadataClient>(sp =>
                    new Wavee.Core.Http.ExtendedMetadataClient(
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("Wavee"),
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetService<ILogger<Wavee.Core.Http.ExtendedMetadataClient>>()))
                .AddTransient<TrackMetadataEnricher>()
                // Unified outbox: pluggable handlers + one shared retry loop.
                // Library save/remove and playlist bulk-add both drain through
                // the same IOutboxProcessor. Register handlers before any
                // service that injects IOutboxProcessor so the DI container
                // has the full handler set on first resolve.
                .AddSingleton<Wavee.Core.Storage.Outbox.IOutboxHandler>(sp =>
                    new Wavee.Core.Library.Spotify.Outbox.LibrarySaveHandler(
                        (Wavee.Core.Http.SpClient)sp.GetRequiredService<ISession>().SpClient,
                        sp.GetRequiredService<ISession>()))
                .AddSingleton<Wavee.Core.Storage.Outbox.IOutboxHandler>(sp =>
                    new Wavee.Core.Library.Spotify.Outbox.LibraryRemoveHandler(
                        (Wavee.Core.Http.SpClient)sp.GetRequiredService<ISession>().SpClient,
                        sp.GetRequiredService<ISession>()))
                .AddSingleton<Wavee.Core.Storage.Outbox.IOutboxHandler>(sp =>
                    new Wavee.Core.Playlists.Outbox.PlaylistAddTracksHandler(
                        (Wavee.Core.Http.SpClient)sp.GetRequiredService<ISession>().SpClient,
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<Wavee.Core.Playlists.IPlaylistCacheService>(),
                        sp.GetRequiredService<IMetadataDatabase>()))
                .AddSingleton<Wavee.Core.Storage.Outbox.IOutboxProcessor>(sp =>
                    new Wavee.Core.Storage.Outbox.OutboxProcessor(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetServices<Wavee.Core.Storage.Outbox.IOutboxHandler>(),
                        sp.GetService<ILogger<Wavee.Core.Storage.Outbox.OutboxProcessor>>()))

                .AddSingleton<Wavee.Core.Library.Spotify.ISpotifyLibraryService>(sp =>
                {
                    var session = sp.GetRequiredService<ISession>();
                    return new Wavee.Core.Library.Spotify.SpotifyLibraryService(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        (Wavee.Core.Http.SpClient)session.SpClient,
                        session,
                        sp.GetRequiredService<Wavee.Core.Storage.Outbox.IOutboxProcessor>(),
                        metadataClient: sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        playlistCache: sp.GetService<Wavee.Core.Playlists.IPlaylistCacheService>(),
                        logger: sp.GetService<ILogger<Wavee.Core.Library.Spotify.SpotifyLibraryService>>());
                })

                // Data services
                .AddSingleton<IDataServiceConfiguration>(new DataServiceConfiguration(startInDemoMode: false))
                .AddSingleton<Wavee.UI.Services.Library.TrackLikeService>(sp =>
                    new Wavee.UI.Services.Library.TrackLikeService(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetService<Wavee.Core.Library.Spotify.ISpotifyLibraryService>(),
                        sp.GetService<Wavee.Local.ILocalLikeService>(),
                        sp.GetService<IUserActionRunner>(),
                        sp.GetService<ILogger<Wavee.UI.Services.Library.TrackLikeService>>()))
                .AddSingleton<ITrackLikeService>(sp =>
                    sp.GetRequiredService<Wavee.UI.Services.Library.TrackLikeService>())
                .AddSingleton<ILibrarySavedActionExecutor>(sp =>
                    sp.GetRequiredService<Wavee.UI.Services.Library.TrackLikeService>())
                .AddSingleton<Wavee.Core.Playlists.IPlaylistCacheService>(sp =>
                    new Wavee.Core.Playlists.PlaylistCacheService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetService<ILogger<Wavee.Core.Playlists.PlaylistCacheService>>(),
                        sp.GetService<Wavee.Connect.Diagnostics.IRemoteStateRecorder>()))
                .AddSingleton<IUserScopeGuard>(sp =>
                    new UserScopeGuard(
                        sp.GetRequiredService<IMetadataDatabase>(),
                        sp.GetRequiredService<Wavee.Core.Playlists.IPlaylistCacheService>(),
                        sp.GetRequiredService<ITrackLikeService>(),
                        sp.GetRequiredService<IProfileCache>(),
                        sp.GetService<ILogger<UserScopeGuard>>()))
                .AddSingleton<Data.Contexts.PinService>()
                .AddSingleton<Wavee.UI.Contracts.IPinService>(sp =>
                    sp.GetRequiredService<Data.Contexts.PinService>())
                .AddSingleton<IPinActionExecutor>(sp =>
                    sp.GetRequiredService<Data.Contexts.PinService>())
                .AddSingleton<Wavee.UI.Contracts.IPlaylistPermissionService, Data.Contexts.PlaylistPermissionService>()
                .AddSingleton<Wavee.UI.Contracts.IRootlistService, Data.Contexts.RootlistService>()
                .AddSingleton<Wavee.UI.Contracts.IPodcastEpisodeService, Data.Contexts.PodcastEpisodeService>()
                .AddSingleton<Data.Contexts.PlaylistMutationService>()
                .AddSingleton<Wavee.UI.Contracts.IPlaylistMutationService>(sp =>
                    sp.GetRequiredService<Data.Contexts.PlaylistMutationService>())
                .AddSingleton<IPlaylistMutationActionExecutor>(sp =>
                    sp.GetRequiredService<Data.Contexts.PlaylistMutationService>())
                // Framework-neutral playlist track filter/sort pipeline — stateless,
                // singleton. Consumed by PlaylistTrackListViewModel.
                .AddSingleton<Wavee.UI.Services.Playlists.PlaylistTrackFilterSorter>()
                // Framework-neutral artist discography pagination math + page
                // fetcher. Consumed by ArtistDiscographyViewModel + Artist
                // discography page; stateless, safe as a shared singleton.
                .AddSingleton<Wavee.UI.Services.Artists.DiscographyPaginationService>()
                // Framework-neutral hero-spotlight picker — folds pinned item /
                // latest release / popular releases into a single decision.
                .AddSingleton<Wavee.UI.Services.Artists.SpotlightSelectionService>()
                .AddSingleton<ILibraryDataService, Data.Contexts.LibraryDataService>()
                // App-wide "Add to playlist" modal session — shared singleton so
                // the floating bar in ShellPage, TrackItem '+' affordances, and
                // playlist-page entry points all see the same target + pending set.
                .AddSingleton<Wavee.UI.Services.AddToPlaylist.IAddToPlaylistSubmitter, Services.AddToPlaylist.LibraryDataServiceAddToPlaylistSubmitter>()
                .AddSingleton<Wavee.UI.Services.AddToPlaylist.IAddToPlaylistSession, Wavee.UI.Services.AddToPlaylist.AddToPlaylistSession>()
                .AddSingleton<Data.Stores.PlaylistStore>()
                .AddSingleton<ILocationService, Data.Contexts.LocationService>()
                .AddTransient<IConcertService, Data.Contexts.ConcertService>()
                .AddTransient<ConcertViewModel>()
                .AddSingleton<IArtistService>(sp =>
                    new Data.Contexts.ArtistService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<Wavee.Core.Http.IColorService>(),
                        sp.GetRequiredService<ILocationService>(),
                        sp.GetRequiredService<IMessenger>(),
                        GetLocalLibraryService(sp),
                        sp.GetService<ILogger<Data.Contexts.ArtistService>>()))
                .AddSingleton<Data.Stores.ArtistStore>()
                .AddSingleton<Data.Stores.AlbumStore>()
                .AddSingleton<Data.Stores.ExtendedMetadataStore>()
                .AddSingleton<Services.IAlbumPrefetcher, Services.AlbumPrefetcher>()
                .AddSingleton<Services.IPlaylistMetadataPrefetcher, Services.PlaylistMetadataPrefetcher>()
                .AddSingleton<IAlbumService>(sp =>
                    new Data.Contexts.AlbumService(
                        sp.GetRequiredService<ISession>().Pathfinder,
                        sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        GetLocalLibraryService(sp),
                        sp.GetService<ILogger<Data.Contexts.AlbumService>>(),
                        cacheCapacities.AlbumTracksHotCacheCapacity,
                        sp.GetRequiredService<Wavee.Core.Http.IExtendedMetadataClient>()))
                .AddSingleton<IPodcastService, Data.Contexts.PodcastService>()
                .AddSingleton<ISearchService, Data.Contexts.SearchService>()
                // Omnibar suggestion plumbing — stateless ranker + LRU cache used
                // by the shell's OmnibarViewModel. Singletons so the cache survives
                // ShellViewModel reconstruction (sign-out/in cycles).
                .AddSingleton<Wavee.UI.Services.Search.OmnibarSuggestionRanker>()
                .AddSingleton<Wavee.UI.Services.Search.OmnibarSuggestionCache>()
                .AddSingleton<Services.ISpotifyLinkPreviewService, Services.SpotifyLinkPreviewService>()
                .AddSingleton<ITrackDescriptorFetcher, Data.Contexts.TrackDescriptorFetcher>()

                // Lyrics
                .AddSingleton<ILyricsService>(sp =>
                    new LyricsService(
                        sp.GetRequiredService<ISession>(),
                        sp.GetService<IMetadataDatabase>(),
                        sp.GetService<ISettingsService>(),
                        sp.GetService<ILogger<LyricsService>>(),
                        cacheCapacities.LyricsMemoryCacheCapacity))
                .AddSingleton<LyricsViewModel>()
                .AddTransient<LyricsAiPanelViewModel>()
                .AddSingleton<ITrackCreditsService, Data.Contexts.TrackCreditsService>()
                .AddSingleton<Wavee.UI.Contracts.ITrackDetailsService, Data.Contexts.TrackDetailsService>()
                .AddSingleton<TrackDetailsViewModel>()

                // ViewModels
                .AddSingleton<MainWindowViewModel>()
                .AddSingleton<ShellViewModel>()
                .AddSingleton<PlayerBarViewModel>()
                .AddTransient<HomeViewModel>(sp =>
                    new HomeViewModel(
                        sp.GetService<IHomeFeedService>(),
                        sp.GetService<ISettingsService>(),
                        sp.GetService<HomeFeedCache>(),
                        sp.GetService<RecentlyPlayedService>(),
                        sp.GetService<HomeResponseParserFactory>(),
                        sp.GetService<IAuthState>(),
                        sp.GetService<ILogger<HomeViewModel>>(),
                        GetLocalLibraryService(sp)))
                .AddTransient<ArtistViewModel>()
                .AddTransient<ArtistDiscographyPageViewModel>()
                .AddTransient<AlbumViewModel>()
                .AddTransient<ShowViewModel>()
                .AddTransient<EpisodePageViewModel>()
                .AddTransient<PodcastBrowseViewModel>()
                .AddTransient<BrowseViewModel>()
                .AddTransient<LibraryPageViewModel>()
                .AddTransient<AlbumsLibraryViewModel>()
                .AddTransient<ArtistsLibraryViewModel>()
                .AddTransient<LikedSongsViewModel>()
                .AddTransient<RecentlyPlayedViewModel>()
                .AddTransient<YourEpisodesViewModel>()
                .AddTransient<PlaylistViewModel>()
                .AddTransient<CreatePlaylistViewModel>()
                .AddTransient<ProfileViewModel>()
                .AddTransient<SpotifyConnectViewModel>()
                // Mini-player VM. Singleton so it keeps its subscriptions
                // stable across page navigation (mounted at shell level).
                .AddSingleton<MiniVideoPlayerViewModel>()
                .AddTransient<SearchViewModel>(sp =>
                    new SearchViewModel(
                        sp.GetRequiredService<Wavee.UI.Contracts.ISearchService>(),
                        sp.GetRequiredService<IPlaybackStateService>(),
                        sp.GetService<ILogger<SearchViewModel>>(),
                        GetLocalLibraryService(sp)))
                .AddTransient<DebugViewModel>()
                .AddTransient<FeedbackViewModel>()
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
                        sp.GetService<IPlaybackStateService>(),
                        sp.GetService<ISession>(),
                        sp.GetRequiredService<IUpdateService>(),
                        sp.GetService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>(),
                        sp.GetService<CommunityToolkit.Mvvm.Messaging.IMessenger>(),
                        sp.GetService<INotificationService>(),
                        sp.GetService<ILogger<SettingsViewModel>>(),
                        AppFeatureFlags.LocalFilesEnabled ? sp.GetService<LocalFilesViewModel>() : null))
                .AddTransient<LocalFilesViewModel>()
                .AddTransient<LocalLibraryViewModel>(sp =>
                    new LocalLibraryViewModel(
                        GetLocalLibraryService(sp),
                        sp.GetService<ILogger<LocalLibraryViewModel>>()))

                // Wavee.Local supporting services + UI facade (v17 redesign)
                .AddSingleton<Wavee.Local.Subtitles.IEmbeddedTrackProber, Services.MediaFoundationEmbeddedTrackProber>()
                .AddSingleton<Wavee.Local.Groups.LocalGroupService>(sp =>
                {
                    var dbPath = System.IO.Path.Combine(AppPaths.AppDataDirectory, "metadata.db");
                    var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                        Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
                    }.ConnectionString;
                    return new Wavee.Local.Groups.LocalGroupService(cs, sp.GetService<ILogger<Wavee.Local.Groups.LocalGroupService>>());
                })
                // Per-user TMDB bearer token, DPAPI-encrypted at rest.
                // BYO model — every user pastes their own free TMDB token in
                // Settings. The enrichment service spins its worker up only
                // when a token is present.
                .AddSingleton<Wavee.Local.Enrichment.ITmdbTokenStore, Services.DpapiTmdbTokenStore>()
                // Spotify search wrapper — feeds the music-enrichment path
                // (Continuation 6: dropped MusicBrainz in favour of Spotify
                // because we're already authenticated + their catalog wins).
                .AddSingleton<Wavee.Local.Enrichment.ISpotifyTrackSearcher, Services.PathfinderSpotifyTrackSearcher>()
                .AddSingleton<Wavee.Local.Enrichment.ILocalEnrichmentService>(sp =>
                    new Wavee.Local.Enrichment.LocalEnrichmentService(
                        (Wavee.Local.LocalLibraryService)sp.GetRequiredService<Wavee.Local.ILocalLibraryService>(),
                        sp.GetRequiredService<Wavee.Local.Enrichment.ITmdbTokenStore>(),
                        spotifySearcher: sp.GetService<Wavee.Local.Enrichment.ISpotifyTrackSearcher>(),
                        httpClient: sp.GetService<System.Net.Http.IHttpClientFactory>()?.CreateClient("enrichment"),
                        logger: sp.GetService<ILogger<Wavee.Local.Enrichment.LocalEnrichmentService>>()))
                .AddSingleton<Wavee.UI.Library.Local.ILocalLibraryFacade>(sp =>
                    new Services.LocalLibraryFacade(
                        (Wavee.Local.LocalLibraryService)sp.GetRequiredService<Wavee.Local.ILocalLibraryService>(),
                        sp.GetRequiredService<Wavee.Local.ILocalLikeService>(),
                        sp.GetRequiredService<Wavee.Local.Enrichment.ILocalEnrichmentService>(),
                        sp.GetRequiredService<Wavee.Local.Groups.LocalGroupService>(),
                        metadataClient: sp.GetService<Wavee.Core.Http.IExtendedMetadataClient>(),
                        httpClientFactory: sp.GetService<System.Net.Http.IHttpClientFactory>(),
                        logger: sp.GetService<ILogger<Services.LocalLibraryFacade>>()))
                // Subscribes to LocalMediaPlayer state and persists resume position +
                // flips watched_at + records local_plays. Singleton so the subscription
                // lives for the lifetime of the app.
                .AddSingleton<Services.LocalPlaybackProgressTracker>(sp =>
                    new Services.LocalPlaybackProgressTracker(
                        sp.GetRequiredService<Wavee.Audio.ILocalMediaPlayer>(),
                        sp.GetRequiredService<Wavee.UI.Library.Local.ILocalLibraryFacade>(),
                        sp.GetService<ILogger<Services.LocalPlaybackProgressTracker>>()))

                // v17 redesign ViewModels — Local/ family
                .AddTransient<ViewModels.Local.LocalLandingViewModel>(sp =>
                    new ViewModels.Local.LocalLandingViewModel(
                        GetLocalLibraryFacade(sp),
                        sp.GetService<ILogger<ViewModels.Local.LocalLandingViewModel>>()))
                .AddTransient<ViewModels.Local.LocalShowsViewModel>(sp =>
                    new ViewModels.Local.LocalShowsViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalShowDetailViewModel>(sp =>
                    new ViewModels.Local.LocalShowDetailViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalMoviesViewModel>(sp =>
                    new ViewModels.Local.LocalMoviesViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalMovieDetailViewModel>(sp =>
                    new ViewModels.Local.LocalMovieDetailViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalPersonDetailViewModel>(sp =>
                    new ViewModels.Local.LocalPersonDetailViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalMusicViewModel>(sp =>
                    new ViewModels.Local.LocalMusicViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalMusicVideosViewModel>(sp =>
                    new ViewModels.Local.LocalMusicVideosViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalOtherViewModel>(sp =>
                    new ViewModels.Local.LocalOtherViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalLikedSongsViewModel>(sp =>
                    new ViewModels.Local.LocalLikedSongsViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalCollectionDetailViewModel>(sp =>
                    new ViewModels.Local.LocalCollectionDetailViewModel(GetLocalLibraryFacade(sp)))
                .AddTransient<ViewModels.Local.LocalItemDetailFlyoutViewModel>(sp =>
                    new ViewModels.Local.LocalItemDetailFlyoutViewModel(
                        GetLocalLibraryFacade(sp),
                        sp.GetService<ILogger<ViewModels.Local.LocalItemDetailFlyoutViewModel>>()))

                // Drag & drop — visual-state singleton (unchanged), framework-neutral
                // registry, and the WinUI→Library mediator adapter. Routes are wired
                // up at resolve time so handlers can capture their service deps.
                .AddSingleton<DragStateService>()
                .AddSingleton<IPlaylistDragDropMediator, LibraryPlaylistMediator>()
                .AddSingleton<IDragDropService>(sp =>
                {
                    var lib = sp.GetRequiredService<IPlaylistDragDropMediator>();
                    var play = sp.GetRequiredService<IPlaybackService>();
                    return new DragDropService()
                        // Tracks → playlist row (add). Owner gating happens server-side.
                        .Register(DragPayloadKind.Tracks, DropTargetKind.PlaylistRow,
                            AddTracksToPlaylistHandler.CanDrop,
                            (c, ct) => AddTracksToPlaylistHandler.HandleAsync(lib, c, ct))
                        // Tracks → same playlist (reorder).
                        .Register(DragPayloadKind.Tracks, DropTargetKind.PlaylistTrackList,
                            ReorderPlaylistTracksHandler.CanDrop,
                            (c, ct) => ReorderPlaylistTracksHandler.HandleAsync(lib, c, ct))
                        // Tracks → queue / now-playing area.
                        .Register(DragPayloadKind.Tracks, DropTargetKind.Queue,
                            canDrop: null,
                            (c, ct) => EnqueueTracksHandler.HandleAsync(play, c, ct))
                        .Register(DragPayloadKind.Tracks, DropTargetKind.NowPlaying,
                            canDrop: null,
                            (c, ct) => EnqueueTracksHandler.HandleAsync(play, c, ct))
                        // Card → now-playing area: switch context.
                        .Register(DragPayloadKind.Album,    DropTargetKind.NowPlaying, null, (c, ct) => SwitchContextHandler.HandleAsync(play, c, ct))
                        .Register(DragPayloadKind.Playlist, DropTargetKind.NowPlaying, null, (c, ct) => SwitchContextHandler.HandleAsync(play, c, ct))
                        .Register(DragPayloadKind.Artist,   DropTargetKind.NowPlaying, null, (c, ct) => SwitchContextHandler.HandleAsync(play, c, ct))
                        // Card → queue: enqueue the context's tracks (deferred — context
                        // expansion needs the library service; the handler short-circuits
                        // when it can't expand).
                        .Register(DragPayloadKind.Album,    DropTargetKind.Queue, null, (c, ct) => SwitchContextHandler.EnqueueAsync(play, c, ct))
                        .Register(DragPayloadKind.Playlist, DropTargetKind.Queue, null, (c, ct) => SwitchContextHandler.EnqueueAsync(play, c, ct))
                        // Album / Artist / Liked / Show → playlist row append tracks.
                        // Playlist → playlist row is position-aware: center
                        // appends tracks, top/bottom reorders the playlist in
                        // the sidebar rootlist.
                        .Register(DragPayloadKind.Album,      DropTargetKind.PlaylistRow,
                            AddContextTracksToPlaylistHandler.CanDrop,
                            (c, ct) => AddContextTracksToPlaylistHandler.HandleAsync(lib, c, ct))
                        .Register(DragPayloadKind.Artist,     DropTargetKind.PlaylistRow,
                            AddContextTracksToPlaylistHandler.CanDrop,
                            (c, ct) => AddContextTracksToPlaylistHandler.HandleAsync(lib, c, ct))
                        .Register(DragPayloadKind.Playlist,   DropTargetKind.PlaylistRow,
                            SidebarReorderHandler.CanDropPlaylistOnPlaylistRow,
                            (c, ct) => SidebarReorderHandler.PlaylistOnPlaylistRowAsync(lib, c, ct))
                        .Register(DragPayloadKind.LikedSongs, DropTargetKind.PlaylistRow,
                            AddContextTracksToPlaylistHandler.CanDrop,
                            (c, ct) => AddContextTracksToPlaylistHandler.HandleAsync(lib, c, ct))
                        .Register(DragPayloadKind.Show,       DropTargetKind.PlaylistRow,
                            AddContextTracksToPlaylistHandler.CanDrop,
                            (c, ct) => AddContextTracksToPlaylistHandler.HandleAsync(lib, c, ct))
                        // Liked / Show on Queue + NowPlaying — Spotify Connect
                        // resolves spotify:collection and spotify:show:* as
                        // playable contexts.
                        .Register(DragPayloadKind.LikedSongs, DropTargetKind.Queue,      null, (c, ct) => SwitchContextHandler.EnqueueAsync(play, c, ct))
                        .Register(DragPayloadKind.Show,       DropTargetKind.Queue,      null, (c, ct) => SwitchContextHandler.EnqueueAsync(play, c, ct))
                        .Register(DragPayloadKind.LikedSongs, DropTargetKind.NowPlaying, null, (c, ct) => SwitchContextHandler.HandleAsync(play, c, ct))
                        .Register(DragPayloadKind.Show,       DropTargetKind.NowPlaying, null, (c, ct) => SwitchContextHandler.HandleAsync(play, c, ct))
                        // Card → sidebar folder row: nest the playlist into the folder.
                        .Register(DragPayloadKind.Playlist, DropTargetKind.FolderRow,
                            canDrop: null,
                            (c, ct) => SidebarReorderHandler.NestPlaylistAsync(lib, c, ct))
                        // Sidebar row → another sidebar row (reorder) or folder (nest).
                        .Register(DragPayloadKind.SidebarItem, DropTargetKind.PlaylistRow,
                            canDrop: null,
                            (c, ct) => SidebarReorderHandler.ReorderAsync(lib, c, ct))
                        .Register(DragPayloadKind.SidebarItem, DropTargetKind.FolderRow,
                            canDrop: null,
                            (c, ct) => SidebarReorderHandler.NestOrReorderAsync(lib, c, ct))
                        .Register(DragPayloadKind.SidebarItem, DropTargetKind.SidebarRoot,
                            canDrop: null,
                            (c, ct) => SidebarReorderHandler.MoveToRootAsync(lib, c, ct));
                })

                // Utilities
                .AddSingleton<AppModel>()

                // Image cache cleanup adapter
                .AddSingleton<ICleanableCache, ImageCacheCleanupAdapter>()
                .AddSingleton<ICleanableCache, PageHostCacheCleanupAdapter>()
                .AddSingleton<MemoryBudgetService>()

                // Rich-type detail-page hot caches. Wired into ArtistStore /
                // AlbumStore / PlaylistStore's ReadHotAsync/WriteHot so a
                // re-navigation after the EntityStore Slot evicts (MaxSlots=64
                // LRU) hits memory instead of re-fetching from Pathfinder. The
                // lean *CacheEntry HotCaches registered upstream in Wavee.Core
                // are a separate concern (search results / sidebar tiles).
                // Sizes reuse WaveeCacheOptions to keep one tuning surface.
                .AddSingleton<Wavee.Core.Storage.Abstractions.IHotCache<Wavee.UI.Contracts.ArtistOverviewResult>>(sp =>
                {
                    var opts = sp.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
                    return new Wavee.Core.Storage.HotCache<Wavee.UI.Contracts.ArtistOverviewResult>(
                        opts.ArtistHotCacheSize,
                        sp.GetService<ILogger<Wavee.Core.Storage.HotCache<Wavee.UI.Contracts.ArtistOverviewResult>>>());
                })
                .AddSingleton<ICleanableCache>(sp =>
                    (ICleanableCache)sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Wavee.UI.Contracts.ArtistOverviewResult>>())

                .AddSingleton<Wavee.Core.Storage.Abstractions.IHotCache<Wavee.UI.Contracts.AlbumDetailResult>>(sp =>
                {
                    var opts = sp.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
                    return new Wavee.Core.Storage.HotCache<Wavee.UI.Contracts.AlbumDetailResult>(
                        opts.AlbumHotCacheSize,
                        sp.GetService<ILogger<Wavee.Core.Storage.HotCache<Wavee.UI.Contracts.AlbumDetailResult>>>());
                })
                .AddSingleton<ICleanableCache>(sp =>
                    (ICleanableCache)sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Wavee.UI.Contracts.AlbumDetailResult>>())

                .AddSingleton<Wavee.Core.Storage.Abstractions.IHotCache<Wavee.UI.Models.PlaylistDetailDto>>(sp =>
                {
                    var opts = sp.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
                    return new Wavee.Core.Storage.HotCache<Wavee.UI.Models.PlaylistDetailDto>(
                        opts.PlaylistHotCacheSize,
                        sp.GetService<ILogger<Wavee.Core.Storage.HotCache<Wavee.UI.Models.PlaylistDetailDto>>>());
                })
                .AddSingleton<ICleanableCache>(sp =>
                    (ICleanableCache)sp.GetRequiredService<Wavee.Core.Storage.Abstractions.IHotCache<Wavee.UI.Models.PlaylistDetailDto>>())

                // Memory diagnostics (in-app panel under Settings → Diagnostics).
                // Off the hot path; resolved lazily when the user opens the panel
                // and only samples while it's visible.
                .AddSingleton<Diagnostics.MemoryDiagnosticsService>();

        return builder.Build();
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

                // Forward to the messenger so the SpotifyConnectViewModel
                // can drive the "Starting audio engine…" sub-text in the
                // sign-in dialog without taking a direct dependency on
                // AudioProcessManager.
                try
                {
                    Ioc.Default.GetService<CommunityToolkit.Mvvm.Messaging.IMessenger>()?
                        .Send(new Data.Messages.AudioProcessStateChangedMessage(state.ToString(), message));
                }
                catch { /* best-effort broadcast */ }

                notifDispatcher?.TryEnqueue(() =>
                {
                    var notifService = Ioc.Default.GetService<INotificationService>();
                    var actSvc = Ioc.Default.GetService<Wavee.UI.WinUI.Data.Contracts.IActivityService>();

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

            var settingsForAudioPipeline = Ioc.Default.GetService<ISettingsService>();

            // Audio cache directory: shared between this process (to check cache hits)
            // and AudioHost (to write new downloads and read cached files).
            var audioCacheSettings = settingsForAudioPipeline?.Settings;
            var audioCacheDirectory = audioCacheSettings?.CacheEnabled != false
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Wavee", "AudioCache")
                : null;
            long? audioCacheMaxBytes = audioCacheDirectory != null
                ? Math.Max(1, audioCacheSettings?.CacheSizeLimitBytes ?? 1L * 1024 * 1024 * 1024)
                : null;

            // Now start the audio process (state/error events are already wired above)
            var clusterVol = (int)(session.PlaybackState?.CurrentState?.Volume ?? 0);
            Wavee.AudioIpc.AudioPipelineProxy proxy;
            var pendingHandoff = PendingUiHandoff;
            PendingUiHandoff = null;
            if (pendingHandoff is not null)
            {
                try
                {
                    logger?.LogInformation(
                        "Adopting existing audio process PID={Pid} for user {User}",
                        pendingHandoff.AudioHostProcessId,
                        username);
                    proxy = await _audioProcessManager.AttachAsync(
                        pendingHandoff.AudioHostProcessId,
                        pendingHandoff.PipeName,
                        pendingHandoff.SessionId,
                        pendingHandoff.LaunchToken,
                        username,
                        creds.AuthData,
                        session.Config.DeviceId,
                        initialVolumePercent: clusterVol,
                        audioPreset: audioCacheSettings?.AudioPreset,
                        audioCacheDirectory: audioCacheDirectory,
                        audioCacheMaxBytes: audioCacheMaxBytes,
                        connectTimeout: TimeSpan.FromSeconds(90),
                        ct: CancellationToken.None);
                    pendingHandoff.TryDeleteFile();
                }
                catch (Exception ex)
                {
                    pendingHandoff.TryDeleteFile();
                    logger?.LogWarning(ex, "Audio process adoption failed; starting a new audio process");
                    proxy = await _audioProcessManager.StartAsync(
                        username,
                        creds.AuthData,
                        session.Config.DeviceId,
                        initialVolumePercent: clusterVol,
                        audioPreset: audioCacheSettings?.AudioPreset,
                        audioCacheDirectory: audioCacheDirectory,
                        audioCacheMaxBytes: audioCacheMaxBytes,
                        CancellationToken.None);
                }
            }
            else
            {
                logger?.LogInformation("Starting audio process for user {User}, initialVolume={Vol}%", username, clusterVol);
                proxy = await _audioProcessManager.StartAsync(
                    username,
                    creds.AuthData,
                    session.Config.DeviceId,
                    initialVolumePercent: clusterVol,
                    audioPreset: audioCacheSettings?.AudioPreset,
                    audioCacheDirectory: audioCacheDirectory,
                    audioCacheMaxBytes: audioCacheMaxBytes,
                    CancellationToken.None);
            }
            await ApplyAudioPipelineSettingsAsync(proxy, settingsForAudioPipeline, logger, CancellationToken.None);

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

            // First-run audio runtime initialisation for DRM-protected playback.
            // The provisioner fetches the active manifest, downloads / verifies
            // the support pack on first launch, and returns a RuntimeAsset the
            // deriver can be wired against. On any failure the deriver simply
            // isn't registered and audio key resolution falls back to AP-only.
            try
            {
                var manager = _audioProcessManager;
                Wavee.Core.Audio.RuntimeAsset? runtime = null;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var provisioner = new Wavee.Core.Audio.AudioRuntimeProvisioner(httpClient, logger);
                    runtime = await provisioner.EnsureAvailableAsync(cts.Token).ConfigureAwait(false);
                }

                if (runtime is not null && manager is not null)
                {
                    var deriver = new Wavee.Core.Audio.AudioHostPlayPlayKeyDeriver(
                        spClient,
                        proxyResolver: () => manager.Proxy,
                        runtime: runtime,
                        cacheService: cacheService,
                        logger: logger);
                    session.SetPlayPlayKeyDeriver(deriver);
                    logger?.LogInformation(
                        "audio runtime ready (pack v{Version} at {Path})",
                        runtime.Config.Version, runtime.Path);
                }
                else
                {
                    logger?.LogInformation("audio runtime unavailable; PlayPlay disabled this session");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "audio runtime initialisation skipped; AP-only mode");
            }

            // Resolve the head-files URL template lazily from the session so we pick
            // up the CDN host Spotify hands us in ProductInfo (e.g.
            // heads-fa-tls13.spotifycdn.com) instead of the legacy hardcoded host.
            var headFileClient = new Wavee.Core.Audio.HeadFileClient(
                httpClient,
                logger,
                urlTemplateResolver: () => session.UserData?.HeadFilesUrl);
            var preferredAudioQuality = MapAudioQualitySetting(settingsForAudioPipeline?.Settings.AudioQuality);
            var trackResolver = new Wavee.Audio.TrackResolver(
                session, spClient, headFileClient, httpClient,
                preferredAudioQuality,
                extMetadataClient, cacheService, logger,
                audioCacheDirectory: audioCacheDirectory,
                videoManifestCache: Ioc.Default.GetService<Wavee.Core.Video.IVideoManifestCache>());

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
                localLibrary: GetLocalLibraryService(),
                localMediaPlayer: GetLocalMediaPlayer(),
                spotifyVideoPlayback: Ioc.Default.GetService<Wavee.Audio.ISpotifyVideoPlayback>(),
                localSpotifyPlaybackEnabled: session.Config.LocalSpotifyPlaybackEnabled,
                contentFilter: Ioc.Default.GetService<Wavee.Audio.IPlaybackContentFilter>());

            // Honor the user's autoplay preference. Read fresh on each check so
            // a toggle in the Settings page takes effect immediately — no event
            // plumbing / debounce needed.
            var settingsForAutoplay = Ioc.Default.GetService<ISettingsService>();
            if (settingsForAutoplay is not null)
                orchestrator.AutoplayEnabledProvider = () => settingsForAutoplay.Settings.AutoplayEnabled;

            // Surface hidden-track filtering to the user via toast. The
            // orchestrator emits an event whenever it drops at least one
            // hidden track from a play path; phrasing differs per surface.
            WireHiddenTrackFilterToasts(orchestrator);

            // Wire up orchestrator (not raw proxy) as the local engine
            var executor = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
            executor?.EnableLocalPlayback(orchestrator);
            executor?.EnableAudioPipelineControl(proxy);
            // Hand the process manager to the executor so play-time can demand a
            // restart when the IPC pipe has died (the existing ProxyRestarted
            // handler still rewires EnableLocalPlayback after the restart succeeds).
            if (_audioProcessManager is not null)
                executor?.AttachAudioProcessManager(_audioProcessManager);

            // Force-resolve LocalPlaybackProgressTracker so it subscribes to the
            // media-player state stream. Without this the singleton stays unbuilt
            // and resume position / watched_at never get persisted.
            if (AppFeatureFlags.LocalFilesEnabled)
                _ = Ioc.Default.GetService<Services.LocalPlaybackProgressTracker>();

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
                        iconGlyph: Styles.FluentGlyphs.Accept,
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
                        localLibrary: GetLocalLibraryService(),
                        localMediaPlayer: GetLocalMediaPlayer(),
                        spotifyVideoPlayback: Ioc.Default.GetService<Wavee.Audio.ISpotifyVideoPlayback>(),
                        localSpotifyPlaybackEnabled: session.Config.LocalSpotifyPlaybackEnabled,
                        contentFilter: Ioc.Default.GetService<Wavee.Audio.IPlaybackContentFilter>());
                    if (settingsForAutoplay is not null)
                        newOrch.AutoplayEnabledProvider = () => settingsForAutoplay.Settings.AutoplayEnabled;
                    WireHiddenTrackFilterToasts(newOrch);
                    var exec = Ioc.Default.GetService<IPlaybackCommandExecutor>() as ConnectCommandExecutor;
                    exec?.EnableLocalPlayback(newOrch);
                    exec?.EnableAudioPipelineControl(newProxy);
                    _ = ApplyAudioPipelineSettingsAsync(newProxy, settingsForAutoplay, logger, CancellationToken.None);
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
    /// Subscribes the global <see cref="INotificationService"/> to the
    /// orchestrator's hidden-track-filtered stream so the user sees a toast
    /// whenever the filter drops a track from a play path. Phrasing depends
    /// on which surface fired (manual play vs. PlayNext vs. Add-to-queue vs.
    /// autoplay-all-hidden).
    /// </summary>
    private static void WireHiddenTrackFilterToasts(Wavee.Audio.PlaybackOrchestrator orchestrator)
    {
        var sub = orchestrator.HiddenTracksFiltered.Subscribe(evt =>
        {
            var dispatcher = _uiDispatcher;
            dispatcher?.TryEnqueue(() =>
            {
                var notifications = Ioc.Default.GetService<Wavee.UI.WinUI.Data.Contracts.INotificationService>();
                if (notifications is null) return;

                var (text, severity) = evt.Surface switch
                {
                    Wavee.Audio.HiddenFilterSurface.PlayContext => (
                        evt.DroppedCount == 1
                            ? "Skipped 1 hidden track"
                            : $"Skipped {evt.DroppedCount} hidden tracks",
                        Wavee.UI.WinUI.Data.Models.NotificationSeverity.Informational),
                    Wavee.Audio.HiddenFilterSurface.PlayNext => (
                        "That track is hidden — not queued",
                        Wavee.UI.WinUI.Data.Models.NotificationSeverity.Informational),
                    Wavee.Audio.HiddenFilterSurface.AddToQueue => (
                        "That track is hidden — not queued",
                        Wavee.UI.WinUI.Data.Models.NotificationSeverity.Informational),
                    Wavee.Audio.HiddenFilterSurface.Autoplay => (
                        "Autoplay had no eligible tracks (all were hidden)",
                        Wavee.UI.WinUI.Data.Models.NotificationSeverity.Warning),
                    _ => ((string?)null, Wavee.UI.WinUI.Data.Models.NotificationSeverity.Informational),
                };
                if (text is not null)
                    notifications.Show(text, severity, TimeSpan.FromSeconds(3));
            });
        });
        _appSubscriptions.Add(sub);
    }

    private static async Task ApplyAudioPipelineSettingsAsync(
        Wavee.AudioIpc.AudioPipelineProxy proxy,
        ISettingsService? settingsService,
        Microsoft.Extensions.Logging.ILogger? logger,
        CancellationToken ct)
    {
        if (settingsService is null)
            return;

        try
        {
            var settings = settingsService.Settings;
            await proxy.SetNormalizationEnabledAsync(settings.NormalizationEnabled, ct).ConfigureAwait(false);
            await proxy.SetEqualizerAsync(settings.EqualizerEnabled, settings.EqualizerBandGains, ct)
                .ConfigureAwait(false);

            logger?.LogInformation(
                "AudioHost pipeline settings applied: normalization={Normalization}, eq={Eq}, bands={Bands}",
                settings.NormalizationEnabled,
                settings.EqualizerEnabled,
                settings.EqualizerBandGains?.Length ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to apply saved AudioHost pipeline settings");
        }
    }

    private static Wavee.Core.Audio.AudioQuality MapAudioQualitySetting(string? quality)
    {
        if (string.Equals(quality, "Normal", StringComparison.OrdinalIgnoreCase))
            return Wavee.Core.Audio.AudioQuality.Normal;
        if (string.Equals(quality, "High", StringComparison.OrdinalIgnoreCase))
            return Wavee.Core.Audio.AudioQuality.High;
        return Wavee.Core.Audio.AudioQuality.VeryHigh;
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
