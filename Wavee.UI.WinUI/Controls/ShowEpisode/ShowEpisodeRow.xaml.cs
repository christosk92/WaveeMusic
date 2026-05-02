using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.ShowEpisode;

/// <summary>
/// Dense single-row presentation for one podcast episode inside the Show
/// detail page's vertical list. Bound through DependencyProperty so each
/// ItemsRepeater container only mutates its own visual state when the
/// episode it represents changes.
/// </summary>
public sealed partial class ShowEpisodeRow : UserControl
{
    public static readonly DependencyProperty EpisodeProperty =
        DependencyProperty.Register(
            nameof(Episode), typeof(ShowEpisodeDto), typeof(ShowEpisodeRow),
            new PropertyMetadata(null, OnEpisodeChanged));

    public static readonly DependencyProperty CoverColorBrushProperty =
        DependencyProperty.Register(
            nameof(CoverColorBrush), typeof(Brush), typeof(ShowEpisodeRow),
            new PropertyMetadata(null, OnCoverColorBrushChanged));

    public ShowEpisodeDto? Episode
    {
        get => (ShowEpisodeDto?)GetValue(EpisodeProperty);
        set => SetValue(EpisodeProperty, value);
    }

    /// <summary>The show's palette dominant tone (lifted via <c>TintColorHelper</c>),
    /// used to colour the row's state tint, border, progress fill, and "In progress"
    /// chip. Pass <c>ShowViewModel.PaletteCoverColorBrush</c>; null falls back to
    /// neutral theme brushes so cold-load doesn't show a green leak.</summary>
    public Brush? CoverColorBrush
    {
        get => (Brush?)GetValue(CoverColorBrushProperty);
        set => SetValue(CoverColorBrushProperty, value);
    }

    /// <summary>Fired when the row body or play button is tapped.</summary>
    public event EventHandler<ShowEpisodeDto>? PlayRequested;

    /// <summary>Fired when the heart button is tapped.</summary>
    public event EventHandler<ShowEpisodeDto>? LikeRequested;

    public ShowEpisodeRow()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private static void OnEpisodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShowEpisodeRow row) row.ApplyEpisode(e.NewValue as ShowEpisodeDto);
    }

    private static void OnCoverColorBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShowEpisodeRow row) row.ApplyState();
    }

    private void ApplyEpisode(ShowEpisodeDto? episode)
    {
        if (episode is null)
        {
            TitleText.Text = "";
            EpisodeNumberText.Text = "";
            DescriptionText.Text = "";
            MetaText.Text = "";
            CoverImage.Source = null;
            CoverImage.Opacity = 0;
            CoverPlaceholderIcon.Opacity = 1;
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
            ExplicitChip.Visibility = Visibility.Collapsed;
            VideoGlyph.Visibility = Visibility.Collapsed;
            ApplyState();
            return;
        }

        TitleText.Text = StripEpisodeNumberPrefix(episode.Title);
        if (string.IsNullOrEmpty(episode.EpisodeNumberTag))
        {
            EpisodeNumberText.Text = "";
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
        }
        else
        {
            EpisodeNumberText.Text = episode.EpisodeNumberTag;
            EpisodeNumberChip.Visibility = Visibility.Visible;
        }

        DescriptionText.Text = episode.DescriptionPreview ?? "";
        MetaText.Text = episode.MetaLine;

        ExplicitChip.Visibility = episode.IsExplicit ? Visibility.Visible : Visibility.Collapsed;
        VideoGlyph.Visibility = episode.IsVideo ? Visibility.Visible : Visibility.Collapsed;
        StatusChipText.Text = episode.StatusText;

        // Cover: nullify first so a recycled container doesn't briefly show
        // the previous episode's cover while the new one decodes.
        CoverImage.Opacity = 0;
        CoverPlaceholderIcon.Opacity = 1;
        if (!string.IsNullOrEmpty(episode.CoverArtUrl))
        {
            try
            {
                CoverImage.Source = new BitmapImage(new Uri(episode.CoverArtUrl));
            }
            catch
            {
                CoverImage.Source = null;
            }
        }
        else
        {
            CoverImage.Source = null;
        }

        ApplyState();
    }

    /// <summary>
    /// Resolves all state-driven chrome (background, border, opacity, progress
    /// fill, status chip, state glyph) from the current <see cref="Episode"/>
    /// and <see cref="CoverColorBrush"/>. Called from both DP-changed callbacks
    /// so either trigger refreshes the row's visuals.
    /// </summary>
    private void ApplyState()
    {
        var episode = Episode;
        if (episode is null)
        {
            RowContainer.Background = TransparentBrush;
            RowContainer.BorderBrush = DefaultBorderBrush();
            RowRoot.Opacity = 1.0;
            ProgressBar.Visibility = Visibility.Collapsed;
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
            StateGlyph.Visibility = Visibility.Collapsed;
            StatusChip.Visibility = Visibility.Collapsed;
            ProgressBar.SizeChanged -= ProgressBar_SizeChanged;
            return;
        }

        var coverColor = ResolveCoverColor();
        EpisodeNumberChip.Background = new SolidColorBrush(WithAlpha(coverColor, 0x2E));
        EpisodeNumberText.Foreground = new SolidColorBrush(coverColor);

        switch (episode.PlayedState)
        {
            case "IN_PROGRESS":
                RowContainer.Background = new SolidColorBrush(WithAlpha(coverColor, 0x1A));
                RowContainer.BorderBrush = new SolidColorBrush(WithAlpha(coverColor, 0x40));
                RowRoot.Opacity = 1.0;

                if (episode.Progress > 0 && episode.Progress < 1)
                {
                    ProgressBar.Visibility = Visibility.Visible;
                    ProgressFill.Background = new SolidColorBrush(coverColor);
                    UpdateProgressFill();
                    ProgressBar.SizeChanged -= ProgressBar_SizeChanged;
                    ProgressBar.SizeChanged += ProgressBar_SizeChanged;
                }
                else
                {
                    ProgressBar.Visibility = Visibility.Collapsed;
                    ProgressBar.SizeChanged -= ProgressBar_SizeChanged;
                }

                StateGlyph.Glyph = FluentGlyphs.PlayingIndicator;
                StateGlyph.Foreground = new SolidColorBrush(coverColor);
                StateGlyph.Visibility = Visibility.Visible;

                StatusChip.Background = new SolidColorBrush(WithAlpha(coverColor, 0x33));
                StatusChipText.Foreground = new SolidColorBrush(coverColor);
                StatusChip.Visibility = Visibility.Visible;
                break;

            case "COMPLETED":
                RowContainer.Background = TransparentBrush;
                RowContainer.BorderBrush = DefaultBorderBrush();
                RowRoot.Opacity = 0.65;

                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.SizeChanged -= ProgressBar_SizeChanged;

                StateGlyph.Glyph = FluentGlyphs.CheckMark;
                StateGlyph.Foreground = new SolidColorBrush(coverColor);
                StateGlyph.Visibility = Visibility.Visible;

                // Played chip stays neutral — the dim opacity carries the state.
                StatusChip.Background = SubtleSecondaryBrush();
                StatusChipText.Foreground = SecondaryTextBrush();
                StatusChip.Visibility = Visibility.Visible;
                break;

            default: // NOT_STARTED and unknown states.
                RowContainer.Background = TransparentBrush;
                RowContainer.BorderBrush = DefaultBorderBrush();
                RowRoot.Opacity = 1.0;

                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.SizeChanged -= ProgressBar_SizeChanged;

                StateGlyph.Visibility = Visibility.Collapsed;
                StatusChip.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private Color ResolveCoverColor()
    {
        if (CoverColorBrush is SolidColorBrush solid)
            return solid.Color;
        // Cold-load fallback — neutral grey, never green.
        return Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    }

    private static Color WithAlpha(Color c, byte alpha)
        => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static Brush DefaultBorderBrush()
        => ResolveTheme("CardStrokeColorDefaultBrush") ?? TransparentBrush;

    private static Brush SubtleSecondaryBrush()
        => ResolveTheme("SubtleFillColorSecondaryBrush") ?? TransparentBrush;

    private static Brush SecondaryTextBrush()
        => ResolveTheme("TextFillColorSecondaryBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private static Brush? ResolveTheme(string key)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var value)
            && value is Brush brush)
            return brush;
        return null;
    }

    private static string StripEpisodeNumberPrefix(string title)
    {
        if (string.IsNullOrEmpty(title) || title[0] != '#') return title;
        var i = 1;
        while (i < title.Length && char.IsDigit(title[i])) i++;
        if (i == 1) return title;
        while (i < title.Length && (title[i] == ' ' || title[i] == '-' || title[i] == ':'))
            i++;
        return i >= title.Length ? title : title[i..];
    }

    private void ProgressBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressFill();

    private void UpdateProgressFill()
    {
        var ep = Episode;
        if (ep is null || ProgressBar.ActualWidth <= 0) return;
        ProgressFill.Width = Math.Max(0, ProgressBar.ActualWidth * ep.Progress);
    }

    private void CoverImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        var fade = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180))
        };
        Storyboard.SetTarget(anim, CoverImage);
        Storyboard.SetTargetProperty(anim, "Opacity");
        fade.Children.Add(anim);

        var fadeOut = new DoubleAnimation
        {
            From = 1, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(120))
        };
        Storyboard.SetTarget(fadeOut, CoverPlaceholderIcon);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        fade.Children.Add(fadeOut);

        fade.Begin();
    }

    private void RowRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateOpacity(HoverFill, 1.0);
        AnimateOpacity(ActionCluster, 1.0);
        if (Episode?.PlayedState == "COMPLETED")
            AnimateOpacity(RowRoot, 0.92);
    }

    private void RowRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimateOpacity(HoverFill, 0.0);
        AnimateOpacity(ActionCluster, 0.0);
        if (Episode?.PlayedState == "COMPLETED")
            AnimateOpacity(RowRoot, 0.65);
    }

    private static void AnimateOpacity(FrameworkElement target, double to)
    {
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(140))
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void RowRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Episode is null) return;
        // Don't double-fire when the user taps an action button — those have
        // their own Click handlers and we'd otherwise both fire LikeRequested
        // and PlayRequested on a single heart tap.
        if (e.OriginalSource is FrameworkElement fe && IsInsideActionCluster(fe)) return;
        PlayRequested?.Invoke(this, Episode);
    }

    private bool IsInsideActionCluster(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (ReferenceEquals(current, ActionCluster)) return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is null) return;
        PlayRequested?.Invoke(this, Episode);
    }

    private void LikeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is null) return;
        LikeRequested?.Invoke(this, Episode);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Mirrors ContentCard.OnUnloaded - null the BitmapImage so the
        // recycled container doesn't keep the decoded surface pinned.
        // ApplyEpisode rebinds correctly on re-realization.
        CoverImage.Source = null;
    }
}
