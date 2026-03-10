using System.Collections.Generic;
using System.ComponentModel;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Centralized playback state. Wraps <see cref="Contexts.IPlayerContext"/> and adds
/// playback context, queue, and command methods.
/// </summary>
public interface IPlaybackStateService : INotifyPropertyChanged
{
    // --- Mirrored from IPlayerContext (read-only view) ---
    bool IsPlaying { get; }
    string? CurrentTrackId { get; }
    string? CurrentTrackTitle { get; }
    string? CurrentArtistName { get; }
    string? CurrentAlbumArt { get; }
    double Position { get; set; }
    double Duration { get; }
    double Volume { get; set; }
    bool IsShuffle { get; }
    RepeatMode RepeatMode { get; }

    // --- Context & queue ---

    /// <summary>
    /// Describes what is currently being played (playlist, album, etc.).
    /// </summary>
    PlaybackContextInfo? CurrentContext { get; }

    /// <summary>
    /// The current playback queue.
    /// </summary>
    IReadOnlyList<QueueItem> Queue { get; }

    /// <summary>
    /// Index of the currently playing item within <see cref="Queue"/>.
    /// </summary>
    int QueuePosition { get; }

    // --- Commands ---
    void PlayPause();
    void Next();
    void Previous();
    void Seek(double positionMs);
    void SetShuffle(bool shuffle);
    void SetRepeatMode(RepeatMode mode);

    /// <summary>
    /// Begins playback of an entire context (playlist, album, etc.).
    /// </summary>
    void PlayContext(PlaybackContextInfo context, int startIndex = 0);

    /// <summary>
    /// Plays a single track, optionally within a context.
    /// </summary>
    void PlayTrack(string trackId, PlaybackContextInfo? context = null);

    /// <summary>
    /// Adds a track to the end of the user queue.
    /// </summary>
    void AddToQueue(string trackId);

    /// <summary>
    /// Adds multiple tracks to the end of the user queue.
    /// </summary>
    void AddToQueue(IEnumerable<string> trackIds);

    /// <summary>
    /// Replaces the queue, sets the playback context, and loads the track at startIndex as current.
    /// </summary>
    void LoadQueue(IReadOnlyList<QueueItem> items, PlaybackContextInfo context, int startIndex = 0);
}
