using System.ComponentModel;

namespace Wavee.UI.WinUI.Data.Contexts;

public interface IPlayerContext : INotifyPropertyChanged
{
    bool IsPlaying { get; }
    bool IsShuffle { get; }
    bool IsRepeat { get; }
    double Volume { get; set; }
    double Position { get; set; }
    double Duration { get; }
    string? CurrentTrackId { get; }
    string? CurrentTrackTitle { get; }
    string? CurrentArtistName { get; }
    string? CurrentAlbumArt { get; }
}
