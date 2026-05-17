using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Single row in the V4A "Popular Releases" list paired to the right of Top
/// Tracks. Cover + 2-line meta + rank badge. Row 0 picks up the "Popular now"
/// accent chip via <see cref="IsFeatured"/>.
/// </summary>
public sealed partial class PopularReleaseRow : UserControl
{
    public event EventHandler<RoutedEventArgs>? CardClick;

    public static readonly DependencyProperty CoverImageUrlProperty =
        DependencyProperty.Register(nameof(CoverImageUrl), typeof(string), typeof(PopularReleaseRow),
            new PropertyMetadata(null, OnCoverImageUrlChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PopularReleaseRow),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(nameof(Meta), typeof(string), typeof(PopularReleaseRow),
            new PropertyMetadata(string.Empty, OnMetaChanged));

    public static readonly DependencyProperty RankProperty =
        DependencyProperty.Register(nameof(Rank), typeof(int), typeof(PopularReleaseRow),
            new PropertyMetadata(1, OnRankChanged));

    public static readonly DependencyProperty IsFeaturedProperty =
        DependencyProperty.Register(nameof(IsFeatured), typeof(bool), typeof(PopularReleaseRow),
            new PropertyMetadata(false, OnIsFeaturedChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(PopularReleaseRow),
            new PropertyMetadata(null, OnAccentBrushChanged));

    public static readonly DependencyProperty AccentForegroundBrushProperty =
        DependencyProperty.Register(nameof(AccentForegroundBrush), typeof(Brush), typeof(PopularReleaseRow),
            new PropertyMetadata(null, OnAccentForegroundBrushChanged));

    // â”€â”€â”€ Navigation DPs (added with the artistâ†’album prefetch + connected
    // animation pass). When NavigationUri is set, the internal click handler
    // self-routes through AlbumNavigationHelper (prefetch + connected anim +
    // count prefill) and the CardClick event also fires for any subscriber
    // that wants to react. Leaving these unset preserves the old event-only
    // behaviour.
    public static readonly DependencyProperty NavigationUriProperty =
        DependencyProperty.Register(nameof(NavigationUri), typeof(string), typeof(PopularReleaseRow),
            new PropertyMetadata(null));

    public static readonly DependencyProperty NavigationTotalTracksProperty =
        DependencyProperty.Register(nameof(NavigationTotalTracks), typeof(int), typeof(PopularReleaseRow),
            new PropertyMetadata(0));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PopularReleaseRow),
            new PropertyMetadata(null));

    public string? CoverImageUrl { get => (string?)GetValue(CoverImageUrlProperty); set => SetValue(CoverImageUrlProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Meta { get => (string)GetValue(MetaProperty); set => SetValue(MetaProperty, value); }
    public int Rank { get => (int)GetValue(RankProperty); set => SetValue(RankProperty, value); }
    public bool IsFeatured { get => (bool)GetValue(IsFeaturedProperty); set => SetValue(IsFeaturedProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush? AccentForegroundBrush { get => (Brush?)GetValue(AccentForegroundBrushProperty); set => SetValue(AccentForegroundBrushProperty, value); }

    public string? NavigationUri { get => (string?)GetValue(NavigationUriProperty); set => SetValue(NavigationUriProperty, value); }
    public int NavigationTotalTracks { get => (int)GetValue(NavigationTotalTracksProperty); set => SetValue(NavigationTotalTracksProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    // Album-metadata viewport prefetch. Single-shot per realization â€” reset
    // in OnUnloaded so container recycling re-fires when the row re-enters
    // the viewport. IAlbumPrefetcher's session-wide dedup keeps duplicate
    // POSTs out of the network.
    private bool _albumPrefetchKicked;
    private bool _playlistPrefetchKicked;
    private const double AlbumPrefetchTriggerDistance = 500;
    private const string AlbumUriPrefix = "spotify:album:";
    private const string PlaylistUriPrefix = "spotify:playlist:";

    public PopularReleaseRow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe on attach so the handler doesn't accumulate in the WinRT
        // EventSource table across ItemsRepeater container recycles.
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        // CompositionImage releases its own pin on Unloaded.
        // Don't clear CoverImage.ImageUrl â€” breaks scroll-back-up.
        _albumPrefetchKicked = false;
        _playlistPrefetchKicked = false;
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        var uri = NavigationUri;
        if (string.IsNullOrEmpty(uri)) return;
        if (args.BringIntoViewDistanceX > AlbumPrefetchTriggerDistance
            || args.BringIntoViewDistanceY > AlbumPrefetchTriggerDistance) return;

        if (!_albumPrefetchKicked && uri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal))
        {
            _albumPrefetchKicked = true;
            Ioc.Default.GetService<IAlbumPrefetcher>()?.EnqueueAlbumPrefetch(uri);
        }
        else if (!_playlistPrefetchKicked && uri.StartsWith(PlaylistUriPrefix, StringComparison.Ordinal))
        {
            _playlistPrefetchKicked = true;
            Ioc.Default.GetService<IPlaylistMetadataPrefetcher>()?.EnqueuePlaylistPrefetch(uri);
        }
    }

    private static void OnCoverImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PopularReleaseRow row) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(e.NewValue as string);
        row.CoverImage.ImageUrl = string.IsNullOrEmpty(httpsUrl) ? null : httpsUrl;
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PopularReleaseRow row) row.TitleText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnMetaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PopularReleaseRow row) row.MetaText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnRankChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PopularReleaseRow row && e.NewValue is int rank)
            row.RankText.Text = rank.ToString("00");
    }

    private static void OnIsFeaturedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PopularReleaseRow row || e.NewValue is not bool) return;
        row.ApplyFeaturedVisuals();
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // AccentBrush DP preserved for compatibility; chrome uses theme accent.
    }

    private static void OnAccentForegroundBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // AccentForegroundBrush DP kept for compatibility; not consumed.
    }

    /// <summary>
    /// Refresh the featured-row chrome (highlight wash + border). Uses a fixed
    /// accent-tinted teal so every artist's featured row reads consistently
    /// with the system theme accent.
    /// </summary>
    private void ApplyFeaturedVisuals()
    {
        PopularNowChip.Visibility = IsFeatured ? Visibility.Visible : Visibility.Collapsed;
        if (!IsFeatured)
        {
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            RootBorder.BorderThickness = new Thickness(0);
            return;
        }

        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, 0x50, 0x90, 0xB8));
        RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x50, 0x90, 0xB8));
        RootBorder.BorderThickness = new Thickness(1);
    }

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        // Self-route when NavigationUri is set â€” fires connected animation
        // from the cover Border to AlbumPage's hero, builds a nav-prefill
        // ContentNavigationParameter with TotalTracks, and respects Ctrl+click
        // for new-tab. Falls back to the CardClick event for any subscriber
        // that needs custom handling (e.g. a host page wiring up analytics).
        var uri = NavigationUri;
        if (!string.IsNullOrEmpty(uri))
        {
            AlbumNavigationHelper.NavigateToAlbum(
                uri,
                title: Title,
                subtitle: Subtitle,
                imageUrl: CoverImageUrl,
                totalTracks: NavigationTotalTracks > 0 ? NavigationTotalTracks : null,
                connectedAnimationSource: CoverContainer);
        }
        CardClick?.Invoke(this, e);
    }
}
