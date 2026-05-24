using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.Contracts;
using Wavee.UI.Formatters;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.ViewModels.Artist;

/// <summary>
/// Owns the artist's "extras" shelves — music videos, merch, playlists &amp;
/// discovery, external links, top cities, gallery photos, concerts. The
/// collections are disjoint and each renders into its own section on the
/// artist page; the VM keeps them together because all seven share the
/// same overview-apply / reset / event-raise rhythm and none warrant a
/// VM of its own.
///
/// <para>The concerts surface lives here because the data is independent
/// from the discography / top-tracks pipelines, even though the header VM
/// reads the snapshot to derive the tour-banner text — that's a one-way
/// pull (header observes, extras owns).</para>
/// </summary>
public sealed partial class ArtistExtrasViewModel : ObservableObject, IDisposable
{
    private readonly Func<string?> _userLocationProvider;
    private readonly Func<string?, bool> _isNearUserChecker;

    public ArtistExtrasViewModel(
        Func<string?> userLocationProvider,
        Func<string?, bool> isNearUserChecker)
    {
        _userLocationProvider = userLocationProvider;
        _isNearUserChecker = isNearUserChecker;
    }

    /// <summary>Music videos surfaced from <c>relatedMusicVideos</c>.</summary>
    private readonly ObservableCollection<MusicVideoVm> _musicVideos = [];
    public IReadOnlyList<MusicVideoVm> MusicVideos => _musicVideos;
    public bool HasMusicVideos => _musicVideos.Count > 0;

    /// <summary>Merch products from <c>goods.merch</c> — Spotify Shop integration.</summary>
    private readonly ObservableCollection<MerchItemVm> _merch = [];
    public IReadOnlyList<MerchItemVm> Merch => _merch;
    public bool HasMerch => _merch.Count > 0;

    /// <summary>Combined Playlists &amp; discovery (playlistsV2 + featuringV2 +
    /// discoveredOnV2) with per-item source subtitles.</summary>
    private readonly ObservableCollection<ArtistPlaylistVm> _playlists = [];
    public IReadOnlyList<ArtistPlaylistVm> Playlists => _playlists;
    public bool HasPlaylists => _playlists.Count > 0;

    /// <summary>Artist's external links (Twitter, Instagram, YouTube, etc.).</summary>
    private readonly ObservableCollection<ArtistSocialLinkVm> _externalLinks = [];
    public IReadOnlyList<ArtistSocialLinkVm> ExternalLinks => _externalLinks;
    public bool HasExternalLinks => _externalLinks.Count > 0;

    /// <summary>Top cities by listener count, with proportional bar widths.</summary>
    private readonly ObservableCollection<ArtistTopCityVm> _topCities = [];
    public IReadOnlyList<ArtistTopCityVm> TopCities => _topCities;
    public bool HasTopCities => _topCities.Count > 0;

    public bool HasConnectSection => HasExternalLinks || HasTopCities;

    /// <summary>Photo URLs from the artist's gallery (largest variant).</summary>
    private readonly ObservableCollection<string> _galleryPhotos = [];
    public IReadOnlyList<string> GalleryPhotos => _galleryPhotos;
    public bool HasGallery => _galleryPhotos.Count > 0;

    private readonly ObservableCollection<ConcertVm> _concerts = [];
    public IReadOnlyList<ConcertVm> Concerts => _concerts;
    public bool HasConcerts => _concerts.Count > 0;

    /// <summary>Fired after the concerts collection has been replaced — the
    /// header VM observes this to refresh its tour-banner projection.</summary>
    public event EventHandler? ConcertsChanged;

    [ObservableProperty]
    private string? _userLocationName;

    public void ApplyOverview(ArtistOverviewResult overview)
    {
        // Concerts (batch swap).
        _concerts.ReplaceWith(overview.Concerts.Select(c => new ConcertVm
        {
            Title = c.Title,
            Venue = c.Venue,
            City = c.City,
            Date = c.Date,
            DateFormatted = c.Date != default
                ? c.Date.ToString("MMM d").ToUpperInvariant()
                : "",
            DayOfWeek = c.Date != default
                ? c.Date.ToString("ddd").ToUpperInvariant()
                : "",
            Year = c.Date != default ? c.Date.Year.ToString() : "",
            IsFestival = c.IsFestival,
            IsNearUser = c.IsNearUser,
            Uri = c.Uri
        }));
        OnPropertyChanged(nameof(HasConcerts));

        UserLocationName = _userLocationProvider();

        // Connect & Markets (batch swap).
        _externalLinks.ReplaceWith(overview.ExternalLinks.Select(l => new ArtistSocialLinkVm
        {
            Name = l.Name,
            Url = l.Url,
            Icon = FluentGlyphs.ResolveSocialIcon(l.Url, l.Name)
        }));
        OnPropertyChanged(nameof(HasExternalLinks));

        // Bar widths normalized against the largest city's listener count.
        var maxListeners = overview.TopCities.Count == 0
            ? 1L
            : overview.TopCities.Max(c => c.NumberOfListeners);
        _topCities.ReplaceWith(overview.TopCities.Take(5).Select(c => new ArtistTopCityVm
        {
            City = c.City,
            Country = c.Country,
            NumberOfListeners = c.NumberOfListeners,
            DisplayCount = NumberFormatter.FormatListenerCount(c.NumberOfListeners),
            RelativeWidth = maxListeners > 0
                ? Math.Max(8, c.NumberOfListeners * 200.0 / maxListeners)
                : 8
        }));
        OnPropertyChanged(nameof(HasTopCities));
        OnPropertyChanged(nameof(HasConnectSection));

        _galleryPhotos.ReplaceWith(
            overview.GalleryPhotos
                .Select(SpotifyImageHelper.ToHttpsUrl)
                .Where(static url => !string.IsNullOrWhiteSpace(url))
                .Select(static url => url!)
                .Distinct(StringComparer.Ordinal));
        OnPropertyChanged(nameof(HasGallery));

        // Playlists & discovery (batch swap) — playlistsV2 + featuringV2 +
        // discoveredOnV2 already merged with per-item subtitles in ArtistService.
        _playlists.ReplaceWith(overview.Playlists.Select(p => new ArtistPlaylistVm
        {
            Uri = p.Uri,
            Name = p.Name,
            ImageUrl = p.ImageUrl,
            Subtitle = p.Subtitle
        }));
        OnPropertyChanged(nameof(HasPlaylists));

        // Music videos (batch swap).
        _musicVideos.ReplaceWith(overview.MusicVideos.Select(v => new MusicVideoVm
        {
            TrackUri = v.TrackUri,
            Title = v.Title,
            ThumbnailUrl = v.ThumbnailUrl,
            AlbumUri = v.AlbumUri,
            Duration = v.Duration,
            IsExplicit = v.IsExplicit
        }));
        OnPropertyChanged(nameof(HasMusicVideos));

        // Merch (batch swap).
        _merch.ReplaceWith(overview.Merch.Select(m => new MerchItemVm
        {
            Name = m.Name,
            Price = m.Price,
            Description = m.Description,
            ImageUrl = m.ImageUrl,
            Uri = m.Uri,
            ShopUrl = m.ShopUrl
        }));
        OnPropertyChanged(nameof(HasMerch));

        ConcertsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetForNewArtist()
    {
        _concerts.Clear();
        _externalLinks.Clear();
        _topCities.Clear();
        _galleryPhotos.Clear();
        _musicVideos.Clear();
        _merch.Clear();
        _playlists.Clear();
        OnPropertyChanged(nameof(HasMusicVideos));
        OnPropertyChanged(nameof(HasMerch));
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(HasExternalLinks));
        OnPropertyChanged(nameof(HasTopCities));
        OnPropertyChanged(nameof(HasConnectSection));
        OnPropertyChanged(nameof(HasGallery));
        OnPropertyChanged(nameof(HasConcerts));
        ConcertsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refresh the IsNearUser flag on every concert. Called after
    /// the user picks a different location so the concert cards update.</summary>
    public void RefreshNearUserFlags()
    {
        foreach (var c in Concerts)
            c.IsNearUser = _isNearUserChecker(c.City);
    }

    public void Dispose()
    {
        // No managed resources — disposal exists for parity with siblings.
    }
}
