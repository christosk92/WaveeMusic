using System;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Hero-rail spotlight card used at the bottom-right of the V4A magazine hero.
/// Renders three modes via the <see cref="Mode"/> DP — Pinned (pin glyph +
/// quoted-comment block with accent left bar, no Save/Share), LatestRelease
/// (animated pulse dot + Save/Share), or PopularRelease (star glyph + Save/
/// Share). The card chrome is theme-aware: acrylic base + flyout-paired
/// stroke, canonical control radii, and WinUI typography ramp.
/// </summary>
public sealed partial class SpotlightReleaseCard : UserControl
{
    private SpriteVisual? _pulseRing;

    public event EventHandler<RoutedEventArgs>? CardClick;
    public event EventHandler<RoutedEventArgs>? PlayClick;
    public event EventHandler<RoutedEventArgs>? SaveClick;
    public event EventHandler<RoutedEventArgs>? ShareClick;

    public static readonly DependencyProperty TagTextProperty =
        DependencyProperty.Register(nameof(TagText), typeof(string), typeof(SpotlightReleaseCard),
            new PropertyMetadata("Latest release", OnTagTextChanged));

    public static readonly DependencyProperty EyebrowTextProperty =
        DependencyProperty.Register(nameof(EyebrowText), typeof(string), typeof(SpotlightReleaseCard),
            new PropertyMetadata("Latest release", OnEyebrowTextChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SpotlightReleaseCard),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(SpotlightReleaseCard),
            new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public static readonly DependencyProperty CommentProperty =
        DependencyProperty.Register(nameof(Comment), typeof(string), typeof(SpotlightReleaseCard),
            new PropertyMetadata(null, OnCommentChanged));

    public static readonly DependencyProperty CoverImageUrlProperty =
        DependencyProperty.Register(nameof(CoverImageUrl), typeof(string), typeof(SpotlightReleaseCard),
            new PropertyMetadata(null, OnCoverImageUrlChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(SpotlightReleaseCard),
            new PropertyMetadata(null, OnAccentBrushChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(SpotlightMode), typeof(SpotlightReleaseCard),
            new PropertyMetadata(SpotlightMode.LatestRelease, OnModeChanged));

    // ─── Navigation DPs (artist→album prefetch + connected animation pass).
    // When NavigationUri is set, CardSurface_Tapped self-routes through
    // AlbumNavigationHelper (prefetch + connected anim + count prefill) and
    // CardClick still fires for any subscriber that wants to react.
    public static readonly DependencyProperty NavigationUriProperty =
        DependencyProperty.Register(nameof(NavigationUri), typeof(string), typeof(SpotlightReleaseCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty NavigationTotalTracksProperty =
        DependencyProperty.Register(nameof(NavigationTotalTracks), typeof(int), typeof(SpotlightReleaseCard),
            new PropertyMetadata(0));

    public string TagText { get => (string)GetValue(TagTextProperty); set => SetValue(TagTextProperty, value); }
    public string EyebrowText { get => (string)GetValue(EyebrowTextProperty); set => SetValue(EyebrowTextProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? Comment { get => (string?)GetValue(CommentProperty); set => SetValue(CommentProperty, value); }
    public string? CoverImageUrl { get => (string?)GetValue(CoverImageUrlProperty); set => SetValue(CoverImageUrlProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public SpotlightMode Mode { get => (SpotlightMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    public string? NavigationUri { get => (string?)GetValue(NavigationUriProperty); set => SetValue(NavigationUriProperty, value); }
    public int NavigationTotalTracks { get => (int)GetValue(NavigationTotalTracksProperty); set => SetValue(NavigationTotalTracksProperty, value); }

    // Album-metadata viewport prefetch. The spotlight card is always at the
    // top of the artist page when rendered, so it effectively fires on first
    // measure — but we still gate via EffectiveViewportChanged so it doesn't
    // fetch when the card is offscreen on a small / narrow window mode.
    private bool _albumPrefetchKicked;
    private bool _playlistPrefetchKicked;
    private const double AlbumPrefetchTriggerDistance = 500;
    private const string AlbumUriPrefix = "spotify:album:";
    private const string PlaylistUriPrefix = "spotify:playlist:";

    public SpotlightReleaseCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        ApplyModeVisuals();
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Mode == SpotlightMode.LatestRelease)
            StartPulseAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Do NOT clear CoverImage.ImageUrl here — CompositionImage releases
        // its own pin on Unloaded. Clearing the URL would break the same-
        // DataContext scroll-back-up path: the outer CoverImageUrl DP keeps
        // its value across virtualization, so the change-callback never
        // refires, and the inner ImageUrl stays null = blank tile.
        StopPulseAnimation();
        _albumPrefetchKicked = false;
        _playlistPrefetchKicked = false;
    }

    private void StartPulseAnimation()
    {
        if (PulseDot == null) return;
        var visual = ElementCompositionPreview.GetElementVisual(PulseDot);
        var compositor = visual.Compositor;

        visual.CenterPoint = new Vector3(4f, 4f, 0f);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(1f, 1f, 0f));
        scale.InsertKeyFrame(0.5f, new Vector3(1.4f, 1.4f, 0f));
        scale.InsertKeyFrame(1f, new Vector3(1f, 1f, 0f));
        scale.Duration = TimeSpan.FromMilliseconds(1800);
        scale.IterationBehavior = AnimationIterationBehavior.Forever;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 1f);
        opacity.InsertKeyFrame(0.5f, 0.55f);
        opacity.InsertKeyFrame(1f, 1f);
        opacity.Duration = TimeSpan.FromMilliseconds(1800);
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private void StopPulseAnimation()
    {
        if (PulseDot == null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(PulseDot);
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
        }
        catch
        {
            // Element may have been torn down already; nothing to clean up.
        }
    }

    private static void OnTagTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightReleaseCard c) c.TagTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnEyebrowTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightReleaseCard c) c.EyebrowTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightReleaseCard c) c.TitleTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightReleaseCard c)
        {
            var text = e.NewValue as string ?? string.Empty;
            c.SubtitleTextBlock.Text = text;
            c.ApplyModeVisuals();
        }
    }

    private static void OnCommentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightReleaseCard c)
        {
            var text = e.NewValue as string ?? string.Empty;
            c.CommentTextBlock.Text = text;
            c.ApplyModeVisuals();
        }
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightReleaseCard c)
        {
            c.ApplyModeVisuals();
            // Re-evaluate pulse: latest pulses, others stay still.
            if (c.Mode == SpotlightMode.LatestRelease)
                c.StartPulseAnimation();
            else
                c.StopPulseAnimation();
        }
    }

    /// <summary>Toggle the header strip's leading affordance (pulse / pin /
    /// star), Comment vs Subtitle visibility, and Save/Share visibility based
    /// on the current Mode + whether Comment/Subtitle have text. Called
    /// whenever Mode, Comment, or Subtitle change.</summary>
    private void ApplyModeVisuals()
    {
        bool isPinned = Mode == SpotlightMode.Pinned;
        bool isLatest = Mode == SpotlightMode.LatestRelease;
        bool isPopular = Mode == SpotlightMode.PopularRelease;
        bool hasComment = !string.IsNullOrEmpty(Comment);
        bool hasSubtitle = !string.IsNullOrEmpty(Subtitle);

        // Exactly one leading affordance per mode. Pulse signals "fresh" and
        // only LatestRelease earns the motion; Pinned + Popular get a static
        // glyph so the eyebrow always has a visual anchor.
        PulseDot.Visibility = isLatest ? Visibility.Visible : Visibility.Collapsed;
        PinGlyph.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
        StarGlyph.Visibility = isPopular ? Visibility.Visible : Visibility.Collapsed;

        // Pinned mode prefers Comment over Subtitle (the editorial blurb is the
        // signal); LatestRelease / PopularRelease prefer Subtitle.
        if (isPinned && hasComment)
        {
            CommentBlock.Visibility = Visibility.Visible;
            SubtitleTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            CommentBlock.Visibility = Visibility.Collapsed;
            SubtitleTextBlock.Visibility = hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
        }

        // Save/Share only meaningful for release-shaped content.
        SaveButton.Visibility = isPinned ? Visibility.Collapsed : Visibility.Visible;
        ShareButton.Visibility = isPinned ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnCoverImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SpotlightReleaseCard card) return;

        var url = e.NewValue as string;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
        card.CoverImage.ImageUrl = string.IsNullOrEmpty(httpsUrl) ? null : httpsUrl;
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightReleaseCard c && e.NewValue is SolidColorBrush b)
        {
            // Only chrome affordances ride the palette brush — pulse dot,
            // Play button background, and the comment block's left accent bar.
            // Inline text stays on the system theme so the card reads as part
            // of the app chrome rather than adopting an unpredictable
            // per-artist colour.
            c.PulseDot.Fill = b;
            c.PlayButton.Background = b;
            c.CommentBlock.BorderBrush = b;
        }
    }

    private void CardSurface_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Self-route to AlbumPage when NavigationUri is set — connected anim
        // from the CoverBorder, nav-prefill with TotalTracks, Ctrl+click new-
        // tab semantics. CardClick still fires for back-compat with any host
        // page subscribing for custom analytics / hooks.
        var uri = NavigationUri;
        if (!string.IsNullOrEmpty(uri))
        {
            AlbumNavigationHelper.NavigateToAlbum(
                uri,
                title: Title,
                subtitle: Subtitle,
                imageUrl: CoverImageUrl,
                totalTracks: NavigationTotalTracks > 0 ? NavigationTotalTracks : null,
                connectedAnimationSource: CoverBorder);
        }
        CardClick?.Invoke(this, e);
    }

    private void InlineAction_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PlayClick?.Invoke(this, e);

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveClick?.Invoke(this, e);

    private void ShareButton_Click(object sender, RoutedEventArgs e) => ShareClick?.Invoke(this, e);
}
