using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Manages the authenticated user's state. Reads from <see cref="ISession"/> when available,
/// falls back to demo/mock data when running in demo mode.
/// </summary>
internal sealed partial class AuthStateService : ObservableObject, IAuthState
{
    private readonly ISession? _session;
    private readonly IMessenger _messenger;
    private readonly IDataServiceConfiguration? _config;
    private readonly ILogger? _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthenticated))]
    private AuthStatus _status = AuthStatus.Unknown;

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
    public string? DisplayName => CurrentUser?.Username; // TODO: Extend UserData with display name
    public string? ProfileImageUrl => null; // TODO: Extend UserData with profile image

    public bool IsAuthenticated => Status == AuthStatus.Authenticated;
    public bool IsPremium => AccountType == Core.Session.AccountType.Premium;

    public event EventHandler<AuthStatus>? AuthStatusChanged;

    public AuthStateService(
        IMessenger messenger,
        IDataServiceConfiguration? config = null,
        ISession? session = null,
        ILogger<AuthStateService>? logger = null)
    {
        _messenger = messenger;
        _config = config;
        _session = session;
        _logger = logger;
    }

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        try
        {
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

            // Real mode: check ISession
            if (_session.IsConnected())
            {
                var userData = _session.GetUserData();
                if (userData != null)
                {
                    CurrentUser = userData;
                    AccountType = await _session.GetAccountTypeAsync(ct);
                    SetStatus(AuthStatus.Authenticated);
                    return true;
                }
            }

            SetStatus(AuthStatus.LoggedOut);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to restore session");
            SetStatus(AuthStatus.Error);
            return false;
        }
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        CurrentUser = null;
        AccountType = null;
        SetStatus(AuthStatus.LoggedOut);

        // TODO: Clear cached credentials, disconnect session
        return Task.CompletedTask;
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
}
