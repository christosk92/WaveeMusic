using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Reusable horizontal facepile of overlapping avatars with a ring halo and an
/// optional "+N more" overflow chip. Mirrors the visual recipe that lived
/// inline in PlaylistPage.RebuildCollaboratorStack so contributors and podcast
/// reply-author previews share one implementation.
/// </summary>
public sealed class StackedAvatars : UserControl
{
    private readonly StackPanel _host;
    private INotifyCollectionChanged? _observed;

    public StackedAvatars()
    {
        _host = new StackPanel { Orientation = Orientation.Horizontal };
        Content = _host;
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(StackedAvatars),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty MaxVisibleProperty = DependencyProperty.Register(
        nameof(MaxVisible), typeof(int), typeof(StackedAvatars),
        new PropertyMetadata(4, OnLayoutPropertyChanged));

    public static readonly DependencyProperty AvatarSizeProperty = DependencyProperty.Register(
        nameof(AvatarSize), typeof(double), typeof(StackedAvatars),
        new PropertyMetadata(28d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(StackedAvatars),
        new PropertyMetadata(2d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty OverlapProperty = DependencyProperty.Register(
        nameof(Overlap), typeof(double), typeof(StackedAvatars),
        new PropertyMetadata(12d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(StackedAvatars),
        new PropertyMetadata(null, OnLayoutPropertyChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int MaxVisible
    {
        get => (int)GetValue(MaxVisibleProperty);
        set => SetValue(MaxVisibleProperty, value);
    }

    public double AvatarSize
    {
        get => (double)GetValue(AvatarSizeProperty);
        set => SetValue(AvatarSizeProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public double Overlap
    {
        get => (double)GetValue(OverlapProperty);
        set => SetValue(OverlapProperty, value);
    }

    /// <summary>
    /// Brush used for the ring halo around each avatar. When unset the control
    /// uses the page-base surface brush so the halo blends into the parent.
    /// </summary>
    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackedAvatars self) return;

        if (self._observed is not null)
        {
            self._observed.CollectionChanged -= self.OnSourceCollectionChanged;
            self._observed = null;
        }

        if (e.NewValue is INotifyCollectionChanged incc)
        {
            self._observed = incc;
            incc.CollectionChanged += self.OnSourceCollectionChanged;
        }

        self.Rebuild();
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StackedAvatars self) self.Rebuild();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _host.Children.Clear();

        if (ItemsSource is null) return;

        var items = Materialize(ItemsSource);
        if (items.Count == 0) return;

        var avatarSize = AvatarSize;
        var ringThickness = RingThickness;
        var outerSize = avatarSize + 2 * ringThickness;
        var overlap = Overlap;
        var visible = Math.Min(items.Count, Math.Max(1, MaxVisible));
        var hasOverflow = items.Count > visible;
        var ringBrush = RingBrush
            ?? (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        for (int i = 0; i < visible; i++)
        {
            var item = items[i];
            var person = new PersonPicture
            {
                Width = avatarSize,
                Height = avatarSize,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? "?" : item.DisplayName
            };

            if (!string.IsNullOrEmpty(item.AvatarUrl))
            {
                var httpsUrl = SpotifyImageHelper.ToHttpsUrl(item.AvatarUrl) ?? item.AvatarUrl;
                if (Uri.TryCreate(httpsUrl, UriKind.Absolute, out var avatarUri))
                {
                    person.ProfilePicture = new BitmapImage(avatarUri)
                    {
                        DecodePixelWidth = (int)(avatarSize * 2)
                    };
                }
            }

            var frame = new Border
            {
                Width = outerSize,
                Height = outerSize,
                CornerRadius = new CornerRadius(outerSize / 2.0),
                Background = ringBrush,
                Padding = new Thickness(ringThickness),
                Margin = i == 0 ? new Thickness(0) : new Thickness(-overlap, 0, 0, 0),
                Child = person
            };
            ToolTipService.SetToolTip(frame, item.DisplayName);
            _host.Children.Add(frame);
        }

        if (hasOverflow)
        {
            var overflowText = new TextBlock
            {
                Text = "+" + (items.Count - visible),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            var inner = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(avatarSize / 2.0),
                Child = overflowText
            };

            var more = new Border
            {
                Width = outerSize,
                Height = outerSize,
                CornerRadius = new CornerRadius(outerSize / 2.0),
                Background = ringBrush,
                Padding = new Thickness(ringThickness),
                Margin = new Thickness(-overlap, 0, 0, 0),
                Child = inner
            };
            _host.Children.Add(more);
        }
    }

    private static IReadOnlyList<StackedAvatarItem> Materialize(IEnumerable source)
    {
        var list = new List<StackedAvatarItem>();
        foreach (var raw in source)
        {
            if (raw is StackedAvatarItem item)
            {
                list.Add(item);
            }
        }
        return list;
    }
}
