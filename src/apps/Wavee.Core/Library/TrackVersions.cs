using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>What kind of alternate a <see cref="TrackVersion"/> is. Derived from WHICH extension kind carried it, not
/// guessed from the title: kind 99 associations are music videos, kind 98 associations are alternate audio.</summary>
public enum TrackVersionKind
{
    /// <summary>The track the user is looking at — always first in the list so its own format can be chosen too.</summary>
    Original,
    /// <summary>A music video (extended-metadata kind 99). Its artwork is a 16:9 still.</summary>
    Video,
    /// <summary>An alternate audio recording — live, remix, sped up (kind 98). Its artwork is square cover art.</summary>
    Audio,
}

/// <summary>One alternate version of a track.
///
/// <paramref name="Title"/> is resolved from the target track, NOT from the association: kinds 98/99 give a
/// <c>target_uri</c> and artwork and NOTHING else — no label, no name. Until the target is resolved this is the uri's
/// id, which is why the resolve step matters.</summary>
public sealed record TrackVersion(
    string Uri,
    TrackVersionKind Kind,
    string Title,
    Image? Artwork,
    long DurationMs = 0,
    double? TempoBpm = null,
    string? MusicalKey = null,
    string? CamelotCode = null,
    uint? CamelotColor = null);

/// <summary>One audio format a track is actually available in (extended-metadata kind 5 AUDIO_FILES).
///
/// <paramref name="FormatId"/> is Spotify's own <c>AudioFile.Format</c> enum value — kept raw so a format we have no
/// name for still round-trips instead of being dropped. <paramref name="AverageBitrate"/> is bits/sec as reported.</summary>
public sealed record AudioFormatOption(
    int FormatId,
    string Label,
    int AverageBitrate,
    bool AvailableOnDevice = true,
    string? UnavailableReason = null);

/// <summary>Everything the expanded track drawer needs: the alternates, and the formats the track itself can play in.
/// Both are fetched ON EXPAND — nothing here rides the row bundle, because a list of 10k rows must not pay for a
/// drawer the user has not opened.</summary>
public sealed record TrackExpansion(
    IReadOnlyList<TrackVersion> Versions,
    IReadOnlyList<AudioFormatOption> Formats,
    TrackWaveform? Waveform = null)
{
    public static readonly TrackExpansion Empty =
        new(System.Array.Empty<TrackVersion>(), System.Array.Empty<AudioFormatOption>());

    public bool IsEmpty => Versions.Count == 0 && Formats.Count == 0 && Waveform is null;
}

/// <summary>A track's drawable waveform, already reduced to one magnitude per column.
///
/// The wire (extension kind 237) ships three ~12 KB byte arrays at 50 Hz — far more resolution than a strip of pixels
/// can show — so the reduction happens ONCE at fetch and the UI only draws. Values are 0..1, normalised against the
/// track's own peak so a quiet track still fills the strip.
///
/// The three bands fold into one magnitude deliberately: a three-colour spectrogram in a 43 px drawer row reads as
/// noise, and the low band arrives at a DIFFERENT length from the other two (a known wire oddity), so treating them as
/// one timebase would drift several seconds by the end of the track.</summary>
public sealed record TrackWaveform(IReadOnlyList<float> Peaks)
{
    public bool IsEmpty => Peaks.Count == 0;
}

/// <summary>Resolves a track's alternate versions and available audio formats. Source-agnostic so the drawer never
/// touches extended-metadata directly.</summary>
public interface ITrackExpansionService
{
    /// <summary>Versions + formats for one track. Returns <see cref="TrackExpansion.Empty"/> rather than throwing when
    /// nothing is available — a track with no alternates is the common case, not an error.</summary>
    Task<TrackExpansion> GetAsync(string trackUri, CancellationToken ct = default);

    /// <summary>Override the audio format for ONE playable, for the next play of it. Null clears the override and
    /// returns the item to the user's global quality preference.</summary>
    void SetFormatOverride(string uri, int? formatId);

    /// <summary>The active per-item format override, or null when the item follows the global preference.</summary>
    int? FormatOverrideFor(string uri);
}

/// <summary>Offline / logged-out: no alternates, no formats. Keeps the drawer on ONE code path.</summary>
public sealed class NullTrackExpansionService : ITrackExpansionService
{
    public static readonly NullTrackExpansionService Instance = new();

    public Task<TrackExpansion> GetAsync(string trackUri, CancellationToken ct = default)
        => Task.FromResult(TrackExpansion.Empty);

    public void SetFormatOverride(string uri, int? formatId) { }
    public int? FormatOverrideFor(string uri) => null;
}
