namespace Wavee.Core;

// The video↔audio association cache (the data side of music videos). Spotify's extended-metadata VIDEO_ASSOCIATIONS
// (99) / AUDIO_ASSOCIATIONS (98) extensions map a track to its counterpart media + the counterpart's file ids. We
// cache that client-side (persisted, etag-revalidated) so the UI can show a has-video indicator at list scale and a
// future, DRM-gated video route can fetch the file by id. NOTE: video files are DRM-protected and resolve over their
// OWN route — never the audio key / audio storage-resolve path. This record stops at the cached file id.

/// <summary>Which FORM a play request wants a playable started in. <see cref="Default"/> means "whatever the user's
/// standing surface intent resolves to" — the behaviour of a bare play — while the other two are an explicit gesture
/// ("play the music video", "play the song"). Carrying it as a value is what stops a caller having to know that the
/// video surface must be lit BEFORE playback for the request to take effect.</summary>
public enum MediaForm { Default, Audio, Video }

/// <summary>One media file variant of an association — a 20-byte content file id (hex) plus its quality discriminant
/// and (for video) resolution. <paramref name="Width"/>/<paramref name="Height"/> are 0 when unknown (e.g. audio).</summary>
public sealed record VideoFileRef(string FileIdHex, int Variant, int Width, int Height);

/// <summary>The cached video↔audio association for one entity uri. <paramref name="HasVideo"/> is the list-level
/// indicator (VIDEO_ASSOCIATIONS returned a non-empty payload); <paramref name="CounterpartUri"/> is the paired
/// entity (the video track for an audio track, or vice-versa); <paramref name="Files"/> are the counterpart's file
/// id variants. <paramref name="Etag"/> drives 304 revalidation; <paramref name="FetchedAt"/> +
/// <paramref name="OfflineTtlSeconds"/> drive freshness/offline reuse. A negative result (no video) is cached too,
/// so we stop re-asking. <paramref name="VideoGidHex"/> is the counterpart video's 32-hex gid (= the video
/// manifest_id, and Connect's <c>associated_video_id</c>) when a decode produced it; null when unknown — it is
/// OPTIONAL and trails the record on purpose so persisted rows written before it deserialize unchanged.</summary>
public sealed record VideoAssociation(
    string Uri,
    bool HasVideo,
    string? CounterpartUri,
    System.Collections.Generic.IReadOnlyList<VideoFileRef> Files,
    string? Etag,
    System.DateTimeOffset FetchedAt,
    long OfflineTtlSeconds,
    string? VideoGidHex = null)
{
    public static readonly System.Collections.Generic.IReadOnlyList<VideoFileRef> NoFiles = System.Array.Empty<VideoFileRef>();

    /// <summary>A cached "this entity has no video" result (404 / empty 200), still worth persisting so a list realize
    /// does not re-ask for every row — but only briefly (see <see cref="IsFresh"/>).</summary>
    public static VideoAssociation None(string uri, string? etag, System.DateTimeOffset fetchedAt, long offlineTtlSeconds)
        => new(uri, false, null, NoFiles, etag, fetchedAt, offlineTtlSeconds);

    // Freshness follows the VERDICT, because the two verdicts are not the same kind of claim. "This track has a video"
    // is a durable fact — videos do not get un-published, and the payload is worth revalidating conditionally. "This
    // track has no video" is a *not yet*: Spotify attaches videos to catalogue that already exists, so a miss is the
    // answer most likely to be wrong tomorrow. Cache them for the same window and a track that gains a video keeps a
    // stale "no" on its row while every un-conditional fetcher (the expand drawer) sees the truth.
    static readonly System.TimeSpan PositiveWindow = System.TimeSpan.FromHours(6);
    static readonly System.TimeSpan NegativeWindow = System.TimeSpan.FromMinutes(30);

    /// <summary>Whether this record can still be served without going to the network.</summary>
    public bool IsFresh(System.DateTimeOffset now) => now - FetchedAt < (HasVideo ? PositiveWindow : NegativeWindow);

    // RevalidationEtag (HasVideo ? Etag : null) is GONE with the video service that owned the conditional: the trait
    // pipeline reaches the wire only through ExtensionEtagCache, which owns every etag decision for every kind in one
    // place. Persisted blobs written with the old computed property still deserialize — System.Text.Json ignores
    // members it has no target for, and it was never a constructor parameter.
}
