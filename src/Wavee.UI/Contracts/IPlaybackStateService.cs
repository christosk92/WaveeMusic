using System.ComponentModel;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.Models;

namespace Wavee.UI.Contracts;

/// <summary>
/// Centralized playback state. Wraps <see cref="Contexts.IPlayerContext"/> and adds
/// playback context, queue, and command methods.
/// </summary>
public interface IPlaybackStateService : INotifyPropertyChanged
{
    // --- Playback state ---
    bool IsPlaying { get; }

    /// <summary>
    /// True while a track is loading — from click until first audio plays (local) or ack received (remote).
    /// </summary>
    bool IsBuffering { get; }

    /// <summary>
    /// The track ID currently being loaded/buffered (for per-row loading indicators).
    /// Null when not buffering.
    /// </summary>
    string? BufferingTrackId { get; }

    string? CurrentTrackId { get; }
    string? CurrentTrackTitle { get; }
    string? CurrentArtistName { get; }
    string? CurrentAlbumArt { get; }
    string? CurrentAlbumArtLarge { get; }
    string? CurrentArtistId { get; }
    string? CurrentAlbumId { get; }
    IReadOnlyList<ArtistCredit>? CurrentArtists { get; }

    string? CurrentOriginalTrackId { get; }
    string? CurrentOriginalTrackTitle { get; }
    string? CurrentOriginalArtistName { get; }
    string? CurrentOriginalAlbumArt { get; }
    string? CurrentOriginalAlbumArtLarge { get; }
    string? CurrentOriginalArtistId { get; }
    string? CurrentOriginalAlbumId { get; }
    double CurrentOriginalDuration { get; }

    /// <summary>
    /// Hex-encoded music-video manifest_id when the current track has a video
    /// variant. Sourced from either the resolved Track proto (Wavee-initiated
    /// playback) or Connect-state metadata (remote-driven). Drives the
    /// "Watch Video" button visibility in the player UI for the
    /// "self-contained" pattern (audio URI itself carries original_video).
    /// </summary>
    string? CurrentTrackManifestId { get; }

    /// <summary>
    /// True when the playing audio track has a music-video variant available
    /// via Spotify's GraphQL associations (linked-URI pattern, where the audio
    /// and video URIs are different catalog entries). Combined with
    /// <see cref="CurrentTrackManifestId"/> in the UI to decide whether to show
    /// the "Watch Video" affordance. Populated by
    /// <c>MusicVideoDiscoveryService</c> via Pathfinder NPV.
    /// </summary>
    bool CurrentTrackHasMusicVideo { get; }

    /// <summary>
    /// True when the active now-playing item is already the Spotify video
    /// variant rather than the audio track.
    /// </summary>
    bool CurrentTrackIsVideo { get; }

    // ── Local-content (TMDB) shape ───────────────────────────────────────
    //
    // Populated when the current track is a <c>wavee:local:track:</c> URI and
    // its row in <c>local_files</c> has been classified — TV episode, movie,
    // music, music video, or other. Used by the PlayerBar to route title-click
    // to the correct detail page (show / movie / album) and by the now-playing
    // surfaces to hide music-only chrome (lyrics / friends / details) when the
    // current item is a film or show. Reset to <see cref="LocalNowPlayingKind.None"/>
    // for Spotify content.

    /// <summary>
    /// Classification of the current local item, or null for Spotify content
    /// (and during the brief window before a freshly-loaded local URI has been
    /// looked up in the index). Drives which controls render in the player
    /// chrome and which detail page the title links to.
    /// </summary>
    Wavee.Local.Classification.LocalContentKind? CurrentLocalContentKind { get; }

    /// <summary>
    /// Local-show id (e.g. "person-of-interest" or whatever the indexer assigned)
    /// when the current track is a TV episode — the navigation target for
    /// title-click. Null otherwise.
    /// </summary>
    string? CurrentLocalSeriesId { get; }

    /// <summary>TMDB-fetched series name (e.g. "Person of Interest"). Null when not enriched.</summary>
    string? CurrentLocalSeriesName { get; }

    /// <summary>Season number for TV episodes. Null otherwise.</summary>
    int? CurrentLocalSeasonNumber { get; }

    /// <summary>Episode number for TV episodes. Null otherwise.</summary>
    int? CurrentLocalEpisodeNumber { get; }

    /// <summary>TMDB-fetched episode title (e.g. "Pilot"). Null when not enriched.</summary>
    string? CurrentLocalEpisodeTitle { get; }

    /// <summary>Release year for movies. Null otherwise.</summary>
    int? CurrentLocalMovieYear { get; }

    /// <summary>TMDB id for the episode / movie (whichever applies). Null when not matched.</summary>
    int? CurrentLocalTmdbId { get; }

    /// <summary>
    /// Theme-appropriate hex color extracted from the current album art.
    /// Uses DarkHex in dark theme, LightHex in light theme.
    /// </summary>
    string? CurrentAlbumArtColor { get; }

    /// <summary>
    /// True if playback is on a remote device (not this app).
    /// </summary>
    bool IsPlayingRemotely { get; }

    /// <summary>
    /// True if the active device doesn't support volume control.
    /// </summary>
    bool IsVolumeRestricted { get; }

    /// <summary>
    /// Display name of the active Spotify device, if remote.
    /// </summary>
    string? ActiveDeviceName { get; }

    /// <summary>
    /// Device type of the active Spotify Connect device (when remote) or Computer locally.
    /// Drives the icon shown in the output-device card.
    /// </summary>
    DeviceType ActiveDeviceType { get; }

    /// <summary>
    /// All Spotify Connect devices visible in the user's cluster (this device + others).
    /// </summary>
    IReadOnlyList<ConnectDevice> AvailableConnectDevices { get; }

    /// <summary>
    /// Friendly name of the current local Windows audio output device (from PortAudio via AudioHost).
    /// Null until the first state update from the audio engine arrives.
    /// </summary>
    string? ActiveAudioDeviceName { get; }

    /// <summary>
    /// Full list of local Windows audio output devices enumerated by PortAudio.
    /// </summary>
    IReadOnlyList<AudioOutputDeviceDto> AvailableAudioDevices { get; }

    /// <summary>
    /// True when the AudioHost IPC pipe is connected and the audio engine is responsive.
    /// </summary>
    bool IsAudioEngineAvailable { get; }
    double Position { get; set; }
    double Duration { get; }
    double PlaybackSpeed { get; }
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
    void SetPlaybackSpeed(double speed);
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
    /// Starts a Spotify "Inspired by …" radio mix seeded from a track,
    /// artist, or album URI. Resolves to a playlist URI server-side and
    /// then plays it via the standard playlist flow. Fire-and-forget;
    /// failures log internally.
    /// </summary>
    /// <param name="seedUri">"spotify:track:&lt;id&gt;" / "spotify:artist:&lt;id&gt;" / "spotify:album:&lt;id&gt;".</param>
    /// <param name="displayName">Optional placeholder shown until the playlist resolves.</param>
    System.Threading.Tasks.Task StartRadioAsync(string seedUri, string? displayName = null);

    /// <summary>
    /// "Add to Queue" — adds a track so it plays AFTER the current context
    /// finishes (post-context bucket on local; tail of remote user queue on
    /// remote, since Connect doesn't model post-context).
    /// </summary>
    void AddToQueue(string trackId);

    /// <summary>
    /// "Add to Queue" (multi) — same semantics as the single-track overload,
    /// applied per track in order.
    /// </summary>
    void AddToQueue(IEnumerable<string> trackIds);

    /// <summary>
    /// "Play Next" — inserts a track at the head of the user queue so it
    /// plays immediately after the current track, then context resumes.
    /// </summary>
    void PlayNext(string trackId);

    /// <summary>
    /// Replaces the queue, sets the playback context, and loads the track at startIndex as current.
    /// </summary>
    void LoadQueue(IReadOnlyList<QueueItem> items, PlaybackContextInfo context, int startIndex = 0);

    /// <summary>
    /// Sets the buffering indicator for a track ID. Call before async play commands.
    /// </summary>
    void NotifyBuffering(string? trackId);

    /// <summary>
    /// Clears the buffering indicator when an optimistic play command does not produce playback.
    /// </summary>
    void ClearBuffering();

    /// <summary>
    /// True once playback has reached end-of-context and auto-advance has
    /// stopped. Clears on next resume. Drives the PlayerBar's inline
    /// "You've reached the end" hint.
    /// </summary>
    bool IsAtEndOfContext { get; }

    /// <summary>
    /// Flip <see cref="IsAtEndOfContext"/> to true. Called from the
    /// orchestrator's <c>EndOfContext</c> subscription in
    /// <c>AppLifecycleHelper</c>.
    /// </summary>
    void NotifyEndOfContext();

    /// <summary>
    /// Clears the end-of-context hint bar immediately. Called by the bar's
    /// dismiss (X) button. Same effect as the auto-clear on resume — just
    /// user-initiated rather than state-driven.
    /// </summary>
    void DismissEndOfContext();

    /// <summary>
    /// Switches the currently-playing track from audio to its music-video
    /// variant at the live playback position. Returns <c>true</c> when an
    /// engine switch was initiated and the caller should navigate to the
    /// video page; <c>false</c> when no manifest could be resolved (e.g.
    /// NPV returned no associated video, the video URI's TrackV4 has no
    /// <c>OriginalVideo</c>, network error). Callers MUST branch on the
    /// result rather than blindly opening the video player.
    /// </summary>
    Task<bool> SwitchToVideoAsync();

    /// <summary>
    /// Switches the current Spotify music-video playback back to the audio
    /// engine at the live playback position. Returns <c>true</c> when the
    /// engine switch was initiated.
    /// </summary>
    Task<bool> SwitchToAudioAsync();
}
