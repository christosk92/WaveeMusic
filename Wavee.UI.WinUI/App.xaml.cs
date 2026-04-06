using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Wavee.Core.Storage;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI;

public partial class App : Application
{
    private static IHost? _host;

    public static AppModel AppModel { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Log the actual exception so we can diagnose Release crashes
        var ex = e.Exception;
        System.Diagnostics.Debug.WriteLine($"*** UNHANDLED EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        System.Diagnostics.Debug.WriteLine($"*** Stack: {ex.StackTrace}");
        if (ex.InnerException != null)
            System.Diagnostics.Debug.WriteLine($"*** Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");

        // Write to a crash log file for Release builds
        try
        {
            var crashLog = AppPaths.CrashLogPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLog)!);
            var redactedMessage = PiiRedactor.Redact(ex.Message);
            var redactedStack = PiiRedactor.Redact(ex.StackTrace ?? string.Empty);
            var redactedInner = ex.InnerException != null
                ? PiiRedactor.Redact(ex.InnerException.ToString())
                : string.Empty;

            System.IO.File.AppendAllText(crashLog,
                $"\n[{System.DateTime.UtcNow:O}] {ex.GetType().Name}: {redactedMessage}\n{redactedStack}\n" +
                (string.IsNullOrWhiteSpace(redactedInner) ? "" : $"Inner: {redactedInner}\n"));
        }
        catch { /* best effort */ }

        e.Handled = true; // Prevent crash — let the app try to continue
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

        // Launch main window
        MainWindow.Instance.Activate();
        _ = MainWindow.Instance.InitializeApplicationAsync();
    }
}
