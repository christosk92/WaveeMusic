using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ProfileViewModel : ObservableObject, ITabBarItemContent, ITrackListViewModel
{
    private readonly ProfileCache? _profileCache;
    private readonly Wavee.Core.Session.Session? _session;
    private readonly IAuthState? _authState;
    private readonly ILogger? _logger;

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string? _profileImageUrl;

    [ObservableProperty]
    private int _followingCount;

    [ObservableProperty]
    private int _publicPlaylistCount;

    [ObservableProperty]
    private int _profileColor;

    [ObservableProperty]
    private string? _heroColorHex;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasData;

    private readonly ObservableCollection<SpotifyProfileArtist> _recentArtists = [];
    private readonly ObservableCollection<SpotifyProfilePlaylist> _publicPlaylists = [];
    private readonly ObservableCollection<SpotifyProfileArtist> _followingArtists = [];
    private readonly ObservableCollection<TopTrackItem> _topTracks = [];
    private readonly ObservableCollection<ITrackItem> _topTrackItems = [];

    public ObservableCollection<SpotifyProfileArtist> RecentArtists => _recentArtists;
    public ObservableCollection<SpotifyProfilePlaylist> PublicPlaylists => _publicPlaylists;
    public ObservableCollection<SpotifyProfileArtist> FollowingArtists => _followingArtists;
    public ObservableCollection<TopTrackItem> TopTracks => _topTracks;
    public ObservableCollection<ITrackItem> TopTrackItems => _topTrackItems;

    // ── ITrackListViewModel implementation ──

    public ICommand SortByCommand { get; } = new RelayCommand<string>(_ => { });
    public ICommand PlayTrackCommand { get; } = new RelayCommand<object>(_ => { });
    public ICommand PlaySelectedCommand { get; } = new RelayCommand(() => { });
    public ICommand PlayAfterCommand { get; } = new RelayCommand(() => { });
    public ICommand AddSelectedToQueueCommand { get; } = new RelayCommand(() => { });
    public ICommand RemoveSelectedCommand { get; } = new RelayCommand(() => { });
    public ICommand AddToPlaylistCommand { get; } = new RelayCommand<object>(_ => { });

    public string SortChevronGlyph => "";
    public bool IsSortingByTitle => false;
    public bool IsSortingByArtist => false;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    public IReadOnlyList<object> SelectedItems { get; set; } = [];
    public int SelectedCount => 0;
    public bool HasSelection => false;
    public string SelectionHeaderText => "";

    public IReadOnlyList<PlaylistSummaryDto> Playlists => [];

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfileViewModel(
        ProfileCache? profileCache = null,
        Wavee.Core.Session.Session? session = null,
        IAuthState? authState = null,
        ILogger<ProfileViewModel>? logger = null)
    {
        _profileCache = profileCache;
        _session = session;
        _authState = authState;
        _logger = logger;

        TabItemParameter = new TabItemParameter
        {
            Title = "Profile",
            PageType = NavigationPageType.Profile
        };
    }

    public async void Initialize()
    {
        // Stage 1: Serve cached data instantly if available
        if (_profileCache != null && _profileCache.HasData && !_profileCache.IsStale)
        {
            var snapshot = _profileCache.GetCached();
            if (snapshot != null)
            {
                ApplySnapshot(snapshot);
                return;
            }
        }

        // Stage 2: Fetch fresh data
        IsLoading = true;
        HasData = false;

        try
        {
            if (_session is null || !_session.IsConnected())
            {
                _logger?.LogWarning("Cannot load profile: session is null or not connected");
                return;
            }

            if (_profileCache != null)
            {
                var snapshot2 = await _profileCache.FetchFreshAsync(_session);
                ApplySnapshot(snapshot2);
                _profileCache.StartBackgroundRefresh(_session);
            }
            else
            {
                _logger?.LogWarning("ProfileCache not available in DI");
            }
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

    /// <summary>
    /// Called by ProfilePage when background refresh completes. Applies diffs on UI thread.
    /// </summary>
    public void ApplyBackgroundRefresh(ProfileSnapshot snapshot)
    {
        // Update scalar properties only if changed
        if (DisplayName != snapshot.DisplayName) DisplayName = snapshot.DisplayName;
        if (ProfileImageUrl != snapshot.ProfileImageUrl) ProfileImageUrl = snapshot.ProfileImageUrl;
        if (FollowingCount != snapshot.FollowingCount) FollowingCount = snapshot.FollowingCount;
        if (PublicPlaylistCount != snapshot.PublicPlaylistCount) PublicPlaylistCount = snapshot.PublicPlaylistCount;
        if (ProfileColor != snapshot.ProfileColor) ProfileColor = snapshot.ProfileColor;
        if (HeroColorHex != snapshot.HeroColorHex) HeroColorHex = snapshot.HeroColorHex;

        // Apply collection diffs
        ProfileCache.DiffArtists(RecentArtists, snapshot.RecentArtists);
        ProfileCache.DiffPlaylists(PublicPlaylists, snapshot.PublicPlaylists);
        ProfileCache.DiffArtists(FollowingArtists, snapshot.FollowingArtists);

        // Rebuild top tracks (indexed, so replace entirely)
        RebuildTopTracks(snapshot.TopTracks);
    }

    private void ApplySnapshot(ProfileSnapshot snapshot)
    {
        DisplayName = snapshot.DisplayName;
        ProfileImageUrl = snapshot.ProfileImageUrl;
        FollowingCount = snapshot.FollowingCount;
        PublicPlaylistCount = snapshot.PublicPlaylistCount;
        ProfileColor = snapshot.ProfileColor;
        HeroColorHex = snapshot.HeroColorHex;

        _recentArtists.Clear();
        foreach (var a in snapshot.RecentArtists) _recentArtists.Add(a);

        _publicPlaylists.Clear();
        foreach (var p in snapshot.PublicPlaylists) _publicPlaylists.Add(p);

        _followingArtists.Clear();
        foreach (var f in snapshot.FollowingArtists) _followingArtists.Add(f);

        RebuildTopTracks(snapshot.TopTracks);

        HasData = true;
        IsLoading = false;

        ContentChanged?.Invoke(this, TabItemParameter!);
    }

    private void RebuildTopTracks(List<TopTrackItem> tracks)
    {
        _topTracks.Clear();
        _topTrackItems.Clear();
        foreach (var item in tracks)
            _topTracks.Add(item);

        int idx = 1;
        foreach (var item in _topTracks.Take(5))
        {
            if (item.Data != null)
                _topTrackItems.Add(new TopTrackAdapter(item.Data, idx++));
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            await _authState!.LogoutAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to sign out");
        }
    }
}
