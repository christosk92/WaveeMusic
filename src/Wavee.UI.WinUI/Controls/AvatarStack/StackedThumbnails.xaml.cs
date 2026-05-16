using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls.Imaging;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.AvatarStack;

/// <summary>
/// Item carried by <see cref="StackedThumbnails"/>. <see cref="ImageUrl"/> may
/// be a raw HTTPS URL, a <c>spotify:image:</c> URI, or a
/// <c>wavee-artwork://</c> URI — all routed through the shared
/// <see cref="CompositionImage"/> control. Null/empty falls back to the
/// music-note placeholder.
/// </summary>
public sealed record StackedThumbnailItem(string? ImageUrl, string? Tooltip = null);

/// <summary>
/// Square-tile cousin of <see cref="AvatarStack"/>. Same overlap geometry
/// (28-DIP tiles, 12-DIP negative-left margin, 2-DIP halo ring), but each
/// tile is a rounded-rect with album art instead of a circular
/// <c>PersonPicture</c>. Uses <see cref="CompositionImage"/> per tile so
/// images go through the app's GPU surface cache (same path as the album-art
/// thumbnails in TrackItem); a music-note placeholder sits behind each
/// surface so tiles with no <c>ImageUrl</c> read as art-less rather than
/// empty squares.
/// </summary>
public sealed partial class StackedThumbnails : UserControl
{
    private const int TileSize = 28;
    private const int RingThickness = 2;
    private const int OuterSize = TileSize + 2 * RingThickness;
    private const int Overlap = 12;
    private const double TileCornerRadius = 4;

    public StackedThumbnails()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IEnumerable<StackedThumbnailItem>),
            typeof(StackedThumbnails),
            new PropertyMetadata(null, OnItemsChanged));

    public IEnumerable<StackedThumbnailItem>? Items
    {
        get => (IEnumerable<StackedThumbnailItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty OverflowCountProperty =
        DependencyProperty.Register(
            nameof(OverflowCount),
            typeof(int),
            typeof(StackedThumbnails),
            new PropertyMetadata(0, OnAnyChanged));

    public int OverflowCount
    {
        get => (int)GetValue(OverflowCountProperty);
        set => SetValue(OverflowCountProperty, value);
    }

    public static readonly DependencyProperty MaxVisibleProperty =
        DependencyProperty.Register(
            nameof(MaxVisible),
            typeof(int),
            typeof(StackedThumbnails),
            new PropertyMetadata(4, OnAnyChanged));

    public int MaxVisible
    {
        get => (int)GetValue(MaxVisibleProperty);
        set => SetValue(MaxVisibleProperty, value);
    }

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StackedThumbnails)d).Rebuild();

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctl = (StackedThumbnails)d;
        if (e.OldValue is INotifyCollectionChanged oldNcc)
            oldNcc.CollectionChanged -= ctl.OnItemsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += ctl.OnItemsCollectionChanged;
        ctl.Rebuild();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Rebuild();

    private void Rebuild()
    {
        HostStack.Children.Clear();

        var items = Items?.ToList() ?? new List<StackedThumbnailItem>();
        if (items.Count == 0 && OverflowCount <= 0) return;

        var ringBrush = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        var visibleCount = Math.Min(items.Count, Math.Max(0, MaxVisible));
        for (var i = 0; i < visibleCount; i++)
        {
            HostStack.Children.Add(BuildTileFrame(items[i], ringBrush, isFirst: i == 0));
        }

        if (OverflowCount > 0)
        {
            HostStack.Children.Add(BuildOverflowBadge(ringBrush, OverflowCount, isFirst: visibleCount == 0));
        }
    }

    private static Border BuildTileFrame(StackedThumbnailItem item, Brush ringBrush, bool isFirst)
    {
        // Two-layer inner tile: placeholder glyph at the back, CompositionImage
        // on top. CompositionImage paints transparent until its surface loads,
        // so the placeholder shows through for art-less tiles AND during the
        // brief load window for net-fetched art.
        var placeholderGlyph = new FontIcon
        {
            FontSize = 14,
            Opacity = 0.55,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Glyph = FluentGlyphs.MusicNote,
        };

        var compositionImage = new CompositionImage
        {
            ImageUrl = item.ImageUrl,
            DecodePixelSize = TileSize * 2,
            Stretch = Stretch.UniformToFill,
            CornerRadius = new CornerRadius(TileCornerRadius),
        };

        var inner = new Grid
        {
            Width = TileSize,
            Height = TileSize,
        };
        inner.Children.Add(placeholderGlyph);
        inner.Children.Add(compositionImage);

        var imageHost = new Border
        {
            Width = TileSize,
            Height = TileSize,
            CornerRadius = new CornerRadius(TileCornerRadius),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = inner,
        };

        var frame = new Border
        {
            Width = OuterSize,
            Height = OuterSize,
            CornerRadius = new CornerRadius(TileCornerRadius + RingThickness),
            Background = ringBrush,
            Padding = new Thickness(RingThickness),
            Margin = isFirst ? new Thickness(0) : new Thickness(-Overlap, 0, 0, 0),
            Child = imageHost,
        };
        if (!string.IsNullOrEmpty(item.Tooltip))
            ToolTipService.SetToolTip(frame, item.Tooltip);
        return frame;
    }

    private static Border BuildOverflowBadge(Brush ringBrush, int overflow, bool isFirst)
    {
        return new Border
        {
            Width = OuterSize,
            Height = OuterSize,
            CornerRadius = new CornerRadius(TileCornerRadius + RingThickness),
            Background = ringBrush,
            Padding = new Thickness(RingThickness),
            Margin = isFirst ? new Thickness(0) : new Thickness(-Overlap, 0, 0, 0),
            Child = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(TileCornerRadius),
                Child = new TextBlock
                {
                    Text = "+" + overflow,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                }
            },
        };
    }
}
