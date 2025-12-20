using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.WinUI.ViewModels;

namespace Wavee.WinUI.Views;

/// <summary>
/// Splash screen page that handles authentication initialization.
/// </summary>
public sealed partial class SplashPage : Page
{
    public SplashViewModel ViewModel { get; }

    public SplashPage()
    {
        InitializeComponent();

        // Get ViewModel from DI
        ViewModel = App.Current.Services.GetService(typeof(SplashViewModel)) as SplashViewModel
            ?? throw new System.InvalidOperationException("SplashViewModel not registered in DI container");

        // Subscribe to initialization complete event
        ViewModel.InitializationComplete += OnInitializationComplete;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Start initialization when page loads
        await ViewModel.InitializeAsync();
    }

    private void OnInitializationComplete(object? sender, InitializationCompleteEventArgs e)
    {
        // Unsubscribe from event
        ViewModel.InitializationComplete -= OnInitializationComplete;

        // Navigate to appropriate page
        if (e.IsAuthenticated)
        {
            // User is authenticated, close splash and show main window
            // This will be handled by App.xaml.cs when we modify OnLaunched
            System.Diagnostics.Debug.WriteLine("Splash: User authenticated, ready for main window");
        }
        else
        {
            // User needs to login, navigate to login page
            System.Diagnostics.Debug.WriteLine("Splash: User not authenticated, ready for login page");
        }
    }
}
