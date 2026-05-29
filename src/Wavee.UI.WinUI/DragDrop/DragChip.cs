using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Builds the small rounded "pill" that follows the cursor during a drag —
/// art thumbnail + title (+ "+N" badge for multi-item drags) — and feeds it into
/// <see cref="DragStartingEventArgs.DragUI"/>. Payload-driven (not an element
/// screenshot), so every drag source produces a consistent compact chip that
/// doesn't obscure the drop target.
/// </summary>
public static class DragChip
{
    private const double ChipMaxWidth = 240;
    private const double ThumbSize = 36;
    private const double CornerRadius = 8;

    /// <summary>
    /// Render a chip for <paramref name="payload"/> and assign it to the drag UI.
    /// Held under a deferral so WinUI waits for the bitmap (incl. async art load).
    /// Best-effort: any failure leaves WinUI's default visual.
    /// </summary>
    public static async Task ApplyAsync(DragStartingEventArgs args, IDragPayload payload, XamlRoot? xamlRoot)
    {
        var deferral = args.GetDeferral();
        // WinUI 3 RenderTargetBitmap only captures elements connected to the live
        // visual tree, so the chip is hosted in a transient off-screen Popup for the
        // duration of the render, then torn down. (An unparented element renders blank.)
        Popup? host = null;
        try
        {
            var chip = BuildChip(payload, out var thumbImage, out var imageUrl);

            host = new Popup
            {
                // Park far off-screen so it never flashes; IsHitTestVisible off.
                HorizontalOffset = -10000,
                VerticalOffset = -10000,
                IsHitTestVisible = false,
                Child = chip,
            };
            if (xamlRoot is not null) host.XamlRoot = xamlRoot;
            host.IsOpen = true;

            // Kick off art load (if any) and wait briefly so the rendered bitmap
            // includes the cover instead of an empty thumb. Falls back to the glyph.
            if (thumbImage is not null && !string.IsNullOrEmpty(imageUrl))
                await LoadThumbAsync(thumbImage, imageUrl!);

            chip.UpdateLayout();
            var w = (int)Math.Ceiling(chip.ActualWidth > 0 ? chip.ActualWidth : chip.DesiredSize.Width);
            var h = (int)Math.Ceiling(chip.ActualHeight > 0 ? chip.ActualHeight : chip.DesiredSize.Height);
            if (w <= 0 || h <= 0) return;

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(chip, w, h);
            var pixels = await rtb.GetPixelsAsync();
            var bmp = SoftwareBitmap.CreateCopyFromBuffer(
                pixels, BitmapPixelFormat.Bgra8, rtb.PixelWidth, rtb.PixelHeight, BitmapAlphaMode.Premultiplied);

            // Anchor below-right of the cursor so the chip trails the pointer and
            // leaves the row the user is reaching toward unobscured.
            args.DragUI.SetContentFromSoftwareBitmap(bmp, new Point(-16, -8));
        }
        catch
        {
            // Best-effort — WinUI falls back to its default drag visual.
        }
        finally
        {
            if (host is not null) { host.IsOpen = false; host.Child = null; }
            deferral.Complete();
        }
    }

    private static Border BuildChip(IDragPayload payload, out Image? thumbImage, out string? imageUrl)
    {
        imageUrl = payload.ImageUrl;
        var isCircle = payload.Kind == DragPayloadKind.Artist; // artists render round art

        var root = new Grid { VerticalAlignment = VerticalAlignment.Center };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Thumbnail (art or kind glyph) ──
        var thumbHost = new Grid
        {
            Width = ThumbSize,
            Height = ThumbSize,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thumbBg = new Border
        {
            CornerRadius = new CornerRadius(isCircle ? ThumbSize / 2 : 4),
            Background = Brush("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
        };
        thumbHost.Children.Add(thumbBg);

        if (!string.IsNullOrEmpty(imageUrl))
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            var clip = new Border
            {
                CornerRadius = new CornerRadius(isCircle ? ThumbSize / 2 : 4),
                Child = img,
            };
            thumbHost.Children.Add(clip);
            thumbImage = img;
        }
        else
        {
            thumbImage = null;
            thumbHost.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = GlyphFor(payload.Kind),
                FontSize = 16,
                Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetColumn(thumbHost, 0);
        root.Children.Add(thumbHost);

        // ── Title ──
        var title = new TextBlock
        {
            Text = string.IsNullOrEmpty(payload.DisplayTitle) ? "Item" : payload.DisplayTitle!,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextFillColorPrimaryBrush", Colors.White),
        };
        Grid.SetColumn(title, 1);
        root.Children.Add(title);

        // ── Count badge (multi-item drags) ──
        if (payload.ItemCount > 1)
        {
            var badge = new Border
            {
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(7, 2, 7, 2),
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brush("AccentFillColorDefaultBrush", Color.FromArgb(0xFF, 0x1D, 0xB9, 0x54)),
                Child = new TextBlock
                {
                    Text = payload.ItemCount.ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("TextOnAccentFillColorPrimaryBrush", Colors.White),
                },
            };
            Grid.SetColumn(badge, 2);
            root.Children.Add(badge);
        }

        // NB: RenderTargetBitmap cannot capture acrylic/backdrop brushes or
        // ThemeShadow — they render flat/empty, which is what made the chip look
        // like a dull plain box. Use an opaque solid fill so the captured bitmap
        // is crisp.
        return new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(CornerRadius),
            Background = new SolidColorBrush(SolidColor("SolidBackgroundFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B))),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Child = root,
        };
    }

    /// <summary>Resolve a theme brush's color, or a fallback. Acrylic brushes are
    /// flattened to their tint so the chip stays opaque (RTB-capturable).</summary>
    private static Color SolidColor(string themeKey, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(themeKey, out var v))
        {
            if (v is SolidColorBrush scb) return scb.Color;
            if (v is AcrylicBrush ab) return ab.TintColor;
        }
        return fallback;
    }

    private static async Task LoadThumbAsync(Image img, string url)
    {
        // Card ImageUrls may be raw spotify:image:... URIs; normalise to the CDN
        // https URL the same way ContentCard does, else BitmapImage can't load it.
        var resolved = Wavee.UI.Helpers.SpotifyImageHelper.ToHttpsUrl(url) ?? url;
        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var uri)) return;
        var tcs = new TaskCompletionSource<bool>();
        var bmp = new BitmapImage
        {
            DecodePixelWidth = (int)ThumbSize * 2,
            DecodePixelType = DecodePixelType.Logical,
        };
        void OnOpened(object? s, RoutedEventArgs e) => tcs.TrySetResult(true);
        void OnFailed(object? s, ExceptionRoutedEventArgs e) => tcs.TrySetResult(false);
        bmp.ImageOpened += OnOpened;
        bmp.ImageFailed += OnFailed;
        img.Source = bmp;
        bmp.UriSource = uri;

        // Don't stall the drag if the CDN is slow — cap the wait; glyph/empty thumb otherwise.
        var done = await Task.WhenAny(tcs.Task, Task.Delay(220));
        bmp.ImageOpened -= OnOpened;
        bmp.ImageFailed -= OnFailed;
        _ = done;
    }

    private static string GlyphFor(DragPayloadKind kind) => kind switch
    {
        DragPayloadKind.Album => FluentGlyphs.Album,
        DragPayloadKind.Artist => FluentGlyphs.Artist,
        DragPayloadKind.Playlist => FluentGlyphs.Playlist,
        DragPayloadKind.SidebarItem => FluentGlyphs.Playlist, // a dragged sidebar row is a playlist/folder
        DragPayloadKind.Show => FluentGlyphs.TvShow,
        _ => FluentGlyphs.MusicNote,
    };

    private static Brush Brush(string themeKey, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(themeKey, out var v) && v is Brush b)
            return b;
        return new SolidColorBrush(fallback);
    }
}
