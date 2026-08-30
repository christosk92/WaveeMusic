using System.Text.Json.Serialization;

namespace Wavee.Sdk;

/// <summary>Whether a playable is served to the audio host or to the video host.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MediaForm>))]
public enum MediaForm
{
    /// <summary>Audio-only: the app plays it through its audio host.</summary>
    [JsonStringEnumMemberName("audio")] Audio,

    /// <summary>Video (with audio): the app plays it through its video host (Media Foundation).</summary>
    [JsonStringEnumMemberName("video")] Video,
}

/// <summary>The answer to <c>playback/match</c>: this module claims the pasted text and can play it.</summary>
/// <param name="PlayableId">Module-private id for the thing to play; travels in the <c>wavee:module:</c> uri.</param>
/// <param name="Title">Best-effort title, shown before <c>playback/resolve</c> completes.</param>
/// <param name="Form">Whether the playable is audio or video.</param>
/// <param name="IsLive">True for a live stream (no seeking, unknown duration).</param>
/// <param name="Confidence">0..1; the router prefers a higher-confidence match when several modules claim the input.</param>
public sealed record MatchResult(string PlayableId, string? Title, MediaForm Form, bool IsLive, double Confidence);

/// <summary>
/// Where the bytes come from. <c>Kind == "url"</c> means the app fetches them itself; <c>Kind == "stream"</c> means
/// the module serves them over <c>stream/open</c> / <c>stream/read</c> / <c>stream/close</c>.
/// </summary>
/// <param name="Kind"><c>"url"</c> or <c>"stream"</c>.</param>
/// <param name="Url">Absolute url when <paramref name="Kind"/> is <c>"url"</c>.</param>
/// <param name="Headers">Extra request headers for the url (the video host cannot set them; keep it empty for HLS).</param>
/// <param name="Container"><c>"progressive"</c>, <c>"hls"</c> or <c>"icy"</c>.</param>
/// <param name="ContentType">MIME type when known; drives codec selection before any sniffing.</param>
/// <param name="StreamId">Module-private stream id when <paramref name="Kind"/> is <c>"stream"</c>.</param>
public sealed record MediaLocator(
    string Kind,
    string? Url,
    Dictionary<string, string>? Headers,
    string? Container,
    string? ContentType,
    string? StreamId)
{
    /// <summary><see cref="Kind"/> value for a locator the app fetches itself.</summary>
    public const string KindUrl = "url";

    /// <summary><see cref="Kind"/> value for a locator whose bytes the module serves over <c>stream/*</c>.</summary>
    public const string KindStream = "stream";

    /// <summary><see cref="Container"/> value for a finite, byte-ranged body.</summary>
    public const string ContainerProgressive = "progressive";

    /// <summary><see cref="Container"/> value for an HLS master playlist.</summary>
    public const string ContainerHls = "hls";

    /// <summary><see cref="Container"/> value for an endless Icecast/SHOUTcast body with interleaved metadata.</summary>
    public const string ContainerIcy = "icy";

    /// <summary>Creates a <c>"url"</c> locator.</summary>
    public static MediaLocator FromUrl(string url, string? container = null, string? contentType = null,
        Dictionary<string, string>? headers = null)
        => new(KindUrl, url, headers, container, contentType, null);

    /// <summary>Creates a <c>"stream"</c> locator whose bytes the module serves over <c>stream/*</c>.</summary>
    public static MediaLocator FromStream(string streamId, string? contentType = null)
        => new(KindStream, null, null, null, contentType, streamId);
}

/// <summary>
/// Source-of-truth wire identity for a playable that the app republishes (Spotify Connect / playback attribution).
/// Only modules that declare the <c>wireMeta</c> capability populate it.
/// </summary>
/// <param name="MediaId">Opaque media id bytes (base64 on the wire).</param>
/// <param name="FileId">Opaque file id bytes (base64 on the wire).</param>
/// <param name="BitrateKbps">Nominal bitrate of the chosen file.</param>
/// <param name="AudioFormat">Format token, e.g. <c>"OGG_VORBIS_320"</c>.</param>
/// <param name="DurationMs">Duration of the chosen file in milliseconds.</param>
public sealed record WireMeta(byte[] MediaId, byte[] FileId, int BitrateKbps, string AudioFormat, long DurationMs);

/// <summary>Source-neutral playback preferences the app passes down on <c>playback/resolve</c>.</summary>
/// <param name="Quality"><c>"normal"</c>, <c>"high"</c>, <c>"veryHigh"</c> or <c>"lossless"</c>.</param>
/// <param name="Metered">True when the connection is metered; modules should pick a cheaper rung.</param>
/// <param name="CrossfadeMs">The app's crossfade setting, so a module can pre-roll accordingly.</param>
public sealed record ResolvePreferences(string Quality, bool Metered, int CrossfadeMs)
{
    /// <summary>The neutral default (normal quality, unmetered, no crossfade).</summary>
    public static ResolvePreferences Default { get; } = new("normal", false, 0);
}

/// <summary>The answer to <c>playback/resolve</c>: everything the app needs to start playing.</summary>
/// <param name="PlayableId">Echo of the requested playable id.</param>
/// <param name="Title">Display title.</param>
/// <param name="Artists">Display artists (may be empty).</param>
/// <param name="ArtworkUrl">Absolute artwork url, or null.</param>
/// <param name="DurationMs">Duration in milliseconds; 0 means unknown (live).</param>
/// <param name="IsLive">True for a live stream: no seeking, no auto-advance on socket drop.</param>
/// <param name="Form">Audio or video.</param>
/// <param name="Media">Where the bytes come from.</param>
/// <param name="ExpiresAtUnixMs">When the locator stops working; the host re-resolves at (or before) that instant.</param>
/// <param name="Caps">Per-playable capability tokens, e.g. <c>preparedNext</c>, <c>connectPublish</c>, <c>wireMeta</c>.</param>
/// <param name="GainDb">Normalization gain in dB; 0 means "none".</param>
/// <param name="Wire">Optional wire identity for republishing (see <see cref="WireMeta"/>).</param>
/// <param name="PageEntityId">The playable's OWN page (see <see cref="ModulePageDoc"/>) — what the art tile and the
/// title link navigate to. Null (the default) leaves both inert, which is what a module without the <c>pages</c>
/// capability wants.</param>
/// <param name="SubtitleEntityId">The page the subtitle links to — the channel, station or show this playable
/// belongs to. Null leaves the subtitle inert.</param>
public sealed record ResolvedPlayable(
    string PlayableId,
    string Title,
    string[] Artists,
    string? ArtworkUrl,
    long DurationMs,
    bool IsLive,
    MediaForm Form,
    MediaLocator Media,
    long? ExpiresAtUnixMs,
    string[] Caps,
    float GainDb = 0f,
    WireMeta? Wire = null,
    string? PageEntityId = null,
    string? SubtitleEntityId = null);

/// <summary>A live "now playing" correction pushed by a module (ICY titles, a stream's current show).</summary>
/// <param name="PlayableId">The playable the update applies to.</param>
/// <param name="Title">New title, or null to leave it alone.</param>
/// <param name="Artists">New artists, or null to leave them alone.</param>
/// <param name="ArtworkUrl">New artwork url, or null to leave it alone.</param>
public sealed record MetadataUpdate(string PlayableId, string? Title, string[]? Artists, string? ArtworkUrl);
