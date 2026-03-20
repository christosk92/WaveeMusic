using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ProfileViewModel : ObservableObject, ITabBarItemContent
{
    private readonly ILogger? _logger;

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string? _profileImageUrl;

    [ObservableProperty]
    private int _followingCount;

    [ObservableProperty]
    private int _publicPlaylistCount;

    /// <summary>
    /// Raw color integer from the spclient profile response, suitable for hero gradient rendering.
    /// </summary>
    [ObservableProperty]
    private int _profileColor;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasData;

    private readonly ObservableCollection<SpotifyProfileArtist> _recentArtists = [];
    private readonly ObservableCollection<SpotifyProfilePlaylist> _publicPlaylists = [];
    private readonly ObservableCollection<SpotifyProfileArtist> _followingArtists = [];

    public ObservableCollection<SpotifyProfileArtist> RecentArtists => _recentArtists;
    public ObservableCollection<SpotifyProfilePlaylist> PublicPlaylists => _publicPlaylists;
    public ObservableCollection<SpotifyProfileArtist> FollowingArtists => _followingArtists;

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfileViewModel(ILogger<ProfileViewModel>? logger = null)
    {
        _logger = logger;

        TabItemParameter = new TabItemParameter
        {
            Title = "Profile",
            PageType = NavigationPageType.Profile
        };
    }

    public async void Initialize()
    {
        IsLoading = true;
        HasData = false;

        try
        {
            var session = Ioc.Default.GetService<Wavee.Core.Session.Session>();
            if (session is null || !session.IsConnected())
            {
                _logger?.LogWarning("Cannot load profile: session is null or not connected");
                return;
            }

            var userData = session.GetUserData();
            if (userData is null)
            {
                _logger?.LogWarning("Cannot load profile: no user data available");
                return;
            }

            var profile = await session.SpClient.GetUserProfileAsync(userData.Username);

            DisplayName = profile.EffectiveDisplayName ?? userData.Username;
            ProfileImageUrl = profile.EffectiveImageUrl;
            FollowingCount = profile.FollowingCount ?? 0;
            PublicPlaylistCount = profile.TotalPublicPlaylistsCount ?? 0;
            ProfileColor = profile.Color ?? 0;

            _recentArtists.Clear();
            _followingArtists.Clear();
            if (profile.RecentlyPlayedArtists is { Count: > 0 })
            {
                foreach (var artist in profile.RecentlyPlayedArtists)
                {
                    _recentArtists.Add(artist);
                }
            }

            _publicPlaylists.Clear();
            if (profile.PublicPlaylists is { Count: > 0 })
            {
                foreach (var playlist in profile.PublicPlaylists)
                {
                    _publicPlaylists.Add(playlist);
                }
            }

            // Fetch following
            try
            {
                var following = await session.SpClient.GetUserFollowingAsync(userData.Username);
                if (following.Profiles != null)
                    foreach (var f in following.Profiles) FollowingArtists.Add(f);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch following list");
            }

            HasData = true;

            ContentChanged?.Invoke(this, TabItemParameter!);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load user profile");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            var authState = Ioc.Default.GetRequiredService<IAuthState>();
            await authState.LogoutAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to sign out");
        }
    }
}
