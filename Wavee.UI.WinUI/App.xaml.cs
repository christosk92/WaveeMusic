using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Wavee.Core.Storage;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Application;

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
            var crashLog = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Wavee", "crash.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLog)!);
            System.IO.File.AppendAllText(crashLog,
                $"\n[{System.DateTime.UtcNow:O}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n" +
                (ex.InnerException != null ? $"Inner: {ex.InnerException}\n" : ""));
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

        // Launch main window
        MainWindow.Instance.Activate();
        _ = MainWindow.Instance.InitializeApplicationAsync();
    }
}
