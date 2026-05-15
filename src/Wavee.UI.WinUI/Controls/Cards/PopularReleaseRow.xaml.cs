using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Helpers;
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
    private static ImageCacheService? _imageCache;

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

    public string? CoverImageUrl { get => (string?)GetValue(CoverImageUrlProperty); set => SetValue(CoverImageUrlProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Meta { get => (string)GetValue(MetaProperty); set => SetValue(MetaProperty, value); }
    public int Rank { get => (int)GetValue(RankProperty); set => SetValue(RankProperty, value); }
    public bool IsFeatured { get => (bool)GetValue(IsFeaturedProperty); set => SetValue(IsFeaturedProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush? AccentForegroundBrush { get => (Brush?)GetValue(AccentForegroundBrushProperty); set => SetValue(AccentForegroundBrushProperty, value); }

    public PopularReleaseRow()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (CoverImage != null) CoverImage.Source = null;
    }

    private static void OnCoverImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PopularReleaseRow row) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(e.NewValue as string);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            row.CoverImage.Source = null;
            return;
        }
        _imageCache ??= Ioc.Default.GetService<ImageCacheService>();
        row.CoverImage.Source = _imageCache?.GetOrCreate(httpsUrl, 112);
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
        // AccentBrush DP is preserved on the control for future flexibility,
        // but the chip + featured wash now use the system theme accent again
        // (matching the rest of the app chrome) rather than the artist's
        // palette. No-op handler.
    }

    private static void OnAccentForegroundBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // AccentForegroundBrush DP kept for compatibility; not consumed.
    }

    /// <summary>Refresh the featured-row chrome (highlight wash + border).
    /// Uses a fixed accent-tinted teal so every artist's featured row reads
    /// consistently with the system theme accent.</summary>
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

        // Fixed teal wash — matches the rest of the system-accent treatment.
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, 0x50, 0x90, 0xB8));
        RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x50, 0x90, 0xB8));
        RootBorder.BorderThickness = new Thickness(1);
    }

    private void CardButton_Click(object sender, RoutedEventArgs e) => CardClick?.Invoke(this, e);
}
