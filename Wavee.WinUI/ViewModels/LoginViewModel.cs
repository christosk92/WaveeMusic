using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Services;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for the login page that handles OAuth authentication.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;
    private readonly ILogger<LoginViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    private bool _isLoggingIn = false;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _loginButtonText = "Log in with Spotify";

    [ObservableProperty]
    private string? _deviceCode;

    [ObservableProperty]
    private string? _verificationUrl;

    [ObservableProperty]
    private string? _qrCodeImageUrl;

    [ObservableProperty]
    private bool _isDeviceCodeActive = false;

    [ObservableProperty]
    private string _deviceCodeStatusMessage = "Scan QR code to continue";

    /// <summary>
    /// Event raised when login is successful.
    /// </summary>
    public event EventHandler? LoginSuccessful;

    public LoginViewModel(
        IAuthenticationService authService,
        ILogger<LoginViewModel> logger)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Subscribe to authentication state changes for device code completion
        _authService.AuthenticationStateChanged += OnAuthenticationStateChanged;

        // Start device code flow on initialization
        InitializeDeviceCodeAsync().SafeFireAndForget();
    }

    /// <summary>
    /// Handles authentication state changes (for device code flow completion).
    /// </summary>
    private void OnAuthenticationStateChanged(object? sender, AuthenticationStateChangedEventArgs e)
    {
        if (e.IsAuthenticated)
        {
            _logger.LogInformation("Device code authentication completed");

            // Marshal UI updates to the UI thread
            _dispatcherQueue.TryEnqueue(() =>
            {
                DeviceCodeStatusMessage = "Login successful!";
                LoginSuccessful?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    /// <summary>
    /// Initializes the device code flow on page load.
    /// </summary>
    private async Task InitializeDeviceCodeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing device code flow");
            DeviceCodeStatusMessage = "Generating QR code...";

            var (userCode, verificationUrl) = await _authService.StartDeviceCodeFlowAsync();

            DeviceCode = userCode;
            VerificationUrl = verificationUrl;

            // Generate QR code image URL using online API
            QrCodeImageUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=250x250&data={Uri.EscapeDataString(verificationUrl)}";

            IsDeviceCodeActive = true;
            DeviceCodeStatusMessage = "Scan QR code or enter code on your device";

            _logger.LogInformation("Device code flow initialized: {UserCode}", userCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize device code flow");
            DeviceCodeStatusMessage = "Failed to generate QR code. Please use login button.";
            IsDeviceCodeActive = false;
        }
    }

    /// <summary>
    /// Executes the OAuth login flow.
    /// </summary>
    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            IsLoggingIn = true;
            ErrorMessage = null;
            LoginButtonText = "Opening browser...";

            _logger.LogInformation("User initiated login");

            // Cancel any ongoing device code flow before starting OAuth
            await _authService.CancelDeviceCodeFlowAsync();

            // Execute OAuth login flow (this will open the browser)
            var success = await _authService.LoginAsync();

            if (success)
            {
                _logger.LogInformation("Login successful");

                LoginButtonText = "Login successful!";
                await Task.Delay(500); // Brief delay to show success message

                // Notify that login succeeded
                LoginSuccessful?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _logger.LogWarning("Login failed");

                ErrorMessage = "Login failed. Please try again.";
                LoginButtonText = "Log in with Spotify";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login error");

            ErrorMessage = $"Login error: {ex.Message}";
            LoginButtonText = "Log in with Spotify";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }
}
