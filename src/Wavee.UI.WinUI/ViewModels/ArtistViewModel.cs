using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Data;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Windows.UI;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>Which kind of content the hero spotlight card is showing. The
/// SpotlightReleaseCard control consumes this to toggle Save/Share button
/// visibility and pulse-dot animation; latest releases pulse to read as
/// "fresh", pinned items / popular releases sit still.</summary>
public enum SpotlightMode
{
    Pinned,
    LatestRelease,
    PopularRelease,
}

public sealed partial class ArtistViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private const int PlayPendingTimeoutMs = 8000;
    private readonly IArtistService _artistService;
    private readonly ArtistStore _artistStore;
    private readonly IAlbumService _albumService;
    private readonly ILocationService _locationService;
    private readonly IPlaybackService _playbackService;
    private readonly IPlaybackStateService _playbackStateService;
    private CompositeDisposable? _subscriptions;
    private string? _appliedOverviewFor;
    private ArtistOverviewResult? _appliedOverview;
    private readonly IColorService _colorService;
    private readonly ITrackLikeService? _likeService;
    private readonly ISettingsService? _settingsService;
    private readonly Wavee.UI.WinUI.Services.ArtistBioSummarizer? _bioSummarizer;
    private CancellationTokenSource? _bioSummaryCts;
    private readonly ILogger? _logger;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _discoCts;
    private CancellationTokenSource? _playPendingCts;
    private int _loadGeneration;
    private bool _disposed;

    // -- Backing data --
    private readonly List<LazyReleaseItem> _allReleases = [];

    // -- UI-bound collections --
    // Bound collections kept as stable instances and mutated in place. Assigning
    // a new reference forces ItemsRepeater/ListView to recycle every realized
    // container; mutating the same instance lets the binding stay subscribed
    // and avoids a full rebuild on cached-page restore. `Has*` derived
    // properties must be raised manually after each mutation since they no
    // longer ride on a property-replacement event.
    private readonly ObservableCollection<LazyTrackItem> _topTracks = [];
    public ObservableCollection<LazyTrackItem> TopTracks => _topTracks;

    private readonly ObservableCollection<LazyReleaseItem> _albums = [];
    public IReadOnlyList<LazyReleaseItem> Albums => _albums;

    private readonly ObservableCollection<LazyReleaseItem> _singles = [];
    public IReadOnlyList<LazyReleaseItem> Singles => _singles;

    private readonly ObservableCollection<LazyReleaseItem> _compilations = [];
    public IReadOnlyList<LazyReleaseItem> Compilations => _compilations;

    private readonly ObservableCollection<RelatedArtistVm> _relatedArtists = [];
    public IReadOnlyList<RelatedArtistVm> RelatedArtists => _relatedArtists;
    public bool HasRelatedArtists => RelatedArtists.Count > 0;

    private readonly ObservableCollection<ConcertVm> _concerts = [];
    public IReadOnlyList<ConcertVm> Concerts => _concerts;

    /// <summary>Top-played releases for the artist — drives the "Popular releases"
    /// shelf paired with Top Tracks in the V4A composition.</summary>
    private readonly ObservableCollection<LazyReleaseItem> _popularReleases = [];
    public IReadOnlyList<LazyReleaseItem> PopularReleases => _popularReleases;
    public bool HasPopularReleases => _popularReleases.Count > 0;

    /// <summary>First 10 top tracks — Spotify's ArtistOverview returns 10 by
    /// default (extendable up to 50), and the V4A magazine page pairs that
    /// dense list next to the popular-releases column. Static slice; raised
    /// manually after every TopTracks rebuild in <see cref="ApplyOverviewState"/>.</summary>
    public IEnumerable<LazyTrackItem> TopTracksFirst10 =>
        _topTracks.Count == 0
            ? System.Array.Empty<LazyTrackItem>()
            : _topTracks.Take(10);

    /// <summary>The Popular Releases column shown in the V4A magazine layout
    /// — 3 rows total, balanced against the 3-row top-tracks grid on its left.
    ///
    /// Three modes mirror <see cref="SpotlightCardMode"/>:
    ///   • Pinned mode: latest release as featured row + 2 popular releases.
    ///   • LatestRelease mode: top 3 popular releases (latest already lives
    ///     in the hero spotlight card).
    ///   • PopularRelease mode: skip the FIRST popular release because the
    ///     hero spotlight is showing it — otherwise the same cover appears
    ///     in the hero AND as the featured row of this column.</summary>
    public IEnumerable<LazyReleaseItem> PopularReleasesDisplayed
    {
        get
        {
            if (_popularReleases.Count == 0)
                return System.Array.Empty<LazyReleaseItem>();

            if (HasPinnedItem && HasLatestRelease)
            {
                var virtualLatest = BuildVirtualLatestReleaseItem();
                if (virtualLatest is not null)
                {
                    var list = new List<LazyReleaseItem>(3) { virtualLatest };
                    list.AddRange(_popularReleases.Take(2));
                    return list;
                }
            }

            // Skip the first popular release when it's already in the hero
            // spotlight (i.e. neither pinned item nor latest release exists,
            // so the spotlight falls back to first popular).
            if (!HasPinnedItem && !HasLatestRelease)
                return _popularReleases.Skip(1).Take(3);

            return _popularReleases.Take(3);
        }
    }

    /// <summary>Build a synthetic <see cref="LazyReleaseItem"/> that renders
    /// the latest-release scalars (<see cref="LatestReleaseName"/> et al.)
    /// through the same <see cref="PopularReleaseRow"/> template the popular
    /// releases use. Used when the pinned item displaces the latest release
    /// from the hero card and we want it to appear in the column instead.</summary>
    private LazyReleaseItem? BuildVirtualLatestReleaseItem()
    {
        if (string.IsNullOrEmpty(LatestReleaseUri) || string.IsNullOrEmpty(LatestReleaseName))
            return null;
        int year = 0;
        if (!string.IsNullOrEmpty(LatestReleaseDate))
        {
            // FormattedDate may be "May 1, 2026" or "2026"; pull the last 4-digit token.
            var m = System.Text.RegularExpressions.Regex.Match(LatestReleaseDate!, @"\b(\d{4})\b");
            if (m.Success) int.TryParse(m.Groups[1].Value, out year);
        }
        var vm = new Wavee.UI.WinUI.ViewModels.ArtistReleaseVm
        {
            Id = LatestReleaseUri!,
            Uri = LatestReleaseUri,
            Name = LatestReleaseName,
            ImageUrl = LatestReleaseImageUrl,
            Type = LatestReleaseType ?? string.Empty,
            TrackCount = LatestReleaseTrackCount,
            Year = year,
        };
        return LazyReleaseItem.Loaded(LatestReleaseUri!, index: 0, data: vm);
    }

    /// <summary>Music videos surfaced from <c>relatedMusicVideos</c>.</summary>
    private readonly ObservableCollection<MusicVideoVm> _musicVideos = [];
    public IReadOnlyList<MusicVideoVm> MusicVideos => _musicVideos;
    public bool HasMusicVideos => _musicVideos.Count > 0;

    /// <summary>Merch products from <c>goods.merch</c> — Spotify Shop integration.</summary>
    private readonly ObservableCollection<MerchItemVm> _merch = [];
    public IReadOnlyList<MerchItemVm> Merch => _merch;
    public bool HasMerch => _merch.Count > 0;

    /// <summary>Appears-On compilations / soundtracks from
    /// <c>relatedContent.appearsOn</c> — V4A discography shelf next to
    /// Albums / Singles / Compilations.</summary>
    private readonly ObservableCollection<LazyReleaseItem> _appearsOn = [];
    public IReadOnlyList<LazyReleaseItem> AppearsOn => _appearsOn;
    public bool HasAppearsOn => _appearsOn.Count > 0;

    /// <summary>Combined Playlists &amp; discovery (playlistsV2 + featuringV2 +
    /// discoveredOnV2) with per-item source subtitles.</summary>
    private readonly ObservableCollection<ArtistPlaylistVm> _playlists = [];
    public IReadOnlyList<ArtistPlaylistVm> Playlists => _playlists;
    public bool HasPlaylists => _playlists.Count > 0;

    [ObservableProperty]
    private ObservableCollection<LocationSearchResultVm> _locationSuggestions = [];

    /// <summary>Artist's external links (Twitter, Instagram, YouTube, etc.).</summary>
    private readonly ObservableCollection<ArtistSocialLinkVm> _externalLinks = [];
    public IReadOnlyList<ArtistSocialLinkVm> ExternalLinks => _externalLinks;

    /// <summary>Top cities by listener count, with proportional bar widths.</summary>
    private readonly ObservableCollection<ArtistTopCityVm> _topCities = [];
    public IReadOnlyList<ArtistTopCityVm> TopCities => _topCities;

    /// <summary>Photo URLs from the artist's gallery (largest variant).</summary>
    private readonly ObservableCollection<string> _galleryPhotos = [];
    public IReadOnlyList<string> GalleryPhotos => _galleryPhotos;

    public bool HasExternalLinks => ExternalLinks.Count > 0;
    public bool HasTopCities => TopCities.Count > 0;
    public bool HasConnectSection => HasExternalLinks || HasTopCities;
    public bool HasGallery => GalleryPhotos.Count > 0;

    [ObservableProperty]
    private string? _userLocationName;

    // -- Scalar properties --

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _artistId;

    [ObservableProperty]
    private ArtistView? _artist;

    public string? ArtistName => Artist?.Name;
    public string? ArtistImageUrl => Artist?.ArtistImageUrl;
    public string? HeaderImageUrl => Artist?.HeaderImageUrl;
    public string? HeaderHeroColorHex => Artist?.HeaderHeroColorHex;
    public ArtistPalette? Palette => Artist?.Palette;

    [ObservableProperty] private Brush? _sectionAccentBrush;
    [ObservableProperty] private Brush? _paletteHeroGradientBrush;
    [ObservableProperty] private Brush? _paletteAccentPillBrush;
    [ObservableProperty] private Brush? _paletteAccentPillForegroundBrush;

    private bool _isDarkTheme = true;

    public string? MonthlyListeners => Artist?.MonthlyListeners;
    public int? WorldRank => Artist?.WorldRank;
    public bool HasWorldRank => WorldRank is > 0;

    public string? WorldRankNumberText
    {
        get
        {
            var rank = WorldRank;
            return rank is > 0 ? $"#{rank.Value:N0}" : null;
        }
    }

    public string MonthlyListenersDescription =>
        string.IsNullOrEmpty(MonthlyListeners)
            ? string.Empty
            : $"{MonthlyListeners} monthly listeners";

    public long Followers => Artist?.Followers ?? 0;
    public string FollowersFormatted => Followers > 0 ? Followers.ToString("N0") : string.Empty;
    public bool HasFollowers => Followers > 0;

    public string? Biography => Artist?.Biography;
    public bool HasBiography => !string.IsNullOrWhiteSpace(Biography);

    public string? BioPeekLine
    {
        get
        {
            var bio = Biography;
            if (string.IsNullOrWhiteSpace(bio)) return null;

            var firstSentenceEnd = bio.IndexOf(". ", StringComparison.Ordinal);
            var sentence = firstSentenceEnd > 0
                ? bio.Substring(0, firstSentenceEnd + 1)
                : bio;

            return sentence.Length > 140
                ? sentence.Substring(0, 139).TrimEnd() + "..."
                : sentence;
        }
    }

    public bool HasBioPeekLine => !string.IsNullOrEmpty(BioPeekLine);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBioSummary))]
    [NotifyPropertyChangedFor(nameof(HasAboutExcerpt))]
    [NotifyPropertyChangedFor(nameof(IsBioFromAi))]
    [NotifyPropertyChangedFor(nameof(BioExcerptText))]
    [NotifyPropertyChangedFor(nameof(HeroBioLine))]
    [NotifyPropertyChangedFor(nameof(HasHeroBioLine))]
    private string? _bioSummaryText;

    [ObservableProperty] private bool _isBioSummaryLoading;

    public bool HasBioSummary => !string.IsNullOrWhiteSpace(BioSummaryText);
    public bool HasAboutExcerpt => HasBioSummary;
    public bool IsBioFromAi => HasBioSummary && !HasBiography;

    public string BioExcerptText => HasBioPeekLine
        ? BioPeekLine!
        : (BioSummaryText ?? string.Empty);

    public string HeroBioLine => BuildHeroBioLine(Biography, BioSummaryText, ArtistName);
    public bool HasHeroBioLine => !string.IsNullOrWhiteSpace(HeroBioLine);

    public bool IsVerified => Artist?.IsVerified == true;
    public bool IsRegistered => Artist?.IsRegistered == true;
    public bool HasArtistTrait => IsVerified || IsRegistered;
    public bool IsRegisteredOnly => !IsVerified && IsRegistered;
    public string ArtistTraitLabel =>
        IsVerified ? "VERIFIED ARTIST" :
        IsRegistered ? "ARTIST PROFILE" :
        string.Empty;

    partial void OnArtistChanged(ArtistView? value)
    {
        ApplyTheme(_isDarkTheme);
        RaiseArtistEnvelopeDependents();
        UpdateTabTitle(value?.Name);
    }

    private void RaiseArtistEnvelopeDependents()
    {
        foreach (var propertyName in ArtistEnvelopeDependentProperties)
            OnPropertyChanged(propertyName);
    }

    private static readonly string[] ArtistEnvelopeDependentProperties =
    [
        nameof(ArtistName), nameof(ArtistImageUrl), nameof(HeaderImageUrl), nameof(HeaderHeroColorHex), nameof(Palette),
        nameof(MonthlyListeners), nameof(MonthlyListenersDescription), nameof(WorldRank), nameof(HasWorldRank), nameof(WorldRankNumberText),
        nameof(Followers), nameof(FollowersFormatted), nameof(HasFollowers), nameof(Biography), nameof(HasBiography),
        nameof(BioPeekLine), nameof(HasBioPeekLine), nameof(HasAboutExcerpt), nameof(IsBioFromAi), nameof(BioExcerptText),
        nameof(HeroBioLine), nameof(HasHeroBioLine), nameof(IsVerified), nameof(IsRegistered), nameof(HasArtistTrait),
        nameof(IsRegisteredOnly), nameof(ArtistTraitLabel), nameof(LatestRelease), nameof(LatestReleaseName), nameof(LatestReleaseImageUrl),
        nameof(LatestReleaseUri), nameof(LatestReleaseDate), nameof(LatestReleaseTrackCount), nameof(LatestReleaseType),
        nameof(HasLatestRelease), nameof(LatestReleaseSubtitle), nameof(HasSpotlightRelease), nameof(SpotlightCardMode),
        nameof(SpotlightReleaseName), nameof(SpotlightReleaseImageUrl), nameof(SpotlightReleaseUri), nameof(SpotlightReleaseSubtitle),
        nameof(SpotlightReleaseTrackCount),
        nameof(SpotlightReleaseTagText), nameof(SpotlightReleaseEyebrowText), nameof(SpotlightCommentText), nameof(HasSpotlightComment),
        nameof(AlbumsTotalCount), nameof(HasAlbums), nameof(SinglesTotalCount), nameof(HasSingles),
        nameof(CompilationsTotalCount), nameof(HasCompilations), nameof(PinnedItem), nameof(HasPinnedItem), nameof(HasPinnedComment),
        nameof(PinnedBackdropImageUrl), nameof(PinnedColumnWidth), nameof(PinnedItemTitle), nameof(PinnedItemComment),
        nameof(PinnedItemThumbnailUrl), nameof(PinnedItemSubtitle), nameof(PinnedItemUri), nameof(WatchFeed), nameof(HasWatchFeed),
        nameof(PopularReleasesDisplayed), nameof(TourBannerHeadline), nameof(TourBannerSubline), nameof(TourBannerEyebrow),
        nameof(TourBannerIsLive), nameof(TourBannerIconGlyph)
    ];

    [ObservableProperty] private bool _isFollowing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistPlayButtonText))]
    private bool _isPlayPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistPlayButtonText))]
    private bool _isArtistContextPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistPlayButtonText))]
    private bool _isArtistContextPaused;

    public string ArtistPlayButtonText => IsArtistContextPlaying ? "Pause" : "Play";

    public ArtistLatestReleaseResult? LatestRelease => Artist?.LatestRelease;
    public string? LatestReleaseName => LatestRelease?.Name;
    public string? LatestReleaseImageUrl => LatestRelease?.ImageUrl;
    public string? LatestReleaseUri => LatestRelease?.Uri;
    public string? LatestReleaseDate => LatestRelease?.FormattedDate;
    public int LatestReleaseTrackCount => LatestRelease?.TrackCount ?? 0;
    public string? LatestReleaseType => LatestRelease?.Type;

    public bool HasLatestRelease =>
        !string.IsNullOrEmpty(LatestReleaseName) && !string.IsNullOrEmpty(LatestReleaseUri);

    public string LatestReleaseSubtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(LatestReleaseType)) parts.Add(LatestReleaseType!);
            if (!string.IsNullOrEmpty(LatestReleaseDate)) parts.Add(LatestReleaseDate!);
            if (LatestReleaseTrackCount > 0)
                parts.Add(LatestReleaseTrackCount == 1 ? "1 track" : $"{LatestReleaseTrackCount} tracks");
            return string.Join(" - ", parts);
        }
    }

    public bool HasSpotlightRelease =>
        !string.IsNullOrEmpty(SpotlightReleaseName) && !string.IsNullOrEmpty(SpotlightReleaseUri);

    public SpotlightMode SpotlightCardMode =>
        HasPinnedItem ? SpotlightMode.Pinned :
        HasLatestRelease ? SpotlightMode.LatestRelease :
        SpotlightMode.PopularRelease;

    public string? SpotlightReleaseName =>
        HasPinnedItem ? PinnedItem?.Title :
        HasLatestRelease ? LatestReleaseName :
        FirstPopularRelease?.Name;

    public string? SpotlightReleaseImageUrl =>
        HasPinnedItem ? PinnedItem?.ImageUrl :
        HasLatestRelease ? LatestReleaseImageUrl :
        FirstPopularRelease?.ImageUrl;

    public string? SpotlightReleaseUri =>
        HasPinnedItem ? PinnedItem?.Uri :
        HasLatestRelease ? LatestReleaseUri :
        FirstPopularRelease?.Uri;

    /// <summary>
    /// Origin-known track count for whatever the spotlight currently
    /// surfaces (pinned / latest / popular). Threaded into the spotlight
    /// card's <c>NavigationTotalTracks</c> DP so AlbumPage's skeleton row
    /// count is exact on click. Returns 0 when the count can't be derived
    /// (e.g. some pinned-item flavours don't carry a track count).
    /// </summary>
    public int SpotlightReleaseTrackCount =>
        HasPinnedItem ? 0 :
        HasLatestRelease ? LatestReleaseTrackCount :
        FirstPopularRelease?.TrackCount ?? 0;

    public string SpotlightReleaseSubtitle =>
        HasPinnedItem ? (PinnedItem?.Subtitle ?? string.Empty) :
        HasLatestRelease ? LatestReleaseSubtitle :
        FormatReleaseSubtitle(FirstPopularRelease);

    public string SpotlightReleaseTagText =>
        HasPinnedItem ? "Pinned" :
        HasLatestRelease ? "Latest release" :
        "Popular now";

    public string SpotlightReleaseEyebrowText =>
        HasPinnedItem ? "Pinned" :
        HasLatestRelease ? "Latest release" :
        "Popular release";

    public string? SpotlightCommentText =>
        HasPinnedItem ? PinnedItem?.Comment : null;

    public bool HasSpotlightComment =>
        HasPinnedItem && HasPinnedComment;

    private Wavee.UI.WinUI.ViewModels.ArtistReleaseVm? FirstPopularRelease =>
        _popularReleases.FirstOrDefault(r => r.IsLoaded && r.Data is not null)?.Data;

    public int AlbumsTotalCount => Artist?.AlbumsTotalCount ?? 0;
    public int SinglesTotalCount => Artist?.SinglesTotalCount ?? 0;
    public int CompilationsTotalCount => Artist?.CompilationsTotalCount ?? 0;

    public bool HasAlbums => AlbumsTotalCount > 0;
    public bool HasSingles => SinglesTotalCount > 0;
    public bool HasCompilations => CompilationsTotalCount > 0;

    [ObservableProperty] private bool _albumsGridView = true;
    [ObservableProperty] private bool _singlesGridView = true;
    [ObservableProperty] private bool _compilationsGridView = true;
    [ObservableProperty] private double _discographyGridScale = 1.0;

    [ObservableProperty] private bool _hasAlbumsError;
    [ObservableProperty] private bool _hasSinglesError;
    [ObservableProperty] private bool _hasCompilationsError;

    private const int RowsPerPage = 4;
    [ObservableProperty] private int _columnCount = 1;
    [ObservableProperty] private int _currentPage;

    private int TracksPerPage => RowsPerPage * ColumnCount;
    public int TotalPages => TopTracks.Count == 0 ? 0 : (int)Math.Ceiling((double)TopTracks.Count / TracksPerPage);
    public bool HasMultiplePages => TotalPages > 1;

    private List<LazyTrackItem>? _pagedTopTracksCache;
    public IEnumerable<LazyTrackItem> PagedTopTracks => _pagedTopTracksCache ??= BuildPagedTopTracks();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopTracksSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTopTracksCount))]
    private ObservableCollection<LazyTrackItem> _selectedTopTracks = [];

    public int SelectedTopTracksCount => SelectedTopTracks.Count;
    public bool HasTopTracksSelection => SelectedTopTracks.Count > 0;

    public bool IsTopTrackSelected(LazyTrackItem item) => SelectedTopTracks.Contains(item);

    public void ClearTopTracksSelection()
    {
        if (SelectedTopTracks.Count == 0) return;
        SelectedTopTracks.Clear();
    }

    private List<LazyTrackItem> BuildPagedTopTracks()
    {
        int start = CurrentPage * TracksPerPage;
        int available = TopTracks.Count - start;
        if (available <= 0) return [];
        int count = Math.Min(TracksPerPage, available);
        var result = new List<LazyTrackItem>(count);
        for (int i = 0; i < count; i++)
            result.Add(TopTracks[start + i]);
        return result;
    }

    [ObservableProperty] private LazyReleaseItem? _expandedAlbum;
    [ObservableProperty] private ObservableCollection<LazyTrackItem> _expandedAlbumTracks = [];
    [ObservableProperty] private bool _isLoadingExpandedTracks;

    public ArtistPinnedItemResult? PinnedItem => Artist?.PinnedItem;
    public ArtistWatchFeedResult? WatchFeed => Artist?.WatchFeed;
    public bool HasPinnedItem => PinnedItem != null;
    public GridLength PinnedColumnWidth => HasPinnedItem ? new GridLength(280) : new GridLength(0);
    public bool HasWatchFeed => WatchFeed != null;
    public bool HasPinnedComment => !string.IsNullOrWhiteSpace(PinnedItem?.Comment);

    public string? PinnedBackdropImageUrl =>
        !string.IsNullOrWhiteSpace(PinnedItem?.BackgroundImageUrl)
            ? PinnedItem!.BackgroundImageUrl
            : PinnedItem?.ImageUrl;

    public string? PinnedItemTitle => PinnedItem?.Title;
    public string? PinnedItemComment => PinnedItem?.Comment;
    public string? PinnedItemThumbnailUrl => PinnedItem?.ImageUrl;
    public string? PinnedItemSubtitle => PinnedItem?.Subtitle;
    public string? PinnedItemUri => PinnedItem?.Uri;
    public bool HasConcerts => Concerts.Count > 0;

    /// <summary>Count of upcoming concerts. Used by the V4A rhythm-break banner
    /// ("On Tour Now") to assemble its headline. Raised by hand whenever
    /// <c>_concerts</c> mutates, alongside <see cref="HasConcerts"/>.</summary>
    public int ConcertCount => _concerts.Count;

    /// <summary>Date of the closest upcoming concert for the rhythm-break sub
    /// line. Null when no concerts are scheduled.</summary>
    public string? FirstConcertDateFormatted =>
        _concerts.Count == 0 ? null : _concerts[0].DateFormatted;

    /// <summary>Venue of the closest upcoming concert.</summary>
    public string? FirstConcertVenue =>
        _concerts.Count == 0 ? null : _concerts[0].Venue;

    /// <summary>City of the closest upcoming concert.</summary>
    public string? FirstConcertCity =>
        _concerts.Count == 0 ? null : _concerts[0].City;

    /// <summary>Tour banner headline used by the rhythm-break — uses the tour
    /// title when distinct from the artist name, else falls back to a generic
    /// phrasing keyed off the upcoming-date count.</summary>
    public string TourBannerHeadline
    {
        get
        {
            if (_concerts.Count == 0) return string.Empty;
            var titled = _concerts.Select(c => c.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (!string.IsNullOrWhiteSpace(titled) && !string.Equals(titled, ArtistName, StringComparison.OrdinalIgnoreCase))
                return titled!;

            // Fallback copy matched to the eyebrow categorisation so a 1-date
            // festival appearance doesn't read as "on tour".
            var allFestivals = _concerts.All(c => c.IsFestival);
            if (allFestivals) return $"Catch {ArtistName} at festivals";
            if (_concerts.Count == 1) return $"{ArtistName} live";
            if (_concerts.Count <= 3) return $"{ArtistName} — live dates";
            return $"{ArtistName} — on tour";
        }
    }

    /// <summary>
    /// Context-aware eyebrow label for the <c>RhythmBreakBanner</c>. The
    /// hardcoded "ON TOUR NOW" label was misleading for the common cases
    /// (single concert / single festival date / many dates months out).
    /// Decision tree (count + festival flag + first-date proximity):
    /// <list type="bullet">
    ///   <item>All festivals → "FESTIVAL APPEARANCES" (regardless of count).</item>
    ///   <item>1 concert → "UPCOMING SHOW".</item>
    ///   <item>2–3 concerts → "UPCOMING DATES".</item>
    ///   <item>≥ 4 concerts, first within 7 days → "ON TOUR NOW".</item>
    ///   <item>≥ 4 concerts, first &gt; 7 days out → "UPCOMING TOUR".</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// True when the banner is currently in the "ON TOUR NOW" state — the
    /// only state that gets the accent left-edge stripe + pulsing dot
    /// treatment. Computed from <see cref="TourBannerEyebrow"/> so it always
    /// matches what the eyebrow text says.
    /// </summary>
    public bool TourBannerIsLive
        => string.Equals(TourBannerEyebrow, "ON TOUR NOW", StringComparison.Ordinal);

    /// <summary>
    /// Segoe Fluent Icons glyph for the banner's icon column. Resolves the
    /// eyebrow categorisation to a centralised <see cref="Styles.FluentGlyphs"/>
    /// constant so PUA codepoints stay out of this .cs file (raw PUA chars
    /// don't survive editor encoding round-trips reliably):
    /// <list type="bullet">
    ///   <item>"FESTIVAL APPEARANCES" → <see cref="Styles.FluentGlyphs.Ribbon"/> (EB44).</item>
    ///   <item>Multi-date tour ("ON TOUR NOW" / "UPCOMING TOUR" / "UPCOMING DATES") → <see cref="Styles.FluentGlyphs.Calendar"/> (E787).</item>
    ///   <item>Single-show ("UPCOMING SHOW") → <see cref="Styles.FluentGlyphs.Microphone"/> (E720).</item>
    /// </list>
    /// </summary>
    public string TourBannerIconGlyph
    {
        get
        {
            var e = TourBannerEyebrow;
            if (e == "FESTIVAL APPEARANCES") return Styles.FluentGlyphs.Ribbon;
            if (e == "UPCOMING SHOW") return Styles.FluentGlyphs.Microphone;
            if (e == "ON TOUR NOW" || e == "UPCOMING TOUR" || e == "UPCOMING DATES")
                return Styles.FluentGlyphs.Calendar;
            return Styles.FluentGlyphs.Calendar;
        }
    }

    public string TourBannerEyebrow
    {
        get
        {
            var count = _concerts.Count;
            if (count == 0) return string.Empty;

            var allFestivals = _concerts.All(c => c.IsFestival);
            if (allFestivals) return "FESTIVAL APPEARANCES";

            if (count == 1) return "UPCOMING SHOW";
            if (count <= 3) return "UPCOMING DATES";

            // count >= 4 → it's a tour. Differentiate "now" vs "upcoming" by
            // when the next upcoming date is. We can't reliably tell whether
            // Pathfinder returns past concerts (it usually only returns
            // future), so "first concert within 7 days" is the proxy for
            // "actively touring".
            var todayLocal = DateTimeOffset.Now.Date;
            var firstUpcoming = _concerts
                .Select(c => c.Date)
                .Where(d => d.Date >= todayLocal)
                .DefaultIfEmpty(_concerts[0].Date)
                .Min();
            var daysUntilFirst = (firstUpcoming.Date - todayLocal).TotalDays;

            return daysUntilFirst <= 7 ? "ON TOUR NOW" : "UPCOMING TOUR";
        }
    }

    public string TourBannerSubline
    {
        get
        {
            if (_concerts.Count == 0) return string.Empty;
            var first = _concerts[0];
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(first.DateFormatted)) parts.Add($"Next: {first.DateFormatted}");
            if (!string.IsNullOrWhiteSpace(first.Venue)) parts.Add(first.Venue!);
            if (!string.IsNullOrWhiteSpace(first.City)) parts.Add(first.City!);
            if (_concerts.Count > 1) parts.Add($"{_concerts.Count} dates total");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>True only when <c>?debug</c> was passed via the navigation
    /// parameter — gates the small "source-chip" pills on each V4A section
    /// header that name the GraphQL fragments backing it.</summary>
    [ObservableProperty] private bool _isDebugMode;

    // -- Location operations (delegated to ILocationService) --

    public async Task<List<LocationSearchResult>> SearchLocationsAsync(string query, CancellationToken ct = default)
        => await _locationService.SearchAsync(query, ct);

    public async Task SaveLocationAsync(string geonameId, string? cityName)
    {
        await _locationService.SaveByGeonameIdAsync(geonameId, cityName);
        UserLocationName = cityName ?? _locationService.CurrentCity;
        RefreshNearUserFlags();
    }

    public async Task<LocationSearchResult?> ResolveCurrentLocationAsync()
    {
        try
        {
            var geolocator = new Windows.Devices.Geolocation.Geolocator();
            var position = await geolocator.GetGeopositionAsync();
            var lat = position.Coordinate.Point.Position.Latitude;
            var lon = position.Coordinate.Point.Position.Longitude;

            var results = await _locationService.SearchByCoordinatesAsync(lat, lon);
            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve current location");
            return null;
        }
    }

    public void RefreshNearUserFlags()
    {
        foreach (var c in Concerts)
            c.IsNearUser = _locationService.IsNearUser(c.City);
    }

    // -- Tab management --
    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    // -- Constructor --

    public ArtistViewModel(
        IArtistService artistService,
        ArtistStore artistStore,
        IAlbumService albumService,
        ILocationService locationService,
        IPlaybackService playbackService,
        IPlaybackStateService playbackStateService,
        IColorService colorService,
        ITrackLikeService? likeService = null,
        ISettingsService? settingsService = null,
        Wavee.UI.WinUI.Services.ArtistBioSummarizer? bioSummarizer = null,
        ILogger<ArtistViewModel>? logger = null)
    {
        _artistService = artistService;
        _artistStore = artistStore;
        _albumService = albumService;
        _locationService = locationService;
        _playbackService = playbackService;
        _playbackStateService = playbackStateService;
        _colorService = colorService;
        _likeService = likeService;
        _settingsService = settingsService;
        _bioSummarizer = bioSummarizer;
        _logger = logger;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        AttachLongLivedServices();

        SelectedTopTracks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedTopTracksCount));
            OnPropertyChanged(nameof(HasTopTracksSelection));
        };

        // Hydrate the discography card-size scale from persisted settings,
        // clamped to the slider's range so a stale config can't render the
        // grid unusable.
        if (_settingsService != null)
        {
            var saved = _settingsService.Settings.ArtistDiscographyGridScale;
            _discographyGridScale = saved >= 0.7 && saved <= 1.6 ? saved : 1.0;
        }

        Diagnostics.LiveInstanceTracker.Register(this);
    }

    partial void OnDiscographyGridScaleChanged(double value)
    {
        // Mirror Library's GridScale persistence — clamp to slider range to
        // protect against out-of-bounds writes from external callers.
        var clamped = Math.Clamp(value, 0.7, 1.6);
        _settingsService?.Update(s => s.ArtistDiscographyGridScale = clamped);
    }

    // -- Initialization --

    public void Initialize(string artistId)
    {
        AttachLongLivedServices();

        // Reset on any artist-id change, including null?firstId. The earlier
        // guard `ArtistId != null && ArtistId != artistId` was defensive
        // against a redundant clear on the very first nav (everything's empty
        // anyway), but on a Required-cache reused page the same VM serves
        // many artists and the prior null-guard occasionally let stale state
        // through (TopTracks, ArtistName, MonthlyListeners) when navigating
        // X?Y in the same tab. Clearing on every change is harmless on first
        // nav and correct on every subsequent nav.
        if (ArtistId != artistId)
        {
            Interlocked.Increment(ref _loadGeneration);
            ResetForNewArtist();
            _appliedOverviewFor = null;
            _appliedOverview = null;
        }

        ArtistId = artistId;
        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Artist, artistId)
        {
            Title = "Artist"
        };
        RefreshFollowState();
        SyncArtistPlaybackState();

        // Drop any prior subscription (cancels its inflight fetch via refcount==0)
        // and start observing the new artist through the reactive store.
        _subscriptions?.Dispose();
        _subscriptions = new CompositeDisposable();

        var sub = _artistStore.Observe(artistId)
            .Subscribe(
                state => _dispatcherQueue.TryEnqueue(() => ApplyOverviewState(state, artistId)),
                ex => _logger?.LogError(ex, "ArtistStore stream faulted for {ArtistId}", artistId));
        _subscriptions.Add(sub);
    }

    private bool IsCurrentLoad(string artistId, int generation)
        => !_disposed
           && generation == Volatile.Read(ref _loadGeneration)
           && string.Equals(ArtistId, artistId, StringComparison.Ordinal);

    /// <summary>
    /// Dispose the store subscription; fetches for this VM stop and any
    /// TaskCanceledException propagation is avoided.
    /// </summary>
    public void Deactivate()
    {
        DetachLongLivedServices();
        _subscriptions?.Dispose();
        _subscriptions = null;
    }

    // Long-lived singleton subscriptions are attached lazily on first use and
    // detached on Hibernate so the (Transient) VM is not pinned by the singleton
    // services' invocation lists across navigations. Idempotent in both directions.
    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        _playbackStateService.PropertyChanged += OnPlaybackStateChanged;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
        _playbackStateService.PropertyChanged -= OnPlaybackStateChanged;
    }

    /// <summary>
    /// Light hibernation for cached pages going off-screen. Disposes the store
    /// subscription and releases the things that pin DirectX textures (hero /
    /// avatar / pinned-card / latest-release image URLs). Data collections
    /// (TopTracks, _allReleases, Albums, Singles, Compilations, RelatedArtists,
    /// Concerts, ExternalLinks, TopCities, GalleryPhotos) and the
    /// <c>_appliedOverviewFor</c> marker are intentionally preserved so a
    /// revisit to the same artist short-circuits in <see cref="ApplyOverviewState"/>
    /// without re-running the heavy <see cref="LoadAsync"/> path (which costs
    /// ~2 s on a popular artist: replaces section snapshots, reseeds
    /// virtualized item containers, kicks off background discography paging
    /// + release-color prefetch). The hero URLs are restored via
    /// <see cref="EnsureHeroUrls"/> in the Ready branch when LoadAsync is
    /// skipped.
    /// </summary>
    public void Hibernate()
    {
        Deactivate();

        SelectedTopTracks.Clear();
        ExpandedAlbumTracks.Clear();
        CancelAndDisposeDiscographyCts();

        if (Artist is { } artist)
        {
            Artist = artist with
            {
                ArtistImageUrl = null,
                HeaderImageUrl = null,
                LatestRelease = artist.LatestRelease is null
                    ? null
                    : artist.LatestRelease with { ImageUrl = null },
                PinnedItem = null
            };
        }
    }

    private void ApplyOverviewState(EntityState<ArtistOverviewResult> state, string expectedArtistId)
    {
        if (_disposed || ArtistId != expectedArtistId)
            return;

        switch (state)
        {
            case EntityState<ArtistOverviewResult>.Initial:
                IsLoading = true;
                break;
            case EntityState<ArtistOverviewResult>.Loading loading:
                IsLoading = loading.Previous is null;
                break;
            case EntityState<ArtistOverviewResult>.Ready ready:
                // Music-video catalog cache pre-warm. Runs unconditionally so
                // the cache is populated whether LoadAsync runs (fresh nav) or
                // we go down the EnsureHeroUrls path (cache-served re-show).
                NoteTopTracksHaveVideo(ready.Value);

                if (_appliedOverviewFor != expectedArtistId || !ReferenceEquals(_appliedOverview, ready.Value))
                {
                    _ = LoadAsync(ready.Value, expectedArtistId);
                }
                else
                {
                    // Same artist, stale-but-not-fresh — Hibernate may have
                    // null'd the hero URL bindings (texture release) without
                    // touching data collections. Restore the URLs from the
                    // cached overview without re-running the heavy LoadAsync.
                    EnsureHeroUrls(ready.Value);
                }
                IsLoading = false;
                break;
            case EntityState<ArtistOverviewResult>.Error error:
                HasError = true;
                ErrorMessage = error.Exception.Message;
                IsLoading = false;
                _logger?.LogError(error.Exception, "ArtistStore reported error for {ArtistId}", expectedArtistId);
                break;
        }
    }

    /// <summary>
    /// Restore the hero / avatar / latest-release / pinned-card image URLs from
    /// the cached overview after a Hibernate-triggered URL null-out. Cheap —
    /// six property assignments. Pairs with <see cref="Hibernate"/>.
    /// </summary>
    /// <summary>
    /// Populates the music-video catalog cache with the top-tracks' has-video
    /// flags. Called from <c>ApplyOverviewState</c> on every Ready state — both
    /// fresh navigations (where LoadAsync runs) and cache-served re-shows
    /// (where only EnsureHeroUrls runs). Harmless to call twice — the cache
    /// is idempotent.
    /// </summary>
    private void NoteTopTracksHaveVideo(ArtistOverviewResult overview)
    {
        if ((overview.TopTracks is null || overview.TopTracks.Count == 0)
            && overview.MusicVideoMappings.Count == 0)
            return;

        var videoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.UI.WinUI.Services.IMusicVideoMetadataService>();
        if (videoMetadata is null) return;

        _logger?.LogInformation("[VideoCache] ArtistViewModel pre-warm: {Count} top tracks for {Artist}",
            overview.TopTracks?.Count ?? 0, ArtistId ?? "<unknown>");
        if (overview.TopTracks is not null)
        {
            foreach (var track in overview.TopTracks)
            {
                if (string.IsNullOrEmpty(track.Uri)) continue;
                videoMetadata.NoteHasVideo(track.Uri, track.HasVideo);
            }
        }

        foreach (var mapping in overview.MusicVideoMappings)
        {
            videoMetadata.NoteVideoUri(mapping.AudioTrackUri, mapping.VideoTrackUri);
            _logger?.LogDebug("[VideoCache]   {AudioUri} -> {VideoUri}",
                mapping.AudioTrackUri, mapping.VideoTrackUri);
        }
    }

    private ArtistView BuildArtistView(
        ArtistOverviewResult overview,
        string? fallbackName = null,
        string? fallbackImageUrl = null)
    {
        return new ArtistView(
            Name: overview.Name ?? fallbackName,
            ArtistImageUrl: overview.ImageUrl ?? fallbackImageUrl,
            HeaderImageUrl: overview.HeaderImageUrl,
            HeaderHeroColorHex: overview.HeroColorHex,
            Palette: overview.Palette,
            MonthlyListeners: overview.MonthlyListeners > 0
                ? overview.MonthlyListeners.ToString("N0")
                : null,
            WorldRank: overview.WorldRank,
            Followers: overview.Followers,
            Biography: overview.Biography,
            IsVerified: overview.IsVerified,
            IsRegistered: overview.IsRegistered,
            LatestRelease: overview.LatestRelease,
            AlbumsTotalCount: overview.AlbumsTotalCount,
            SinglesTotalCount: overview.SinglesTotalCount,
            CompilationsTotalCount: overview.CompilationsTotalCount,
            PinnedItem: overview.PinnedItem,
            WatchFeed: overview.WatchFeed);
    }

    private void EnsureHeroUrls(ArtistOverviewResult overview)
    {
        if (Artist is null)
        {
            Artist = BuildArtistView(overview);
            return;
        }

        var latest = Artist.LatestRelease;
        if (overview.LatestRelease is not null && string.IsNullOrEmpty(latest?.ImageUrl))
        {
            latest = (latest ?? overview.LatestRelease) with
            {
                ImageUrl = overview.LatestRelease.ImageUrl
            };
        }

        var next = Artist with
        {
            ArtistImageUrl = string.IsNullOrEmpty(Artist.ArtistImageUrl)
                ? overview.ImageUrl
                : Artist.ArtistImageUrl,
            HeaderImageUrl = string.IsNullOrEmpty(Artist.HeaderImageUrl)
                ? overview.HeaderImageUrl
                : Artist.HeaderImageUrl,
            LatestRelease = latest,
            PinnedItem = Artist.PinnedItem ?? overview.PinnedItem
        };

        if (!Equals(next, Artist))
            Artist = next;
    }

    private void ResetForNewArtist()
    {
        Artist = null;
        BioSummaryText = null;
        IsBioSummaryLoading = false;
        _bioSummaryCts?.Cancel();
        IsFollowing = false;
        HasData = false;
        CurrentPage = 0;
        ExpandedAlbum = null;
        ExpandedAlbumTracks.Clear();
        IsPlayPending = false;
        IsArtistContextPlaying = false;
        IsArtistContextPaused = false;

        TopTracks.Clear();
        _allReleases.Clear();
        _albums.Clear();
        _singles.Clear();
        _compilations.Clear();
        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        // Clear below-the-fold sections synchronously during the artist swap.
        // A previous low-priority clear could run after a warm-cache Ready path
        // had already repopulated these collections, leaving discovery, videos,
        // concerts, merch, and popular/spotlight sections collapsed until the
        // next navigation.
        _relatedArtists.Clear();
        _concerts.Clear();
        _externalLinks.Clear();
        _topCities.Clear();
        _galleryPhotos.Clear();
        _popularReleases.Clear();
        _musicVideos.Clear();
        _merch.Clear();
        _appearsOn.Clear();
        _playlists.Clear();
        NotifySecondarySectionsChanged();
    }

    private void NotifySecondarySectionsChanged()
    {
        OnPropertyChanged(nameof(HasRelatedArtists));
        OnPropertyChanged(nameof(HasConcerts));
        OnPropertyChanged(nameof(HasExternalLinks));
        OnPropertyChanged(nameof(HasTopCities));
        OnPropertyChanged(nameof(HasConnectSection));
        OnPropertyChanged(nameof(HasGallery));
        OnPropertyChanged(nameof(HasPopularReleases));
        NotifySpotlightReleaseChanged();
        OnPropertyChanged(nameof(HasMusicVideos));
        OnPropertyChanged(nameof(HasMerch));
        OnPropertyChanged(nameof(HasAppearsOn));
        OnPropertyChanged(nameof(HasPlaylists));
    }

    private CancellationToken CreateFreshDiscographyToken()
    {
        CancelAndDisposeDiscographyCts();
        _discoCts = new CancellationTokenSource();
        return _discoCts.Token;
    }

    private void CancelAndDisposeDiscographyCts()
    {
        var cts = Interlocked.Exchange(ref _discoCts, null);
        if (cts == null) return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    public void PrefillFrom(ContentNavigationParameter nav)
    {
        if (string.IsNullOrEmpty(nav.Title) && string.IsNullOrEmpty(nav.ImageUrl))
            return;

        var current = Artist ?? new ArtistView(
            Name: null,
            ArtistImageUrl: null,
            HeaderImageUrl: null,
            HeaderHeroColorHex: null,
            Palette: null,
            MonthlyListeners: null,
            WorldRank: null,
            Followers: 0,
            Biography: null,
            IsVerified: false,
            IsRegistered: false,
            LatestRelease: null,
            AlbumsTotalCount: 0,
            SinglesTotalCount: 0,
            CompilationsTotalCount: 0,
            PinnedItem: null,
            WatchFeed: null);

        Artist = current with
        {
            Name = !string.IsNullOrEmpty(nav.Title) ? nav.Title : current.Name,
            ArtistImageUrl = !string.IsNullOrEmpty(nav.ImageUrl) ? nav.ImageUrl : current.ArtistImageUrl
        };
    }

    /// <summary>
    /// Distributes _allReleases into Albums, Singles, Compilations collections.
    /// </summary>

    /// <summary>
    /// Distributes _allReleases into Albums, Singles, Compilations collections.
    /// </summary>
    private void DispatchReleases()
    {
        var albums = new List<LazyReleaseItem>();
        var singles = new List<LazyReleaseItem>();
        var compilations = new List<LazyReleaseItem>();

        foreach (var group in _allReleases
            .GroupBy(r => r.Data?.Type ?? InferTypeFromId(r.Id))
            .OrderBy(g => g.Key))
        {
            var sorted = group.OrderByDescending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue);
            var target = group.Key switch
            {
                "ALBUM" => albums,
                "SINGLE" => singles,
                "COMPILATION" => compilations,
                _ => null
            };

            if (target == null) continue;
            target.AddRange(sorted);
        }

        _albums.ReplaceWith(albums);
        _singles.ReplaceWith(singles);
        _compilations.ReplaceWith(compilations);
    }

    private static string InferTypeFromId(string id)
    {
        if (id.StartsWith("album-ph")) return "ALBUM";
        if (id.StartsWith("single-ph")) return "SINGLE";
        if (id.StartsWith("comp-ph")) return "COMPILATION";
        return "ALBUM";
    }

    private static string BuildHeroBioLine(string? biography, string? summary, string? artistName)
    {
        var source = FirstSentenceOrText(biography) ?? FirstSentenceOrText(summary);
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // Strip HTML tags + decode entities so &#39;, &#34;, etc. render as real characters
        // and any <em>/<a> markup from Spotify HTML doesn't leak as visible text.
        var stripped = System.Text.RegularExpressions.Regex.Replace(source, @"<[^>]+>", " ");
        var line = System.Net.WebUtility.HtmlDecode(stripped).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (!string.IsNullOrWhiteSpace(artistName))
            line = StripLeadingArtistSubject(line, artistName!);

        line = StripLeadingArticle(line);
        line = EnsureSentenceCasing(line);
        if (line.Length > 150)
            line = line.Substring(0, 147).TrimEnd() + "...";
        return line;
    }

    private static string? FirstSentenceOrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var end = normalized.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? normalized.Substring(0, end + 1) : normalized;
    }

    private static string StripLeadingArtistSubject(string line, string artistName)
    {
        var prefixes = new[]
        {
            $"{artistName} is ",
            $"{artistName} are ",
            $"{artistName} was ",
            $"{artistName} were "
        };

        foreach (var prefix in prefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line.Substring(prefix.Length).TrimStart();
        }

        return line;
    }

    private static string StripLeadingArticle(string line)
    {
        if (line.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            return line[2..].TrimStart();
        if (line.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
            return line[3..].TrimStart();
        if (line.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            return line[4..].TrimStart();
        return line;
    }

    private static string EnsureSentenceCasing(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return string.Empty;

        line = char.ToUpperInvariant(line[0]) + line[1..];
        return line.EndsWith(".", StringComparison.Ordinal) ||
               line.EndsWith("!", StringComparison.Ordinal) ||
               line.EndsWith("?", StringComparison.Ordinal)
            ? line
            : line + ".";
    }

    private static string FormatReleaseSubtitle(Wavee.UI.WinUI.ViewModels.ArtistReleaseVm? release)
    {
        if (release is null) return string.Empty;

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(release.Type)) parts.Add(ToTitleCaseInvariant(release.Type));
        if (release.Year > 0) parts.Add(release.Year.ToString());
        if (release.TrackCount > 0)
            parts.Add(release.TrackCount == 1 ? "1 track" : $"{release.TrackCount} tracks");

        return string.Join(" - ", parts);
    }

    private static string ToTitleCaseInvariant(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private void NotifySpotlightReleaseChanged()
    {
        OnPropertyChanged(nameof(HasSpotlightRelease));
        OnPropertyChanged(nameof(SpotlightCardMode));
        OnPropertyChanged(nameof(SpotlightReleaseName));
        OnPropertyChanged(nameof(SpotlightReleaseSubtitle));
        OnPropertyChanged(nameof(SpotlightReleaseImageUrl));
        OnPropertyChanged(nameof(SpotlightReleaseUri));
        OnPropertyChanged(nameof(SpotlightReleaseTagText));
        OnPropertyChanged(nameof(SpotlightReleaseEyebrowText));
        OnPropertyChanged(nameof(SpotlightCommentText));
        OnPropertyChanged(nameof(HasSpotlightComment));
        OnPropertyChanged(nameof(PopularReleasesDisplayed));
    }

    // -- Load data from real Pathfinder API --

    /// <summary>
    /// Apply a freshly-fetched ArtistOverviewResult from the ArtistStore and
    /// kick off the downstream cascade (extended tracks, discography pages,
    /// concerts, color prefetch). Called by ApplyOverviewState on each
    /// Ready emission; idempotent per (artistId, overview-ref).
    /// </summary>
    private async Task LoadAsync(ArtistOverviewResult overview, string artistId)
    {
        var generation = Volatile.Read(ref _loadGeneration);
        if (!IsCurrentLoad(artistId, generation)) return;
        _appliedOverviewFor = artistId;
        _appliedOverview = overview;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        try
        {

            var fallbackName = ArtistName;
            var fallbackImageUrl = ArtistImageUrl;
            Artist = BuildArtistView(overview, fallbackName, fallbackImageUrl);
            var releaseImageByUri = new Dictionary<string, string>(StringComparer.Ordinal);
            var releaseImageByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddReleaseImages(overview.Albums, releaseImageByUri, releaseImageByName);
            AddReleaseImages(overview.Singles, releaseImageByUri, releaseImageByName);
            AddReleaseImages(overview.Compilations, releaseImageByUri, releaseImageByName);

            // -- Top tracks (batch to avoid N+1 CollectionChanged events) --
            var newTracks = new ObservableCollection<LazyTrackItem>();
            // Populate the music-video catalog cache as we map top tracks.
            // Avoids a redundant NPV roundtrip when the user clicks a track
            // they've already seen on this artist page.
            var videoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Wavee.UI.WinUI.Services.IMusicVideoMetadataService>();
            _logger?.LogInformation("[VideoCache] ArtistViewModel populating cache with {Count} top tracks (cacheResolved={HasCache})",
                overview.TopTracks.Count, videoMetadata is not null);
            int idx = 1;
            foreach (var track in overview.TopTracks)
            {
                var trackVm = new ArtistTopTrackVm
                {
                    Id = track.Id,
                    Index = idx,
                    Title = track.Title,
                    Uri = track.Uri,
                    AlbumName = track.AlbumName,
                    AlbumImageUrl = track.AlbumImageUrl
                                    ?? TryGetReleaseImage(track, releaseImageByUri, releaseImageByName),
                    AlbumUri = track.AlbumUri,
                    Duration = track.Duration,
                    PlayCountRaw = track.PlayCount,
                    ArtistNames = track.ArtistNames,
                    IsExplicit = track.IsExplicit,
                    IsPlayable = track.IsPlayable,
                    HasCanvasVideo = track.HasVideo
                };
                newTracks.Add(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                if (videoMetadata is not null && !string.IsNullOrEmpty(track.Uri))
                {
                    videoMetadata.NoteHasVideo(track.Uri, track.HasVideo);
                    _logger?.LogDebug("[VideoCache]   {Uri} ? hasVideo={HasVideo}", track.Uri, track.HasVideo);
                }
                idx++;
            }

            if (videoMetadata is not null)
            {
                foreach (var mapping in overview.MusicVideoMappings)
                {
                    videoMetadata.NoteVideoUri(mapping.AudioTrackUri, mapping.VideoTrackUri);
                    _logger?.LogDebug("[VideoCache]   {AudioUri} -> {VideoUri}",
                        mapping.AudioTrackUri, mapping.VideoTrackUri);
                }

                // Light the music-video badge on rows whose Spotify track is
                // linked to a local music-video file. Fire-and-forget; the
                // VM's HasLinkedLocalVideo setter raises PropertyChanged so
                // TrackItem updates its badge live when the result lands.
                var topTrackVms = newTracks
                    .Where(i => i.IsLoaded && i.Data is ArtistTopTrackVm)
                    .Select(i => (ArtistTopTrackVm)i.Data!)
                    .ToList();
                if (topTrackVms.Count > 0)
                {
                    _ = videoMetadata.ApplyAvailabilityToAsync(
                        topTrackVms,
                        static t => t.Uri,
                        static (t, v) => t.HasLinkedLocalVideo = v,
                        CancellationToken.None);
                }
            }

            // Pad + shimmer placeholders
            var loadedCount = idx - 1;
            var pageSize = TracksPerPage > 0 ? TracksPerPage : 12;
            var remainder = loadedCount % pageSize;
            var padCount = remainder > 0 ? pageSize - remainder : 0;
            for (int i = 0; i < padCount + pageSize; i++)
            {
                newTracks.Add(LazyTrackItem.Placeholder($"placeholder-{idx}", idx));
                idx++;
            }
            _topTracks.ReplaceWith(newTracks);
            NotifyPaginationChanged();
            OnPropertyChanged(nameof(TopTracksFirst10));

            // -- Backfill missing cover art (background, parallel) --
            // Spotify's getArtistOverview GraphQL response is inconsistent: many
            // tracks come back without albumOfTrack.coverArt populated. Resolve
            // them via the extended-metadata pipeline and patch the VMs.
            _ = EnrichMissingTopTrackImagesAsync(artistId, generation);

            // -- Extended top tracks (background, parallel) --

            // -- Releases --
            _allReleases.Clear();
            AddReleasesToList(overview.Albums, "ALBUM", "album-ph", overview.AlbumsTotalCount);
            AddReleasesToList(overview.Singles, "SINGLE", "single-ph", overview.SinglesTotalCount);
            AddReleasesToList(overview.Compilations, "COMPILATION", "comp-ph", overview.CompilationsTotalCount);
            DispatchReleases();


            // -- Background discography pagination --

            // -- Related artists (batch swap) --
            _relatedArtists.ReplaceWith(overview.RelatedArtists.Select(ra => new RelatedArtistVm
            {
                Id = ra.Id,
                Uri = ra.Uri,
                Name = ra.Name,
                ImageUrl = ra.ImageUrl
            }));
            OnPropertyChanged(nameof(HasRelatedArtists));

            // -- Concerts (batch swap) --
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
            OnPropertyChanged(nameof(ConcertCount));
            OnPropertyChanged(nameof(FirstConcertDateFormatted));
            OnPropertyChanged(nameof(FirstConcertVenue));
            OnPropertyChanged(nameof(FirstConcertCity));
            OnPropertyChanged(nameof(TourBannerHeadline));
            OnPropertyChanged(nameof(TourBannerSubline));
            OnPropertyChanged(nameof(TourBannerEyebrow));
            OnPropertyChanged(nameof(TourBannerIsLive));
            OnPropertyChanged(nameof(TourBannerIconGlyph));

            UserLocationName = _locationService.CurrentCity;


            // -- Connect & Markets + Gallery (batch swap) --
            _externalLinks.ReplaceWith(overview.ExternalLinks.Select(l => new ArtistSocialLinkVm
            {
                Name = l.Name,
                Url = l.Url,
                Icon = Wavee.UI.WinUI.Styles.FluentGlyphs.ResolveSocialIcon(l.Url, l.Name)
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
                DisplayCount = FormatListenerCount(c.NumberOfListeners),
                RelativeWidth = maxListeners > 0
                    ? Math.Max(8, c.NumberOfListeners * 200.0 / maxListeners)
                    : 8
            }));
            OnPropertyChanged(nameof(HasTopCities));
            OnPropertyChanged(nameof(HasConnectSection));

            _galleryPhotos.ReplaceWith(overview.GalleryPhotos);
            OnPropertyChanged(nameof(HasGallery));

            // -- Popular releases (batch swap) — same shape as Albums but
            // ranked by play count rather than newest-first. Drives the
            // V4A "Popular releases" sidebar pairing.
            int popIdx = 0;
            _popularReleases.ReplaceWith(overview.PopularReleases.Select(r =>
                LazyReleaseItem.Loaded(r.Id, popIdx++, new Wavee.UI.WinUI.ViewModels.ArtistReleaseVm
                {
                    Id = r.Id,
                    Uri = r.Uri,
                    Name = r.Name,
                    Type = r.Type,
                    ImageUrl = r.ImageUrl,
                    ReleaseDate = r.ReleaseDate,
                    TrackCount = r.TrackCount,
                    Label = r.Label,
                    Year = r.Year
                })));
            OnPropertyChanged(nameof(HasPopularReleases));
            OnPropertyChanged(nameof(PopularReleasesDisplayed));
            NotifySpotlightReleaseChanged();

            // -- Appears On (batch swap) — same shape as Compilations.
            int appearsIdx = 0;
            _appearsOn.ReplaceWith(overview.AppearsOn.Select(r =>
                LazyReleaseItem.Loaded(r.Id, appearsIdx++, new Wavee.UI.WinUI.ViewModels.ArtistReleaseVm
                {
                    Id = r.Id,
                    Uri = r.Uri,
                    Name = r.Name,
                    Type = r.Type,
                    ImageUrl = r.ImageUrl,
                    ReleaseDate = r.ReleaseDate,
                    TrackCount = r.TrackCount,
                    Label = r.Label,
                    Year = r.Year
                })));
            OnPropertyChanged(nameof(HasAppearsOn));

            // -- Playlists & discovery (batch swap) — playlistsV2 + featuringV2
            // + discoveredOnV2 already merged with per-item subtitles in ArtistService.
            _playlists.ReplaceWith(overview.Playlists.Select(p => new ArtistPlaylistVm
            {
                Uri = p.Uri,
                Name = p.Name,
                ImageUrl = p.ImageUrl,
                Subtitle = p.Subtitle
            }));
            OnPropertyChanged(nameof(HasPlaylists));

            // -- Music videos (batch swap) --
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

            // -- Merch (batch swap) --
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

            CurrentPage = 0;
            NotifyPaginationChanged();
            _ = StartDeferredArtistWorkAsync(artistId, generation, overview);

            // V4A: kick off the on-device "about this artist" excerpt when
            // Spotify's ArtistOverview has no biography. Gated through
            // AiCapabilities.IsArtistBioSummarizeEnabled inside the summarizer
            // so a thin artist on a non-Copilot+ PC or with AI disabled is a
            // cheap no-op. Fire-and-forget — the result lands on a bound
            // observable property, the page reveals it via implicit animation.
            if (_bioSummarizer is not null && string.IsNullOrWhiteSpace(overview.Biography))
                _ = LoadBioSummaryAsync(artistId);
        }
        catch (SessionException)
        {
            HasError = true;
            ErrorMessage = "Connecting to Spotify…";
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ErrorMapper.ToUserMessage(ex);
            _logger?.LogError(ex, "Failed to load artist {ArtistId}", ArtistId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // -- Mapping helpers --

    private async Task StartDeferredArtistWorkAsync(
        string artistId,
        int generation,
        ArtistOverviewResult overview)
    {
        // Let ArtistPage render hero/top-tracks before starting secondary work.
        // These tasks update below-the-fold images, color chips, extended tracks,
        // and remaining discography pages; starting them in the same dispatcher
        // slice as LoadAsync makes PlayerBar artist navigation feel heavy.
        await Task.Yield();
        await Task.Delay(48);

        if (!IsCurrentLoad(artistId, generation))
            return;

        _ = LoadExtendedTopTracksAsync(artistId, generation);

        var releasesSnapshot = _allReleases
            .Where(item => item.IsLoaded && item.Data != null)
            .Select(item => item.Data!)
            .ToList();
        _ = PrefetchReleaseColorsAsync(artistId, generation, releasesSnapshot);

        var discoToken = CreateFreshDiscographyToken();
        _ = Task.Run(() => FetchRemainingDiscographyAsync(
            artistId, generation,
            overview.Albums.Count, overview.AlbumsTotalCount,
            overview.Singles.Count, overview.SinglesTotalCount,
            overview.Compilations.Count, overview.CompilationsTotalCount,
            discoToken), discoToken);
    }

    private void AddReleasesToList(
        List<ArtistReleaseResult> releases,
        string type,
        string phPrefix,
        int totalCount)
    {
        int count = 0;
        foreach (var r in releases)
        {
            var vm = new Wavee.UI.WinUI.ViewModels.ArtistReleaseVm
            {
                Id = r.Id,
                Uri = r.Uri,
                Name = r.Name,
                Type = type,
                ImageUrl = r.ImageUrl,
                ReleaseDate = r.ReleaseDate,
                TrackCount = r.TrackCount,
                Label = r.Label,
                Year = r.Year
            };
            _allReleases.Add(LazyReleaseItem.Loaded(vm.Id, count, vm));
            count++;
        }

        var maxPlaceholders = Math.Min(totalCount - count, 20);
        for (int i = count; i < count + maxPlaceholders; i++)
            _allReleases.Add(LazyReleaseItem.Placeholder($"{phPrefix}-{i}", i));
    }

    private static void AddReleaseImages(
        IEnumerable<ArtistReleaseResult> releases,
        IDictionary<string, string> byUri,
        IDictionary<string, string> byName)
    {
        foreach (var release in releases)
        {
            if (string.IsNullOrWhiteSpace(release.ImageUrl))
                continue;

            if (!string.IsNullOrWhiteSpace(release.Uri))
                byUri.TryAdd(release.Uri, release.ImageUrl);

            if (!string.IsNullOrWhiteSpace(release.Name))
                byName.TryAdd(release.Name, release.ImageUrl);
        }
    }

    private static string? TryGetReleaseImage(
        ArtistTopTrackResult track,
        IReadOnlyDictionary<string, string> releaseImageByUri,
        IReadOnlyDictionary<string, string> releaseImageByName)
    {
        if (!string.IsNullOrWhiteSpace(track.AlbumUri)
            && releaseImageByUri.TryGetValue(track.AlbumUri, out var byUri))
        {
            return byUri;
        }

        if (!string.IsNullOrWhiteSpace(track.AlbumName)
            && releaseImageByName.TryGetValue(track.AlbumName, out var byName))
        {
            return byName;
        }

        return null;
    }

    private async Task PrefetchReleaseColorsAsync(
        string artistId,
        int generation,
        IEnumerable<Wavee.UI.WinUI.ViewModels.ArtistReleaseVm> releases)
    {
        var releasesByUrl = new Dictionary<string, List<Wavee.UI.WinUI.ViewModels.ArtistReleaseVm>>(StringComparer.Ordinal);

        foreach (var release in releases)
        {
            if (!string.IsNullOrEmpty(release.ColorHex))
                continue;

            var imageUrl = SpotifyImageHelper.ToHttpsUrl(release.ImageUrl) ?? release.ImageUrl;
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            if (!releasesByUrl.TryGetValue(imageUrl, out var mapped))
            {
                mapped = [];
                releasesByUrl[imageUrl] = mapped;
            }

            mapped.Add(release);
        }

        if (releasesByUrl.Count == 0)
            return;

        try
        {
            var colors = await _colorService
                .GetColorsAsync(releasesByUrl.Keys.ToList())
                .ConfigureAwait(false);

            if (colors.Count == 0)
                return;

            if (!IsCurrentLoad(artistId, generation))
                return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!IsCurrentLoad(artistId, generation))
                    return;

                foreach (var (url, mappedReleases) in releasesByUrl)
                {
                    if (!colors.TryGetValue(url, out var color))
                        continue;

                    var hex = color.DarkHex ?? color.RawHex ?? color.LightHex;
                    if (string.IsNullOrEmpty(hex))
                        continue;

                    foreach (var release in mappedReleases)
                    {
                        if (string.IsNullOrEmpty(release.ColorHex))
                            release.ColorHex = hex;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to prefetch artist release colors for {Count} images", releasesByUrl.Count);
        }
    }

    // -- Background discography pagination --

    private async Task FetchRemainingDiscographyAsync(
        string artistUri,
        int generation,
        int albumsLoaded, int albumsTotal,
        int singlesLoaded, int singlesTotal,
        int compilationsLoaded, int compilationsTotal,
        CancellationToken ct)
    {
        var tasks = new List<Task>();

        if (albumsLoaded < albumsTotal)
            tasks.Add(FetchDiscographyGroupAsync(artistUri, generation,
                "ALBUM", "album-ph", albumsLoaded, albumsTotal, ct));

        if (singlesLoaded < singlesTotal)
            tasks.Add(FetchDiscographyGroupAsync(artistUri, generation,
                "SINGLE", "single-ph", singlesLoaded, singlesTotal, ct));

        if (compilationsLoaded < compilationsTotal)
            tasks.Add(FetchDiscographyGroupAsync(artistUri, generation,
                "COMPILATION", "comp-ph", compilationsLoaded, compilationsTotal, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    // -- Album expand/collapse --

    [RelayCommand]
    private async Task ExpandAlbum(LazyReleaseItem? album)
    {
        if (album == null || !album.IsLoaded || album.Data == null) return;

        if (ExpandedAlbum?.Id == album.Id)
        {
            CollapseAlbum();
            return;
        }

        ExpandedAlbum = album;
        IsLoadingExpandedTracks = true;
        ExpandedAlbumTracks.Clear();

        var trackCount = album.Data.TrackCount;
        if (trackCount <= 0)
        {
            trackCount = album.Data.Type switch
            {
                "SINGLE" => 2,
                "COMPILATION" => 20,
                _ => 12
            };
        }

        for (int i = 0; i < trackCount; i++)
            ExpandedAlbumTracks.Add(LazyTrackItem.Placeholder($"expanded-{i}", i + 1));

        try
        {
            var albumUri = album.Data.Uri ?? $"spotify:album:{album.Data.Id}";
            var tracks = await _albumService.GetTracksAsync(albumUri);

            for (int i = 0; i < Math.Min(tracks.Count, ExpandedAlbumTracks.Count); i++)
                ExpandedAlbumTracks[i] = LazyTrackItem.Loaded(tracks[i].Id, i + 1, tracks[i]);

            while (ExpandedAlbumTracks.Count > tracks.Count)
                ExpandedAlbumTracks.RemoveAt(ExpandedAlbumTracks.Count - 1);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load album tracks for {AlbumUri}", album.Data.Uri);
        }
        finally
        {
            IsLoadingExpandedTracks = false;
        }
    }

    [RelayCommand]
    private void CollapseAlbum()
    {
        ExpandedAlbum = null;
        IsLoadingExpandedTracks = false;
    }

    // -- Background discography pagination --

    private async Task FetchDiscographyGroupAsync(
        string artistUri,
        int generation,
        string type,
        string phPrefix,
        int alreadyLoaded,
        int totalCount,
        CancellationToken ct)
    {
        try
        {
            const int pageSz = 20;
            var offset = alreadyLoaded;

            var allReleases = new List<(int Offset, List<ArtistReleaseResult> Items)>();
            while (offset < totalCount)
            {
                ct.ThrowIfCancellationRequested();
                var releases = await _artistService.GetDiscographyPageAsync(artistUri, type, offset, pageSz, ct);
                if (releases.Count == 0) break;
                allReleases.Add((offset, releases));
                offset += releases.Count;
            }

            if (allReleases.Count == 0) return;
            if (ct.IsCancellationRequested || !IsCurrentLoad(artistUri, generation))
                return;

            var createdReleaseVms = new List<Wavee.UI.WinUI.ViewModels.ArtistReleaseVm>();
            var tcs = new TaskCompletionSource();
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested || !IsCurrentLoad(artistUri, generation))
                    {
                        tcs.SetResult();
                        return;
                    }

                    foreach (var (pageOffset, releases) in allReleases)
                    {
                        int i = pageOffset;
                        foreach (var r in releases)
                        {
                            var vm = new Wavee.UI.WinUI.ViewModels.ArtistReleaseVm
                            {
                                Id = r.Id,
                                Uri = r.Uri,
                                Name = r.Name,
                                Type = type,
                                ImageUrl = r.ImageUrl,
                                ReleaseDate = r.ReleaseDate,
                                TrackCount = r.TrackCount,
                                Label = r.Label,
                                Year = r.Year
                            };
                            createdReleaseVms.Add(vm);

                            var phKey = $"{phPrefix}-{i}";
                            var existing = _allReleases.FirstOrDefault(x => x.Id == phKey);
                            if (existing != null)
                                existing.Populate(vm);
                            else
                                _allReleases.Add(LazyReleaseItem.Loaded(r.Id, i, vm));
                            i++;
                        }
                    }
                    DispatchReleases();
                    tcs.SetResult();
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            await tcs.Task;
            if (IsCurrentLoad(artistUri, generation))
                _ = PrefetchReleaseColorsAsync(artistUri, generation, createdReleaseVms);
        }
        catch (OperationCanceledException) { /* navigated away */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Discography {Type} fetch failed for {ArtistId}", type, artistUri);

            var tcsCleanup = new TaskCompletionSource();
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!IsCurrentLoad(artistUri, generation))
                    {
                        tcsCleanup.SetResult();
                        return;
                    }

                    _allReleases.RemoveAll(i => !i.IsLoaded && i.Id.StartsWith(phPrefix));
                    DispatchReleases();

                    switch (type)
                    {
                        case "ALBUM": HasAlbumsError = true; break;
                        case "SINGLE": HasSinglesError = true; break;
                        case "COMPILATION": HasCompilationsError = true; break;
                    }

                    tcsCleanup.SetResult();
                }
                catch (Exception cleanupEx) { tcsCleanup.SetException(cleanupEx); }
            });

            try { await tcsCleanup.Task; }
            catch (Exception cleanupEx2) { _logger?.LogDebug(cleanupEx2, "Discography cleanup failed (non-critical)"); }
        }
    }

    // -- Commands --

    [RelayCommand]
    private void Retry()
    {
        HasError = false;
        ErrorMessage = null;
        if (!string.IsNullOrEmpty(ArtistId))
        {
            _appliedOverviewFor = null;
            _appliedOverview = null;
            _artistStore.Invalidate(ArtistId);
        }
    }

    [RelayCommand]
    private async Task RetryDiscographyAsync()
    {
        var albumsLoaded = Albums.Count(a => a.IsLoaded);
        var singlesLoaded = Singles.Count(s => s.IsLoaded);
        var compilationsLoaded = Compilations.Count(c => c.IsLoaded);

        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        var artistId = ArtistId;
        if (string.IsNullOrEmpty(artistId))
            return;

        var generation = Volatile.Read(ref _loadGeneration);
        var ct = CreateFreshDiscographyToken();

        await Task.Run(() => FetchRemainingDiscographyAsync(
            artistId, generation,
            albumsLoaded, AlbumsTotalCount,
            singlesLoaded, SinglesTotalCount,
            compilationsLoaded, CompilationsTotalCount,
            ct), ct);
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        if (string.IsNullOrEmpty(ArtistId) || _likeService == null) return;
        var wasSaved = IsFollowing;
        IsFollowing = !wasSaved;
        _likeService.ToggleSave(SavedItemType.Artist, ArtistId, wasSaved);
    }

    private void RefreshFollowState()
    {
        if (!string.IsNullOrEmpty(ArtistId) && _likeService != null)
            IsFollowing = _likeService.IsSaved(SavedItemType.Artist, ArtistId);
    }

    private void OnSaveStateChanged()
    {
        _dispatcherQueue?.TryEnqueue(RefreshFollowState);
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentContext)
            or nameof(IPlaybackStateService.IsPlaying)
            or nameof(IPlaybackStateService.IsBuffering))
        {
            _dispatcherQueue.TryEnqueue(SyncArtistPlaybackState);
        }
    }

    private void SyncArtistPlaybackState()
    {
        bool isArtistContext = IsArtistContextActive();
        IsArtistContextPlaying = isArtistContext && _playbackStateService.IsPlaying;
        IsArtistContextPaused = isArtistContext && !_playbackStateService.IsPlaying;

        if (IsPlayPending && (!isArtistContext || IsArtistContextPlaying))
            SetPlayPending(false);
    }

    private bool IsArtistContextActive()
    {
        var artistId = ArtistId;
        var contextUri = _playbackStateService.CurrentContext?.ContextUri;
        if (string.IsNullOrWhiteSpace(artistId) || string.IsNullOrWhiteSpace(contextUri))
            return false;

        var canonicalArtistUri = artistId.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)
            ? artistId
            : $"spotify:artist:{artistId}";

        return string.Equals(contextUri, artistId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextUri, canonicalArtistUri, StringComparison.OrdinalIgnoreCase);
    }

    private void SetPlayPending(bool pending)
    {
        if (IsPlayPending == pending)
            return;

        IsPlayPending = pending;
        _playPendingCts?.Cancel();
        _playPendingCts?.Dispose();
        _playPendingCts = null;

        if (!pending)
            return;

        _playPendingCts = new CancellationTokenSource();
        _ = ClearPlayPendingAfterTimeoutAsync(_playPendingCts.Token);
    }

    private async Task ClearPlayPendingAfterTimeoutAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(PlayPendingTimeoutMs, ct);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!ct.IsCancellationRequested && IsPlayPending)
                {
                    SetPlayPending(false);
                    _playbackStateService.ClearBuffering();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task PlayTopTracksAsync()
    {
        if (string.IsNullOrEmpty(ArtistId)) return;

        PlaybackResult result;
        if (IsArtistContextPlaying)
        {
            result = await _playbackService.PauseAsync();
        }
        else if (IsArtistContextPaused)
        {
            SetPlayPending(true);
            _playbackStateService.NotifyBuffering(null);
            result = await _playbackService.ResumeAsync();
        }
        else
        {
            SetPlayPending(true);
            _playbackStateService.NotifyBuffering(null);
            result = await _playbackService.PlayContextAsync(
                ArtistId,
                new PlayContextOptions { PlayOriginFeature = "artist_page" });
        }

        if (!result.IsSuccess)
        {
            SetPlayPending(false);
            _playbackStateService.ClearBuffering();
            _logger?.LogWarning("PlayTopTracks failed: {Error}", result.ErrorMessage);
        }
    }

    [RelayCommand]
    private async Task PlayTrackAsync(ITrackItem? track)
    {
        if (track == null || string.IsNullOrEmpty(ArtistId)) return;

        // Build rich QueueItems from TopTracks so remote clients receive per-track
        // uid + metadata (artist_uri, album_uri, album_title, title, track_player)
        // the same way Spotify desktop does. Without this, the published queue
        // comes across as bare track URIs with context_uri="spotify:internal:queue".
        // Mirrors PlaylistViewModel.BuildQueueAndPlay.
        var queueItems = new List<QueueItem>(TopTracks.Count);
        int startIndex = -1;
        foreach (var t in TopTracks)
        {
            if (!t.IsLoaded || t.Data is not ITrackItem item) continue;
            if (string.IsNullOrEmpty(item.Uri)) continue;

            if (startIndex < 0 && item.Uri == track.Uri)
                startIndex = queueItems.Count;

            var metadata = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(item.ArtistId))
                metadata["artist_uri"] = $"spotify:artist:{item.ArtistId}";
            if (!string.IsNullOrEmpty(item.AlbumId))
                metadata["album_uri"] = $"spotify:album:{item.AlbumId}";
            if (!string.IsNullOrEmpty(item.AlbumName))
                metadata["album_title"] = item.AlbumName;
            if (!string.IsNullOrEmpty(item.Title))
                metadata["title"] = item.Title;
            metadata["track_player"] = "audio";

            queueItems.Add(new QueueItem
            {
                TrackId = item.Id,
                Title = item.Title,
                ArtistName = item.ArtistName,
                AlbumArt = item.ImageUrl,
                DurationMs = item.Duration.TotalMilliseconds,
                IsUserQueued = false,
                // "toptrack{id}" matches the uid pattern Spotify's
                // context-resolve/v1/spotify:artist:{id} returns for page 0
                // (the top-tracks page). The server uses this to address a
                // specific instance for skip-to-uid.
                Uid = $"toptrack{item.Id}",
                Metadata = metadata,
                AlbumName = !string.IsNullOrEmpty(item.AlbumName) ? item.AlbumName : null,
                AlbumUri = !string.IsNullOrEmpty(item.AlbumId) ? $"spotify:album:{item.AlbumId}" : null,
                ArtistUri = !string.IsNullOrEmpty(item.ArtistId) ? $"spotify:artist:{item.ArtistId}" : null,
                IsExplicit = item.IsExplicit,
            });
        }

        if (queueItems.Count == 0 || startIndex < 0)
        {
            // Clicked track isn't in the local TopTracks cache — fall back to
            // server-side context resolution.
            var fallbackResult = await _playbackService.PlayTrackInContextAsync(track.Uri, ArtistId,
                new PlayContextOptions { PlayOriginFeature = "artist_page" });
            if (!fallbackResult.IsSuccess)
            {
                _playbackStateService.ClearBuffering();
                _logger?.LogWarning("Play artist track failed: {Error}", fallbackResult.ErrorMessage);
            }
            return;
        }

        var context = new PlaybackContextInfo
        {
            ContextUri = ArtistId,
            Type = PlaybackContextType.Artist,
            Name = ArtistName,
            ImageUrl = ArtistImageUrl,
            // Matches context-resolve/v1/spotify:artist:{id}.metadata. Forwarded
            // into PlayerState.context_metadata so other clients render
            // "Playing from {artist}" correctly.
            FormatAttributes = new Dictionary<string, string>
            {
                ["context_description"] = ArtistName ?? string.Empty,
                ["artist_context_type"] = "km_artist",
            }
        };

        var trackUris = queueItems.Select(item => item.TrackId).ToList();
        var result = await _playbackService.PlayTracksAsync(
            trackUris,
            startIndex,
            context,
            queueItems,
            CancellationToken.None);

        if (!result.IsSuccess)
        {
            _playbackStateService.ClearBuffering();
            _logger?.LogWarning("Play artist top-track queue failed: {Error}", result.ErrorMessage);
        }
    }

    /// <summary>Kicks the IPlaybackStateService radio endpoint with the current
    /// artist URI. Mirrors the Artist context-menu "Artist radio" affordance
    /// (<c>ArtistContextMenuBuilder</c>) so the V4A hero's pill button reuses
    /// the same code path. No-op when the artist hasn't loaded yet.</summary>
    [RelayCommand]
    private async Task PlayArtistRadioAsync()
    {
        if (string.IsNullOrEmpty(ArtistId)) return;
        var name = ArtistName is { Length: > 0 } n ? $"{n} Radio" : "Artist Radio";
        await _playbackStateService.StartRadioAsync(ArtistId, name);
    }

    /// <summary>Generates an on-device biography excerpt via Phi Silica. Called
    /// from <see cref="ApplyOverviewState"/> when the ArtistOverview returned
    /// no biography. Guarded against double-runs by an internal CTS — switching
    /// to a different artist cancels the prior generation. Result lands on
    /// <see cref="BioSummaryText"/>; failures collapse the About excerpt to
    /// empty (no error chrome — the section just doesn't render).</summary>
    public async Task LoadBioSummaryAsync(string artistId)
    {
        if (_bioSummarizer is null) return;
        if (string.IsNullOrEmpty(artistId)) return;
        if (string.IsNullOrEmpty(ArtistName)) return;

        _bioSummaryCts?.Cancel();
        var cts = _bioSummaryCts = new CancellationTokenSource();

        try
        {
            IsBioSummaryLoading = true;
            BioSummaryText = null;

            var topTrackNames = _topTracks
                .Where(t => t.IsLoaded && t.Data is { } d && !string.IsNullOrEmpty(d.Title))
                .Select(t => ((Data.Contracts.ITrackItem)t.Data!).Title!)
                .Where(s => !string.IsNullOrEmpty(s))
                .Take(5)
                .ToList();

            var monthlyDisplay = string.IsNullOrEmpty(MonthlyListeners)
                ? null
                : $"{MonthlyListeners} monthly listeners";

            var result = await _bioSummarizer.SummarizeBioAsync(
                artistId,
                ArtistName!,
                genres: null, // Not on overview today; passed when available
                monthlyListenersDisplay: monthlyDisplay,
                topTrackNames: topTrackNames,
                deltaProgress: null,
                ct: cts.Token);

            if (cts.IsCancellationRequested) return;
            if (result.Kind == LyricsAiResultKind.Ok)
                BioSummaryText = result.Text;
        }
        catch (OperationCanceledException) { /* artist switched */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadBioSummaryAsync failed for {ArtistId}", artistId);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsBioSummaryLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleAlbumsView() => AlbumsGridView = !AlbumsGridView;

    [RelayCommand]
    private void ToggleSinglesView() => SinglesGridView = !SinglesGridView;

    [RelayCommand]
    private void ToggleCompilationsView() => CompilationsGridView = !CompilationsGridView;

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages - 1)
            CurrentPage++;
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPage > 0)
            CurrentPage--;
    }

    // -- Pagination notifications --

    private void NotifyPaginationChanged()
    {
        _pagedTopTracksCache = null;
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(PagedTopTracks));
    }

    partial void OnCurrentPageChanged(int value)
    {
        ClearTopTracksSelection();
        NotifyPaginationChanged();
    }

    partial void OnColumnCountChanged(int value)
    {
        CurrentPage = 0;
        ClearTopTracksSelection();
        NotifyPaginationChanged();
    }

    private void UpdateTabTitle(string? value)
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(value))
        {
            TabItemParameter.Title = value;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    // -- Extended top tracks --

    /// <summary>
    /// Backfills <see cref="ArtistTopTrackVm.AlbumImageUrl"/> for any
    /// initial top tracks the GraphQL overview returned without cover art.
    /// Resolves the missing URLs via the extended-metadata pipeline (cache
    /// + batched TrackV4 fetch) and populates the existing
    /// <see cref="LazyTrackItem"/> wrappers so visible <c>TrackItem</c>
    /// controls receive the image-property notification immediately.
    /// </summary>
    private async Task EnrichMissingTopTrackImagesAsync(string artistId, int generation)
    {
        try
        {
            if (!IsCurrentLoad(artistId, generation))
                return;

            // Snapshot URIs needing enrichment (called off-dispatcher right
            // after TopTracks is replaced — safe to read without a lock).
            var snapshot = TopTracks.ToList();
            var missing = snapshot
                .Where(item => item.IsLoaded && item.Data is ArtistTopTrackVm vm
                               && !string.IsNullOrEmpty(vm.Uri)
                               && string.IsNullOrEmpty(vm.AlbumImageUrl))
                .Select(item => ((ArtistTopTrackVm)item.Data!).Uri!)
                .Distinct()
                .ToList();

            if (missing.Count == 0) return;

            var images = await Task.Run(() => _artistService.GetTrackImagesAsync(missing));
            if (images.Count == 0) return;
            if (!IsCurrentLoad(artistId, generation)) return;

            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (!IsCurrentLoad(artistId, generation))
                    return;

                bool anyPatched = false;
                for (int i = 0; i < TopTracks.Count; i++)
                {
                    var entry = TopTracks[i];
                    if (!entry.IsLoaded || entry.Data is not ArtistTopTrackVm vm) continue;
                    if (vm.Uri is not { Length: > 0 } uri) continue;
                    if (!string.IsNullOrEmpty(vm.AlbumImageUrl)) continue;
                    if (!TryGetTrackImage(images, uri, out var imageUrl) || string.IsNullOrEmpty(imageUrl)) continue;

                    var patched = new ArtistTopTrackVm
                    {
                        Id = vm.Id,
                        Index = vm.Index,
                        Title = vm.Title,
                        Uri = vm.Uri,
                        AlbumName = vm.AlbumName,
                        AlbumImageUrl = imageUrl,
                        AlbumUri = vm.AlbumUri,
                        Duration = vm.Duration,
                        PlayCountRaw = vm.PlayCountRaw,
                        ArtistNames = vm.ArtistNames,
                        IsExplicit = vm.IsExplicit,
                        IsPlayable = vm.IsPlayable,
                        HasCanvasVideo = vm.HasCanvasVideo,
                        HasLinkedLocalVideo = vm.HasLinkedLocalVideo,
                    };
                    entry.Populate(patched);
                    anyPatched = true;
                }

                if (anyPatched)
                {
                    OnPropertyChanged(nameof(TopTracksFirst10));
                }

                _logger?.LogInformation(
                    "Top-track image enrichment for {Artist}: requested={RequestedCount}, resolved={ResolvedCount}, patched={Patched}",
                    artistId,
                    missing.Count,
                    images.Count(kvp => !string.IsNullOrEmpty(kvp.Value)),
                    anyPatched);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich missing top-track images");
        }
    }

    private static bool TryGetTrackImage(
        IReadOnlyDictionary<string, string?> images,
        string uri,
        out string? imageUrl)
    {
        if (images.TryGetValue(uri, out imageUrl))
            return true;

        const string trackPrefix = "spotify:track:";
        var bareId = uri.StartsWith(trackPrefix, StringComparison.Ordinal)
            ? uri[trackPrefix.Length..]
            : uri;

        if (images.TryGetValue(bareId, out imageUrl))
            return true;

        return images.TryGetValue($"{trackPrefix}{bareId}", out imageUrl);
    }

    private async Task LoadExtendedTopTracksAsync(string artistUri, int generation)
    {
        try
        {
            var extendedTracks = await Task.Run(async () => await _artistService.GetExtendedTopTracksAsync(artistUri));
            if (extendedTracks.Count == 0) return;
            if (!IsCurrentLoad(artistUri, generation)) return;

            var existingUris = new HashSet<string>(
                TopTracks
                    .Where(i => i.IsLoaded && i.Data != null)
                    .Select(i => ((ArtistTopTrackVm)i.Data!).Uri ?? ""));

            var startIdx = TopTracks.Count(i => i.IsLoaded) + 1;

            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (!IsCurrentLoad(artistUri, generation))
                    return;

                // Remove all placeholder items
                for (int i = TopTracks.Count - 1; i >= 0; i--)
                {
                    if (!TopTracks[i].IsLoaded)
                        TopTracks.RemoveAt(i);
                }

                int idx = startIdx;
                var addedVms = new List<ArtistTopTrackVm>();
                foreach (var track in extendedTracks)
                {
                    if (existingUris.Contains(track.Uri ?? "")) continue;

                    var trackVm = new ArtistTopTrackVm
                    {
                        Id = track.Id,
                        Index = idx,
                        Title = track.Title,
                        Uri = track.Uri,
                        AlbumName = track.AlbumName,
                        AlbumImageUrl = track.AlbumImageUrl,
                        AlbumUri = track.AlbumUri,
                        Duration = track.Duration,
                        PlayCountRaw = track.PlayCount,
                        ArtistNames = track.ArtistNames,
                        IsExplicit = track.IsExplicit,
                        IsPlayable = track.IsPlayable,
                        HasCanvasVideo = track.HasVideo
                    };

                    TopTracks.Add(LazyTrackItem.Loaded(trackVm.Id, idx, trackVm));
                    addedVms.Add(trackVm);
                    idx++;
                }

                _pagedTopTracksCache = null;
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(HasMultiplePages));
                OnPropertyChanged(nameof(PagedTopTracks));

                if (addedVms.Count > 0)
                {
                    var videoMetadata = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                        .GetService<Wavee.UI.WinUI.Services.IMusicVideoMetadataService>();
                    if (videoMetadata is not null)
                    {
                        _ = videoMetadata.ApplyAvailabilityToAsync(
                            addedVms,
                            static t => t.Uri,
                            static (t, v) => t.HasLinkedLocalVideo = v,
                            CancellationToken.None);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load extended top tracks for {Artist}", artistUri);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DetachLongLivedServices();

        _subscriptions?.Dispose();
        _subscriptions = null;

        _playPendingCts?.Cancel();
        _playPendingCts?.Dispose();
        _playPendingCts = null;

        CancelAndDisposeDiscographyCts();
    }

    /// <summary>
    /// Theme-aware palette refresh. Page calls this on init + on
    /// ActualThemeChanged + after Palette lands. Mirrors PlaylistViewModel
    /// and AlbumViewModel: dark theme ? HigherContrast (deepest), light ?
    /// HighContrast (saturated but a step brighter). MinContrast is skipped —
    /// too pastel for white-on-tint text. When no palette is available the
    /// brushes are nulled so bound elements render untinted.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;

        var tier = Palette is null
            ? null
            : (isDark
                ? (Palette.HigherContrast ?? Palette.HighContrast)
                : (Palette.HighContrast ?? Palette.HigherContrast));

        if (tier == null)
        {
            // Fall back to system accent when no palette is available so the
            // Play button + avatar ring still render correctly on cold load.
            SectionAccentBrush = ResolveSystemBrush("AccentFillColorDefaultBrush");
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = ResolveSystemBrush("AccentFillColorDefaultBrush");
            PaletteAccentPillForegroundBrush = ResolveSystemBrush("TextOnAccentFillColorPrimaryBrush");
            return;
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);

        // Use BackgroundTinted (the artist's actual cover-derived color) for
        // accents instead of TextAccent. TextAccent often resolves to Spotify's
        // brand green (#1DB954) regardless of the cover photo, which made every
        // artist accent look identical (and disconnected from the visual).
        var accentBase = TintColorHelper.BrightenForTint(bgTint, targetMax: 210);

        // Section bar — lifted accent. Drop alpha in Light mode so the bar reads
        // as an accent rather than a stoplight against the lighter page.
        SectionAccentBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDark ? 255 : 200), accentBase.R, accentBase.G, accentBase.B));

        // Hero scrim — same alpha cadence used by AlbumViewModel/PlaylistViewModel.
        // Light mode blends palette colors toward white and cuts alphas so dark
        // covers don't drag the page dark.
        var heroBg     = isDark ? bg     : TintColorHelper.LightTint(bg);
        var heroBgTint = isDark ? bgTint : TintColorHelper.LightTint(bgTint);
        var (a0, a1, a2, a3) = isDark ? (240, 176, 80, 0) : (140, 100, 50, 0);
        var heroGrad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a0, heroBgTint.R, heroBgTint.G, heroBgTint.B), Offset = 0.0 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a1, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 0.35 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a2, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 0.65 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a3, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 1.0 });
        PaletteHeroGradientBrush = heroGrad;

        // Play button — same lifted accent as the section bar so the page
        // reads as one color identity, with luma-based contrast text.
        PaletteAccentPillBrush = new SolidColorBrush(accentBase);
        var accentLuma = (accentBase.R * 299 + accentBase.G * 587 + accentBase.B * 114) / 1000;
        PaletteAccentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
    }


    private static Brush? ResolveSystemBrush(string resourceKey)
    {
        if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
            && res.TryGetValue(resourceKey, out var value)
            && value is Brush brush)
            return brush;
        return null;
    }

    /// <summary>
    /// Formats a long listener count like "1.2M" / "453K" / "812".
    /// Mirrors monthly-listener formatting used elsewhere in the app.
    /// </summary>
    private static string FormatListenerCount(long count)
    {
        if (count >= 1_000_000)
            return (count / 1_000_000.0).ToString("0.#") + "M";
        if (count >= 1_000)
            return (count / 1_000.0).ToString("0.#") + "K";
        return count.ToString("N0");
    }
}

public sealed record ArtistSocialLinkVm
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required FontAwesome6.EFontAwesomeIcon Icon { get; init; }
}

public sealed record ArtistTopCityVm
{
    public required string City { get; init; }
    public string? Country { get; init; }
    public long NumberOfListeners { get; init; }
    public required string DisplayCount { get; init; }
    public double RelativeWidth { get; init; }
}

// -- View Models (UI-layer records) --

public sealed class ArtistTopTrackVm : Data.Contracts.ITrackItem
{
    public required string Id { get; init; }
    public int Index { get; set; }
    public string? Uri { get; init; }
    public string? AlbumImageUrl { get; init; }
    public string? AlbumUri { get; init; }
    public long PlayCountRaw { get; init; }
    public bool IsPlayable { get; init; }
    /// <summary>
    /// True when the Spotify track itself ships a Canvas video (set at row
    /// build time from the API payload).
    /// </summary>
    public bool HasCanvasVideo { get; init; }

    private bool _hasLinkedLocalVideo;
    /// <summary>
    /// True when the audio URI has a linked local music-video file. Populated
    /// asynchronously by <see cref="IMusicVideoMetadataService.ApplyAvailabilityToAsync"/>
    /// after top tracks load. Fires PropertyChanged on both itself and
    /// <see cref="HasVideo"/> so <c>TrackItem</c>'s badge updates live.
    /// </summary>
    public bool HasLinkedLocalVideo
    {
        get => _hasLinkedLocalVideo;
        set
        {
            if (_hasLinkedLocalVideo == value) return;
            _hasLinkedLocalVideo = value;
            OnPropertyChanged(nameof(HasLinkedLocalVideo));
            OnPropertyChanged(nameof(HasVideo));
        }
    }

    public bool HasVideo => HasCanvasVideo || _hasLinkedLocalVideo;

    // -- ITrackItem implementation --
    string Data.Contracts.ITrackItem.Uri => Uri ?? $"spotify:track:{Id}";
    string Data.Contracts.ITrackItem.Title => Title ?? "";
    // ArtistName feeds the artist column in TrackItem. Play count is shown
    // in its own column via the explicit PlayCountText binding, so this no
    // longer hijacks the field to spell out the play count.
    string Data.Contracts.ITrackItem.ArtistName => ArtistNames ?? "";
    string Data.Contracts.ITrackItem.ArtistId => "";
    string Data.Contracts.ITrackItem.AlbumName => AlbumName ?? "";
    string Data.Contracts.ITrackItem.AlbumId => AlbumUri ?? "";
    string? Data.Contracts.ITrackItem.ImageUrl => AlbumImageUrl;
    TimeSpan Data.Contracts.ITrackItem.Duration => Duration;
    bool Data.Contracts.ITrackItem.IsExplicit => IsExplicit;
    string Data.Contracts.ITrackItem.DurationFormatted => DurationFormatted;
    int Data.Contracts.ITrackItem.OriginalIndex => Index;
    bool Data.Contracts.ITrackItem.IsLoaded => true;
    bool Data.Contracts.ITrackItem.HasVideo => HasVideo;

    // -- Public properties --
    public string? Title { get; init; }
    public string? AlbumName { get; init; }
    public string? ArtistNames { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    public string PlayCountFormatted => PlayCountRaw.ToString("N0");

    public string DurationFormatted =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set => SetField(ref _isLiked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed partial class ArtistReleaseVm : ObservableObject
{
    public string Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string Type { get; init; } // ALBUM, SINGLE, COMPILATION
    public string? ImageUrl { get; init; }
    public DateTimeOffset ReleaseDate { get; init; }
    public int TrackCount { get; init; }
    public string? Label { get; init; }
    public int Year { get; init; }

    /// <summary>
    /// Discography-card subtitle: "Oct 10, 2025 · 10 tracks" — full release
    /// date plus track count, matching the prototype. Falls back to year-only
    /// when the date hasn't been resolved (e.g. legacy releases with only a
    /// year), and to track count alone if even the year is missing.
    /// </summary>
    public string SubtitleDetail
    {
        get
        {
            var datePart = ReleaseDate.Year > 1
                ? ReleaseDate.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture)
                : (Year > 0 ? Year.ToString(System.Globalization.CultureInfo.CurrentCulture) : null);
            var tracksPart = TrackCount > 0
                ? $"{TrackCount} track{(TrackCount == 1 ? "" : "s")}"
                : null;
            return (datePart, tracksPart) switch
            {
                ({ } d, { } t) => $"{d} · {t}",
                ({ } d, null) => d,
                (null, { } t) => t,
                _ => string.Empty,
            };
        }
    }

    [ObservableProperty]
    private string? _colorHex;
}

public sealed class RelatedArtistVm
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed class ConcertVm : INotifyPropertyChanged
{
    public string? Title { get; init; }
    public string? Venue { get; init; }
    public string? City { get; init; }
    public string? DateFormatted { get; init; }
    public string? DayOfWeek { get; init; }
    public string? Year { get; init; }
    public bool IsFestival { get; init; }
    public string? Uri { get; init; }

    /// <summary>
    /// Raw date/time of the concert. Preserved alongside the formatted strings
    /// so the artist-page tour banner can categorise "Upcoming show" vs
    /// "Upcoming tour" vs "On tour now" by counting + first-date proximity,
    /// without re-parsing <see cref="DateFormatted"/>.
    /// </summary>
    public DateTimeOffset Date { get; init; }

    private bool _isNearUser;
    public bool IsNearUser
    {
        get => _isNearUser;
        set
        {
            if (_isNearUser == value) return;
            _isNearUser = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNearUser)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LocationSearchResultVm
{
    public string? GeonameId { get; init; }
    public string? CityName { get; init; }
    public string? CountryName { get; init; }
    public string DisplayName => $"{CityName}, {CountryName}";
}

public sealed class MusicVideoVm
{
    public required string TrackUri { get; init; }
    public string? Title { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? AlbumUri { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsExplicit { get; init; }

    public string DurationFormatted => Duration.TotalSeconds <= 0
        ? string.Empty
        : Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");
}

public sealed class MerchItemVm
{
    public string? Name { get; init; }
    public string? Price { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? Uri { get; init; }
    public string? ShopUrl { get; init; }
}

/// <summary>Card VM for the V4A "Playlists and discovery" shelf. <see cref="Subtitle"/>
/// is source-derived in <c>ArtistService.MapPlaylists</c> (e.g. "Spotify · official",
/// "Aria Maelstrom · discovered on").</summary>
public sealed class ArtistPlaylistVm
{
    public required string Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? Subtitle { get; init; }
}
