using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Compact "Fans also like" pill — 40 px capsule with a 28 px circular avatar
/// and a name. Lives in the AlbumPage footer below the music-video strip on
/// short releases (≤ 2 tracks). Designed to be reusable for the Search page +
/// Artist page "Related artists" surfaces later.
/// </summary>
public sealed partial class ArtistPillCard : UserControl
{
    public static readonly DependencyProperty ArtistUriProperty =
        DependencyProperty.Register(nameof(ArtistUri), typeof(string), typeof(ArtistPillCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ArtistNameProperty =
        DependencyProperty.Register(nameof(ArtistName), typeof(string), typeof(ArtistPillCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(ArtistPillCard),
            new PropertyMetadata(null, OnImageUrlChanged));

    public string? ArtistUri
    {
        get => (string?)GetValue(ArtistUriProperty);
        set => SetValue(ArtistUriProperty, value);
    }

    public string? ArtistName
    {
        get => (string?)GetValue(ArtistNameProperty);
        set => SetValue(ArtistNameProperty, value);
    }

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public ArtistPillCard()
    {
        InitializeComponent();
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ArtistPillCard card) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(e.NewValue as string);
        // PersonPicture handles its own initials fallback when ProfilePicture
        // is null, so we don't need to flip placeholder visibility. Decode at
        // 56 px (2× the 28 px avatar) so the bitmap doesn't oversample.
        card.Avatar.ProfilePicture = string.IsNullOrEmpty(httpsUrl)
            ? null
            : new BitmapImage(new System.Uri(httpsUrl))
              {
                  DecodePixelWidth = 56,
                  DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical
              };
    }

    private void PillRoot_Click(object sender, RoutedEventArgs e)
    {
        var uri = ArtistUri;
        if (string.IsNullOrEmpty(uri)) return;
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenArtist(uri, ArtistName ?? "Artist", openInNewTab);
    }
}
