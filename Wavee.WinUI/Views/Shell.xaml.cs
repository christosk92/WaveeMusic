using System;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.WinUI.Services;
using Wavee.WinUI.ViewModels;

namespace Wavee.WinUI.Views;

/// <summary>
/// Navigation shell with resizable NavigationView pane.
/// </summary>
public sealed partial class Shell : Page
{
    public ShellViewModel ViewModel { get; }
    public App App => App.Instance;

    private readonly ILogger<Shell> _logger;
    private readonly INavigationService _navigationService;

    public Shell()
    {
        _logger = App.Current.Services.GetService(typeof(ILogger<Shell>)) as ILogger<Shell>
            ?? throw new InvalidOperationException("ILogger<Shell> not registered in DI container");

        _logger.LogDebug("Shell constructor starting");
        InitializeComponent();
        _logger.LogDebug("InitializeComponent completed");

        // Get services from DI
        _logger.LogDebug("Resolving INavigationService from DI");
        _navigationService = App.Current.Services.GetService(typeof(INavigationService)) as INavigationService
            ?? throw new InvalidOperationException("INavigationService not registered in DI container");

        _logger.LogDebug("Resolving ShellViewModel from DI");
        ViewModel = App.Current.Services.GetService(typeof(ShellViewModel)) as ShellViewModel
            ?? throw new InvalidOperationException("ShellViewModel not registered in DI container");

        _logger.LogDebug("Services resolved, subscribing to Loaded event");
        Loaded += OnLoaded;
        _logger.LogInformation("Shell constructed successfully");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Shell OnLoaded event fired");

        // Initialize navigation service with the frame
        _logger.LogDebug("Initializing NavigationService with ContentFrame");
        _navigationService.Initialize(ContentFrame);

        // Navigate to Home page by default
        _logger.LogInformation("Navigating to Home page");
        _navigationService.Navigate<Home>();

        // Select the Home item by default
        if (NavView.MenuItems.Count > 0)
        {
            _logger.LogDebug("Setting default selected item in NavView (MenuItems count: {Count})", NavView.MenuItems.Count);
            ViewModel.SelectedItem = NavView.MenuItems[0];
        }
        else
        {
            _logger.LogWarning("NavView has no menu items");
        }

        _logger.LogInformation("Shell initialization completed");
    }

    private void OnNavigationViewItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        _logger.LogDebug("NavigationView item invoked: {InvokedItem}", args.InvokedItem);
        ViewModel.OnNavigationItemInvoked(args);
    }
}
