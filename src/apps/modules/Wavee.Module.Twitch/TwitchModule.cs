using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wavee.Sdk;

namespace Wavee.Module.Twitch;

/// <summary>
/// Resolves a Twitch channel or VOD to ONE usher HLS multivariant-playlist url with MPEG-TS renditions, which the
/// app hands straight to Media Foundation. Anonymous: no Device-Id, no Client-Integrity, no Authorization — which
/// also means server-side ads are unavoidable.
/// </summary>
public sealed partial class TwitchModule : WaveeModule
{
    /// <summary>The web player's public client id (the one every anonymous client uses).</summary>
    public const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    /// <summary>The GraphQL endpoint.</summary>
    public const string GqlEndpoint = "https://gql.twitch.tv/gql";

    /// <summary>The persisted <c>PlaybackAccessToken</c> hash (fallback when the inline query is refused).</summary>
    public const string PlaybackAccessTokenHash =
        "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9";

    /// <summary>The persisted <c>StreamMetadata</c> hash.</summary>
    public const string StreamMetadataHash =
        "b57f9b910f8cd1a4659d894fe7550ccc81ec9052c01e438b290fd66a040b9b93";

    /// <summary>A current desktop-Chrome UA; odd UAs make GQL answer "server error".</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/139.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly Func<int> _playerSlot;

    /// <summary>The ctor <see cref="ModuleRunner"/> uses: a default handler with redirects and decompression on.</summary>
    public TwitchModule() : this(null)
    {
    }

    /// <summary>Test/host seam: run every request through <paramref name="handler"/>.</summary>
    /// <param name="handler">The transport, or null for the module's own <see cref="SocketsHttpHandler"/>.</param>
    /// <param name="disposeHandler">True to dispose <paramref name="handler"/> with the module.</param>
    /// <param name="playerSlot">Supplies the usher <c>p</c> parameter; defaults to a random 7-digit number.</param>
    public TwitchModule(HttpMessageHandler? handler, bool disposeHandler = false, Func<int>? playerSlot = null)
    {
        bool ownsHandler = handler is null || disposeHandler;
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, ownsHandler) { Timeout = TimeSpan.FromSeconds(20) };
        _playerSlot = playerSlot ?? (static () => Random.Shared.Next(1_000_000, 10_000_000));
    }

    // ---- match -------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override ValueTask<MatchResult?> MatchAsync(string input, CancellationToken ct)
    {
        TwitchLinkKind kind = TwitchUrls.Parse(input, out string? playableId);
        MatchResult? result = kind switch
        {
            TwitchLinkKind.Live => new MatchResult(playableId!, null, MediaForm.Video, true, 1.0),
            TwitchLinkKind.Vod => new MatchResult(playableId!, null, MediaForm.Video, false, 1.0),
            _ => null,
        };
        return new ValueTask<MatchResult?>(result);
    }

    // ---- resolve -----------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
        => ResolveAsync(playableId, null, ct);

    /// <inheritdoc/>
    public override async ValueTask<ResolvedPlayable> ResolveAsync(string playableId, ResolvePreferences? prefs,
        CancellationToken ct)
    {
        TwitchLinkKind kind = TwitchUrls.Split(playableId, out string value);
        if (kind is TwitchLinkKind.None)
        {
            throw new ModuleException(ModuleErrorCode.NotOwned, $"'{playableId}' is not a Twitch playable id.");
        }

        TwitchAccessToken token = await AccessTokenAsync(kind, value, ct).ConfigureAwait(false);
        TwitchTokenValue decoded = DecodeToken(token.Value);

        if (decoded.Authorization is { Forbidden: true } auth)
        {
            throw new ModuleException(ModuleErrorCode.Unavailable,
                string.IsNullOrWhiteSpace(auth.Reason) ? "Twitch refused playback for this channel." : auth.Reason!);
        }

        if (decoded.GeoblockReason is { Length: > 0 } geo)
        {
            throw new ModuleException(ModuleErrorCode.GeoBlocked, "This channel is not available in your region.")
            {
                Detail = geo,
            };
        }

        if (decoded.Chansub?.RestrictedBitrates is { Length: > 0 } restricted &&
            Array.Exists(restricted, r => RestrictedRendition().IsMatch(r)))
        {
            Host.Log(ModuleLogLevel.Warn,
                "Source quality on this channel is subscriber-only; Twitch will serve a transcode.");
        }

        int slot = _playerSlot();
        string primary = kind is TwitchLinkKind.Live
            ? UsherLiveUrl(value, token.Signature ?? string.Empty, token.Value ?? string.Empty, slot, legacy: false)
            : UsherVodUrl(value, token.Signature ?? string.Empty, token.Value ?? string.Empty, slot, legacy: false);

        UsherOutcome usher = await FetchUsherAsync(primary, ct).ConfigureAwait(false);
        if (!usher.Ok)
        {
            string fallback = kind is TwitchLinkKind.Live
                ? UsherLiveUrl(value, token.Signature ?? string.Empty, token.Value ?? string.Empty, slot, legacy: true)
                : UsherVodUrl(value, token.Signature ?? string.Empty, token.Value ?? string.Empty, slot, legacy: true);
            Host.Log(ModuleLogLevel.Warn,
                $"usher answered {usher.Status} for the v2 endpoint; retrying the legacy endpoint.");
            usher = await FetchUsherAsync(fallback, ct).ConfigureAwait(false);
            if (usher.Ok) primary = fallback;
        }

        TwitchUser? user = kind is TwitchLinkKind.Live
            ? await TryStreamMetadataAsync(value, ct).ConfigureAwait(false)
            : null;

        if (!usher.Ok) throw UsherFailure(usher, kind, user);

        string title = user?.BroadcastSettings?.Title
                       ?? user?.LastBroadcast?.Title
                       ?? user?.DisplayName
                       ?? (kind is TwitchLinkKind.Live ? value : $"Twitch video {value}");

        string[] artists = user?.DisplayName is { Length: > 0 } display
            ? [display]
            : kind is TwitchLinkKind.Live ? [value] : [];

        // On Twitch the thing and its owner are the same entity: a live playable IS the channel, and a VOD belongs
        // to the channel the token was minted for. Both link slots therefore point at `channel:<login>`.
        string? login = kind is TwitchLinkKind.Live
            ? value
            : Login(user?.Login) ?? Login(decoded.Channel);
        string? entity = login is null ? null : ChannelEntityPrefix + login;

        return new ResolvedPlayable(
            PlayableId: playableId,
            Title: title,
            Artists: artists,
            // Same fallback as the page's stage poster: without it a live resolve carries no artwork at all whenever
            // the metadata query omits the preview, and the player bar falls back to a blank tile.
            ArtworkUrl: PreviewImage(user?.Stream?.PreviewImageURL)
                        ?? (kind is TwitchLinkKind.Live && login is not null ? PreviewFor(login) : null),
            DurationMs: 0,
            IsLive: kind is TwitchLinkKind.Live,
            Form: MediaForm.Video,
            Media: MediaLocator.FromUrl(primary, MediaLocator.ContainerHls, "application/vnd.apple.mpegurl"),
            ExpiresAtUnixMs: decoded.Expires > 0 ? decoded.Expires * 1000L : null,
            Caps: [],
            PageEntityId: entity,
            SubtitleEntityId: entity);
    }

    private static string? Login(string? raw)
        => TwitchUrls.IsLogin(raw) ? raw!.ToLowerInvariant() : null;

    private static ModuleException UsherFailure(UsherOutcome usher, TwitchLinkKind kind, TwitchUser? user)
    {
        if (usher.ErrorCode is "vod_manifest_restricted" or "unauthorized_entitlements")
        {
            return new ModuleException(ModuleErrorCode.NeedsAuth,
                "This is a subscriber-only stream; Wavee cannot sign in to Twitch.") { Detail = usher.ErrorCode };
        }

        if (kind is TwitchLinkKind.Live && user is not null && user.Stream is null)
        {
            return new ModuleException(ModuleErrorCode.Offline, "This channel is offline.");
        }

        if (usher.Status >= 500)
        {
            return new ModuleException(ModuleErrorCode.Transient, "Twitch is having trouble serving this stream.")
            {
                Detail = usher.Status.ToString(CultureInfo.InvariantCulture),
            };
        }

        return new ModuleException(ModuleErrorCode.Unavailable,
            usher.Error is { Length: > 0 } text ? text : "Twitch would not serve this stream.")
        {
            Detail = usher.Status.ToString(CultureInfo.InvariantCulture),
        };
    }

    [GeneratedRegex(@"^(?:.+_)?(?:archives|live|chunked)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RestrictedRendition();

    // ---- pages -------------------------------------------------------------------------------------------------

    /// <summary>The <c>channel:&lt;login&gt;</c> entity-id prefix (see <see cref="ModulePageDoc"/>).</summary>
    public const string ChannelEntityPrefix = "channel:";

    /// <summary>The public channel url for a login.</summary>
    /// <param name="login">The channel login.</param>
    public static string ChannelUrl(string login) => "https://www.twitch.tv/" + login;

    /// <inheritdoc/>
    public override async ValueTask<ModulePageDoc?> GetPageAsync(string entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        string id = entityId.Trim();
        if (!id.StartsWith(ChannelEntityPrefix, StringComparison.Ordinal)) return null;

        string login = id[ChannelEntityPrefix.Length..].ToLowerInvariant();
        if (!TwitchUrls.IsLogin(login)) return null;

        TwitchMetadataEnvelope? envelope;
        try
        {
            envelope = await GqlAsync(StreamMetadataBody(login), TwitchJsonContext.Default.TwitchMetadataEnvelope, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ModuleException(ModuleErrorCode.Transient, "Twitch returned an unreadable channel document.")
            {
                Detail = ex.Message,
            };
        }

        TwitchUser? user = envelope?.Data?.User;
        return user is null ? null : ChannelPage(login, user);
    }

    /// <summary>Builds the <c>channel:&lt;login&gt;</c> page out of a <c>StreamMetadata</c> answer.</summary>
    /// <param name="login">The channel login.</param>
    /// <param name="user">What <c>StreamMetadata</c> said about it.</param>
    private static ModulePageDoc ChannelPage(string login, TwitchUser user)
    {
        TwitchStream? stream = user.Stream;
        bool live = stream is not null;
        string name = user.DisplayName is { Length: > 0 } display ? display : login;
        string? streamTitle = Blank(user.BroadcastSettings?.Title) ?? Blank(user.LastBroadcast?.Title);
        string? game = Blank(stream?.Game?.DisplayName) ?? Blank(stream?.Game?.Name);
        string? viewers = stream?.ViewersCount is { } n && n >= 0
            ? n.ToString("N0", CultureInfo.InvariantCulture)
            : null;

        var facts = new List<string[]>(3);
        facts.Add(["Status", live ? "Live" : "Offline"]);
        if (game is not null) facts.Add(["Category", game]);
        if (viewers is not null) facts.Add(["Viewers", viewers]);

        var sections = new List<PageSection>(2) { PageSection.FromFacts([.. facts], "About") };
        if (!live)
        {
            sections.Add(PageSection.FromText(
                streamTitle is null
                    ? "This channel is not live right now."
                    : $"This channel is not live right now. Its last broadcast was “{streamTitle}”.",
                "Offline"));
        }

        var actions = new List<PageAction>(2);
        if (live) actions.Add(PageAction.Play(TwitchUrls.LivePrefix + login, "Play"));
        actions.Add(PageAction.OpenUrl(ChannelUrl(login), "Open on Twitch"));

        string? avatar = Blank(user.ProfileImageURL);
        // The API's own preview when it sent one, else the CDN path for this login — see PreviewFor for why the
        // fallback is needed at all (the persisted query often omits the member entirely). Only on the live arm: the
        // path exists exactly while the channel is live.
        string? preview = PreviewImage(stream?.PreviewImageURL) ?? (live ? PreviewFor(login) : null);
        string? meta = MetaLine(live ? "Live" : "Offline", game, viewers is null ? null : viewers + " watching");

        // Live and offline are two different KINDS of page, not one page with a badge. A live channel's identity IS
        // the picture moving on it, so it goes out as a WATCH document: the app stages the preview at 16:9 and swaps
        // the live video in once this entity is playing, which is why the hero's own art must be the stream preview
        // and the channel's face has to move to AvatarUrl instead of losing the fight for the single image slot.
        // An offline channel has no such picture — a poster-less stage is worse than today's page — so it stays the
        // entity layout, where the avatar is the right (and only) art. Both keep the same facts section: the watch
        // layout folds that row into its description card, the entity layout draws it as tiles.
        // A watch page's TITLE is what is on and its channel row is WHO — the shape the layout is built around, and the
        // shape YouTube's document already has. Twitch's own model is the other way round (the channel is the entity;
        // the stream title is a property of it), so the live arm swaps them. Without the swap the caption names the
        // wrong thing twice: a title reading "shroud" over a channel row reading "going for apache". When there is no
        // stream title the channel name carries the title instead, and the row is dropped rather than repeated.
        string liveTitle = Blank(streamTitle) ?? name;
        string? liveWho = string.Equals(liveTitle, name, StringComparison.Ordinal) ? null : name;

        var hero = live
            ? new PageHero(
                liveTitle,
                "Live stream",
                liveWho,
                preview ?? avatar,   // avatar only as a last resort: an empty stage reads as a broken page
                meta,
                IsLive: true,
                AvatarUrl: avatar,
                // Null on purpose. Per ResolveAsync above, on Twitch the thing and its owner are the SAME entity —
                // `channel:<login>` is both the live playable's page and its channel — so a subtitle link would
                // navigate to the page already on screen. There is no owner page to invent.
                SubtitleEntityId: null)
            : new PageHero(name, "Channel", null, avatar ?? preview, meta, IsLive: false);

        string template = live ? ModulePageDoc.TemplateWatch : ModulePageDoc.TemplateEntity;
        return new ModulePageDoc(ModulePageDoc.CurrentVersion, template, hero,
            [.. actions], [.. sections], ExpiresAtUnixMs: null);
    }

    /// <summary>The width baked into a templated <c>previewImageURL</c>.</summary>
    public const int PreviewWidth = 1920;

    /// <summary>The height baked into a templated <c>previewImageURL</c> — 16:9, the aspect the watch stage draws.</summary>
    public const int PreviewHeight = 1080;

    /// <summary>
    /// Substitutes the <c>{width}</c>/<c>{height}</c> placeholders Twitch leaves in <c>previewImageURL</c> whenever
    /// the query asked for no explicit size — which the persisted <c>StreamMetadata</c> query does not, so the live
    /// path sees them most of the time. Left in place the url is not fetchable at all (braces are not valid in a
    /// path), and the watch stage would show an empty poster. 1920x1080 because the stage is full-width 16:9 and
    /// the CDN re-encodes to whatever is asked; asking small and upscaling is the only way to make this look bad.
    /// </summary>
    /// <param name="url">The raw <c>previewImageURL</c>, possibly templated, possibly null.</param>
    /// <returns>A fetchable absolute url, or null when there was no preview.</returns>
    public static string? PreviewImage(string? url)
    {
        if (Blank(url) is not { } text) return null;
        if (!text.Contains('{', StringComparison.Ordinal)) return text;
        return text
            .Replace("{width}", PreviewWidth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{height}", PreviewHeight.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// The CDN path Twitch serves a live channel's current frame from, built from the login the same way
    /// <see cref="ChannelUrl"/> and the usher playlist url already are. It is needed because the persisted
    /// <c>StreamMetadata</c> query frequently answers with **no** <c>previewImageURL</c> at all (verified against a
    /// live channel: the member is simply absent), and without it the watch stage falls back to the channel avatar —
    /// a 70x70 image stretched across a full-width 16:9 poster, which looks broken rather than merely plain.
    /// <para>This is a url CONVENTION, not invented metadata: it asserts nothing about the channel that the module did
    /// not already learn from the API (that this login is live). If the channel is not live the path 404s, which is
    /// why it is only ever used on the live arm and why an image that fails to load degrades to the poster ground.</para>
    /// </summary>
    /// <param name="login">The lower-cased channel login.</param>
    public static string PreviewFor(string login)
        => $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{login}-{PreviewWidth.ToString(CultureInfo.InvariantCulture)}x{PreviewHeight.ToString(CultureInfo.InvariantCulture)}.jpg";

    /// <summary>Joins the non-empty parts of a meta line with a middle dot.</summary>
    /// <param name="parts">The candidate parts, nulls and blanks skipped.</param>
    private static string? MetaLine(params string?[] parts)
    {
        string joined = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return joined.Length == 0 ? null : joined;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- usher -------------------------------------------------------------------------------------------------

    /// <summary>Builds the live multivariant-playlist url.</summary>
    /// <param name="login">The lower-cased channel login.</param>
    /// <param name="signature">The token's signature.</param>
    /// <param name="token">The token document (url-encoded here).</param>
    /// <param name="playerSlot">The <c>p</c> cache-buster (a 7-digit number).</param>
    /// <param name="legacy">True for the pre-v2 <c>/api/channel/hls/</c> endpoint.</param>
    /// <remarks>
    /// Deliberately WITHOUT <c>fast_bread</c>: it adds <c>#EXT-X-TWITCH-PREFETCH</c> low-latency tags that Media
    /// Foundation's HLS source does not understand. <c>supported_codecs=h264</c> only: h265/av1 renditions are fMP4
    /// with <c>EXT-X-MAP</c>, which MF cannot play.
    /// </remarks>
    public static string UsherLiveUrl(string login, string signature, string token, int playerSlot, bool legacy)
    {
        string root = legacy ? "https://usher.ttvnw.net/api/channel/hls/" : "https://usher.ttvnw.net/api/v2/channel/hls/";
        return $"{root}{Uri.EscapeDataString(login.ToLowerInvariant())}.m3u8" +
               $"?sig={Uri.EscapeDataString(signature)}&token={Uri.EscapeDataString(token)}" +
               CommonUsherQuery(playerSlot);
    }

    /// <summary>Builds the VOD multivariant-playlist url.</summary>
    /// <param name="vodId">The numeric video id.</param>
    /// <param name="signature">The token's signature.</param>
    /// <param name="token">The token document (url-encoded here).</param>
    /// <param name="playerSlot">The <c>p</c> cache-buster (a 7-digit number).</param>
    /// <param name="legacy">True for the pre-v2 <c>/vod/</c> endpoint, which names the params <c>sig</c>/<c>token</c>.</param>
    public static string UsherVodUrl(string vodId, string signature, string token, int playerSlot, bool legacy)
    {
        string root = legacy ? "https://usher.ttvnw.net/vod/" : "https://usher.ttvnw.net/vod/v2/";
        string sigKey = legacy ? "sig" : "nauthsig";
        string tokenKey = legacy ? "token" : "nauth";
        return $"{root}{Uri.EscapeDataString(vodId)}.m3u8" +
               $"?{sigKey}={Uri.EscapeDataString(signature)}&{tokenKey}={Uri.EscapeDataString(token)}" +
               CommonUsherQuery(playerSlot);
    }

    private static string CommonUsherQuery(int playerSlot)
        => "&allow_source=true&allow_audio_only=true&playlist_include_framerate=true" +
           "&supported_codecs=h264&platform=web&p=" + playerSlot.ToString(CultureInfo.InvariantCulture);

    /// <summary>What one usher fetch produced.</summary>
    /// <param name="Ok">True when a real multivariant playlist came back.</param>
    /// <param name="Status">The HTTP status code (0 = the request never completed).</param>
    /// <param name="Error">Usher's human-readable error, when it sent a JSON error body.</param>
    /// <param name="ErrorCode">Usher's machine error token, when it sent one.</param>
    private readonly record struct UsherOutcome(bool Ok, int Status, string? Error, string? ErrorCode);

    private async Task<UsherOutcome> FetchUsherAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://player.twitch.tv");
        request.Headers.TryAddWithoutValidation("Origin", "https://player.twitch.tv");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new UsherOutcome(false, 0, ex.Message, null);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && body.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                return new UsherOutcome(true, (int)response.StatusCode, null, null);
            }

            (string? error, string? code) = ParseUsherError(body);
            return new UsherOutcome(false, (int)response.StatusCode, error, code);
        }
    }

    /// <summary>Reads usher's <c>[{"type":"error","error":"…","error_code":"…"}]</c> body.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The error text and machine token, both null when the body is not a usher error.</returns>
    public static (string? Error, string? ErrorCode) ParseUsherError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart().FirstOrDefault() is not ('[' or '{')) return (null, null);
        try
        {
            string text = body.TrimStart();
            UsherError[]? errors = text[0] is '['
                ? JsonSerializer.Deserialize(text, TwitchJsonContext.Default.UsherErrorArray)
                : [JsonSerializer.Deserialize(text, TwitchJsonContext.Default.UsherError)!];
            if (errors is null || errors.Length == 0) return (null, null);
            UsherError first = errors[0];
            return (first?.Error, first?.ErrorCode);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    // ---- gql ---------------------------------------------------------------------------------------------------

    /// <summary>The inline anonymous access-token query (the primary form: nothing to rotate).</summary>
    /// <param name="kind">Live or VOD.</param>
    /// <param name="value">The login (live) or numeric id (VOD).</param>
    public static string InlineQuery(TwitchLinkKind kind, string value)
        => kind is TwitchLinkKind.Live
            ? $$"""{ streamPlaybackAccessToken(channelName: "{{value}}", params: {platform: "web", playerBackend: "mediaplayer", playerType: "site"}) { value signature } }"""
            : $$"""{ videoPlaybackAccessToken(id: "{{value}}", params: {platform: "web", playerBackend: "mediaplayer", playerType: "site"}) { value signature } }""";

    private async Task<TwitchAccessToken> AccessTokenAsync(TwitchLinkKind kind, string value, CancellationToken ct)
    {
        TwitchTokenEnvelope? inline = await GqlAsync(InlineBody(kind, value),
            TwitchJsonContext.Default.TwitchTokenEnvelope, ct).ConfigureAwait(false);

        if (TokenOf(inline, kind) is { } fromInline && fromInline.Value is { Length: > 0 }) return fromInline;

        bool retired = inline?.Errors is { Length: > 0 } errors &&
                       Array.Exists(errors, e => string.Equals(e.Message, "PersistedQueryNotFound", StringComparison.Ordinal));
        Host.Log(ModuleLogLevel.Info,
            retired
                ? "Twitch retired the inline query; falling back to the persisted PlaybackAccessToken hash."
                : "The inline access-token query returned nothing; trying the persisted PlaybackAccessToken hash.");

        TwitchTokenEnvelope? persisted = await GqlAsync(PersistedTokenBody(kind, value),
            TwitchJsonContext.Default.TwitchTokenEnvelope, ct).ConfigureAwait(false);

        if (TokenOf(persisted, kind) is { } fromPersisted && fromPersisted.Value is { Length: > 0 })
        {
            return fromPersisted;
        }

        throw new ModuleException(ModuleErrorCode.Unavailable,
            "Channel not found, or Twitch requires a browser for this channel.");
    }

    private static TwitchAccessToken? TokenOf(TwitchTokenEnvelope? envelope, TwitchLinkKind kind)
        => kind is TwitchLinkKind.Live
            ? envelope?.Data?.StreamPlaybackAccessToken
            : envelope?.Data?.VideoPlaybackAccessToken;

    private static TwitchTokenValue DecodeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ModuleException(ModuleErrorCode.Unavailable, "Twitch returned an empty playback token.");
        }

        try
        {
            return JsonSerializer.Deserialize(value, TwitchJsonContext.Default.TwitchTokenValue)
                   ?? throw new ModuleException(ModuleErrorCode.Unavailable, "Twitch returned an empty playback token.");
        }
        catch (JsonException ex)
        {
            throw new ModuleException(ModuleErrorCode.Unavailable, "Twitch returned an unreadable playback token.")
            {
                Detail = ex.Message,
            };
        }
    }

    private async Task<TwitchUser?> TryStreamMetadataAsync(string login, CancellationToken ct)
    {
        try
        {
            TwitchMetadataEnvelope? envelope = await GqlAsync(StreamMetadataBody(login),
                TwitchJsonContext.Default.TwitchMetadataEnvelope, ct).ConfigureAwait(false);
            return envelope?.Data?.User;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ModuleException)
        {
            Host.Log(ModuleLogLevel.Debug, $"StreamMetadata failed for {login}: {ex.Message}");
            return null;
        }
    }

    private async Task<T?> GqlAsync<T>(byte[] body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GqlEndpoint);
        request.Headers.TryAddWithoutValidation("Client-ID", ClientId);
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://player.twitch.tv");
        request.Headers.TryAddWithoutValidation("Origin", "https://player.twitch.tv");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModuleException(ModuleErrorCode.Transient,
                $"Twitch answered {(int)response.StatusCode} for a GraphQL request.");
        }

        byte[] payload = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, info);
    }

    /// <summary>Builds the inline access-token request body.</summary>
    /// <param name="kind">Live or VOD.</param>
    /// <param name="value">The login (live) or numeric id (VOD).</param>
    public static byte[] InlineBody(TwitchLinkKind kind, string value)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("query", InlineQuery(kind, value));
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Builds the persisted <c>PlaybackAccessToken</c> request body (the hash fallback).</summary>
    /// <param name="kind">Live or VOD.</param>
    /// <param name="value">The login (live) or numeric id (VOD).</param>
    public static byte[] PersistedTokenBody(TwitchLinkKind kind, string value)
    {
        bool live = kind is TwitchLinkKind.Live;
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("operationName", "PlaybackAccessToken");
            WritePersistedQuery(w, PlaybackAccessTokenHash);
            w.WritePropertyName("variables");
            w.WriteStartObject();
            w.WriteBoolean("isLive", live);
            w.WriteString("login", live ? value : string.Empty);
            w.WriteBoolean("isVod", !live);
            w.WriteString("vodID", live ? string.Empty : value);
            w.WriteString("playerType", "embed");
            w.WriteString("platform", "site");
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Builds the persisted <c>StreamMetadata</c> request body.</summary>
    /// <param name="login">The channel login.</param>
    public static byte[] StreamMetadataBody(string login)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("operationName", "StreamMetadata");
            WritePersistedQuery(w, StreamMetadataHash);
            w.WritePropertyName("variables");
            w.WriteStartObject();
            w.WriteString("channelLogin", login);
            w.WriteBoolean("includeIsDJ", true);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WritePersistedQuery(Utf8JsonWriter w, string hash)
    {
        w.WritePropertyName("extensions");
        w.WriteStartObject();
        w.WritePropertyName("persistedQuery");
        w.WriteStartObject();
        w.WriteNumber("version", 1);
        w.WriteString("sha256Hash", hash);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <inheritdoc/>
    public override ValueTask ShutdownAsync(CancellationToken ct)
    {
        _http.Dispose();
        return default;
    }
}
