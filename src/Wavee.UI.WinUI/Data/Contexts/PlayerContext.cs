using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.WinUI.Data.Contexts;

internal sealed partial class PlayerContext : ObservableObject, IPlayerContext
{
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isShuffle;

    [ObservableProperty]
    private bool _isRepeat;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private double _duration;

    [ObservableProperty]
    private string? _currentTrackId;

    [ObservableProperty]
    private string? _currentTrackTitle;

    [ObservableProperty]
    private string? _currentArtistName;

    [ObservableProperty]
    private string? _currentAlbumArt;
}
