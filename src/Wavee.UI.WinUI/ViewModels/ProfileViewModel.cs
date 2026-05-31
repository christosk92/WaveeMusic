using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;
using Windows.UI;

namespace Wavee.UI.WinUI.ViewModels;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfileViewModel : Wavee.UI.ViewModels.Helpers.TrackListViewModelBase, ITabBarItemContent, ITrackListViewModel
{
    private readonly ProfileCache? _profileCache;
    private readonly IUserProfileService? _profileService;
    private readonly IAuthState? _authState;
    private readonly IUserFollowService? _userFollowService;
    private readonly ILogger? _logger;
    private ProfileSnapshot? _lastSnapshot;
    private bool _isHibernated;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string? ProfileImageUrl { get; set; }

    [ObservableProperty]
    public partial int FollowingCount { get; set; }

    [ObservableProperty]
    public partial int PublicPlaylistCount { get; set; }

    [ObservableProperty]
    public partial int ProfileColor { get; set; }

    [ObservableProperty]
    public partial string? HeroColorHex { get; set; }

    /// <summary>Soft top-left radial wash tinted from <see cref="HeroColorHex"/>.
    /// Mirrors HomePage's PageBleedHost — keeps the visual family consistent
    /// across pages with extracted-color identity. Rebuilt whenever
    /// <see cref="HeroColorHex"/> or <see cref="IsDarkTheme"/> changes.</summary>
    [ObservableProperty]
    public partial Brush? PageBleedBrush { get; set; }

    /// <summary>Theme flag the page sets on load / theme-change so the bleed
    /// brush can pick the right alpha curve (dark themes need a deeper push).</summary>
    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; } = true;

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentUser { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFollowing { get; set; }

    [ObservableProperty]
    public partial string? Username { get; set; }

    [ObservableProperty]
    public partial string? UserUri { get; set; }

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

    public string SortChevronGlyph => "";
    public bool IsSortingByTitle => false;
    public bool IsSortingByArtist => false;
    public bool IsSortingByAlbum => false;
    public bool IsSortingByAddedAt => false;

    // Profile page has no track-row selection; SelectedItems / SelectedCount /
    // HasSelection inherited from TrackListViewModelBase return inert defaults.
    // Header is overridden to "" so a stray selection-mode binding never paints
    // a "0 tracks selected" string on the profile chrome.
    public override string SelectionHeaderText => string.Empty;

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfileViewModel(
        ProfileCache? profileCache = null,
        IUserProfileService? profileService = null,
        IAuthState? authState = null,
        IUserFollowService? userFollowService = null,
        ILogger<ProfileViewModel>? logger = null)
    {
        _profileCache = profileCache;
        _profileService = profileService;
        _authState = authState;
        _userFollowService = userFollowService;
        _logger = logger;

        TabItemParameter = new TabItemParameter
        {
            Title = "Profile",
            PageType = NavigationPageType.Profile
        };
    }

    /// <summary>
    /// Loads the profile for <paramref name="parameter"/>.Uri (a <c>spotify:user:{id}</c>),
    /// or the authenticated user when the parameter is null. Auth-user path uses the
    /// hot-snapshot ProfileCache; other-user path uses ProfileService one-shot.
    /// </summary>
    public async void Initialize(ContentNavigationParameter? parameter = null)
    {
        var targetUsername = ResolveTargetUsername(parameter);
        var authUsername = _authState?.Username ?? _profileService?.AuthenticatedUsername;
        var loadingSelf = string.IsNullOrEmpty(targetUsername)
            || (authUsername != null && string.Equals(authUsername, targetUsername, StringComparison.OrdinalIgnoreCase));

        // Optimistic: prefill from navigation parameter so the hero renders immediately.
        if (parameter != null)
        {
            if (!string.IsNullOrEmpty(parameter.Title)) DisplayName = parameter.Title!;
            if (!string.IsNullOrEmpty(parameter.ImageUrl)) ProfileImageUrl = parameter.ImageUrl;
            if (!string.IsNullOrEmpty(parameter.Uri)) UserUri = parameter.Uri;
        }

        if (loadingSelf)
        {
            // Stage 1: serve cached if hot
            if (_profileCache != null && _profileCache.HasData && !_profileCache.IsStale)
            {
                var snapshot = _profileCache.GetCached();
                if (snapshot != null)
                {
                    ApplySnapshot(snapshot with { IsCurrentUser = true });
                    return;
                }
            }

            IsLoading = true;
            HasData = false;
            try
            {
                if (_profileService is null || !_profileService.IsAvailable)
                {
                    _logger?.LogWarning("Cannot load profile: profile service unavailable");
                    return;
                }
                if (_profileCache != null)
                {
                    var snapshot2 = await _profileCache.FetchFreshAsync();
                    ApplySnapshot(snapshot2 with { IsCurrentUser = true });
                    _profileCache.StartBackgroundRefresh();
                }
                else
                {
                    _logger?.LogWarning("ProfileCache not available in DI");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load authenticated user profile");
            }
            finally
            {
                IsLoading = false;
            }
        }
        else
        {
            IsLoading = true;
            HasData = false;
            try
            {
                if (_profileService is null || !_profileService.IsAvailable)
                {
                    _logger?.LogWarning("Cannot load profile for {User}: profile service unavailable", targetUsername);
                    return;
                }
                var loaded = await _profileService.LoadAsync(targetUsername!);
                ApplySnapshot((ProfileSnapshot)loaded);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load profile for {User}", targetUsername);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    private static string? ResolveTargetUsername(ContentNavigationParameter? parameter)
    {
        if (parameter is null || string.IsNullOrWhiteSpace(parameter.Uri)) return null;
        return ProfileFetcher.NormalizeUsername(parameter.Uri);
    }

    /// <summary>
    /// Called by ProfilePage when background refresh completes. Applies diffs on UI thread.
    /// Only used for the authenticated-user path.
    /// </summary>
    public void ApplyBackgroundRefresh(ProfileSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        if (_isHibernated)
            return;

        if (DisplayName != snapshot.DisplayName) DisplayName = snapshot.DisplayName;
        if (ProfileImageUrl != snapshot.ProfileImageUrl) ProfileImageUrl = snapshot.ProfileImageUrl;
        if (FollowingCount != snapshot.FollowingCount) FollowingCount = snapshot.FollowingCount;
        if (PublicPlaylistCount != snapshot.PublicPlaylistCount) PublicPlaylistCount = snapshot.PublicPlaylistCount;
        if (ProfileColor != snapshot.ProfileColor) ProfileColor = snapshot.ProfileColor;
        if (HeroColorHex != snapshot.HeroColorHex) HeroColorHex = snapshot.HeroColorHex;

        ProfileCache.DiffArtists(RecentArtists, snapshot.RecentArtists);
        ProfileCache.DiffPlaylists(PublicPlaylists, snapshot.PublicPlaylists);
        ProfileCache.DiffArtists(FollowingArtists, snapshot.FollowingArtists);

        RebuildTopTracks(snapshot.TopTracks);
    }

    private void ApplySnapshot(ProfileSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _isHibernated = false;

        DisplayName = snapshot.DisplayName;
        ProfileImageUrl = snapshot.ProfileImageUrl;
        FollowingCount = snapshot.FollowingCount;
        PublicPlaylistCount = snapshot.PublicPlaylistCount;
        ProfileColor = snapshot.ProfileColor;
        HeroColorHex = snapshot.HeroColorHex;
        IsCurrentUser = snapshot.IsCurrentUser;
        IsFollowing = snapshot.IsFollowing;
        Username = snapshot.Username;
        UserUri = snapshot.UserUri;

        _recentArtists.ReplaceWith(snapshot.RecentArtists);
        _publicPlaylists.ReplaceWith(snapshot.PublicPlaylists);
        _followingArtists.ReplaceWith(snapshot.FollowingArtists);

        RebuildTopTracks(snapshot.TopTracks);

        HasData = true;
        IsLoading = false;

        if (TabItemParameter != null && !string.IsNullOrEmpty(snapshot.DisplayName))
            TabItemParameter.Title = snapshot.DisplayName;

        ContentChanged?.Invoke(this, TabItemParameter!);
    }

    public void Hibernate()
    {
        if (_isHibernated)
            return;

        _isHibernated = true;
        IsLoading = false;
        HasData = false;
        ProfileImageUrl = null;
        PageBleedBrush = null;

        // Resilient resets on hibernate — raw Clear() on these bound profile collections can
        // E_FAIL while the cached page's surfaces are dropped by the nav cache (issue #6).
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_recentArtists, []);
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_publicPlaylists, []);
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_followingArtists, []);
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_topTracks, []);
        Wavee.UI.WinUI.Extensions.ObservableCollectionExtensions.ReplaceWith(_topTrackItems, []);
    }

    public void ResumeFromHibernate()
    {
        if (!_isHibernated)
            return;

        if (_lastSnapshot != null)
        {
            ApplySnapshot(_lastSnapshot);
            return;
        }

        _isHibernated = false;
        Initialize(null);
    }

    private void RebuildTopTracks(List<TopTrackItem> tracks)
    {
        _topTracks.ReplaceWith(tracks);

        int idx = 1;
        var visibleRows = new List<ITrackItem>(Math.Min(5, tracks.Count));
        foreach (var item in _topTracks.Take(5))
        {
            if (item.Data != null)
                visibleRows.Add(new TopTrackAdapter(item.Data, idx++));
        }
        _topTrackItems.ReplaceWith(visibleRows);
    }

    partial void OnHeroColorHexChanged(string? value) => RebuildPageBleedBrush();
    partial void OnIsDarkThemeChanged(bool value) => RebuildPageBleedBrush();

    private void RebuildPageBleedBrush()
    {
        if (!TintColorHelper.TryParseHex(HeroColorHex, out var raw))
        {
            PageBleedBrush = null;
            return;
        }
        var lifted = TintColorHelper.BrightenForTint(raw, targetMax: 220);
        var radial = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.0, 0.0),
            GradientOrigin = new Windows.Foundation.Point(0.0, 0.0),
            RadiusX = 1.0,
            RadiusY = 1.0,
            MappingMode = Microsoft.UI.Xaml.Media.BrushMappingMode.RelativeToBoundingBox
        };
        radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(IsDarkTheme ? 130 : 80), lifted.R, lifted.G, lifted.B), Offset = 0.0 });
        radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(IsDarkTheme ? 60 : 40), lifted.R, lifted.G, lifted.B), Offset = 0.5 });
        radial.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, lifted.R, lifted.G, lifted.B), Offset = 1.0 });
        PageBleedBrush = radial;
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

    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        if (IsCurrentUser || _userFollowService is null || string.IsNullOrEmpty(Username)) return;
        var wasFollowing = IsFollowing;
        IsFollowing = !wasFollowing;   // optimistic
        var ok = wasFollowing
            ? await _userFollowService.UnfollowAsync(Username!, CancellationToken.None)
            : await _userFollowService.FollowAsync(Username!, CancellationToken.None);
        if (!ok) IsFollowing = wasFollowing;  // revert on API failure (service swallows + logs)
    }
}
