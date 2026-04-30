using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Home card for episode entities (<c>spotify:episode:*</c>). Renders cover +
/// title + publisher + a played-state-dependent bottom row (date+duration,
/// in-progress bar, or "Played" check). Bound directly to a
/// <see cref="HomeSectionItem"/> via DataContext — keeps the template selector
/// path simple and avoids a sibling DP per field.
/// </summary>
public sealed partial class EpisodeCard : UserControl
{
    private const double HoverCoverScale = 1.03;
    private const double HoverCoverDurationMs = 200;
    private const int CoverDecodeSize = 200;

    private readonly ImageCacheService? _imageCache;
    private bool _isHovered;
    private HomeSectionItem? _boundItem;

    public EpisodeCard()
    {
        _imageCache = Ioc.Default.GetService<ImageCacheService>();
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundItem != null)
        {
            _boundItem.PropertyChanged -= OnItemPropertyChanged;
            _boundItem = null;
        }

        CoverImage.Source = null;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_boundItem != null)
            _boundItem.PropertyChanged -= OnItemPropertyChanged;

        CoverImage.Source = null;
        _boundItem = args.NewValue as HomeSectionItem;

        if (_boundItem != null)
            _boundItem.PropertyChanged += OnItemPropertyChanged;

        RenderAll();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Item fields can mutate post-instantiation (image enrichment, played-
        // state refresh on next Home parse). Re-render on every change — the
        // work is cheap and the alternative (per-property if/else ladder) bloats
        // the file without measurable benefit.
        RenderAll();
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void RenderAll()
    {
        var item = _boundItem;
        TitleText.Text = item?.Title ?? string.Empty;
        PublisherText.Text = item?.PublisherName ?? item?.Subtitle ?? string.Empty;

        // Cover image — bind via Source so we don't have to manage placeholder
        // visibility manually; CoverImage_ImageOpened hides the glyph.
        var imageUrl = SpotifyImageHelper.ToHttpsUrl(item?.ImageUrl) ?? item?.ImageUrl;
        if (!string.IsNullOrEmpty(imageUrl))
        {
            try
            {
                CoverImage.Source = _imageCache?.GetOrCreate(imageUrl, CoverDecodeSize)
                    ?? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(imageUrl))
                    {
                        DecodePixelWidth = CoverDecodeSize,
                        DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical
                    };
            }
            catch
            {
                CoverImage.Source = null;
                CoverPlaceholderIcon.Visibility = Visibility.Visible;
            }
        }
        else
        {
            CoverImage.Source = null;
            CoverPlaceholderIcon.Visibility = Visibility.Visible;
        }

        VideoChip.Visibility = item?.IsVideoPodcast == true ? Visibility.Visible : Visibility.Collapsed;

        UpdateStateRow(item);
    }

    /// <summary>
    /// Renders the bottom band — progress bar (only InProgress) and the state
    /// row (glyph + label). Three branches drive everything off
    /// <see cref="HomeSectionItem.PlayedState"/>; durations / dates format
    /// inline because the rules are short and one-shot.
    /// </summary>
    private void UpdateStateRow(HomeSectionItem? item)
    {
        var state = item?.PlayedState;
        var durationMs = item?.DurationMs ?? 0;
        var positionMs = item?.PlayedPositionMs ?? 0;

        switch (state)
        {
            case EpisodePlayedState.InProgress when durationMs > 0:
            {
                ProgressBar.Visibility = Visibility.Visible;
                StateGlyph.Visibility = Visibility.Collapsed;

                var fraction = Math.Clamp((double)positionMs / durationMs, 0d, 1d);
                // Width is set on next layout pass — Loaded handles initial.
                if (ProgressBar.ActualWidth > 0)
                    ProgressFill.Width = ProgressBar.ActualWidth * fraction;

                var remainingMs = Math.Max(0, durationMs - positionMs);
                StateText.Text = $"{FormatMinutes(remainingMs)} left";
                StateText.Foreground = (Brush)Resources["EpisodeAccentBrush"];
                break;
            }

            case EpisodePlayedState.Completed:
            {
                ProgressBar.Visibility = Visibility.Collapsed;
                StateGlyph.Visibility = Visibility.Visible;
                StateText.Text = "Played";
                StateText.Foreground = (Brush)Resources["EpisodeAccentBrush"];
                break;
            }

            default:
            {
                // NotStarted, null, or InProgress without a duration — fall
                // through to "{date} · {duration}". Date omitted when no
                // ReleaseDateIso. Duration omitted when no DurationMs.
                ProgressBar.Visibility = Visibility.Collapsed;
                StateGlyph.Visibility = Visibility.Collapsed;

                var date = FormatRelativeDate(item?.ReleaseDateIso);
                var duration = durationMs > 0 ? FormatMinutes(durationMs) : null;

                StateText.Text = (date, duration) switch
                {
                    ({ Length: > 0 } d, { Length: > 0 } dur) => $"{d} · {dur}",
                    ({ Length: > 0 } d, _) => d,
                    (_, { Length: > 0 } dur) => dur,
                    _ => string.Empty
                };
                StateText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                break;
            }
        }
    }

    /// <summary>
    /// "23 min" under an hour, "1.4 hr" beyond. Picked from Spotify's own
    /// rounding behaviour on episode shelves so listings read consistently.
    /// </summary>
    private static string FormatMinutes(long ms)
    {
        if (ms < 60_000) return "<1 min";
        var minutes = ms / 60_000.0;
        if (minutes < 60) return $"{Math.Round(minutes)} min";
        var hours = ms / 3_600_000.0;
        return $"{Math.Round(hours, 1)} hr";
    }

    /// <summary>
    /// Relative wording within 30 days ("Today", "Yesterday", "{N}d ago"); falls
    /// through to absolute "MMM d" for older episodes. Keeps the bottom row
    /// scannable for the freshest content (the common case on Home).
    /// </summary>
    private static string? FormatRelativeDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return null;

        var days = (DateTimeOffset.UtcNow - dto).TotalDays;
        if (days < 0) return dto.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
        if (days < 1) return "Today";
        if (days < 2) return "Yesterday";
        if (days < 30) return $"{(int)days}d ago";
        return dto.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
    }

    // ── Layout / image ───────────────────────────────────────────────────

    private void CoverContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Square enforcement — no other constraint sets the height, so without
        // this the cover collapses to whatever the placeholder glyph needs.
        if (e.NewSize.Width > 0 && Math.Abs(CoverContainer.Height - e.NewSize.Width) > 0.5)
            CoverContainer.Height = e.NewSize.Width;

        // ScaleTransform pivot = center of cover. RenderTransformOrigin would
        // do this declaratively but the property is on UIElement, not Grid in
        // this context, so we pin the transform's centre by setting the
        // CenterX/Y in code as the size resolves.
        CoverScale.CenterX = e.NewSize.Width / 2;
        CoverScale.CenterY = e.NewSize.Height / 2;

        // Re-derive the in-progress fill width as the card resizes.
        if (ProgressBar.Visibility == Visibility.Visible && _boundItem is { DurationMs: > 0 } it)
        {
            var fraction = Math.Clamp((double)(it.PlayedPositionMs ?? 0) / it.DurationMs.Value, 0d, 1d);
            ProgressFill.Width = ProgressBar.ActualWidth * fraction;
        }
    }

    private void CoverImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        CoverPlaceholderIcon.Visibility = Visibility.Collapsed;
    }

    // ── Hover affordances ────────────────────────────────────────────────

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isHovered) return;
        _isHovered = true;
        AnimateHover(true);
    }

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isHovered) return;
        _isHovered = false;
        AnimateHover(false);
    }

    /// <summary>
    /// Cover scales from 1.0 to 1.03 over 200 ms — same easing curve as
    /// <see cref="LikedSongsRecentCard"/>'s heart so the two card types feel
    /// like they belong to one design system. The play chip is always visible
    /// (the strongest "this is an episode" signal), so it just rides the
    /// cover-scale transform up on hover; no separate opacity animation.
    /// </summary>
    private void AnimateHover(bool toHover)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(HoverCoverDurationMs);
        var targetScale = toHover ? HoverCoverScale : 1.0;

        var sb = new Storyboard();

        var scaleX = new DoubleAnimation { To = targetScale, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleX, CoverScale);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation { To = targetScale, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleY, CoverScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        sb.Children.Add(scaleY);

        sb.Begin();
    }

    // ── Tap / nav ────────────────────────────────────────────────────────

    private void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var item = _boundItem;
        var uri = item?.Uri;
        if (string.IsNullOrEmpty(uri))
        {
            e.Handled = true;
            return;
        }

        // Episodes route to PlayEpisode; shows route to OpenShow. The card
        // itself owns the dispatch so it doesn't depend on the ContentCard
        // path — both stubs log a TODO until dedicated pages land.
        if (uri.Contains(":episode:", StringComparison.Ordinal))
            NavigationHelpers.PlayEpisode(uri);
        else if (uri.Contains(":show:", StringComparison.Ordinal))
            NavigationHelpers.OpenShow(uri, item?.Title ?? string.Empty, NavigationHelpers.IsCtrlPressed());

        e.Handled = true;
    }
}
