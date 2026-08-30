using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Module.YouTube;

/// <summary>The subset of <c>youtubei/v1/player</c> this module reads. Unknown members are ignored by STJ.</summary>
/// <param name="PlayabilityStatus">Whether the video can be served to this client at all.</param>
/// <param name="VideoDetails">Title/author/live flags.</param>
/// <param name="StreamingData">Where the HLS master lives and when it dies.</param>
/// <param name="Microformat">Carries the live-broadcast schedule.</param>
/// <param name="ResponseContext">The session identity InnerTube hands back on EVERY response.</param>
public sealed record YtPlayerResponse(
    YtPlayabilityStatus? PlayabilityStatus,
    YtVideoDetails? VideoDetails,
    YtStreamingData? StreamingData,
    YtMicroformat? Microformat,
    YtResponseContext? ResponseContext = null);

/// <summary>
/// The identity block on every InnerTube response, <c>/player</c> and <c>/next</c> alike. The module reads exactly one
/// member of it and echoes that back on the next request; until it did, every single call presented as a brand-new
/// anonymous client, which is the shape an anti-bot system is looking for.
/// <para>The same block also carries <c>mainAppWebResponseContext.loggedOut: true</c> on every response this module
/// has ever seen. It is deliberately NOT modelled: it only ever says what we already know (a JS-less client cannot
/// sign in), so reading it would add a DTO that can never change a decision.</para>
/// </summary>
/// <param name="VisitorData">The opaque, unauthenticated visitor id. Sent back as <c>X-Goog-Visitor-Id</c> and as
/// <c>context.client.visitorData</c>.</param>
public sealed record YtResponseContext(string? VisitorData);

/// <summary>Why (or whether) YouTube will serve this video to the requesting client.</summary>
/// <param name="Status"><c>OK</c>, <c>LOGIN_REQUIRED</c>, <c>UNPLAYABLE</c>, <c>ERROR</c>, <c>LIVE_STREAM_OFFLINE</c>, …</param>
/// <param name="Reason">Human-readable reason shown verbatim when nothing better is known.</param>
/// <param name="DesktopLegacyAgeGateReason">Present (any value) when the video is age-gated.</param>
public sealed record YtPlayabilityStatus(
    string? Status,
    string? Reason,
    JsonElement? DesktopLegacyAgeGateReason);

/// <summary>The video's own description of itself.</summary>
/// <param name="VideoId">Must equal the requested id; a mismatch means the IP is being blocked.</param>
/// <param name="Title">Display title.</param>
/// <param name="Author">Channel name.</param>
/// <param name="ChannelId">Channel id.</param>
/// <param name="LengthSeconds">Duration in seconds, as a string; <c>"0"</c> for a live stream.</param>
/// <param name="IsLive">True while broadcasting.</param>
/// <param name="IsLiveContent">True for anything that ever was a broadcast.</param>
/// <param name="IsUpcoming">True for a scheduled premiere/broadcast that has not started.</param>
/// <param name="IsPostLiveDvr">True for the DVR window right after a broadcast ended.</param>
/// <param name="IsLowLatencyLiveStream">True for a low-latency broadcast.</param>
/// <param name="IsPrivate">True for a private video.</param>
/// <param name="Thumbnail">Thumbnail set; the widest entry becomes the artwork.</param>
/// <param name="ShortDescription">The video's description, as plain text with real newlines. Page copy only.</param>
/// <param name="ViewCount">Total views (live: concurrent-ish), as a string. Page copy only.</param>
public sealed record YtVideoDetails(
    string? VideoId,
    string? Title,
    string? Author,
    string? ChannelId,
    string? LengthSeconds,
    bool IsLive,
    bool IsLiveContent,
    bool IsUpcoming,
    bool IsPostLiveDvr,
    bool IsLowLatencyLiveStream,
    bool IsPrivate,
    YtThumbnailSet? Thumbnail,
    string? ShortDescription = null,
    string? ViewCount = null);

/// <summary>The thumbnail array wrapper.</summary>
/// <param name="Thumbnails">Thumbnails, smallest first in practice.</param>
public sealed record YtThumbnailSet(YtThumbnail[]? Thumbnails);

/// <summary>One thumbnail.</summary>
/// <param name="Url">Absolute image url.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
public sealed record YtThumbnail(string? Url, int Width, int Height);

/// <summary>Where the media lives. Only <paramref name="HlsManifestUrl"/> is ever used.</summary>
/// <param name="ExpiresInSeconds">Session lifetime as a string, typically <c>"21540"</c>.</param>
/// <param name="HlsManifestUrl">The HLS master (MPEG-TS renditions) handed to Media Foundation.</param>
/// <param name="DashManifestUrl">Ignored: Win32 Media Engine has no DASH.</param>
/// <param name="ServerAbrStreamingUrl">Present without an HLS url = a SABR-only session.</param>
public sealed record YtStreamingData(
    string? ExpiresInSeconds,
    string? HlsManifestUrl,
    string? DashManifestUrl,
    string? ServerAbrStreamingUrl);

/// <summary>Microformat wrapper.</summary>
/// <param name="PlayerMicroformatRenderer">The renderer holding the broadcast schedule.</param>
public sealed record YtMicroformat(YtPlayerMicroformatRenderer? PlayerMicroformatRenderer);

/// <summary>
/// The renderer holding the broadcast schedule and the video's civil metadata. Every member below rides the SAME
/// <c>/player</c> response the resolve path already pays for, so reading them costs no extra request — which is why
/// they are modelled here rather than asked of <c>/next</c>: a VOD's date comes free.
/// </summary>
/// <param name="LiveBroadcastDetails">Live-now flag plus start/end timestamps.</param>
/// <param name="PublishDate">When the video was published, e.g. <c>"2026-08-20T09:00:00-07:00"</c>. For a broadcast
/// this is the scheduled/actual start; for a VOD it is the date YouTube shows under the title.</param>
/// <param name="UploadDate">When the file was uploaded. Equal to <paramref name="PublishDate"/> for most videos and
/// earlier for one published after the fact, so it is only the fallback.</param>
/// <param name="OwnerChannelName">The channel name, as the microformat spells it. A second opinion on
/// <see cref="YtVideoDetails.Author"/>, not a replacement.</param>
/// <param name="ExternalChannelId">The <c>UC…</c> channel id; the fallback when
/// <see cref="YtVideoDetails.ChannelId"/> is absent.</param>
/// <param name="ViewCount">Lifetime views as a string — the same number as <see cref="YtVideoDetails.ViewCount"/>,
/// kept because either one being present is enough and some age-gated responses carry only this copy.</param>
public sealed record YtPlayerMicroformatRenderer(
    YtLiveBroadcastDetails? LiveBroadcastDetails,
    string? PublishDate = null,
    string? UploadDate = null,
    string? OwnerChannelName = null,
    string? ExternalChannelId = null,
    string? ViewCount = null);

/// <summary>The broadcast schedule.</summary>
/// <param name="IsLiveNow">True while the broadcast is on air.</param>
/// <param name="StartTimestamp">ISO-8601 start instant, present for scheduled broadcasts.</param>
/// <param name="EndTimestamp">ISO-8601 end instant, present once it has finished.</param>
public sealed record YtLiveBroadcastDetails(bool IsLiveNow, string? StartTimestamp, string? EndTimestamp);

/// <summary>One InnerTube client block from <c>clients.json</c>.</summary>
/// <param name="Key">Short key used in logs and diagnostics, e.g. <c>visionos</c>.</param>
/// <param name="ClientName">InnerTube <c>clientName</c>, e.g. <c>VISIONOS</c>.</param>
/// <param name="ClientVersion">InnerTube <c>clientVersion</c>.</param>
/// <param name="ClientId">Numeric client id sent as <c>X-YouTube-Client-Name</c>.</param>
/// <param name="UserAgent">The exact UA this client must send.</param>
/// <param name="DeviceMake">Optional <c>deviceMake</c>.</param>
/// <param name="DeviceModel">Optional <c>deviceModel</c>.</param>
/// <param name="OsName">Optional <c>osName</c>.</param>
/// <param name="OsVersion">Optional <c>osVersion</c>.</param>
/// <param name="AndroidSdkVersion">Optional <c>androidSdkVersion</c> (Android only; required by InnerTube).</param>
/// <param name="Warning">Logged when this client is the one that answered (e.g. the iOS 30-second cut-off).</param>
/// <param name="Role">What this client is FOR: <see cref="RolePlayback"/> (the default when the member is absent) or
/// <see cref="RoleMetadata"/>. The two roles never mix — a metadata client is skipped by the <c>/player</c> fallback
/// walk and a playback client is never asked for a page — because the constraint that rules a client out differs per
/// endpoint (see <see cref="RoleMetadata"/>).</param>
public sealed record YouTubeClient(
    string Key,
    string ClientName,
    string ClientVersion,
    int ClientId,
    string UserAgent,
    string? DeviceMake = null,
    string? DeviceModel = null,
    string? OsName = null,
    string? OsVersion = null,
    int? AndroidSdkVersion = null,
    string? Warning = null,
    string? Role = null)
{
    /// <summary><see cref="Role"/> value for a client the <c>/player</c> fallback walk may try. This is the default:
    /// a block with no <c>role</c> member is a playback client, which keeps every older <c>clients.json</c> valid.</summary>
    public const string RolePlayback = "playback";

    /// <summary>
    /// <see cref="Role"/> value for a client used ONLY for metadata endpoints (<c>/next</c>), never for
    /// <c>/player</c>. That distinction is what lets WEB back in: WEB is banned from <c>/player</c> because it
    /// answers with a SABR-only session that needs the JS player, but <c>/next</c> returns no streams at all, so the
    /// ban has nothing to bite on — and WEB is the one client whose <c>twoColumnWatchNextResults</c> shape is stable
    /// (the mobile clients answer <c>singleColumnWatchNextResults</c> instead).
    /// </summary>
    public const string RoleMetadata = "metadata";

    /// <summary>True when this block may be tried for a <c>/player</c> call.</summary>
    [JsonIgnore]
    public bool IsPlayback => !string.Equals(Role, RoleMetadata, StringComparison.Ordinal);

    /// <summary>True when this block is a metadata-only client.</summary>
    [JsonIgnore]
    public bool IsMetadata => string.Equals(Role, RoleMetadata, StringComparison.Ordinal);
}

/// <summary>The <c>clients.json</c> document: the fallback order, as data.</summary>
/// <param name="SchemaVersion">Document version (currently 1).</param>
/// <param name="Clients">Client blocks, tried in order.</param>
public sealed record YouTubeClientTable(int SchemaVersion, YouTubeClient[] Clients);

// ---- youtubei/v1/next (the watch-next endpoint) ------------------------------------------------------------------
//
// Everything below models METADATA only: /next carries no stream urls whatsoever, which is exactly why the WEB client
// (banned from /player) is the right one to ask. The module treats every member as absent-until-proven-present —
// InnerTube reshapes this document without notice, and a page that renders fine WITHOUT the enrichment is the
// contract, so a shape that stopped matching costs one log line and nothing else.

/// <summary>
/// An InnerTube text node. YouTube spells the same string two ways depending on whether it carries link runs:
/// <c>{"simpleText":"…"}</c> for plain copy and <c>{"runs":[{"text":"…"},…]}</c> once any part is a link or a styled
/// span. Both forms appear inside ONE response, so every read goes through <see cref="Plain"/>.
/// </summary>
/// <param name="SimpleText">The whole string, when YouTube had no runs to describe.</param>
/// <param name="Runs">The pieces, concatenated in order by <see cref="Plain"/>.</param>
public sealed record YtText(string? SimpleText, YtTextRun[]? Runs)
{
    /// <summary>Flattens either spelling to one string, or null when the node says nothing.</summary>
    /// <param name="node">The text node, possibly null.</param>
    public static string? Plain(YtText? node)
    {
        if (node is null) return null;
        if (node.SimpleText is { Length: > 0 } simple)
        {
            return simple.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }

        if (node.Runs is not { Length: > 0 } runs) return null;

        var sb = new StringBuilder(64);
        foreach (YtTextRun run in runs)
        {
            if (run.Text is { Length: > 0 } text) sb.Append(text);
        }

        string joined = sb.ToString().Trim();
        return joined.Length == 0 ? null : joined;
    }
}

/// <summary>One piece of a runs-style text node.</summary>
/// <param name="Text">This piece's literal characters, joined verbatim with its neighbours.</param>
public sealed record YtTextRun(string? Text);

/// <summary>The subset of <c>youtubei/v1/next</c> this module reads.</summary>
/// <param name="Contents">The layout wrapper; WEB answers <c>twoColumnWatchNextResults</c>.</param>
/// <param name="ResponseContext">The same identity block <c>/player</c> answers with. Read here too because a page
/// open is often the FIRST request of a session, so this is where the visitor id is usually learned.</param>
public sealed record YtNextResponse(YtNextContents? Contents, YtResponseContext? ResponseContext = null);

/// <summary>The layout wrapper.</summary>
/// <param name="TwoColumnWatchNextResults">The desktop watch layout. A mobile client would answer
/// <c>singleColumnWatchNextResults</c> instead, which is why the metadata client is pinned to WEB.</param>
public sealed record YtNextContents(YtTwoColumnWatchNextResults? TwoColumnWatchNextResults);

/// <summary>The two columns of the watch page.</summary>
/// <param name="Results">The left column: title, view count, date, owner.</param>
/// <param name="SecondaryResults">The right column: the up-next rail.</param>
public sealed record YtTwoColumnWatchNextResults(
    YtWatchNextResults? Results,
    YtWatchNextSecondaryResults? SecondaryResults);

/// <summary>YouTube's doubled <c>results.results</c> nesting for the left column.</summary>
/// <param name="Results">The inner holder.</param>
public sealed record YtWatchNextResults(YtWatchNextResultsInner? Results);

/// <summary>The left column's renderer list.</summary>
/// <param name="Contents">Renderers in order; the module takes the first primary/secondary info block it sees and
/// ignores everything else (comment threads, merch shelves, ticket promos).</param>
public sealed record YtWatchNextResultsInner(YtWatchNextContent[]? Contents);

/// <summary>One left-column renderer. Exactly one member is populated per entry; an unknown renderer deserializes to
/// all-null and is skipped.</summary>
/// <param name="VideoPrimaryInfoRenderer">Title, view count and date block.</param>
/// <param name="VideoSecondaryInfoRenderer">Owner and description block.</param>
public sealed record YtWatchNextContent(
    YtVideoPrimaryInfoRenderer? VideoPrimaryInfoRenderer,
    YtVideoSecondaryInfoRenderer? VideoSecondaryInfoRenderer);

/// <summary>The block above the description.</summary>
/// <param name="ViewCount">The view/watching counter.</param>
/// <param name="DateText">YouTube's own rendered date line — <c>"Started streaming 3 hours ago"</c>,
/// <c>"Premiered Aug 20, 2026"</c>, <c>"Aug 20, 2026"</c>. Used VERBATIM: the module does no relative-time arithmetic
/// of its own, because YouTube already did it, in the language the request asked for (<c>hl=en</c>).</param>
public sealed record YtVideoPrimaryInfoRenderer(YtViewCount? ViewCount, YtText? DateText);

/// <summary>The view-count wrapper.</summary>
/// <param name="VideoViewCountRenderer">The counter itself.</param>
public sealed record YtViewCount(YtVideoViewCountRenderer? VideoViewCountRenderer);

/// <summary>
/// The counter. For a broadcast this is the CONCURRENT audience (<c>"12,345 watching now"</c>) and it is the only
/// place that number appears: <c>videoDetails.viewCount</c> on the same video is a lifetime total that rises
/// monotonically whether or not anybody is watching, so a live page quoting it is lying.
/// </summary>
/// <param name="ViewCount">The long form, e.g. <c>"12,345 watching now"</c> or <c>"987,654 views"</c>.</param>
/// <param name="IsLive">True when <paramref name="ViewCount"/> is a concurrent count rather than a lifetime one.</param>
/// <param name="ExtraShortViewCount">The compact form, e.g. <c>"12K watching"</c>; the fallback for a tight line.</param>
public sealed record YtVideoViewCountRenderer(YtText? ViewCount, bool IsLive, YtText? ExtraShortViewCount);

/// <summary>The block that owns the description.</summary>
/// <param name="Owner">The channel row.</param>
public sealed record YtVideoSecondaryInfoRenderer(YtOwner? Owner);

/// <summary>The owner wrapper.</summary>
/// <param name="VideoOwnerRenderer">The channel row itself.</param>
public sealed record YtOwner(YtVideoOwnerRenderer? VideoOwnerRenderer);

/// <summary>
/// The channel row under the video: the ONLY place a JS-less client is handed a channel AVATAR. The player response
/// names the channel but never pictures it, which is why <c>channel:&lt;id&gt;</c> pages had no art before this.
/// </summary>
/// <param name="Title">The channel name.</param>
/// <param name="Thumbnail">The avatar set; the widest entry is the one used.</param>
/// <param name="SubscriberCountText">YouTube's rendered subscriber line, e.g. <c>"1.2M subscribers"</c>.</param>
/// <param name="NavigationEndpoint">Carries the channel's browse id.</param>
public sealed record YtVideoOwnerRenderer(
    YtText? Title,
    YtThumbnailSet? Thumbnail,
    YtText? SubscriberCountText,
    YtNavigationEndpoint? NavigationEndpoint);

/// <summary>The subset of an InnerTube navigation endpoint this module reads.</summary>
/// <param name="BrowseEndpoint">Where a browse tap would go.</param>
public sealed record YtNavigationEndpoint(YtBrowseEndpoint? BrowseEndpoint);

/// <summary>A browse target.</summary>
/// <param name="BrowseId">The <c>UC…</c> channel id, for a channel row.</param>
public sealed record YtBrowseEndpoint(string? BrowseId);

/// <summary>YouTube's doubled <c>secondaryResults.secondaryResults</c> nesting for the up-next rail.</summary>
/// <param name="SecondaryResults">The inner holder.</param>
public sealed record YtWatchNextSecondaryResults(YtWatchNextSecondaryResultsInner? SecondaryResults);

/// <summary>The up-next rail.</summary>
/// <param name="Results">Rail entries in order; anything that is neither a <c>lockupViewModel</c> nor a
/// <c>compactVideoRenderer</c> — continuations, ad slots, playlist rails — deserializes to all-null and is
/// skipped.</param>
public sealed record YtWatchNextSecondaryResultsInner(YtSecondaryResult[]? Results);

/// <summary>
/// One up-next rail entry. YouTube is mid-migration on this surface and BOTH shapes are live: a capture taken
/// 2026-08-23 answered 20 x <c>lockupViewModel</c> and 0 x <c>compactVideoRenderer</c> for a WEB <c>/next</c> call,
/// while the older renderer is still what other sessions get. It is an A/B-able surface, not a version bump, so the
/// module reads the view-model first and falls back to the renderer rather than picking one and dropping the other.
/// </summary>
/// <param name="LockupViewModel">The current shape.</param>
/// <param name="CompactVideoRenderer">The older shape.</param>
public sealed record YtSecondaryResult(
    YtLockupViewModel? LockupViewModel,
    YtCompactVideoRenderer? CompactVideoRenderer);

// ---- the lockupViewModel rail -------------------------------------------------------------------------------------
//
// Every shape below is transcribed from a real WEB /next capture (2026-08-23, 20 rail entries) rather than from
// memory. Where a member's meaning is INFERRED rather than observed in that capture, the member says so.

/// <summary>
/// One up-next entry in YouTube's current view-model shape. Unlike <see cref="YtCompactVideoRenderer"/> its strings
/// are plain <c>{"content":"…"}</c> objects rather than <see cref="YtText"/> nodes, and its facts arrive as an
/// ordered grid of metadata rows instead of named members.
/// </summary>
/// <param name="ContentId">The 11-character video id, for a video lockup.</param>
/// <param name="ContentType">What this lockup is; only <see cref="VideoContentType"/> is playable. Playlists and
/// mixes ride the same envelope under their own types and are skipped, because their <c>contentId</c> is a list id
/// that the resolve path could not play.</param>
/// <param name="ContentImage">The thumbnail and its overlay badges.</param>
/// <param name="Metadata">The title and the metadata grid.</param>
public sealed record YtLockupViewModel(
    string? ContentId,
    string? ContentType,
    YtContentImage? ContentImage,
    YtLockupMetadata? Metadata)
{
    /// <summary>The one <see cref="ContentType"/> that names a playable video.</summary>
    public const string VideoContentType = "LOCKUP_CONTENT_TYPE_VIDEO";
}

/// <summary>The thumbnail wrapper.</summary>
/// <param name="ThumbnailViewModel">The thumbnail itself.</param>
public sealed record YtContentImage(YtThumbnailViewModel? ThumbnailViewModel);

/// <summary>A lockup's thumbnail.</summary>
/// <param name="Image">The image sources.</param>
/// <param name="Overlays">Overlays drawn on it; only the bottom overlay carries the duration badge, and an entry that
/// is some other overlay (<c>animatedThumbnailOverlayViewModel</c> is common) deserializes to null and is skipped.</param>
public sealed record YtThumbnailViewModel(YtImageSources? Image, YtThumbnailOverlay[]? Overlays);

/// <summary>An image, spelled <c>sources</c> here rather than <c>thumbnails</c> — the entries themselves are the same
/// <see cref="YtThumbnail"/> shape, so the widest-wins pick is shared with the renderer path.</summary>
/// <param name="Sources">The sizes, smallest first in practice. An entry describing a client-side icon rather than a
/// url deserializes with a null <see cref="YtThumbnail.Url"/> and is skipped.</param>
public sealed record YtImageSources(YtThumbnail[]? Sources);

/// <summary>One thumbnail overlay.</summary>
/// <param name="ThumbnailBottomOverlayViewModel">The bottom-right overlay, when this entry is one.</param>
public sealed record YtThumbnailOverlay(YtThumbnailBottomOverlayViewModel? ThumbnailBottomOverlayViewModel);

/// <summary>The bottom overlay.</summary>
/// <param name="Badges">Its badges; the first one carrying text is the one used.</param>
public sealed record YtThumbnailBottomOverlayViewModel(YtThumbnailBadge[]? Badges);

/// <summary>One overlay badge wrapper.</summary>
/// <param name="ThumbnailBadgeViewModel">The badge itself.</param>
public sealed record YtThumbnailBadge(YtThumbnailBadgeViewModel? ThumbnailBadgeViewModel);

/// <summary>An overlay badge. Every entry in the 2026-08-23 capture carried a duration (<c>"2:33:02"</c>,
/// <c>"28:07"</c>).</summary>
/// <param name="Text">The badge's text: a duration for a finished video and — INFERRED, because no live entry
/// appeared in the capture — <c>"LIVE"</c> for a broadcast.</param>
/// <param name="BadgeStyle">The style token, e.g. <c>THUMBNAIL_OVERLAY_BADGE_STYLE_DEFAULT</c>. Deliberately NOT the
/// live test: the capture shows that style on ordinary videos and never showed a live one, so keying off a live style
/// token nobody has observed would be a guess dressed as a fact.</param>
public sealed record YtThumbnailBadgeViewModel(string? Text, string? BadgeStyle);

/// <summary>The lockup metadata wrapper.</summary>
/// <param name="LockupMetadataViewModel">The metadata itself.</param>
public sealed record YtLockupMetadata(YtLockupMetadataViewModel? LockupMetadataViewModel);

/// <summary>A lockup's title and facts.</summary>
/// <param name="Title">The video title, as a plain content object.</param>
/// <param name="Metadata">The metadata grid.</param>
public sealed record YtLockupMetadataViewModel(YtContent? Title, YtContentMetadata? Metadata);

/// <summary>The metadata-grid wrapper.</summary>
/// <param name="ContentMetadataViewModel">The grid itself.</param>
public sealed record YtContentMetadata(YtContentMetadataViewModel? ContentMetadataViewModel);

/// <summary>
/// The metadata grid: rows of parts, positional rather than named. In the capture row 0 is always the channel name
/// and row 1 is <c>["36K views", "1 month ago"]</c>; some entries carry a third row holding a <c>badges</c> member
/// and no <c>metadataParts</c> at all, which is why a row's parts are optional.
/// </summary>
/// <param name="MetadataRows">The rows, in display order.</param>
public sealed record YtContentMetadataViewModel(YtMetadataRow[]? MetadataRows);

/// <summary>One metadata row.</summary>
/// <param name="MetadataParts">Its parts, or null for a row that holds something else (a "New" badge).</param>
public sealed record YtMetadataRow(YtMetadataPart[]? MetadataParts);

/// <summary>One metadata part.</summary>
/// <param name="Text">The part's text.</param>
public sealed record YtMetadataPart(YtContent? Text);

/// <summary>A plain view-model string. The view-model surface spells text this way; the renderer surface uses
/// <see cref="YtText"/>, and the two never appear in the same subtree.</summary>
/// <param name="Content">The literal characters.</param>
public sealed record YtContent(string? Content);

/// <summary>One up-next video.</summary>
/// <param name="VideoId">The 11-character id; an entry without one is dropped, because it cannot be played.</param>
/// <param name="Title">The video title.</param>
/// <param name="LongBylineText">The channel name.</param>
/// <param name="Thumbnail">Thumbnail set; the widest entry becomes the card art.</param>
/// <param name="LengthText">Rendered duration, e.g. <c>"1:01:12"</c>. Absent for a live entry, which has no end.</param>
/// <param name="ViewCountText">Rendered view line, e.g. <c>"1.2M views"</c> or <c>"12K watching"</c>.</param>
/// <param name="Badges">Overlay badges; <see cref="YtMetadataBadgeRenderer.LiveNowStyle"/> is the live marker.</param>
public sealed record YtCompactVideoRenderer(
    string? VideoId,
    YtText? Title,
    YtText? LongBylineText,
    YtThumbnailSet? Thumbnail,
    YtText? LengthText,
    YtText? ViewCountText,
    YtBadge[]? Badges);

/// <summary>One badge wrapper.</summary>
/// <param name="MetadataBadgeRenderer">The badge itself.</param>
public sealed record YtBadge(YtMetadataBadgeRenderer? MetadataBadgeRenderer);

/// <summary>A badge.</summary>
/// <param name="Style">The style token; <see cref="LiveNowStyle"/> is the only one the module acts on.</param>
/// <param name="Label">The badge's own text, e.g. <c>"LIVE"</c>.</param>
public sealed record YtMetadataBadgeRenderer(string? Style, string? Label)
{
    /// <summary>The <see cref="Style"/> token marking an entry as on air right now.</summary>
    public const string LiveNowStyle = "BADGE_STYLE_TYPE_LIVE_NOW";
}

/// <summary>Source-generated serializer for every JSON shape this module reads. No reflection, so NativeAOT-clean.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(YtPlayerResponse))]
[JsonSerializable(typeof(YtNextResponse))]
[JsonSerializable(typeof(YouTubeClientTable))]
[JsonSerializable(typeof(YouTubeSession))]
public sealed partial class YouTubeJsonContext : JsonSerializerContext;
