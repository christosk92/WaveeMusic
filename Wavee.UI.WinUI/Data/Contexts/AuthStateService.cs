using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Authentication;
using Wavee.Core.Session;
using Wavee.OAuth;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Manages the authenticated user's state. Bridges Wavee.Core OAuth/Session to the UI layer.
/// Supports demo mode fallback, cached credential restore, and real OAuth flows.
/// </summary>
internal sealed partial class AuthStateService : ObservableObject, IAuthState, IDisposable
{
    private static readonly string[] OAuthScopes =
        ["streaming", "user-read-playback-state", "user-modify-playback-state",
         "user-read-private", "user-read-email",
         "user-library-read", "playlist-read-private", "playlist-read-collaborative"];

    private readonly IMessenger _messenger;
    private readonly IDataServiceConfiguration? _config;
    private readonly ILogger? _logger;
    private readonly Session? _session;
    private readonly ICredentialsCache? _credentialsCache;
    private readonly SessionConfig? _sessionConfig;
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly System.Net.Http.IHttpClientFactory? _httpClientFactory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthenticated))]
    private AuthStatus _status = AuthStatus.Unknown;

    [ObservableProperty]
    private string? _connectionError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Username))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(ProfileImageUrl))]
    [NotifyPropertyChangedFor(nameof(IsPremium))]
    private UserData? _currentUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPremium))]
    private AccountType? _accountType;

    public string? Username => CurrentUser?.Username;
    public string? DisplayName => CurrentUser?.DisplayName ?? CurrentUser?.Username;
    public string? ProfileImageUrl => CurrentUser?.ProfileImageUrl;

    public bool IsAuthenticated => Status == AuthStatus.Authenticated;
    public bool IsPremium => AccountType == Core.Session.AccountType.Premium;

    public event EventHandler<AuthStatus>? AuthStatusChanged;

    public AuthStateService(
        IMessenger messenger,
        IDataServiceConfiguration? config = null,
        Session? session = null,
        ICredentialsCache? credentialsCache = null,
        SessionConfig? sessionConfig = null,
        IPlaybackStateService? playbackStateService = null,
        System.Net.Http.IHttpClientFactory? httpClientFactory = null,
        ILogger<AuthStateService>? logger = null)
    {
        _messenger = messenger;
        _config = config;
        _session = session;
        _credentialsCache = credentialsCache;
        _sessionConfig = sessionConfig;
        _playbackStateService = playbackStateService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        ConnectionError = null;
        SetStatus(AuthStatus.Authenticating);

        // Demo mode: return mock authenticated state
        if (_config?.IsDemoMode == true || _session == null)
        {
            CurrentUser = new UserData
            {
                Username = "demo_user",
                CountryCode = "US",
                AccountType = Core.Session.AccountType.Premium
            };
            AccountType = Core.Session.AccountType.Premium;
            SetStatus(AuthStatus.Authenticated);
            return true;
        }

        // Already connected
        if (_session.IsConnected())
        {
            await PopulateUserFromSession(ct);
            return true;
        }

        // Try cached credentials with retry
        if (_credentialsCache != null)
        {
            var lastUser = await _credentialsCache.LoadLastUsernameAsync(ct);
            var cached = await _credentialsCache.LoadCredentialsAsync(lastUser, ct);
            if (cached != null)
            {
                var retryDelays = new[] { 0, 2000, 5000 };
                Exception? lastEx = null;

                for (int attempt = 0; attempt < retryDelays.Length; attempt++)
                {
                    try
                    {
                        if (attempt > 0)
                        {
                            _logger?.LogInformation("Connection retry {Attempt}/3...", attempt + 1);
                            await Task.Delay(retryDelays[attempt], ct);
                        }

                        await _session.ConnectAsync(cached, _credentialsCache, ct);
                        await PopulateUserFromSession(ct);
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        _logger?.LogWarning(ex, "Connection attempt {Attempt}/3 failed", attempt + 1);
                    }
                }

                // All retries exhausted
                _logger?.LogError(lastEx, "Failed to restore session after 3 attempts");
                ConnectionError = "Could not connect to Spotify. Check your internet connection.";
                SetStatus(AuthStatus.Error);
                return false;
            }
        }

        SetStatus(AuthStatus.LoggedOut);
        return false;
    }

    /// <summary>
    /// Manually retry connection after a failure.
    /// </summary>
    public Task<bool> RetryConnectionAsync(CancellationToken ct = default)
        => TryRestoreSessionAsync(ct);

    public async Task LoginWithAuthorizationCodeAsync(CancellationToken ct = default)
    {
        if (_session == null || _sessionConfig == null)
            throw new InvalidOperationException("Session infrastructure not available");

        SetStatus(AuthStatus.Authenticating);

        using var client = OAuthClient.Create(
            _sessionConfig.GetClientId(), OAuthScopes, openBrowser: true,
            logger: _logger as ILogger);

        var token = await client.GetAccessTokenAsync(ct);
        var creds = Credentials.WithAccessToken(token.AccessToken);
        await _session.ConnectAsync(creds, _credentialsCache, ct);
        await PopulateUserFromSession(ct);
    }

    public async Task LoginWithDeviceCodeAsync(
        Action<DeviceCodeInfo> onDeviceCodeReceived,
        CancellationToken ct = default)
    {
        if (_session == null || _sessionConfig == null)
            throw new InvalidOperationException("Session infrastructure not available");

        SetStatus(AuthStatus.Authenticating);

        using var client = OAuthClient.CreateCustom(
            _sessionConfig.GetClientId(), OAuthScopes, OAuthFlow.DeviceCode,
            logger: _logger as ILogger);

        client.DeviceCodeReceived += (_, e) =>
        {
            onDeviceCodeReceived(new DeviceCodeInfo(
                e.UserCode, e.VerificationUri,
                e.VerificationUriComplete, e.ExpiresIn));
        };

        var token = await client.GetAccessTokenAsync(ct);
        var creds = Credentials.WithAccessToken(token.AccessToken);
        await _session.ConnectAsync(creds, _credentialsCache, ct);
        await PopulateUserFromSession(ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        CurrentUser = null;
        AccountType = null;

        // Clear cached credentials
        if (_credentialsCache != null)
        {
            try
            {
                await _credentialsCache.ClearCredentialsAsync(cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clear cached credentials");
            }
        }

        // Stop Spotify playback and remove Spotify tracks from queue
        if (_playbackStateService != null)
        {
            if (_playbackStateService.CurrentTrackId?.StartsWith("spotify:") == true)
            {
                _playbackStateService.PlayPause(); // stop current Spotify track
            }
        }

        // Tear down playback engine resources to avoid leaks on re-login
        Helpers.Application.AppLifecycleHelper.TeardownPlaybackEngine();

        SetStatus(AuthStatus.LoggedOut);
    }

    private async Task PopulateUserFromSession(CancellationToken ct)
    {
        if (_session == null) return;

        var userData = _session.GetUserData();
        if (userData == null) return;

        // Enrich with spclient profile (display name + avatar)
        try
        {
            var profile = await _session.SpClient.GetUserProfileAsync(userData.Username, ct);
            userData = userData with
            {
                DisplayName = profile.EffectiveDisplayName,
                ProfileImageUrl = profile.EffectiveImageUrl
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch user profile, using canonical username");
        }

        CurrentUser = userData;
        AccountType = await _session.GetAccountTypeAsync(ct);

        // Initialize local playback engine (AudioPipeline) after session is fully connected
        var httpClient = _httpClientFactory!.CreateClient("Wavee");
        var audioHttpClient = _httpClientFactory.CreateClient("WaveeAudio");
        Helpers.Application.AppLifecycleHelper.InitializePlaybackEngine(_session, httpClient, audioHttpClient, _logger);

        // SetStatus fires AuthStatusChangedMessage → LibrarySyncOrchestrator handles sync
        SetStatus(AuthStatus.Authenticated);
    }

    private void SetStatus(AuthStatus newStatus)
    {
        if (Status == newStatus) return;

        Status = newStatus;
        AuthStatusChanged?.Invoke(this, newStatus);
        _messenger.Send(new AuthStatusChangedMessage(newStatus));
    }

    partial void OnCurrentUserChanged(UserData? value)
    {
        _messenger.Send(new UserProfileUpdatedMessage(value));
    }

    public void Dispose()
    {
        // Future: disconnect session if needed
    }
}
