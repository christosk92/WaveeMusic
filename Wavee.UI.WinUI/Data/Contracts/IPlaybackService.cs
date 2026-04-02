using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Generic playback command interface.
/// All methods return <see cref="PlaybackResult"/> with success/failure information.
/// Routes commands to the active playback backend (Spotify Web API, or future local engine).
/// </summary>
public interface IPlaybackService : INotifyPropertyChanged
{
    // ── Play commands ──

    /// <summary>
    /// Starts playback of a context (album, playlist, artist, etc.).
    /// </summary>
    Task<PlaybackResult> PlayContextAsync(
        string contextUri,
        PlayContextOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Plays a specific track within a context.
    /// </summary>
    Task<PlaybackResult> PlayTrackInContextAsync(
        string trackUri,
        string contextUri,
        PlayContextOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Plays a list of track URIs directly (no context).
    /// </summary>
    Task<PlaybackResult> PlayTracksAsync(
        IReadOnlyList<string> trackUris,
        int startIndex = 0,
        CancellationToken ct = default);

    // ── Transport controls ──

    Task<PlaybackResult> ResumeAsync(CancellationToken ct = default);
    Task<PlaybackResult> PauseAsync(CancellationToken ct = default);
    Task<PlaybackResult> TogglePlayPauseAsync(CancellationToken ct = default);
    Task<PlaybackResult> SkipNextAsync(CancellationToken ct = default);
    Task<PlaybackResult> SkipPreviousAsync(CancellationToken ct = default);
    Task<PlaybackResult> SeekAsync(long positionMs, CancellationToken ct = default);

    // ── Options ──

    Task<PlaybackResult> SetShuffleAsync(bool enabled, CancellationToken ct = default);
    Task<PlaybackResult> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default);
    Task<PlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct = default);

    // ── Queue ──

    Task<PlaybackResult> AddToQueueAsync(string trackUri, CancellationToken ct = default);

    // ── Device ──

    Task<PlaybackResult> TransferPlaybackAsync(
        string deviceId,
        bool startPlaying = true,
        CancellationToken ct = default);

    // ── Observable state ──

    /// <summary>True while a play command is buffering/loading.</summary>
    bool IsBuffering { get; }

    /// <summary>True while any command is being executed.</summary>
    bool IsExecutingCommand { get; }

    /// <summary>Device ID of the currently active Spotify device.</summary>
    string? ActiveDeviceId { get; }

    /// <summary>Display name of the currently active device.</summary>
    string? ActiveDeviceName { get; }

    /// <summary>True if playback is happening on a remote device (not this app).</summary>
    bool IsPlayingRemotely { get; }

    /// <summary>Observable stream of playback errors for toast/notification display.</summary>
    IObservable<PlaybackErrorEvent> Errors { get; }

    /// <summary>Raised when a play command starts loading a track. Carries the track URI being loaded.</summary>
    event Action<string?>? BufferingStarted;
}
