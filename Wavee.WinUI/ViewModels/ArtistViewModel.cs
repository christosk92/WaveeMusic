using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for an artist
/// </summary>
public partial class ArtistViewModel : ContentItemViewModel
{
    [ObservableProperty]
    public partial string? Genres { get; set; }

    [ObservableProperty]
    public partial int? Popularity { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowArtistLabel))]
    public partial bool IsFollowing { get; set; }

    /// <summary>
    /// Gets the label for the Follow/Unfollow button
    /// </summary>
    public override string FollowArtistLabel => IsFollowing ? "Unfollow" : "Follow Artist";

    /// <summary>
    /// Command to follow or unfollow the artist
    /// </summary>
    protected override void FollowArtist()
    {
        // TODO: Integrate with Spotify API to follow/unfollow artist
        IsFollowing = !IsFollowing;
        System.Diagnostics.Debug.WriteLine($"{(IsFollowing ? "Following" : "Unfollowed")} artist: {Name} (Uri: {Uri})");
    }

    /// <summary>
    /// Command to start artist radio
    /// </summary>
    protected override void GoToArtistRadio()
    {
        // TODO: Implement artist radio playback
        System.Diagnostics.Debug.WriteLine($"Start artist radio for: {Name} (Uri: {Uri})");
    }

    public ArtistViewModel(ArtistDto dto)
    {
        Uri = dto.Uri ?? string.Empty;
        Name = dto.Profile?.Name ?? string.Empty;
        Image = ImageHelper.GetBitmapFromCoverArt(dto.Visuals?.AvatarImage);
        Popularity = dto.Popularity;
        IsCircularImage = true; // Artists have circular images

        // Extract dominant color from avatar image
        var colors = ImageHelper.GetColorsFromCoverArt(dto.Visuals?.AvatarImage);
        DominantColor = ColorHelper.GetBrush(colors);

        // Format genres
        if (dto.Genres?.Length > 0)
        {
            Genres = string.Join(", ", dto.Genres);
            Subtitle = Genres;
        }
        else
        {
            Subtitle = "Artist";
        }
    }
}
