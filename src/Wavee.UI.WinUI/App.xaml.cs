using System;
using Wavee.Audio;
using System.Collections.Immutable;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Wavee.Core.Storage;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services;
using UnhandledExceptionEventArgs = System.UnhandledExceptionEventArgs;

namespace Wavee.UI.WinUI;

public partial class App : Application
{
    private static IHost? _host;
    private static readonly SemaphoreSlim _shutdownGate = new(1, 1);
    private static Task? _shutdownTask;

    public static AppModel AppModel { get; private set; } = null!;

    public App()
    {
        AppLocalization.ApplyLanguageOverride(SettingsService.PeekLanguage());
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogUnhandledException("XamlUnhandledException", e.Exception);

        e.Handled = true; // Prevent crash — let the app try to continue
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => LogUnhandledException(
            $"AppDomainUnhandledException (IsTerminating={e.IsTerminating})",
            e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown exception object"));

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandledException("TaskSchedulerUnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void LogUnhandledException(string source, Exception ex)
    {
        // Keep crash logging allocation-light and resilient: this can run while the process
        // is already unstable or terminating on a background thread.
        try
        {
            var summary = $"{source}: {ex.GetType().Name}: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"*** {summary}");
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                System.Diagnostics.Debug.WriteLine($"*** Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
                System.Diagnostics.Debug.WriteLine($"*** Inner: {ex.InnerException}");
        }
        catch
        {
            // Best effort only.
        }

        try
        {
            var crashLog = AppPaths.CrashLogPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLog)!);
            var redactedMessage = PiiRedactor.Redact(ex.Message);
            var redactedStack = PiiRedactor.Redact(ex.StackTrace ?? string.Empty);
            var redactedInner = ex.InnerException != null
                ? PiiRedactor.Redact(ex.InnerException.ToString())
                : string.Empty;

            System.IO.File.AppendAllText(
                crashLog,
                $"\n[{System.DateTime.UtcNow:O}] [{source}] {ex.GetType().Name}: {redactedMessage}\n{redactedStack}\n" +
                (string.IsNullOrWhiteSpace(redactedInner) ? "" : $"Inner: {redactedInner}\n"));
        }
        catch
        {
            // Best effort only.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // App-notification activation must be wired BEFORE we touch
        // AppInstance.GetActivatedEventArgs() — the WinAppSDK docs are emphatic
        // about this ordering. Subscribing to NotificationInvoked + calling
        // Register() turns the COM activation kind into our NotificationInvoked
        // handler instead of a silent no-op.
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.NotificationInvoked += OnAppNotificationInvoked;
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            // Notification registration failure must never block app start —
            // the rest of the app works fine without toast deep-linking.
            LogUnhandledException("AppNotificationRegister", ex);
        }

        // Configure dependency injection
        _host = AppLifecycleHelper.ConfigureHost();
        Ioc.Default.ConfigureServices(_host.Services);

        // Force-resolve MetadataDatabase up-front so any schema migration
        // runs *before* the main UI comes up. On failure we surface a
        // blocking error window instead of silently dropping caches — users
        // get to decide whether to rebuild (losing cached library data) or
        // quit and try again later.
        try
        {
            // Registered as IMetadataDatabase singleton; resolving it triggers
            // the MetadataDatabase ctor (and the migration runner). The ctor
            // throws MetadataMigrationException on schema failure, which DI
            // wraps in a trivial initializer exception — unwrap to match.
            _ = Ioc.Default.GetRequiredService<Wavee.Core.Storage.Abstractions.IMetadataDatabase>();
        }
        catch (Exception ex) when (UnwrapMigrationException(ex) is MetadataMigrationException migrationEx)
        {
            LogUnhandledException("MetadataMigration", migrationEx);
            ShowMigrationErrorWindow(migrationEx);
            return;
        }

        // Get AppModel instance
        AppModel = Ioc.Default.GetRequiredService<AppModel>();

        // Start background cache cleanup
        Ioc.Default.GetRequiredService<CacheCleanupService>().Start();
        Ioc.Default.GetRequiredService<MemoryBudgetService>().Start();

        // Local file library: point the image pipeline at the cache directory
        // and kick off the indexer (initial scan + watcher + periodic loop).
        // The migration above guaranteed the v15 tables exist.
        if (Services.AppFeatureFlags.LocalFilesEnabled)
        {
            try
            {
                var cacheOpts = Ioc.Default.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
                Wavee.UI.WinUI.Helpers.SpotifyImageHelper.LocalArtworkRoot = cacheOpts.LocalArtworkDirectory;

                var indexer = Ioc.Default.GetRequiredService<Wavee.Local.LocalIndexerHostedService>();
                _ = indexer.StartAsync();
            }
            catch (Exception ex)
            {
                // Local library is best-effort — never block app launch on it.
                LogUnhandledException("LocalIndexerStart", ex);
            }
        }

        // Auto-navigate to the video player when a video track becomes active.
        // The orchestrator dispatches video files to ILocalMediaPlayer; this
        // surfaces those plays in a dedicated page so the user actually sees
        // the frames. Idempotent: tracks that share a single playback session
        // (e.g. seeking, pausing) don't re-navigate.
        if (Services.AppFeatureFlags.LocalFilesEnabled)
        {
            try
            {
                var videoEngine = Ioc.Default.GetService<Wavee.Audio.ILocalMediaPlayer>();
                if (videoEngine is not null)
                {
                    string? lastNavigatedUri = null;
                    // Mini-as-default routing: the floating MiniVideoPlayer
                    // in the shell handles surfacing whenever video playback
                    // becomes active. Auto-navigating to the fullscreen page
                    // hijacks whatever the user was browsing — instead the
                    // Mini appears bottom-right and the user clicks it to
                    // expand via ConnectedAnimation. The subscription is
                    // kept (no functional behaviour) so any future per-track
                    // hooks have a single place to land.
                    videoEngine.StateChanges.Subscribe(state =>
                    {
                        var uri = state.TrackUri;
                        if (string.IsNullOrEmpty(uri)) { lastNavigatedUri = null; return; }
                        lastNavigatedUri = uri;
                        // Consume the suppressor token so callers that rely
                        // on "didn't auto-nav this URI" semantics stay correct.
                        VideoAutoNavigationSuppressor.TryConsume(uri);
                    });
                }
            }
            catch (Exception ex)
            {
                LogUnhandledException("VideoAutoNavSetup", ex);
            }
        }

        // Resume the memory-diagnostics periodic logger if it was enabled in a prior session.
        // Cheap when off (single bool check); when on it samples every 30s and appends to
        // %AppData%\Wavee\diag\memory-{date}.csv so leak hunts persist across the panel
        // being open or closed.
        if (Services.AppFeatureFlags.DiagnosticsEnabled
            && Ioc.Default.GetService<Wavee.UI.WinUI.Data.Contracts.ISettingsService>()?.Settings.MemoryDiagnosticsEnabled == true)
        {
            AppLifecycleHelper.SetMemoryDiagnostics(true);
        }

        // Launch main window FIRST — these singletons used to be resolved
        // eagerly here, blocking first paint by 30-90 ms while their
        // constructors wired up IMessenger handlers + dealer subscriptions.
        // None of them produce user-visible state on the first frame, so
        // defer to a Low-priority dispatcher tick after Activate. The
        // existing IsZeroRevisionCounter check elsewhere doesn't depend on
        // them being live before window paint.
        MainWindow.Instance.Activate();
        _ = MainWindow.Instance.InitializeApplicationAsync();

        MainWindow.Instance.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                // LibrarySyncOrchestrator + IActivityService register IMessenger
                // handlers in their ctors; UiOperationProfiler sets a static
                // Instance for hot-path access. All three are subscribers, not
                // producers — being live ~50 ms after first paint is safe.
                Ioc.Default.GetRequiredService<Data.Contexts.LibrarySyncOrchestrator>();
                Ioc.Default.GetRequiredService<Data.Contracts.IActivityService>();
                Ioc.Default.GetRequiredService<Services.UiOperationProfiler>();
                Ioc.Default.GetRequiredService<Diagnostics.NavigationDiagnostics>();
            });

        // (Post-startup forced Gen2 compact removed. It produced one of the ~65
        // forced Gen2 compacts per session that were responsible for navigation
        // stalls and a pathological Gen0 ≈ Gen1 ≈ Gen2 counter ratio. DATAS GC
        // self-tunes the post-startup slack as part of its normal heap-count
        // adaptation — no manual hint needed.)
    }

    /// <summary>
    /// DI factory exceptions wrap the original throw in
    /// <c>InvalidOperationException</c> (or similar). Walk the inner chain
    /// looking for our typed marker so we can present the right UI.
    /// </summary>
    private static MetadataMigrationException? UnwrapMigrationException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is MetadataMigrationException mig) return mig;
            ex = ex.InnerException;
        }
        return null;
    }

    private void ShowMigrationErrorWindow(MetadataMigrationException migrationEx)
    {
        var window = new Views.MigrationErrorWindow(
            migrationEx,
            onRebuild: () => OnUserChoseRebuild(migrationEx),
            onQuit: OnUserChoseQuit);
        window.Activate();
    }

    private void OnUserChoseRebuild(MetadataMigrationException migrationEx)
    {
        // Downgrade failures can't be fixed by rebuilding — the DB on disk
        // was made by a newer Wavee, rebuilding would still just recreate a
        // v{Actual} DB next time that newer Wavee opens it. Treat Rebuild as
        // "Quit" in that specific case (button copy already set to Quit by
        // the window when reason is Downgrade).
        if (migrationEx.Reason == MetadataMigrationFailureReason.Downgrade)
        {
            OnUserChoseQuit();
            return;
        }

        try
        {
            // Find the DB path through the already-constructed options — the
            // host was built successfully; only the DB ctor threw. Safe to
            // resolve WaveeCacheOptions without re-triggering MetadataDatabase.
            var opts = Ioc.Default.GetRequiredService<Wavee.Core.DependencyInjection.WaveeCacheOptions>();
            var dbPath = opts.DatabasePath;

            // Tear down the host so the DB singleton's (failed) connection
            // strings and any open handles are released before we delete.
            _host?.Dispose();
            _host = null;

            MetadataDatabase.DeleteDatabaseFile(dbPath);

            // Re-enter launch. The next DB resolve sees a missing file and
            // creates a fresh v{CurrentSchemaVersion} schema.
            OnLaunched(null!);
        }
        catch (Exception ex)
        {
            LogUnhandledException("MetadataRebuild", ex);
            OnUserChoseQuit();
        }
    }

    private void OnUserChoseQuit()
    {
        try { _host?.Dispose(); } catch { /* best-effort */ }
        _host = null;
        Exit();
    }

    internal static Task ShutdownHostAsync()
    {
        lock (_shutdownGate)
        {
            _shutdownTask ??= ShutdownHostCoreAsync();
            return _shutdownTask;
        }
    }

    /// <summary>
    /// Fires when the user clicks an app notification (toast body or button)
    /// while the app is already running. Cold-start activations come through
    /// AppInstance.GetCurrent().GetActivatedEventArgs() in OnLaunched, but for
    /// the AI feature pilot every relevant click happens with the app already
    /// alive (the toast was posted by an in-process service moments earlier).
    ///
    /// We delegate routing to <see cref="AppNotificationActivationRouter"/>
    /// so the action vocabulary stays in one place.
    /// </summary>
    private void OnAppNotificationInvoked(
        Microsoft.Windows.AppNotifications.AppNotificationManager sender,
        Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
    {
        try
        {
            var dispatcher = MainWindow.Instance?.DispatcherQueue;
            AppNotificationActivationRouter.Route(args.Arguments.ToImmutableDictionary(), dispatcher);
        }
        catch (Exception ex)
        {
            LogUnhandledException("AppNotificationInvoked", ex);
        }
    }

    private static async Task ShutdownHostCoreAsync()
    {
        await _shutdownGate.WaitAsync();
        try
        {
            var host = Interlocked.Exchange(ref _host, null);
            if (host == null)
                return;

            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                LogUnhandledException("HostStopAsync", ex);
            }

            try
            {
                host.Dispose();
            }
            catch (Exception ex)
            {
                LogUnhandledException("HostDispose", ex);
            }
        }
        finally
        {
            _shutdownGate.Release();
        }
    }
}
