using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Configure dependency injection
        _host = AppLifecycleHelper.ConfigureHost();
        Ioc.Default.ConfigureServices(_host.Services);

        // Get AppModel instance
        AppModel = Ioc.Default.GetRequiredService<AppModel>();

        // Launch main window
        MainWindow.Instance.Activate();
        _ = MainWindow.Instance.InitializeApplicationAsync();
    }
}
