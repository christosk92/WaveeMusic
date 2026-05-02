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

namespace Wavee.UI.WinUI.Controls.ShowEpisode;

/// <summary>
/// Cinematic Resume banner that promotes the in-progress episode out of the
/// Listen Next grid into a full-width hero card. Layout: a 1:1 cover image
/// pinned to the left at banner height, with a palette-tinted content area
/// on the right hosting the eyebrow, title, meta line, progress, and Resume
/// button. The whole surface is clickable; <see cref="PlayRequested"/> fires
/// when the user clicks the body or the Resume button.
/// </summary>
public sealed partial class ShowResumeBanner : UserControl
{
    public static readonly DependencyProperty EpisodeProperty =
        DependencyProperty.Register(
            nameof(Episode), typeof(ShowEpisodeDto), typeof(ShowResumeBanner),
            new PropertyMetadata(null, OnEpisodeChanged));

    public static readonly DependencyProperty CoverColorBrushProperty =
        DependencyProperty.Register(
            nameof(CoverColorBrush), typeof(Brush), typeof(ShowResumeBanner),
            new PropertyMetadata(null, OnCoverColorBrushChanged));

    public static readonly DependencyProperty CoverArtUrlProperty =
        DependencyProperty.Register(
            nameof(CoverArtUrl), typeof(string), typeof(ShowResumeBanner),
            new PropertyMetadata(null, OnCoverArtUrlChanged));

    public ShowEpisodeDto? Episode
    {
        get => (ShowEpisodeDto?)GetValue(EpisodeProperty);
        set => SetValue(EpisodeProperty, value);
    }

    /// <summary>The show's palette dominant tone (lifted via <c>TintColorHelper</c>),
    /// used to colour the right-side content area. Pass <c>ViewModel.PaletteCoverColorBrush</c>.</summary>
    public Brush? CoverColorBrush
    {
        get => (Brush?)GetValue(CoverColorBrushProperty);
        set => SetValue(CoverColorBrushProperty, value);
    }

    public string? CoverArtUrl
    {
        get => (string?)GetValue(CoverArtUrlProperty);
        set => SetValue(CoverArtUrlProperty, value);
    }

    public event EventHandler<ShowEpisodeDto>? PlayRequested;

    public ShowResumeBanner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnEpisodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShowResumeBanner b) b.ApplyEpisode(e.NewValue as ShowEpisodeDto);
    }

    private static void OnCoverColorBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShowResumeBanner b) b.ApplyCoverColor(e.NewValue as Brush);
    }

    private static void OnCoverArtUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShowResumeBanner b) b.ApplyCover(e.NewValue as string);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Re-apply on Loaded — DependencyProperty setters can fire before the
        // template is realised when the control is inflated inside a DataTemplate.
        ApplyEpisode(Episode);
        ApplyCoverColor(CoverColorBrush);
        ApplyCover(CoverArtUrl);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Drop the decoded backdrop so a recycled container doesn't pin the surface.
        BackdropImage.Source = null;
    }

    private void ApplyEpisode(ShowEpisodeDto? episode)
    {
        if (episode is null)
        {
            EpisodeNumberText.Text = "";
            EpisodeNumberChip.Visibility = Visibility.Collapsed;
            TitleText.Text = "";
            MetaText.Text = "";
            ProgressFill.Width = 0;
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
        MetaText.Text = BuildMetaLine(episode);

        UpdateProgressFill();
    }

    private static string StripEpisodeNumberPrefix(string title)
    {
        // Most numbered podcasts use "#1 - Brian Redban" / "#42: Lex Fridman".
        // Strip the leading "#N - " so the title block doesn't double up with
        // the EpisodeNumber chip we render to its left.
        if (string.IsNullOrEmpty(title) || title[0] != '#') return title;
        var i = 1;
        while (i < title.Length && char.IsDigit(title[i])) i++;
        if (i == 1) return title;
        while (i < title.Length && (title[i] == ' ' || title[i] == '-' || title[i] == ':' || title[i] == '–' || title[i] == '—'))
            i++;
        return i >= title.Length ? title : title[i..];
    }

    private static string BuildMetaLine(ShowEpisodeDto episode)
    {
        var parts = new System.Collections.Generic.List<string>(3);
        if (!string.IsNullOrEmpty(episode.DurationOrRemainingText))
            parts.Add(episode.DurationOrRemainingText);
        if (!string.IsNullOrEmpty(episode.DateText))
            parts.Add(episode.DateText);
        return string.Join("  ·  ", parts);
    }

    private void ApplyCoverColor(Brush? brush)
    {
        var coverColor = brush is SolidColorBrush solid
            ? solid.Color
            : Color.FromArgb(0xFF, 0x40, 0x40, 0x40);

        // Backplate fills the right side of the banner with the show's tone.
        PaletteBackplate.Background = new SolidColorBrush(coverColor);

        // Soft seam blend — darker version of the cover color fading to transparent
        // across ~40% of the right area so the cover and the palette area don't
        // butt up with a hard vertical line.
        var seam = Darken(coverColor, 0.55);
        OverlayStopLeft.Color = Color.FromArgb(0xCC, seam.R, seam.G, seam.B);
        OverlayStopRight.Color = Color.FromArgb(0x00, seam.R, seam.G, seam.B);

        // Pick text/CTA foreground by luma. Light palettes get black text;
        // dark palettes get white. Same recipe ArtistViewModel uses.
        var luma = (coverColor.R * 299 + coverColor.G * 587 + coverColor.B * 114) / 1000;
        var foreground = luma > 160
            ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var inverse = foreground.R == 0
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

        var fgBrush = new SolidColorBrush(foreground);
        EyebrowText.Foreground = fgBrush;
        TitleText.Foreground = fgBrush;
        MetaText.Foreground = fgBrush;

        // Frosted episode chip — soft contrasting overlay regardless of palette.
        EpisodeNumberChip.Background = new SolidColorBrush(WithAlpha(foreground, 0x2E));
        EpisodeNumberText.Foreground = fgBrush;

        // Progress lane: track is the foreground at low alpha; fill is full-alpha foreground.
        ProgressTrack.Background = new SolidColorBrush(WithAlpha(foreground, 0x33));
        ProgressFill.Background = fgBrush;

        // Resume button — strong CTA. Inverts to ensure it pops off the palette wash.
        ResumeButton.Background = fgBrush;
        ResumeButton.Foreground = new SolidColorBrush(inverse);
    }

    private static Color WithAlpha(Color c, byte alpha)
        => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Darken(Color c, double factor)
    {
        // factor in [0,1] — 0 returns c, 1 returns black.
        factor = Math.Clamp(factor, 0, 1);
        return Color.FromArgb(
            c.A,
            (byte)Math.Round(c.R * (1 - factor)),
            (byte)Math.Round(c.G * (1 - factor)),
            (byte)Math.Round(c.B * (1 - factor)));
    }

    private void ApplyCover(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            BackdropImage.Source = null;
            return;
        }

        try
        {
            BackdropImage.Source = new BitmapImage(new Uri(url));
        }
        catch
        {
            BackdropImage.Source = null;
        }
    }

    private void ProgressLane_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressFill();

    private void UpdateProgressFill()
    {
        if (Episode is not { } ep) { ProgressFill.Width = 0; return; }
        if (ProgressFill.Parent is not FrameworkElement track || track.ActualWidth <= 0) return;
        ProgressFill.Width = Math.Max(0, track.ActualWidth * Math.Clamp(ep.Progress, 0, 1));
    }

    private void BannerRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
        => SetHoverState(true);

    private void BannerRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        => SetHoverState(false);

    private void SetHoverState(bool hovered)
    {
        var rootVisual = ElementCompositionPreview.GetElementVisual(BannerRoot);
        rootVisual.Offset = new Vector3(0, hovered ? -2f : 0f, 0);

        var btnVisual = ElementCompositionPreview.GetElementVisual(ResumeButton);
        btnVisual.CenterPoint = new Vector3(
            (float)(ResumeButton.ActualWidth / 2.0),
            (float)(ResumeButton.ActualHeight / 2.0),
            0);
        btnVisual.Scale = new Vector3(hovered ? 1.04f : 1f, hovered ? 1.04f : 1f, 1f);
    }

    private void BannerRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Episode is null) return;
        // Don't double-fire when the explicit Resume button handles its own click.
        if (e.OriginalSource is FrameworkElement fe && IsInsideResumeButton(fe)) return;
        PlayRequested?.Invoke(this, Episode);
    }

    private bool IsInsideResumeButton(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ResumeButton)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is null) return;
        PlayRequested?.Invoke(this, Episode);
    }
}
