using System;
using System.Threading.Tasks;
using Wavee.Core.Session;

namespace Wavee.WinUI.Services;

/// <summary>
/// Service for managing authentication state and session lifecycle in the application.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Gets whether the user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the current authenticated user's data, or null if not authenticated.
    /// </summary>
    UserData? CurrentUser { get; }

    /// <summary>
    /// Gets the active Spotify session, or null if not authenticated.
    /// </summary>
    ISession? Session { get; }

    /// <summary>
    /// Event raised when authentication state changes (login/logout).
    /// </summary>
    event EventHandler<AuthenticationStateChangedEventArgs>? AuthenticationStateChanged;

    /// <summary>
    /// Initializes the authentication service by attempting to load cached credentials and auto-login.
    /// Should be called on app startup.
    /// </summary>
    /// <returns>True if auto-login succeeded, false if user needs to login manually.</returns>
    Task<bool> InitializeAsync();

    /// <summary>
    /// Performs the OAuth login flow and establishes an authenticated session.
    /// Opens browser for user authorization.
    /// </summary>
    /// <returns>True if login succeeded, false otherwise.</returns>
    Task<bool> LoginAsync();

    /// <summary>
    /// Logs out the current user by disposing the session and clearing cached credentials.
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Gets the active authenticated session. Throws if not authenticated.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not authenticated.</exception>
    Task<ISession> GetSessionAsync();

    /// <summary>
    /// Starts the device code flow and returns the user code and verification URL for display.
    /// </summary>
    /// <returns>Tuple containing (userCode, verificationUrl) for QR code generation.</returns>
    Task<(string userCode, string verificationUrl)> StartDeviceCodeFlowAsync();

    /// <summary>
    /// Cancels any ongoing device code flow.
    /// </summary>
    Task CancelDeviceCodeFlowAsync();
}

/// <summary>
/// Event args for authentication state changes.
/// </summary>
public class AuthenticationStateChangedEventArgs : EventArgs
{
    public bool IsAuthenticated { get; init; }
    public UserData? User { get; init; }
}
