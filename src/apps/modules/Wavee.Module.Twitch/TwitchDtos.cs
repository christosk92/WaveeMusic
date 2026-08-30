using System.Text.Json.Serialization;

namespace Wavee.Module.Twitch;

/// <summary>The <c>gql.twitch.tv</c> envelope for a playback-access-token query (inline or persisted).</summary>
/// <param name="Data">The payload, or null when only <paramref name="Errors"/> came back.</param>
/// <param name="Errors">GraphQL errors; <c>PersistedQueryNotFound</c> means the stored hash was retired.</param>
public sealed record TwitchTokenEnvelope(TwitchTokenData? Data, TwitchGqlError[]? Errors);

/// <summary>The two token shapes, only one of which is ever populated.</summary>
/// <param name="StreamPlaybackAccessToken">Populated for a live channel.</param>
/// <param name="VideoPlaybackAccessToken">Populated for a VOD.</param>
public sealed record TwitchTokenData(
    TwitchAccessToken? StreamPlaybackAccessToken,
    TwitchAccessToken? VideoPlaybackAccessToken);

/// <summary>A signed usher token.</summary>
/// <param name="Value">The token document, itself a JSON string (see <see cref="TwitchTokenValue"/>).</param>
/// <param name="Signature">The signature usher checks.</param>
public sealed record TwitchAccessToken(string? Value, string? Signature);

/// <summary>One GraphQL error entry.</summary>
/// <param name="Message">The error text, e.g. <c>PersistedQueryNotFound</c>.</param>
public sealed record TwitchGqlError(string? Message);

/// <summary>The decoded <c>value</c> of an access token. Snake-cased on the wire, so every member is named.</summary>
/// <param name="Expires">Unix seconds when usher stops accepting the token.</param>
/// <param name="Authorization">Whether playback is forbidden and why.</param>
/// <param name="GeoblockReason">Non-empty when the channel is blocked in this region.</param>
/// <param name="Chansub">Subscriber-gating details.</param>
/// <param name="ChannelId">The channel's numeric id.</param>
/// <param name="UserIp">The IP the token is pinned to.</param>
/// <param name="Channel">The channel login the token was minted for — the only place a VOD id reveals its channel.</param>
public sealed record TwitchTokenValue(
    [property: JsonPropertyName("expires")] long Expires,
    [property: JsonPropertyName("authorization")] TwitchTokenAuthorization? Authorization,
    [property: JsonPropertyName("geoblock_reason")] string? GeoblockReason,
    [property: JsonPropertyName("chansub")] TwitchChansub? Chansub,
    [property: JsonPropertyName("channel_id")] long ChannelId,
    [property: JsonPropertyName("user_ip")] string? UserIp,
    [property: JsonPropertyName("channel")] string? Channel = null);

/// <summary>The token's authorization verdict.</summary>
/// <param name="Forbidden">True when Twitch refuses playback outright.</param>
/// <param name="Reason">The message to show when <paramref name="Forbidden"/> is true.</param>
public sealed record TwitchTokenAuthorization(
    [property: JsonPropertyName("forbidden")] bool Forbidden,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>Subscriber gating carried by the token.</summary>
/// <param name="RestrictedBitrates">Rendition names only subscribers may fetch.</param>
public sealed record TwitchChansub(
    [property: JsonPropertyName("restricted_bitrates")] string[]? RestrictedBitrates);

/// <summary>The <c>StreamMetadata</c> envelope.</summary>
/// <param name="Data">The payload.</param>
public sealed record TwitchMetadataEnvelope(TwitchMetadataData? Data);

/// <summary>The <c>StreamMetadata</c> payload.</summary>
/// <param name="User">The channel, or null when the login does not exist.</param>
public sealed record TwitchMetadataData(TwitchUser? User);

/// <summary>A channel as <c>StreamMetadata</c> reports it.</summary>
/// <param name="DisplayName">The channel's display name.</param>
/// <param name="Stream">The current broadcast; null means offline.</param>
/// <param name="LastBroadcast">The previous broadcast, used for a title when the current one has none.</param>
/// <param name="BroadcastSettings">The channel's configured stream title.</param>
/// <param name="Login">The channel login, when the query returned it.</param>
/// <param name="ProfileImageURL">The channel's avatar. It is the OWNER's picture, so on a live channel it goes to
/// the hero's <c>AvatarUrl</c> and the stream preview takes the art slot; offline it is the hero's art.</param>
public sealed record TwitchUser(
    string? DisplayName,
    TwitchStream? Stream,
    TwitchBroadcast? LastBroadcast,
    TwitchBroadcast? BroadcastSettings,
    string? Login = null,
    string? ProfileImageURL = null);

/// <summary>The live broadcast.</summary>
/// <param name="Id">Stream id.</param>
/// <param name="Game">The category being streamed.</param>
/// <param name="PreviewImageURL">Thumbnail url — the picture the watch stage posters. Often templated with
/// <c>{width}</c>/<c>{height}</c>; see <c>TwitchModule.PreviewImage</c>.</param>
/// <param name="ViewersCount">How many people are watching right now, when the query returned it.</param>
public sealed record TwitchStream(string? Id, TwitchGame? Game, string? PreviewImageURL, int? ViewersCount = null);

/// <summary>A Twitch category.</summary>
/// <param name="Name">Category name, e.g. <c>Just Chatting</c>.</param>
/// <param name="DisplayName">Localized category name when present.</param>
public sealed record TwitchGame(string? Name, string? DisplayName);

/// <summary>A broadcast's title (both <c>lastBroadcast</c> and <c>broadcastSettings</c> carry this shape).</summary>
/// <param name="Title">The stream title.</param>
public sealed record TwitchBroadcast(string? Title);

/// <summary>One entry of usher's JSON error body.</summary>
/// <param name="Type">Always <c>error</c>.</param>
/// <param name="Error">Human-readable error.</param>
/// <param name="ErrorCode">Machine token, e.g. <c>vod_manifest_restricted</c>.</param>
public sealed record UsherError(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_code")] string? ErrorCode);

/// <summary>Source-generated serializer for every JSON shape this module reads. No reflection, so NativeAOT-clean.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TwitchTokenEnvelope))]
[JsonSerializable(typeof(TwitchTokenValue))]
[JsonSerializable(typeof(TwitchMetadataEnvelope))]
[JsonSerializable(typeof(UsherError))]
[JsonSerializable(typeof(UsherError[]))]
public sealed partial class TwitchJsonContext : JsonSerializerContext;
