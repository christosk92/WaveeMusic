using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.Formatters;
using Wavee.UI.Formatters.Artist;
using Wavee.UI.Helpers.Artist;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.ViewModels.Artist;

/// <summary>
/// Owns the artist hero envelope — image / verified / monthly listener
/// traits, world rank, follower count, palette-derived brushes, tour banner
/// projection. Extracted from <c>ArtistViewModel</c> so the header surface
/// can evolve independently of the discography / bio / extras.
///
/// <para>The header does NOT own the bound collections (top tracks /
/// concerts / popular releases) — it only reads accessors the parent
/// supplies (<see cref="ConcertsSnapshotProvider"/>, etc.) so values like
/// the tour banner headline / concert-count stat can derive from sibling
/// state without direct cross-child coupling.</para>
/// </summary>
public sealed partial class ArtistHeaderViewModel : ObservableObject, IDisposable
{
    private readonly PaletteGradientCompositor _paletteCompositor;
    private bool _isDarkTheme = true;
    private bool _isHighContrastTheme;

    /// <summary>
    /// Accessor for the current concert snapshot. Supplied by the parent so
    /// the tour banner can derive its text without holding a reference to
    /// the <c>ArtistTopTracks</c>-sibling collection directly.
    /// </summary>
    public required Func<IReadOnlyList<ConcertVm>> ConcertsSnapshotProvider { get; init; }

    public ArtistHeaderViewModel(PaletteGradientCompositor paletteCompositor)
    {
        _paletteCompositor = paletteCompositor;
    }

    // ── Backing envelope (record) ───────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArtistName))]
    [NotifyPropertyChangedFor(nameof(ArtistImageUrl))]
    [NotifyPropertyChangedFor(nameof(HeaderImageUrl))]
    [NotifyPropertyChangedFor(nameof(HeaderHeroColorHex))]
    [NotifyPropertyChangedFor(nameof(Palette))]
    [NotifyPropertyChangedFor(nameof(MonthlyListeners))]
    [NotifyPropertyChangedFor(nameof(MonthlyListenersDescription))]
    [NotifyPropertyChangedFor(nameof(WorldRank))]
    [NotifyPropertyChangedFor(nameof(HasWorldRank))]
    [NotifyPropertyChangedFor(nameof(WorldRankNumberText))]
    [NotifyPropertyChangedFor(nameof(Followers))]
    [NotifyPropertyChangedFor(nameof(FollowersFormatted))]
    [NotifyPropertyChangedFor(nameof(HasFollowers))]
    [NotifyPropertyChangedFor(nameof(IsVerified))]
    [NotifyPropertyChangedFor(nameof(IsRegistered))]
    [NotifyPropertyChangedFor(nameof(HasArtistTrait))]
    [NotifyPropertyChangedFor(nameof(IsRegisteredOnly))]
    [NotifyPropertyChangedFor(nameof(ArtistTraitLabel))]
    [NotifyPropertyChangedFor(nameof(LatestRelease))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseName))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseImageUrl))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseUri))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseDate))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseTrackCount))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseType))]
    [NotifyPropertyChangedFor(nameof(HasLatestRelease))]
    [NotifyPropertyChangedFor(nameof(LatestReleaseSubtitle))]
    [NotifyPropertyChangedFor(nameof(PinnedItem))]
    [NotifyPropertyChangedFor(nameof(HasPinnedItem))]
    [NotifyPropertyChangedFor(nameof(HasPinnedComment))]
    [NotifyPropertyChangedFor(nameof(PinnedBackdropImageUrl))]
    [NotifyPropertyChangedFor(nameof(PinnedColumnWidth))]
    [NotifyPropertyChangedFor(nameof(PinnedItemTitle))]
    [NotifyPropertyChangedFor(nameof(PinnedItemComment))]
    [NotifyPropertyChangedFor(nameof(PinnedItemThumbnailUrl))]
    [NotifyPropertyChangedFor(nameof(PinnedItemSubtitle))]
    [NotifyPropertyChangedFor(nameof(PinnedItemUri))]
    [NotifyPropertyChangedFor(nameof(WatchFeed))]
    [NotifyPropertyChangedFor(nameof(HasWatchFeed))]
    [NotifyPropertyChangedFor(nameof(TourBannerHeadline))]
    [NotifyPropertyChangedFor(nameof(TourBannerSubline))]
    [NotifyPropertyChangedFor(nameof(TourBannerEyebrow))]
    [NotifyPropertyChangedFor(nameof(TourBannerIsLive))]
    [NotifyPropertyChangedFor(nameof(TourBannerIconGlyph))]
    private ArtistView? _artist;

    /// <summary>Fired whenever the underlying envelope changes — the parent
    /// listens so it can re-run sibling fan-outs (e.g. spotlight selection,
    /// theme reapply) without each child observing the other's state.</summary>
    public event EventHandler? ArtistChanged;

    partial void OnArtistChanged(ArtistView? value)
    {
        // Property change notifications for the envelope-dependent surfaces are
        // emitted automatically by the MVVM Toolkit generator via the
        // [NotifyPropertyChangedFor] attributes on _artist. Theme + downstream
        // fan-out still need an explicit hook.
        ApplyTheme(_isDarkTheme, _isHighContrastTheme);
        ArtistChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Envelope-projected scalars ──────────────────────────────────────────

    public string? ArtistName => Artist?.Name;
    public string? ArtistImageUrl => Artist?.ArtistImageUrl;
    public string? HeaderImageUrl => Artist?.HeaderImageUrl;
    public string? HeaderHeroColorHex => Artist?.HeaderHeroColorHex;
    public ArtistPalette? Palette => Artist?.Palette;

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

    public bool IsVerified => Artist?.IsVerified == true;
    public bool IsRegistered => Artist?.IsRegistered == true;
    public bool HasArtistTrait => IsVerified || IsRegistered;
    public bool IsRegisteredOnly => !IsVerified && IsRegistered;
    public string ArtistTraitLabel =>
        IsVerified ? "VERIFIED ARTIST" :
        IsRegistered ? "ARTIST PROFILE" :
        string.Empty;

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

    // ── Palette-derived brushes (assigned by ApplyTheme) ────────────────────

    [ObservableProperty] private Brush? _sectionAccentBrush;
    [ObservableProperty] private Brush? _paletteHeroGradientBrush;
    [ObservableProperty] private Brush? _paletteAccentPillBrush;
    [ObservableProperty] private Brush? _paletteAccentPillForegroundBrush;

    // ── Tour banner projection ──────────────────────────────────────────────

    public int ConcertCount => ConcertsSnapshotProvider().Count;

    /// <summary>Date of the closest upcoming concert for the rhythm-break
    /// subline. Null when no concerts are scheduled.</summary>
    public string? FirstConcertDateFormatted
    {
        get
        {
            var concerts = ConcertsSnapshotProvider();
            return concerts.Count == 0 ? null : concerts[0].DateFormatted;
        }
    }

    /// <summary>Venue of the closest upcoming concert.</summary>
    public string? FirstConcertVenue
    {
        get
        {
            var concerts = ConcertsSnapshotProvider();
            return concerts.Count == 0 ? null : concerts[0].Venue;
        }
    }

    /// <summary>City of the closest upcoming concert.</summary>
    public string? FirstConcertCity
    {
        get
        {
            var concerts = ConcertsSnapshotProvider();
            return concerts.Count == 0 ? null : concerts[0].City;
        }
    }

    /// <summary>
    /// Rhythm-break tour banner texts. Computed once per access by
    /// <see cref="ArtistTourBannerFormatter"/> so the headline / eyebrow /
    /// subline / icon / live-state all reflect the same snapshot. The bound
    /// XAML properties (<see cref="TourBannerHeadline"/> et al.) project off
    /// this single computation — when concerts change the parent raises
    /// <see cref="NotifyConcertsChanged"/> so the bindings re-read together.
    /// </summary>
    private ArtistTourBannerText TourBannerText()
    {
        var concerts = ConcertsSnapshotProvider();
        if (concerts.Count == 0)
        {
            return new ArtistTourBannerText(
                Headline: string.Empty,
                Eyebrow: string.Empty,
                Subline: string.Empty,
                IsLive: false,
                IconKind: ArtistTourIconKind.Calendar);
        }
        var first = concerts[0];
        var snapshot = new ArtistTourSnapshot(
            ArtistName: ArtistName ?? string.Empty,
            ConcertCount: concerts.Count,
            AllFestivals: AllFestivals(concerts),
            FirstConcertTitle: FirstConcertTitle(concerts),
            FirstConcertDateLocal: first.Date,
            FirstConcertDateFormatted: first.DateFormatted,
            FirstConcertVenue: first.Venue,
            FirstConcertCity: first.City,
            NowLocal: DateTimeOffset.Now);
        return ArtistTourBannerFormatter.Format(snapshot);
    }

    private static bool AllFestivals(IReadOnlyList<ConcertVm> concerts)
    {
        for (int i = 0; i < concerts.Count; i++)
            if (!concerts[i].IsFestival) return false;
        return true;
    }

    private static string? FirstConcertTitle(IReadOnlyList<ConcertVm> concerts)
    {
        for (int i = 0; i < concerts.Count; i++)
        {
            var t = concerts[i].Title;
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }
        return null;
    }

    public string TourBannerHeadline => TourBannerText().Headline;
    public string TourBannerEyebrow => TourBannerText().Eyebrow;
    public string TourBannerSubline => TourBannerText().Subline;
    public bool TourBannerIsLive => TourBannerText().IsLive;

    /// <summary>
    /// Resolves the formatter's framework-neutral <see cref="ArtistTourIconKind"/>
    /// to a concrete <see cref="FluentGlyphs"/> codepoint. Glyph constants
    /// live in WinUI; the formatter stays free of them.
    /// </summary>
    public string TourBannerIconGlyph => TourBannerText().IconKind switch
    {
        ArtistTourIconKind.Festival   => FluentGlyphs.Ribbon,
        ArtistTourIconKind.Microphone => FluentGlyphs.Microphone,
        _                             => FluentGlyphs.Calendar,
    };

    /// <summary>
    /// Re-raise the every concerts-dependent property after the parent has
    /// mutated the concert snapshot. The parent owns the collection so the
    /// header needs an explicit nudge here.
    /// </summary>
    public void NotifyConcertsChanged()
    {
        OnPropertyChanged(nameof(ConcertCount));
        OnPropertyChanged(nameof(FirstConcertDateFormatted));
        OnPropertyChanged(nameof(FirstConcertVenue));
        OnPropertyChanged(nameof(FirstConcertCity));
        OnPropertyChanged(nameof(TourBannerHeadline));
        OnPropertyChanged(nameof(TourBannerSubline));
        OnPropertyChanged(nameof(TourBannerEyebrow));
        OnPropertyChanged(nameof(TourBannerIsLive));
        OnPropertyChanged(nameof(TourBannerIconGlyph));
    }

    // ── Theme + palette refresh ─────────────────────────────────────────────

    /// <summary>
    /// Theme-aware palette refresh. Delegates to
    /// <see cref="PaletteGradientCompositor"/> for the actual brush math.
    /// </summary>
    public void ApplyTheme(bool isDark, bool isHighContrast = false)
    {
        _isDarkTheme = isDark;
        _isHighContrastTheme = isHighContrast;

        var descriptor = _paletteCompositor.Compose(Palette, isDark, isHighContrast);
        SectionAccentBrush = descriptor.SectionAccentBrush;
        PaletteHeroGradientBrush = descriptor.HeroGradientBrush;
        PaletteAccentPillBrush = descriptor.AccentPillBrush;
        PaletteAccentPillForegroundBrush = descriptor.AccentPillForegroundBrush;
    }

    public void Dispose()
    {
        // No managed resources — disposal exists for parity with the other
        // child VMs so the parent can fan out cleanup uniformly.
    }
}
