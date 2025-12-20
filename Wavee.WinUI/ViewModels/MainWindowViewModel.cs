using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.WinUI.Services;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for the main application window, managing user profile display.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    private string _userInitials = "";

    [ObservableProperty]
    private string _username = "User";

    [ObservableProperty]
    private string? _accountType;

    public MainWindowViewModel(
        IAuthenticationService authService,
        ILogger<MainWindowViewModel> logger)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Subscribe to authentication state changes
        _authService.AuthenticationStateChanged += OnAuthenticationStateChanged;

        // Initialize with current user data
        UpdateUserProfile(_authService.CurrentUser);
    }

    /// <summary>
    /// Updates the user profile display.
    /// </summary>
    private void UpdateUserProfile(UserData? user)
    {
        if (user != null)
        {
            Username = user.Username;
            UserInitials = GetInitials(user.Username);
            AccountType = user.AccountType?.ToString() ?? "Unknown";

            _logger.LogDebug("Updated user profile: {Username}", user.Username);
        }
        else
        {
            Username = "User";
            UserInitials = "?";
            AccountType = null;

            _logger.LogDebug("Cleared user profile");
        }
    }

    /// <summary>
    /// Handles authentication state changes.
    /// </summary>
    private void OnAuthenticationStateChanged(object? sender, AuthenticationStateChangedEventArgs e)
    {
        UpdateUserProfile(e.User);
    }

    /// <summary>
    /// Logs out the current user.
    /// </summary>
    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            _logger.LogInformation("User initiated logout");

            await _authService.LogoutAsync();

            // Note: After logout, the app should close or navigate back to login
            // This will be handled by the Window closing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout failed");
        }
    }

    /// <summary>
    /// Gets initials from username (first 2 characters, uppercase).
    /// </summary>
    private static string GetInitials(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "?";

        // For email addresses, use the part before @
        var displayName = username.Contains('@')
            ? username.Substring(0, username.IndexOf('@'))
            : username;

        // Get first 2 characters, uppercase
        return displayName.Length >= 2
            ? displayName.Substring(0, 2).ToUpperInvariant()
            : displayName.ToUpperInvariant();
    }
}
