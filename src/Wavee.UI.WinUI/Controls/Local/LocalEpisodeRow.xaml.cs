using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Local.Models;
using Wavee.UI.WinUI.Converters;

namespace Wavee.UI.WinUI.Controls.Local;

/// <summary>
/// Dense single-row presentation for one local TV episode. Mirrors
/// Controls/ShowEpisode/ShowEpisodeRow.xaml's pattern so the local-files
/// surface shares the same visual language as Spotify podcast lists.
/// Bound through a single <see cref="Episode"/> DP — when the DP changes,
/// visuals refresh in-place without re-instantiating the row.
/// </summary>
public sealed partial class LocalEpisodeRow : UserControl
{
    public static readonly DependencyProperty EpisodeProperty =
        DependencyProperty.Register(
            nameof(Episode), typeof(LocalEpisode), typeof(LocalEpisodeRow),
            new PropertyMetadata(null, OnEpisodeChanged));

    public LocalEpisode? Episode
    {
        get => (LocalEpisode?)GetValue(EpisodeProperty);
        set => SetValue(EpisodeProperty, value);
    }

    public event EventHandler<LocalEpisode>? PlayRequested;
    public event EventHandler<(LocalEpisode Episode, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs Args)>? ContextRequested;

    private static readonly SpotifyImageConverter ImageConverter = new();

    public LocalEpisodeRow()
    {
        InitializeComponent();
    }

    private static void OnEpisodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (LocalEpisodeRow)d;
        row.Apply(e.NewValue as LocalEpisode);
    }

    private void Apply(LocalEpisode? ep)
    {
        if (ep is null)
        {
            TitleText.Text = string.Empty;
            OverviewText.Text = string.Empty;
            DurationText.Text = string.Empty;
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
            SubtitleGlyph.Visibility = Visibility.Collapsed;
            WatchedGlyph.Visibility = Visibility.Collapsed;
            ResumeBar.Visibility = Visibility.Collapsed;
            ThumbImage.Source = null;
            ThumbImage.Opacity = 0;
            return;
        }

        // Title — fall back to the file name when enrichment hasn't filled
        // in a real title yet. Missing-from-disk rows always have a TMDB
        // title (we populate via the roster cache), so the FilePath fallback
        // only kicks in for on-disk-but-unenriched episodes.
        TitleText.Text = !string.IsNullOrWhiteSpace(ep.Title)
            ? ep.Title
            : (ep.FilePath is { } path
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : $"S{ep.Season} E{ep.Episode}");

        // v19 — missing-from-disk rows render at reduced opacity with a
        // "Not in library" overlay. Toggles via the root grid's Opacity
        // and the dedicated badge.
        RowRoot.Opacity = ep.IsOnDisk ? 1.0 : 0.55;
        NotInLibraryBadge.Visibility = ep.IsOnDisk
            ? Visibility.Collapsed : Visibility.Visible;

        // Episode-number chip — `S1 E2` style. Hide when both are 0.
        if (ep.Season > 0 || ep.Episode > 0)
        {
            EpisodeNumberText.Text = $"S{ep.Season} E{ep.Episode}";
            EpisodeNumberChip.Visibility = Visibility.Visible;
        }
        else
        {
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
        }

        // Overview line — only render when enrichment populated it. Otherwise
        // collapse so the row stays vertically compact.
        if (!string.IsNullOrWhiteSpace(ep.Overview))
        {
            OverviewText.Text = ep.Overview;
            OverviewText.Visibility = Visibility.Visible;
        }
        else
        {
            OverviewText.Text = string.Empty;
            OverviewText.Visibility = Visibility.Collapsed;
        }

        DurationText.Text = FormatDuration(ep.DurationMs);

        SubtitleGlyph.Visibility = ep.SubtitleCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        WatchedGlyph.Visibility = ep.WatchedAt is > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Resume-bar fill underneath the thumb. Only show for in-progress
        // episodes (>0 position, not fully watched).
        if (ep.LastPositionMs > 0 && ep.DurationMs > 0 && ep.WatchedAt is null)
        {
            var fraction = Math.Clamp(ep.LastPositionMs / (double)ep.DurationMs, 0.0, 1.0);
            ResumeFill.Width = 160 * fraction;
            ResumeBar.Visibility = Visibility.Visible;
        }
        else
        {
            ResumeBar.Visibility = Visibility.Collapsed;
        }

        // Load the still image via SpotifyImageConverter (which understands
        // wavee-artwork:// URIs and the on-disk image cache). Null → placeholder.
        if (!string.IsNullOrEmpty(ep.StillImageUri))
        {
            var src = ImageConverter.Convert(ep.StillImageUri, typeof(ImageSource), "320", string.Empty) as ImageSource;
            ThumbImage.Source = src;
            ThumbImage.Opacity = src is null ? 0 : 1;
        }
        else
        {
            ThumbImage.Source = null;
            ThumbImage.Opacity = 0;
        }
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
    {
        ThumbImage.Opacity = 1;
    }

    private void RowRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (Episode is not { IsOnDisk: true }) return; // missing rows don't show hover-play
        AnimateOpacity(HoverFill, 1.0, 120);
        AnimateOpacity(PlayOverlay, 1.0, 120);
    }

    private void RowRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimateOpacity(HoverFill, 0.0, 160);
        AnimateOpacity(PlayOverlay, 0.0, 160);
    }

    private void RowRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Episode is { IsOnDisk: true } ep)
            PlayRequested?.Invoke(this, ep);
        // Missing rows: do nothing on tap. The row's reduced opacity and
        // "Not in library" badge already telegraph the state.
    }

    private void RowRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Episode is { } ep)
            ContextRequested?.Invoke(this, (ep, e));
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
