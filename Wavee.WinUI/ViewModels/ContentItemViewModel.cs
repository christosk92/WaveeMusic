using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Models.Dto;
using Windows.UI;
using ColorHelper = Wavee.WinUI.Helpers.ColorHelper;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// Base ViewModel for all content items (albums, playlists, artists, shows, episodes)
/// </summary>
public abstract partial class ContentItemViewModel : ObservableObject
{
    private Color? _cachedShadowColor;

    [ObservableProperty]
    public partial string Uri { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial BitmapImage? Image { get; set; }

    [ObservableProperty]
    public partial string? Subtitle { get; set; }

    [ObservableProperty]
    public partial bool IsCircularImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShadowColor))]
    public partial SolidColorBrush? DominantColor { get; set; }

    partial void OnDominantColorChanged(SolidColorBrush? value)
    {
        // Recompute cached shadow color when dominant color changes
        _cachedShadowColor = null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    public partial bool IsPlaying { get; set; }

    /// <summary>
    /// Gets the play/pause icon glyph based on current playback state.
    /// Returns pause icon if playing, play icon if not playing.
    /// </summary>
    public string PlayPauseIcon => IsPlaying ? "\uE769" : "\uE768"; // Pause : Play

    /// <summary>
    /// Gets the corner radius for the image based on content type.
    /// Artists use circular images (radius 999), others use rounded corners (radius 8).
    /// </summary>
    public CornerRadius ImageCornerRadius => IsCircularImage ? new CornerRadius(999) : new CornerRadius(8,8,0,0);

    public Thickness ImageMargin => IsCircularImage ? new Thickness() : new Thickness(-12, -12, -12, 0);

    /// <summary>
    /// Gets the theme-aware shadow color based on the dominant color.
    /// Dark mode uses vibrant colors, light mode uses desaturated softer colors.
    /// The value is cached to avoid repeated computation during XAML binding.
    /// </summary>
    public Color ShadowColor
    {
        get
        {
            // Return cached value if available
            if (_cachedShadowColor.HasValue)
            {
                return _cachedShadowColor.Value;
            }

            System.Diagnostics.Debug.WriteLine($"[ShadowColor] Computing shadow color for Name={Name}");

            if (DominantColor?.Color == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ShadowColor] DominantColor is null, returning Black");
                _cachedShadowColor = Colors.Black;
                return _cachedShadowColor.Value;
            }

            var baseColor = DominantColor.Color;
            System.Diagnostics.Debug.WriteLine($"[ShadowColor] baseColor: A={baseColor.A}, R={baseColor.R}, G={baseColor.G}, B={baseColor.B}");

            // If the color is transparent, it means no color was extracted, so use black
            if (baseColor.A == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[ShadowColor] baseColor is transparent (A=0), returning Black");
                _cachedShadowColor = Colors.Black;
                return _cachedShadowColor.Value;
            }

            // Detect current theme
            var isDarkTheme = ElementThemeHelper.IsDarkTheme();
            System.Diagnostics.Debug.WriteLine($"[ShadowColor] isDarkTheme={isDarkTheme}");

            Color result;
            if (isDarkTheme)
            {
                // Dark mode: Keep vibrant colors
                result = baseColor;
            }
            else
            {
                // Light mode: Desaturate and lighten
                result = ColorHelper.DesaturateColor(baseColor, 0.6);
            }

            System.Diagnostics.Debug.WriteLine($"[ShadowColor] Cached and returning color: A={result.A}, R={result.R}, G={result.G}, B={result.B}");
            _cachedShadowColor = result;
            return result;
        }
    }

    /// <summary>
    /// Gets whether this is an Album item (for conditional visibility in UI).
    /// </summary>
    public virtual bool IsAlbum => this is AlbumViewModel;

    /// <summary>
    /// Gets whether this is a Playlist item (for conditional visibility in UI).
    /// </summary>
    public virtual bool IsPlaylist => this is PlaylistViewModel;

    /// <summary>
    /// Gets whether this is an Artist item (for conditional visibility in UI).
    /// </summary>
    public virtual bool IsArtist => this is ArtistViewModel;

    /// <summary>
    /// Gets whether this is a Show (podcast) item (for conditional visibility in UI).
    /// </summary>
    public virtual bool IsShow => this is ShowViewModel;

    /// <summary>
    /// Gets whether this item can be added to a playlist (albums and playlists).
    /// </summary>
    public virtual bool CanAddToPlaylist => IsAlbum || IsPlaylist;

    /// <summary>
    /// Gets the label for Follow/Unfollow button (for artists). Override in ArtistViewModel.
    /// </summary>
    public virtual string FollowArtistLabel => "Follow Artist";

    /// <summary>
    /// Virtual command for artist-specific "Follow" action. Implemented in ArtistViewModel.
    /// </summary>
    [RelayCommand]
    protected virtual void FollowArtist()
    {
        // Default implementation does nothing - override in ArtistViewModel
    }

    /// <summary>
    /// Virtual command for artist-specific "Radio" action. Implemented in ArtistViewModel.
    /// </summary>
    [RelayCommand]
    protected virtual void GoToArtistRadio()
    {
        // Default implementation does nothing - override in ArtistViewModel
    }

    /// <summary>
    /// Virtual command for album-specific "Go to Artist" action. Implemented in AlbumViewModel.
    /// </summary>
    [RelayCommand]
    protected virtual void GoToArtist()
    {
        // Default implementation does nothing - override in AlbumViewModel
    }

    /// <summary>
    /// Virtual command for album-specific "Add to Playlist" action. Implemented in AlbumViewModel.
    /// </summary>
    [RelayCommand]
    protected virtual void AddToPlaylist()
    {
        // Default implementation does nothing - override in AlbumViewModel
    }

    /// <summary>
    /// Command to navigate to the content item
    /// </summary>
    [RelayCommand]
    private void Navigate()
    {
        var webUrl = SpotifyUriHelper.UriToWebUrl(Uri);
        if (!string.IsNullOrEmpty(webUrl))
        {
            // TODO: Implement navigation logic
            System.Diagnostics.Debug.WriteLine($"Navigate to: {webUrl}");
        }
    }

    /// <summary>
    /// Command to play or pause the content item
    /// </summary>
    [RelayCommand]
    private void PlayPause()
    {
        // TODO: Integrate with PlaybackService
        System.Diagnostics.Debug.WriteLine($"PlayPause: {Name} (Uri: {Uri})");
        // Mock toggle for now
        IsPlaying = !IsPlaying;
    }

    /// <summary>
    /// Command to add the content item to library or playlist
    /// </summary>
    [RelayCommand]
    private void AddToLibrary()
    {
        // TODO: Implement library/playlist add logic
        System.Diagnostics.Debug.WriteLine($"AddToLibrary: {Name} (Uri: {Uri})");
    }

    /// <summary>
    /// Command to play content now (replace queue)
    /// </summary>
    [RelayCommand]
    private void PlayNow()
    {
        // TODO: Integrate with PlaybackService
        System.Diagnostics.Debug.WriteLine($"PlayNow: {Name} (Uri: {Uri})");
        IsPlaying = true;
    }

    /// <summary>
    /// Command to play content next (insert after current track)
    /// </summary>
    [RelayCommand]
    private void PlayNext()
    {
        // TODO: Integrate with PlaybackService - insert after current track
        System.Diagnostics.Debug.WriteLine($"PlayNext: {Name} (Uri: {Uri})");
    }

    /// <summary>
    /// Command to play content later (add to end of queue)
    /// </summary>
    [RelayCommand]
    private void PlayLater()
    {
        // TODO: Integrate with PlaybackService - add to end of queue
        System.Diagnostics.Debug.WriteLine($"PlayLater: {Name} (Uri: {Uri})");
    }

    /// <summary>
    /// Command to share the content item (copy link)
    /// </summary>
    [RelayCommand]
    private void Share()
    {
        var webUrl = SpotifyUriHelper.UriToWebUrl(Uri);
        if (!string.IsNullOrEmpty(webUrl))
        {
            // TODO: Implement share/copy link logic
            System.Diagnostics.Debug.WriteLine($"Share: {Name} - {webUrl}");
            // TODO: Copy to clipboard: DataPackage dataPackage = new DataPackage();
        }
    }

    /// <summary>
    /// Factory method to create the appropriate ViewModel from a ContentWrapperDto
    /// </summary>
    public static ContentItemViewModel? FromWrapper(ContentWrapperDto? wrapper)
    {
        return wrapper switch
        {
            AlbumResponseWrapperDto albumWrapper when albumWrapper.Data != null && !string.IsNullOrEmpty(albumWrapper.Data.Uri) => new AlbumViewModel(albumWrapper.Data),
            PlaylistResponseWrapperDto playlistWrapper when playlistWrapper.Data != null && !string.IsNullOrEmpty(playlistWrapper.Data.Uri) => new PlaylistViewModel(playlistWrapper.Data),
            ArtistResponseWrapperDto artistWrapper when artistWrapper.Data != null && !string.IsNullOrEmpty(artistWrapper.Data.Uri) => new ArtistViewModel(artistWrapper.Data),
            ShowResponseWrapperDto showWrapper when showWrapper.Data != null && !string.IsNullOrEmpty(showWrapper.Data.Uri) => new ShowViewModel(showWrapper.Data),
            _ => null
        };
    }
}
