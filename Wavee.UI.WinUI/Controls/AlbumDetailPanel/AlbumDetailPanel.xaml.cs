using System;
using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.AlbumDetailPanel;

/// <summary>
/// Expandable inline album detail panel (Apple Music style).
/// Shows album info, action buttons, track list, and album art with color-matched gradient.
/// Triangle notch points up at the source card. Animates in/out via implicit animations.
/// </summary>
public sealed partial class AlbumDetailPanel : UserControl
{
    public static readonly DependencyProperty AlbumProperty =
        DependencyProperty.Register(nameof(Album), typeof(ArtistReleaseVm), typeof(AlbumDetailPanel),
            new PropertyMetadata(null, OnAlbumChanged));

    public static readonly DependencyProperty TracksProperty =
        DependencyProperty.Register(nameof(Tracks), typeof(IEnumerable), typeof(AlbumDetailPanel),
            new PropertyMetadata(null, OnTracksChanged));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(AlbumDetailPanel),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ColorHexProperty =
        DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(AlbumDetailPanel),
            new PropertyMetadata(null, OnColorHexChanged));

    public static readonly DependencyProperty NotchOffsetXProperty =
        DependencyProperty.Register(nameof(NotchOffsetX), typeof(double), typeof(AlbumDetailPanel),
            new PropertyMetadata(0.0, OnNotchOffsetXChanged));

    /// <summary>The album to display.</summary>
    public ArtistReleaseVm? Album
    {
        get => (ArtistReleaseVm?)GetValue(AlbumProperty);
        set => SetValue(AlbumProperty, value);
    }

    /// <summary>Track list items source.</summary>
    public IEnumerable? Tracks
    {
        get => (IEnumerable?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    /// <summary>Whether tracks are still loading.</summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>Hex color string (e.g. "#2A4B7C") extracted from album art. Drives gradient background.</summary>
    public string? ColorHex
    {
        get => (string?)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    /// <summary>Horizontal offset (px) for the triangle notch, measured from the panel's left edge to the notch center.</summary>
    public double NotchOffsetX
    {
        get => (double)GetValue(NotchOffsetXProperty);
        set => SetValue(NotchOffsetXProperty, value);
    }

    /// <summary>Raised when the close button is clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when play is clicked.</summary>
    public event EventHandler? PlayRequested;

    /// <summary>Raised when shuffle is clicked.</summary>
    public event EventHandler? ShuffleRequested;

    private const double NotchWidth = 28;

    public AlbumDetailPanel()
    {
        InitializeComponent();
        OuterGrid.SizeChanged += OuterGrid_SizeChanged;
    }

    private void OuterGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Maintain square aspect ratio for the image area, capped at 45% of panel width
        var height = e.NewSize.Height;
        var maxWidth = e.NewSize.Width * 0.45;
        ImageArea.Width = Math.Min(height, maxWidth);
    }

    private static void OnAlbumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        var album = e.NewValue as ArtistReleaseVm;

        if (album != null)
        {
            panel.AlbumNameText.Text = album.Name ?? "";
            panel.TypeText.Text = album.Type ?? "ALBUM";
            panel.YearText.Text = album.Year.ToString();
            panel.FooterYearText.Text = album.Year.ToString();

            // Load album art
            if (!string.IsNullOrEmpty(album.ImageUrl))
            {
                var url = SpotifyImageHelper.ToHttpsUrl(album.ImageUrl);
                if (!string.IsNullOrEmpty(url))
                {
                    panel.ArtBrush.ImageSource = new BitmapImage(new Uri(url));
                }
            }

            // Apply color if not yet set (fallback to theme default)
            if (string.IsNullOrEmpty(panel.ColorHex))
            {
                panel.ApplyThemeDefaultColor();
            }
        }
    }

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        var hex = e.NewValue as string;

        if (!string.IsNullOrEmpty(hex))
        {
            var raw = ParseHexColor(hex);
            var isDark = panel.ActualTheme == ElementTheme.Dark;
            var color = ToneDown(raw, isDark);

            panel.ColorBackground.Background = new SolidColorBrush(color);
            panel.ArtGradientStop.Color = color;
            panel.NotchTriangle.Fill = new SolidColorBrush(color);
        }
        else
        {
            panel.ApplyThemeDefaultColor();
        }
    }

    private static void OnNotchOffsetXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        var offset = (double)e.NewValue;
        // Center the notch on the offset point
        panel.NotchTranslate.X = offset - (NotchWidth / 2);
    }

    private void ApplyThemeDefaultColor()
    {
        var isDark = ActualTheme == ElementTheme.Dark;
        var bgColor = isDark
            ? Windows.UI.Color.FromArgb(255, 30, 30, 35)
            : Windows.UI.Color.FromArgb(255, 230, 230, 235);

        ColorBackground.Background = new SolidColorBrush(bgColor);
        ArtGradientStop.Color = bgColor;
        NotchTriangle.Fill = new SolidColorBrush(bgColor);
    }

    /// <summary>
    /// Blends the extracted color toward the theme base so it's not overly bright/saturated.
    /// Dark mode: blend 55% toward dark gray. Light mode: blend 50% toward light gray.
    /// </summary>
    private static Windows.UI.Color ToneDown(Windows.UI.Color color, bool isDark)
    {
        var factor = isDark ? 0.45 : 0.50; // how much of the original color to keep
        var baseR = isDark ? 25 : 235;
        var baseG = isDark ? 25 : 235;
        var baseB = isDark ? 30 : 240;

        return Windows.UI.Color.FromArgb(255,
            (byte)(color.R * factor + baseR * (1 - factor)),
            (byte)(color.G * factor + baseG * (1 - factor)),
            (byte)(color.B * factor + baseB * (1 - factor)));
    }

    private static Windows.UI.Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        if (hex.Length == 8)
        {
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        }
        return Windows.UI.Color.FromArgb(255, 30, 30, 35);
    }

    private static void OnTracksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlbumDetailPanel)d;
        panel.TrackRepeater.ItemsSource = e.NewValue as IEnumerable;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void PlayButton_Click(object sender, RoutedEventArgs e)
        => PlayRequested?.Invoke(this, EventArgs.Empty);

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        => ShuffleRequested?.Invoke(this, EventArgs.Empty);

    private void ViewAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        if (Album == null) return;
        var param = new ContentNavigationParameter
        {
            Uri = Album.Uri ?? Album.Id,
            Title = Album.Name,
            ImageUrl = Album.ImageUrl
        };
        NavigationHelpers.OpenAlbum(param, Album.Name ?? "Album");
    }
}
