using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.WinUI.Services;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for the splash screen that handles auto-login initialization.
/// </summary>
public sealed partial class SplashViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;
    private readonly ILogger<SplashViewModel> _logger;

    [ObservableProperty]
    private string _statusMessage = "Initializing...";

    [ObservableProperty]
    private bool _isInitializing = true;

    /// <summary>
    /// Event raised when initialization is complete.
    /// </summary>
    public event EventHandler<InitializationCompleteEventArgs>? InitializationComplete;

    public SplashViewModel(
        IAuthenticationService authService,
        ILogger<SplashViewModel> logger)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initializes the authentication service and attempts auto-login.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Splash screen initializing authentication");

            StatusMessage = "Loading cached credentials...";
            await Task.Delay(500); // Brief delay for splash visibility

            // Try to auto-login with cached credentials
            var autoLoginSucceeded = await _authService.InitializeAsync();

            if (autoLoginSucceeded)
            {
                StatusMessage = "Welcome back!";
                await Task.Delay(500); // Brief delay to show success message

                _logger.LogInformation("Auto-login succeeded, navigating to main window");

                // Notify that we're authenticated
                IsInitializing = false;
                InitializationComplete?.Invoke(this, new InitializationCompleteEventArgs { IsAuthenticated = true });
            }
            else
            {
                StatusMessage = "Please login to continue";
                await Task.Delay(300);

                _logger.LogInformation("Auto-login failed, navigating to login page");

                // Notify that we need to login
                IsInitializing = false;
                InitializationComplete?.Invoke(this, new InitializationCompleteEventArgs { IsAuthenticated = false });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Splash initialization failed");

            StatusMessage = "Initialization failed";
            await Task.Delay(1000);

            // Navigate to login on error
            IsInitializing = false;
            InitializationComplete?.Invoke(this, new InitializationCompleteEventArgs { IsAuthenticated = false });
        }
    }
}

/// <summary>
/// Event args for initialization complete.
/// </summary>
public sealed class InitializationCompleteEventArgs : EventArgs
{
    /// <summary>
    /// Gets whether the user is authenticated after initialization.
    /// </summary>
    public bool IsAuthenticated { get; init; }
}
