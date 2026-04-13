using System;
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
        // Configure dependency injection
        _host = AppLifecycleHelper.ConfigureHost();
        Ioc.Default.ConfigureServices(_host.Services);

        // Get AppModel instance
        AppModel = Ioc.Default.GetRequiredService<AppModel>();

        // Start background cache cleanup
        Ioc.Default.GetRequiredService<CacheCleanupService>().Start();

        // Eagerly activate — registers IMessenger handlers
        Ioc.Default.GetRequiredService<Data.Contexts.LibrarySyncOrchestrator>();
        Ioc.Default.GetRequiredService<Data.Contracts.IActivityService>();

        // Eagerly create profiler — sets static Instance for hot-path access
        Ioc.Default.GetRequiredService<Services.UiOperationProfiler>();

        // Launch main window
        MainWindow.Instance.Activate();
        _ = MainWindow.Instance.InitializeApplicationAsync();

        // One-shot post-startup heap compaction. Startup allocates aggressively across
        // many code paths (XAML parse, DI graph construction, library sync, home feed),
        // leaving gen2 + LOH fragmented. Scheduling a single compacting GC ~5s after the
        // window is active reclaims the post-startup slack with one short pause that
        // lands while the user is still orienting themselves — invisible in practice.
        // This is a one-off, not a periodic loop.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(
                    generation: GC.MaxGeneration,
                    mode: GCCollectionMode.Aggressive,
                    blocking: true,
                    compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(
                    generation: GC.MaxGeneration,
                    mode: GCCollectionMode.Aggressive,
                    blocking: true,
                    compacting: true);
            }
            catch
            {
                // Best-effort; if the GC hint fails for any reason we just keep running.
            }
        });
    }

    internal static Task ShutdownHostAsync()
    {
        lock (_shutdownGate)
        {
            _shutdownTask ??= ShutdownHostCoreAsync();
            return _shutdownTask;
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
