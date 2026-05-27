using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.WinUI.Data.Contexts;

[global::WinRT.GeneratedBindableCustomProperty]
internal sealed partial class PlayerContext : ObservableObject, IPlayerContext
{
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsShuffle { get; set; }

    [ObservableProperty]
    public partial bool IsRepeat { get; set; }

    [ObservableProperty]
    public partial double Volume { get; set; } = 1.0;

    [ObservableProperty]
    public partial double Position { get; set; }

    [ObservableProperty]
    public partial double Duration { get; set; }

    [ObservableProperty]
    public partial string? CurrentTrackId { get; set; }

    [ObservableProperty]
    public partial string? CurrentTrackTitle { get; set; }

    [ObservableProperty]
    public partial string? CurrentArtistName { get; set; }

    [ObservableProperty]
    public partial string? CurrentAlbumArt { get; set; }
}