using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Enums;
using Wavee.UI.Models;

namespace Wavee.UI.Contracts;

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
    /// Plays a list of track URIs. When <paramref name="context"/> is supplied
    /// (e.g. the playlist or album the tracks came from), its URI is published
    /// in PlayerState.context_uri so remote Spotify clients see the real source
    /// instead of an anonymous "internal queue". Pass <paramref name="richTracks"/>
    /// with per-track uids / metadata (from the playlist API) when available —
    /// those are published as <c>ProvidedTrack.uid</c> and
    /// <c>ProvidedTrack.metadata</c> so remote clients see the full-fidelity
    /// queue. When <paramref name="richTracks"/> is supplied, it must align
    /// one-to-one with <paramref name="trackUris"/>.
    /// </summary>
    Task<PlaybackResult> PlayTracksAsync(
        IReadOnlyList<string> trackUris,
        int startIndex = 0,
        PlaybackContextInfo? context = null,
        IReadOnlyList<QueueItem>? richTracks = null,
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
    Task<PlaybackResult> SetPlaybackSpeedAsync(double speed, CancellationToken ct = default);
    Task<PlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct = default);

    // ── Queue ──

    Task<PlaybackResult> AddToQueueAsync(string trackUri, CancellationToken ct = default);

    // ── Device ──

    Task<PlaybackResult> TransferPlaybackAsync(
        string deviceId,
        bool startPlaying = true,
        CancellationToken ct = default);

    /// <summary>
    /// Switch the local Windows audio output device used by the AudioHost process.
    /// </summary>
    /// <param name="deviceIndex">PortAudio device index from <c>AudioOutputDeviceDto</c>.</param>
    Task<PlaybackResult> SwitchAudioOutputAsync(
        int deviceIndex,
        CancellationToken ct = default);

    /// <summary>
    /// Switches the currently-playing track from audio to its music-video
    /// variant at the live playback position. Preserves queue / context / play
    /// origin. No-op if the track has no video variant or video is already
    /// active.
    ///
    /// <paramref name="manifestIdOverride"/> lets the UI inject a lazily-
    /// resolved manifest_id for linked-URI tracks (Pathfinder NPV path).
    /// </summary>
    Task<PlaybackResult> SwitchToVideoAsync(
        string? manifestIdOverride = null,
        string? videoTrackUriOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Switches the currently-playing music-video variant back to audio at the
    /// live playback position.
    /// </summary>
    Task<PlaybackResult> SwitchToAudioAsync(CancellationToken ct = default);

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
