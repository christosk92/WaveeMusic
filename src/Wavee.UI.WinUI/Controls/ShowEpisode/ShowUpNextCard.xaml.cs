using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.ShowEpisode;

/// <summary>
/// Compact episode tile for the Up Next grid below the Resume banner. Bound by
/// DependencyProperty so each <c>ItemsRepeater</c> container only mutates its own
/// visual state when the episode it represents changes.
/// </summary>
public sealed partial class ShowUpNextCard : UserControl
{
    public static readonly DependencyProperty EpisodeProperty =
        DependencyProperty.Register(
            nameof(Episode), typeof(ShowEpisodeDto), typeof(ShowUpNextCard),
            new PropertyMetadata(null, OnEpisodeChanged));

    public static readonly DependencyProperty CoverColorBrushProperty =
        DependencyProperty.Register(
            nameof(CoverColorBrush), typeof(Brush), typeof(ShowUpNextCard),
            new PropertyMetadata(null, OnCoverColorBrushChanged));

    public ShowEpisodeDto? Episode
    {
        get => (ShowEpisodeDto?)GetValue(EpisodeProperty);
        set => SetValue(EpisodeProperty, value);
    }

    /// <summary>The show's palette dominant tone (lifted via <c>TintColorHelper</c>),
    /// used to colour the "#N" tag, hover border, and reveal play button. Pass
    /// <c>ViewModel.PaletteCoverColorBrush</c>.</summary>
    public Brush? CoverColorBrush
    {
        get => (Brush?)GetValue(CoverColorBrushProperty);
        set => SetValue(CoverColorBrushProperty, value);
    }

    /// <summary>Fired when the card body is tapped — should navigate to the episode detail page.</summary>
    public event EventHandler<ShowEpisodeDto>? OpenRequested;

    /// <summary>Fired when the explicit play button is tapped — should start playback.</summary>
    public event EventHandler<ShowEpisodeDto>? PlayRequested;

    private Brush _restingBorderBrush;
    private SolidColorBrush? _hoverBorderBrush;
    private SolidColorBrush? _accentSolidBrush;

    public ShowUpNextCard()
    {
        InitializeComponent();
        _restingBorderBrush = CardRoot.BorderBrush;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnEpisodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShowUpNextCard c) c.ApplyEpisode(e.NewValue as ShowEpisodeDto);
    }

    private static void OnCoverColorBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShowUpNextCard c) c.ApplyAccent(e.NewValue as Brush);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Re-apply when the template realises inside the DataTemplate; the
        // DependencyProperty setters can fire before the named parts exist.
        ApplyEpisode(Episode);
        ApplyAccent(CoverColorBrush);
    }

    private void ApplyEpisode(ShowEpisodeDto? episode)
    {
        if (episode is null)
        {
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
            EpisodeNumberText.Text = "";
            TitleText.Text = "";
            DurationText.Text = "";
            DateText.Text = "";
            ProgressLane.Visibility = Visibility.Collapsed;
            ProgressFill.Width = 0;
            ApplyCover(null);
            return;
        }

        if (string.IsNullOrEmpty(episode.EpisodeNumberTag))
        {
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
            EpisodeNumberText.Text = "";
        }
        else
        {
            EpisodeNumberChip.Visibility = Visibility.Visible;
            EpisodeNumberText.Text = episode.EpisodeNumberTag;
        }

        TitleText.Text = StripEpisodeNumberPrefix(episode.Title);
        DurationText.Text = episode.DurationOrRemainingText ?? "";
        DateText.Text = episode.DateText ?? "";
        ApplyCover(episode.CoverArtUrl);

        if (episode.HasProgress)
        {
            ProgressLane.Visibility = Visibility.Visible;
            UpdateProgressFill();
        }
        else
        {
            ProgressLane.Visibility = Visibility.Collapsed;
            ProgressFill.Width = 0;
        }
    }

    private void ApplyCover(string? coverArtUrl)
    {
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        CoverPlaceholderIcon.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(coverArtUrl))
            return;

        if (!Uri.TryCreate(coverArtUrl, UriKind.Absolute, out var uri))
            return;

        CoverImage.Visibility = Visibility.Visible;
        // Cover renders at ~88 px in the up-next slot. Decode at 200 px (above
        // 200% DPI render size) keeps text-on-art crisp without allocating the
        // native 640×640 source texture for an 88-px slot.
        CoverImage.Source = new BitmapImage(uri) { DecodePixelWidth = 200 };
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CoverImage.Source = null;
    }

    private static string StripEpisodeNumberPrefix(string title)
    {
        if (string.IsNullOrEmpty(title) || title[0] != '#') return title;
        var i = 1;
        while (i < title.Length && char.IsDigit(title[i])) i++;
        if (i == 1) return title;
        while (i < title.Length && (title[i] == ' ' || title[i] == '-' || title[i] == ':' || title[i] == '–' || title[i] == '—'))
            i++;
        return i >= title.Length ? title : title[i..];
    }

    private void ApplyAccent(Brush? brush)
    {
        var color = brush is SolidColorBrush solid
            ? solid.Color
            : Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

        _accentSolidBrush = new SolidColorBrush(color);

        EpisodeNumberChip.Background = new SolidColorBrush(WithAlpha(color, 0x2E));
        EpisodeNumberText.Foreground = new SolidColorBrush(color);
        ProgressFill.Background = _accentSolidBrush;
        PlayButton.Background = _accentSolidBrush;
        PlayButton.Foreground = new SolidColorBrush(BestForegroundFor(color));

        _hoverBorderBrush = new SolidColorBrush(WithAlpha(color, 0x73));
    }

    private static Color WithAlpha(Color c, byte alpha)
        => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color BestForegroundFor(Color background)
    {
        var luma = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
        return luma > 160
            ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private void ProgressLane_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressFill();

    private void UpdateProgressFill()
    {
        if (Episode is not { } ep) { ProgressFill.Width = 0; return; }
        if (ProgressLane.ActualWidth <= 0) return;
        ProgressFill.Width = Math.Max(0, ProgressLane.ActualWidth * Math.Clamp(ep.Progress, 0, 1));
    }

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
        => SetHoverState(true);

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        => SetHoverState(false);

    private void SetHoverState(bool hovered)
    {
        var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.Offset = new Vector3(0, hovered ? -2f : 0f, 0);

        if (hovered)
        {
            if (_hoverBorderBrush is not null) CardRoot.BorderBrush = _hoverBorderBrush;
            PlayButton.Opacity = 1;
        }
        else
        {
            CardRoot.BorderBrush = _restingBorderBrush;
            PlayButton.Opacity = 0;
        }
    }

    private void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Episode is null) return;
        if (e.OriginalSource is FrameworkElement fe && IsInsidePlayButton(fe)) return;
        ConnectedAnimationHelper.PrepareAnimation(ConnectedAnimationHelper.PodcastEpisodeArt, CoverContainer);
        OpenRequested?.Invoke(this, Episode);
    }

    private bool IsInsidePlayButton(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, PlayButton)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is null) return;
        PlayRequested?.Invoke(this, Episode);
    }
}
