namespace Wavee.Tests.Modules.Fixtures;

/// <summary>
/// Sanitized <c>gql.twitch.tv</c> and <c>usher.ttvnw.net</c> bodies, shaped exactly like the real endpoints. Kept
/// as raw string literals so the bodies are verbatim but need no copy-to-output plumbing in the test csproj.
/// </summary>
public static class TwitchFixtures
{
    /// <summary>The channel every fixture talks about.</summary>
    public const string Login = "examplestreamer";

    /// <summary>A normal token document, as it arrives inside the access token's <c>value</c> member.</summary>
    public const string TokenValue = """
    {"adblock":false,"authorization":{"forbidden":false,"reason":""},"blackout_enabled":false,"channel":"examplestreamer","channel_id":123456789,"chansub":{"restricted_bitrates":[],"view_until":1924905600},"ci_gb":false,"geoblock_reason":"","device_id":"0000000000000000","expires":1767225600,"extended_history_allowed":false,"game":"Just Chatting","hide_ads":false,"https_required":true,"mature":false,"partner":true,"platform":"web","player_type":"site","private":{"allowed_to_view":true},"privileged":false,"role":"","server_ads":true,"show_ads":true,"subscriber":false,"turbo":false,"user_id":null,"user_ip":"203.0.113.7","version":2}
    """;

    /// <summary>A token whose authorization is refused outright.</summary>
    public const string TokenValueForbidden = """
    {"authorization":{"forbidden":true,"reason":"This channel is temporarily unavailable."},"channel":"examplestreamer","channel_id":123456789,"chansub":{"restricted_bitrates":[]},"expires":1767225600,"geoblock_reason":"","user_ip":"203.0.113.7"}
    """;

    /// <summary>A token carrying a region block.</summary>
    public const string TokenValueGeoblocked = """
    {"authorization":{"forbidden":false,"reason":""},"channel":"examplestreamer","channel_id":123456789,"chansub":{"restricted_bitrates":[]},"expires":1767225600,"geoblock_reason":"blocked in your country","user_ip":"203.0.113.7"}
    """;

    /// <summary>A token where source quality is subscriber-only.</summary>
    public const string TokenValueRestrictedBitrates = """
    {"authorization":{"forbidden":false,"reason":""},"channel":"examplestreamer","channel_id":123456789,"chansub":{"restricted_bitrates":["chunked","720p60"]},"expires":1767225600,"geoblock_reason":"","user_ip":"203.0.113.7"}
    """;

    /// <summary>The GQL envelope for a live channel, with <paramref name="value"/> as the token document.</summary>
    /// <param name="value">The token document to embed (JSON-escaped here).</param>
    public static string LiveTokenEnvelope(string value = TokenValue) => LivePrefix + Quote(value) + LiveSuffix;

    /// <summary>The GQL envelope for a VOD.</summary>
    /// <param name="value">The token document to embed (JSON-escaped here).</param>
    public static string VodTokenEnvelope(string value = TokenValue) => VodPrefix + Quote(value) + VodSuffix;

    private const string LivePrefix = """{"data":{"streamPlaybackAccessToken":{"value":""";

    private const string LiveSuffix = ""","signature":"c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00","__typename":"PlaybackAccessToken"}},"extensions":{"durationMilliseconds":24,"operationName":"PlaybackAccessToken","requestID":"01AAAA"}}""";

    private const string VodPrefix = """{"data":{"videoPlaybackAccessToken":{"value":""";

    private const string VodSuffix = ""","signature":"beefbeefbeefbeefbeefbeefbeefbeefbeefbeef","__typename":"PlaybackAccessToken"}}}""";

    /// <summary>What GQL answers when a persisted hash has been retired.</summary>
    public const string PersistedQueryNotFound = """
    {"errors":[{"message":"PersistedQueryNotFound","path":["playbackAccessToken"]}],"data":null}
    """;

    /// <summary>What GQL answers for a channel that does not exist (or when integrity is being enforced).</summary>
    public const string NullTokenEnvelope = """
    {"data":{"streamPlaybackAccessToken":null},"extensions":{"operationName":"PlaybackAccessToken"}}
    """;

    /// <summary><c>StreamMetadata</c> for a channel that is live.</summary>
    public const string StreamMetadataLive = """
    {"data":{"user":{"id":"123456789","login":"examplestreamer","displayName":"ExampleStreamer","primaryColorHex":"6441A4","profileImageURL":"https://static-cdn.jtvnw.net/user-default-pictures/300x300.png","lastBroadcast":{"id":"48000000000","title":"Yesterday's build stream","__typename":"Broadcast"},"broadcastSettings":{"id":"123456789","title":"Building a Rust parser","__typename":"BroadcastSettings"},"stream":{"id":"48000000001","type":"live","createdAt":"2026-08-22T10:00:00Z","game":{"id":"509658","name":"Science & Technology","displayName":"Science & Technology","__typename":"Game"},"previewImageURL":"https://static-cdn.jtvnw.net/previews-ttv/live_user_examplestreamer-1920x1080.jpg","viewersCount":1234,"__typename":"Stream"},"__typename":"User"}},"extensions":{"operationName":"StreamMetadata"}}
    """;

    /// <summary>
    /// <c>StreamMetadata</c> for a live channel whose <c>previewImageURL</c> still carries the
    /// <c>{width}</c>/<c>{height}</c> placeholders — which is what Twitch returns whenever the query asked for no
    /// explicit size, i.e. what the persisted query the module sends actually gets back most of the time.
    /// </summary>
    public const string StreamMetadataLiveTemplatedPreview = """
    {"data":{"user":{"id":"123456789","login":"examplestreamer","displayName":"ExampleStreamer","profileImageURL":"https://static-cdn.jtvnw.net/user-default-pictures/300x300.png","broadcastSettings":{"id":"123456789","title":"Building a Rust parser","__typename":"BroadcastSettings"},"stream":{"id":"48000000001","type":"live","game":{"id":"509658","name":"Science & Technology","displayName":"Science & Technology","__typename":"Game"},"previewImageURL":"https://static-cdn.jtvnw.net/previews-ttv/live_user_examplestreamer-{width}x{height}.jpg","viewersCount":1234,"__typename":"Stream"},"__typename":"User"}},"extensions":{"operationName":"StreamMetadata"}}
    """;

    /// <summary>
    /// <c>StreamMetadata</c> for a live channel whose <c>stream</c> block carries **no <c>previewImageURL</c> member at
    /// all</c>. This is not a hypothetical: it is what a real live channel answered with when the module was run
    /// against the live service, and it is why the page falls back to the login-derived preview path rather than to
    /// the channel avatar (a 70x70 image has no business being a full-width 16:9 poster).
    /// </summary>
    public const string StreamMetadataLiveNoPreview = """
    {"data":{"user":{"id":"123456789","login":"examplestreamer","displayName":"ExampleStreamer","profileImageURL":"https://static-cdn.jtvnw.net/jtv_user_pictures/abcdef-profile_image-70x70.png","broadcastSettings":{"id":"123456789","title":"Building a Rust parser","__typename":"BroadcastSettings"},"stream":{"id":"48000000001","type":"live","game":{"id":"509658","name":"Science & Technology","displayName":"Science & Technology","__typename":"Game"},"viewersCount":1234,"__typename":"Stream"},"__typename":"User"}},"extensions":{"operationName":"StreamMetadata"}}
    """;

    /// <summary><c>StreamMetadata</c> for a channel that is offline (<c>stream</c> is null).</summary>
    public const string StreamMetadataOffline = """
    {"data":{"user":{"id":"123456789","login":"examplestreamer","displayName":"ExampleStreamer","lastBroadcast":{"id":"48000000000","title":"Yesterday's build stream","__typename":"Broadcast"},"broadcastSettings":{"id":"123456789","title":"Building a Rust parser","__typename":"BroadcastSettings"},"stream":null,"__typename":"User"}},"extensions":{"operationName":"StreamMetadata"}}
    """;

    /// <summary><c>StreamMetadata</c> for a login that does not exist.</summary>
    public const string StreamMetadataNoUser = """
    {"data":{"user":null},"extensions":{"operationName":"StreamMetadata"}}
    """;

    /// <summary>A usher v2 multivariant playlist (the shape twitch.tv has served since ~Feb 2026).</summary>
    public const string UsherMasterV2 = """
    #EXTM3U
    #EXT-X-SESSION-DATA:DATA-ID="com.amazon.ivs.manifest-node",VALUE="video-weaver.lhr03"
    #EXT-X-SESSION-DATA:DATA-ID="com.amazon.ivs.server-time",VALUE="1767225000.00"
    #EXT-X-STREAM-INF:BANDWIDTH=6000000,CODECS="avc1.4D402A,mp4a.40.2",RESOLUTION=1920x1080,IVS-NAME="1080p60",FRAME-RATE=60.000
    https://video-weaver.lhr03.hls.ttvnw.net/v1/playlist/AAAA.m3u8
    #EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS="mp4a.40.2",IVS-NAME="audio_only"
    https://video-weaver.lhr03.hls.ttvnw.net/v1/playlist/BBBB.m3u8
    """;

    /// <summary>Usher's JSON error body for a subscriber-gated VOD.</summary>
    public const string UsherRestricted = """
    [{"type":"error","error":"Manifest is restricted","error_code":"vod_manifest_restricted"}]
    """;

    /// <summary>Usher's JSON error body for a channel with nothing on air.</summary>
    public const string UsherTransoceanic = """
    [{"type":"error","error":"Can not find channel","error_code":"transcode_does_not_exist"}]
    """;

    /// <summary>JSON-escapes and quotes a string so it can be embedded as a member value.</summary>
    /// <param name="value">The raw text (the fixtures never contain control characters).</param>
    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                       .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
