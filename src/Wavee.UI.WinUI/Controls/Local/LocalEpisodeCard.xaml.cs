using System;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Local.Models;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Converters;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.Local;

/// <summary>
/// Grid-tile presentation for one local TV episode. Sibling to
/// <see cref="LocalEpisodeRow"/>: same data shape, same events, but laid
/// out as a 16:9 card with title + synopsis below so episode lists can
/// render as responsive grids instead of dense rows. Owns the episode-
/// specific overlays (resume position bar, "not in library" badge,
/// watched checkmark) that <c>ContentCard</c> doesn't model natively.
/// </summary>
public sealed partial class LocalEpisodeCard : UserControl
{
    public static readonly DependencyProperty EpisodeProperty =
        DependencyProperty.Register(
            nameof(Episode), typeof(LocalEpisode), typeof(LocalEpisodeCard),
            new PropertyMetadata(null, OnEpisodeChanged));

    public LocalEpisode? Episode
    {
        get => (LocalEpisode?)GetValue(EpisodeProperty);
        set => SetValue(EpisodeProperty, value);
    }

    public event EventHandler<LocalEpisode>? PlayRequested;
    public event EventHandler<(LocalEpisode Episode, RightTappedRoutedEventArgs Args)>? ContextRequested;

    private static readonly SpotifyImageConverter ImageConverter = new();

    private long _isPlayingToken = -1;
    private long _isPausedToken = -1;
    private bool _isHovered;
    private bool _isThisEpisodePlaying;
    private bool _isThisEpisodePaused;

    public LocalEpisodeCard()
    {
        InitializeComponent();
        Loaded += LocalEpisodeCard_Loaded;
        Unloaded += LocalEpisodeCard_Unloaded;
    }

    private void LocalEpisodeCard_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LocalEpisodeCard_Loaded;
        // Seed the composition CenterPoint so the hover scale animation
        // pivots from the centre instead of the top-left corner.
        UpdateCenterPoint();
        SizeChanged += (_, _) => UpdateCenterPoint();

        // Listen to the global TrackStateBehavior attached properties so the
        // playing equalizer / pause-on-hover state stays in sync with the
        // actual player without each card having to subscribe to
        // IPlaybackStateService itself.
        TrackStateBehavior.EnsurePlaybackSubscription();
        _isPlayingToken = CardRoot.RegisterPropertyChangedCallback(
            TrackStateBehavior.IsPlayingProperty, OnTrackStateChanged);
        _isPausedToken = CardRoot.RegisterPropertyChangedCallback(
            TrackStateBehavior.IsPausedProperty, OnTrackStateChanged);

        // Initial sync — TrackStateBehavior pushes state when TrackId is set,
        // but if the episode was assigned before Loaded fired we still need
        // to refresh here.
        RefreshPlayingState();
    }

    private void LocalEpisodeCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_isPlayingToken != -1)
        {
            CardRoot.UnregisterPropertyChangedCallback(TrackStateBehavior.IsPlayingProperty, _isPlayingToken);
            _isPlayingToken = -1;
        }
        if (_isPausedToken != -1)
        {
            CardRoot.UnregisterPropertyChangedCallback(TrackStateBehavior.IsPausedProperty, _isPausedToken);
            _isPausedToken = -1;
        }
        TrackStateBehavior.SetTrackId(CardRoot, null);
    }

    private void OnTrackStateChanged(DependencyObject d, DependencyProperty dp)
        => RefreshPlayingState();

    private void UpdateCenterPoint()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new Vector3((float)(ActualWidth / 2), (float)(ActualHeight / 2), 0);
    }

    private static void OnEpisodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LocalEpisodeCard)d).Apply(e.NewValue as LocalEpisode);
    }

    private void Apply(LocalEpisode? ep)
    {
        if (ep is null)
        {
            TitleText.Text = string.Empty;
            OverviewText.Text = string.Empty;
            DurationText.Text = string.Empty;
            NotInLibraryBadge.Visibility = Visibility.Collapsed;
            WatchedBadge.Visibility = Visibility.Collapsed;
            ResumeBar.Visibility = Visibility.Collapsed;
            ThumbImage.Source = null;
            ThumbImage.Opacity = 0;
            TrackStateBehavior.SetTrackId(CardRoot, null);
            RefreshPlayingState();
            return;
        }

        // Wire the global track-state registry so this card learns about
        // playing / paused state for its episode without each card having to
        // subscribe to IPlaybackStateService individually. Roster rows that
        // aren't on disk have null TrackUri; null trackId is a valid no-op.
        TrackStateBehavior.SetTrackId(CardRoot, ep.TrackUri);

        // Build "S1 E1 · Pilot" — fall back to file name when title is empty,
        // same as LocalEpisodeRow. Missing-from-disk rows always have a TMDB
        // title (roster cache), so the FilePath fallback only kicks in for
        // on-disk-but-unenriched episodes.
        var rawTitle = !string.IsNullOrWhiteSpace(ep.Title)
            ? ep.Title
            : (ep.FilePath is { } path
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : $"S{ep.Season} E{ep.Episode}");
        var prefix = ep.Season > 0 || ep.Episode > 0 ? $"S{ep.Season} E{ep.Episode} · " : string.Empty;
        TitleText.Text = prefix + rawTitle;

        // Missing-from-disk rows render at reduced opacity + show the badge.
        CardRoot.Opacity = ep.IsOnDisk ? 1.0 : 0.55;
        NotInLibraryBadge.Visibility = ep.IsOnDisk
            ? Visibility.Collapsed : Visibility.Visible;

        WatchedBadge.Visibility = ep.WatchedAt is > 0
            ? Visibility.Visible : Visibility.Collapsed;

        DurationText.Text = FormatDuration(ep.DurationMs);

        OverviewText.Text = ep.Overview ?? string.Empty;
        OverviewText.Visibility = string.IsNullOrWhiteSpace(ep.Overview)
            ? Visibility.Collapsed : Visibility.Visible;

        // Resume bar — only for in-progress, not-yet-watched episodes.
        if (ep.LastPositionMs > 0 && ep.DurationMs > 0 && ep.WatchedAt is null)
        {
            ApplyResumeFill(ThumbContainer.ActualWidth);
            ResumeBar.Visibility = Visibility.Visible;
        }
        else
        {
            ResumeBar.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(ep.StillImageUri))
        {
            var src = ImageConverter.Convert(ep.StillImageUri, typeof(ImageSource), "480", string.Empty) as ImageSource;
            ThumbImage.Source = src;
            ThumbImage.Opacity = src is null ? 0 : 1;
        }
        else
        {
            ThumbImage.Source = null;
            ThumbImage.Opacity = 0;
        }
    }

    /// <summary>
    /// Drives both the 16:9 thumb height and the resume-bar fill width from
    /// the container's current width — runs on every layout pass so it
    /// stays correct as the parent UniformGridLayout resizes.
    /// </summary>
    private void ThumbContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0) return;
        var targetHeight = e.NewSize.Width * 9.0 / 16.0;
        if (Math.Abs(ThumbContainer.Height - targetHeight) > 0.5)
            ThumbContainer.Height = targetHeight;
        ApplyResumeFill(e.NewSize.Width);
    }

    private void ApplyResumeFill(double containerWidth)
    {
        if (Episode is not { } ep || ep.DurationMs <= 0 || ep.LastPositionMs <= 0) return;
        if (containerWidth <= 0) return;
        var fraction = Math.Clamp(ep.LastPositionMs / (double)ep.DurationMs, 0.0, 1.0);
        ResumeFill.Width = containerWidth * fraction;
    }

    private static string FormatDuration(long ms)
    {
        if (ms <= 0) return string.Empty;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
            : $"{ts.Minutes}m";
    }

    private void ThumbImage_ImageOpened(object sender, RoutedEventArgs e)
        => ThumbImage.Opacity = 1;

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (Episode is not { IsOnDisk: true }) return; // missing rows: no hover affordance
        _isHovered = true;
        ScaleCard(1.03f, 180);
        RefreshPlayingState();
    }

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        ScaleCard(1.0f, 200);
        RefreshPlayingState();
    }

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is not { IsOnDisk: true } ep) return;

        // If this episode is already the active item — playing or paused —
        // clicking the card opens the cinematic video page instead of
        // restarting playback (which would scrub back to zero) or pausing
        // (which the bottom PlayerBar already exposes). The hover-pause
        // glyph is a visual cue that this row IS the active one; the click
        // navigates to where the user can actually see the video.
        if (_isThisEpisodePlaying || _isThisEpisodePaused)
        {
            Wavee.UI.WinUI.Helpers.Navigation.NavigationHelpers.OpenVideoPlayer();
            return;
        }

        PlayRequested?.Invoke(this, ep);
    }

    /// <summary>
    /// Sync hover / equalizer / play-overlay visuals to the current playback
    /// state. Mirrors TrackItem.UpdateRowOverlay / UpdateCompactOverlay — the
    /// single source of truth is <see cref="TrackStateBehavior"/>'s attached
    /// IsPlaying / IsPaused on <see cref="CardRoot"/>.
    /// </summary>
    private void RefreshPlayingState()
    {
        _isThisEpisodePlaying = TrackStateBehavior.GetIsPlaying(CardRoot);
        _isThisEpisodePaused = TrackStateBehavior.GetIsPaused(CardRoot);

        // Update the hover-play button's glyph so hovering a playing episode
        // shows the pause icon. PlayOverlayGlyph uses FluentGlyphs constants
        // here instead of raw PUA literals per the project convention.
        PlayOverlayGlyph.Glyph = _isThisEpisodePlaying
            ? FluentGlyphs.Pause
            : FluentGlyphs.Play;

        var showButton = _isHovered && Episode is { IsOnDisk: true };
        var showEqualizer = !showButton && (_isThisEpisodePlaying || _isThisEpisodePaused);

        // Animate play-overlay opacity for the same soft fade ContentCard uses.
        AnimateOpacity(PlayOverlay, showButton ? 1.0 : 0.0, showButton ? 140 : 160);

        if (showEqualizer)
        {
            PlayingEqualizerBadge.Visibility = Visibility.Visible;
            PlayingEqualizer.IsActive = _isThisEpisodePlaying;
            PlayingEqualizerBadge.Opacity = _isThisEpisodePlaying ? 1.0 : 0.7;
        }
        else
        {
            PlayingEqualizerBadge.Visibility = Visibility.Collapsed;
            PlayingEqualizer.IsActive = false;
        }
    }

    private void CardButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Episode is { } ep)
            ContextRequested?.Invoke(this, (ep, e));
    }

    private void ScaleCard(float to, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
        var compositor = visual.Compositor;
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(to, to, 1f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale", anim);
    }

    private static void AnimateOpacity(UIElement target, double to, int durationMs)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 2 },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }
}
