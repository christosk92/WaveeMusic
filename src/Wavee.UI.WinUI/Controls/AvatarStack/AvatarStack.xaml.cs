using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.AvatarStack;

/// <summary>
/// Item carried by <see cref="AvatarStack"/>. <see cref="ImageUrl"/> may be a raw
/// HTTPS URL or a <c>spotify:image:</c> URI — both are routed through
/// <see cref="SpotifyImageHelper.ToHttpsUrl"/>. Null/empty image falls back to
/// initials via <see cref="PersonPicture.DisplayName"/>.
/// </summary>
public sealed record AvatarStackItem(string DisplayName, string? ImageUrl);

/// <summary>
/// Reusable stacked-avatar strip with optional "+N" overflow badge. Replaces
/// the hand-rolled rebuild logic that used to live in <c>PlaylistPage</c>
/// code-behind; consumed there and on the album page header.
///
/// Visual spec: 28 DIP <see cref="PersonPicture"/> per item, 32 DIP outer halo
/// (2 DIP <c>SolidBackgroundFillColorBaseBrush</c> ring), 12 DIP negative-left
/// margin overlap, default <see cref="MaxVisible"/>=4. The "+N" badge re-uses
/// the same halo + a tinted inner disc so the cluster reads as a homogeneous
/// row of circles.
/// </summary>
public sealed partial class AvatarStack : UserControl
{
    private const int AvatarSize = 28;
    private const int RingThickness = 2;
    private const int OuterSize = AvatarSize + 2 * RingThickness;
    private const int Overlap = 12;

    public AvatarStack()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IEnumerable<AvatarStackItem>),
            typeof(AvatarStack),
            new PropertyMetadata(null, OnItemsChanged));

    /// <summary>
    /// Avatar items to render (truncated by <see cref="MaxVisible"/> internally).
    /// Set to <c>null</c> or empty to hide the strip.
    /// </summary>
    public IEnumerable<AvatarStackItem>? Items
    {
        get => (IEnumerable<AvatarStackItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty OverflowCountProperty =
        DependencyProperty.Register(
            nameof(OverflowCount),
            typeof(int),
            typeof(AvatarStack),
            new PropertyMetadata(0, OnAnyChanged));

    /// <summary>
    /// Number to display in the trailing "+N" badge. <c>0</c> hides the badge.
    /// Independent of <see cref="Items"/>.Count so consumers can show a small
    /// fixed avatar set with a separate "more contributors" tally — for example,
    /// the album header shows billed-artist avatars plus a count of additional
    /// distinct artists pulled from the tracklist.
    /// </summary>
    public int OverflowCount
    {
        get => (int)GetValue(OverflowCountProperty);
        set => SetValue(OverflowCountProperty, value);
    }

    public static readonly DependencyProperty MaxVisibleProperty =
        DependencyProperty.Register(
            nameof(MaxVisible),
            typeof(int),
            typeof(AvatarStack),
            new PropertyMetadata(4, OnAnyChanged));

    /// <summary>
    /// Maximum number of avatars rendered before truncation. Items beyond this
    /// limit are dropped from the strip; the consumer decides whether to count
    /// them into <see cref="OverflowCount"/>.
    /// </summary>
    public int MaxVisible
    {
        get => (int)GetValue(MaxVisibleProperty);
        set => SetValue(MaxVisibleProperty, value);
    }

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AvatarStack)d).Rebuild();

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctl = (AvatarStack)d;
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

        var items = Items?.ToList() ?? new List<AvatarStackItem>();
        if (items.Count == 0 && OverflowCount <= 0) return;

        var visibleCount = Math.Min(items.Count, Math.Max(0, MaxVisible));

        // Page-base brush gives the ring the same colour as the surface behind
        // the panel — so the halo reads as transparent space between stacked
        // avatars (matches Spotify's stacked-profile-picture pattern).
        var ringBrush = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        for (var i = 0; i < visibleCount; i++)
        {
            var item = items[i];
            HostStack.Children.Add(BuildAvatarFrame(item, ringBrush, isFirst: i == 0));
        }

        if (OverflowCount > 0)
        {
            HostStack.Children.Add(BuildOverflowBadge(ringBrush, OverflowCount, isFirst: visibleCount == 0));
        }
    }

    private static Border BuildAvatarFrame(AvatarStackItem item, Brush ringBrush, bool isFirst)
    {
        var person = new PersonPicture
        {
            Width = AvatarSize,
            Height = AvatarSize,
            DisplayName = item.DisplayName,
        };

        if (!string.IsNullOrEmpty(item.ImageUrl))
        {
            var httpsUrl = SpotifyImageHelper.ToHttpsUrl(item.ImageUrl) ?? item.ImageUrl;
            if (Uri.TryCreate(httpsUrl, UriKind.Absolute, out var avatarUri))
            {
                person.ProfilePicture = new BitmapImage(avatarUri)
                {
                    DecodePixelWidth = AvatarSize * 2
                };
            }
        }

        var frame = new Border
        {
            Width = OuterSize,
            Height = OuterSize,
            CornerRadius = new CornerRadius(OuterSize / 2.0),
            Background = ringBrush,
            Padding = new Thickness(RingThickness),
            Margin = isFirst ? new Thickness(0) : new Thickness(-Overlap, 0, 0, 0),
            Child = person,
        };
        ToolTipService.SetToolTip(frame, item.DisplayName);
        return frame;
    }

    private static Border BuildOverflowBadge(Brush ringBrush, int overflow, bool isFirst)
    {
        return new Border
        {
            Width = OuterSize,
            Height = OuterSize,
            CornerRadius = new CornerRadius(OuterSize / 2.0),
            Background = ringBrush,
            Padding = new Thickness(RingThickness),
            Margin = isFirst ? new Thickness(0) : new Thickness(-Overlap, 0, 0, 0),
            Child = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(AvatarSize / 2.0),
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
