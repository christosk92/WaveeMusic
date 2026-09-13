using System;
using System.Collections.Generic;
using System.Globalization;
using Google.Protobuf;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Protocol.Playback;
using Pl = Wavee.Protocol.Playlist;
// EntityKind: the ONE uri vocabulary (Wavee.Core), not the transport's thin Backend.Metadata projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.SpotifyLive;

// ── The live context resolver (the proto-free IContextResolver impl) ──────────────────────────────────────────────────
// Maps an opaque context uri → ordered, hydrated tracks via ONE unified server call: GET /context-resolve/v1/{uri}. The
// FG mapping of WaveeMusic's 700-line ContextResolver:
//   • its 3 bespoke caches + retry/cooldown dict   → none here: the GET is cheap; the cost is the metadata, and that is
//                                                     already deduped + sealed by the hydration ledger (design 2.1).
//   • its protobuf JsonParser.Parse<Context>       → a streaming Utf8JsonReader (proto-free, no full-doc alloc), the same
//                                                     choice DealerFrameParser makes.
//   • its bespoke batched-metadata client          → IEntityHydrator.EnsureManyAsync (ledger-deduped, batched, gzipped).
// The server decides order + sorting; we preserve it and apply skip_to (uid→uri→index) on top. Collections are URI-only
// on the wire — their sort/filter rides on context.url's query, which we forward verbatim.
public sealed class LiveContextResolver : IContextResolver
{
    sealed record ArtistContextWire(List<QueuedRef> Refs, Dictionary<string, string> Metadata);

    // Eager-load at most this many pages on resolve; the rest are lazy via LoadMoreAsync (Phase B wires the queue refill).
    const int MaxEagerPages = 8;

    readonly ITransport _transport;
    readonly IEntityHydrator _hydrator;
    readonly IStore _store;
    readonly WaveeLogger _log;
    readonly Resource<string, ArtistContextWire> _artistCache;

    /// <param name="hydrator">THE façade — REQUIRED. The resolved order is raised to Identity through it (one catalogue
    /// POST per 300, deduped session-wide by the ledger) instead of this file owning its own metadata client.</param>
    /// <param name="store">REQUIRED too (design §3): <c>HydrateAsync</c> reads every resolved row back out of it, so a
    /// null here is an NRE inside a queue resolve rather than a composition-root failure — the exact asymmetry
    /// wiring-discipline exists to remove.</param>
    public LiveContextResolver(ITransport transport, IEntityHydrator hydrator, IStore store,
        Func<SessionContext> ctx, WaveeLogger log = default)
    {
        _transport = transport;
        _hydrator = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
        _artistCache = new Resource<string, ArtistContextWire>(FetchArtistWireAsync,
            new FreshnessPolicy.Etag(TimeSpan.FromMinutes(15)), ctx, maxEntries: 16, name: "connect.context.artist",
            debugLog: log);
    }

    public async Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default)
    {
        // 0) NOT OURS TO ANSWER. A context outside Spotify's catalog (a playback module's `wavee:module:…`, a local
        //    folder, a session playlist) and Spotify's own metadata-in-the-uri `spotify:local:*` namespace both resolve
        //    to nothing here — the endpoint answers 400 and the caller logs a warning for a question that never had an
        //    answer. Refuse LOCALLY, before the HTTP: an empty resolve is what "Spotify does not know this context"
        //    means, and every caller already handles it. (Embedded pages are checked first: those rows travel INSIDE the
        //    command, so a non-Spotify context that carries its own page is still playable.)
        if (spec.EmbeddedPages is not { Count: > 0 } && !ContextResolve.IsSpotifyContext(spec.Uri))
        {
            _log.Info("context-resolve skipped (not a Spotify context): " + spec.Uri);
            return ResolvedContext.Empty;
        }

        // 1) The command embedded a custom-ordered page (a sorted/filtered playlist sent inline) → play it verbatim.
        if (spec.EmbeddedPages is { Count: > 0 } embedded)
        {
            var hydratedEmbedded = await HydrateAsync(embedded, ct).ConfigureAwait(false);
            int s = ContextResolve.ResolveStartIndex(hydratedEmbedded, spec);
            return new ResolvedContext(hydratedEmbedded, s, null, null, ContextResolve.IsInfinite(spec.Uri),
                spec.Metadata, spec.Uri);
        }

        // 2) An artist play does NOT use context-resolve — it resolves via the playlist-v2 "popular release segments"
        // list (a zstd SelectedListContent), while the top-level context_uri/feature stay the original spotify:artist:*.
        if (IsArtistUri(spec.Uri))
            return await ResolveArtistAsync(spec, ct).ConfigureAwait(false);
        if (TryArtistIdFromListUri(spec.Uri, out var artistId))
            return await ResolveArtistAsync(spec with { Uri = "spotify:artist:" + artistId }, ct).ConfigureAwait(false);

        // 3) Resolve via the unified context-resolve endpoint, eager-loading a bounded number of pages.
        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        ContextJson.Result jsonInfo = default;
        var resp = await _transport.Request(Channel.Spclient, ResolvePath(spec), default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log.Warn($"context-resolve failed ({resp.Status}): {spec.Uri}");
            return ResolvedContext.Empty;
        }
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage, out jsonInfo);

        int pages = 1;
        while (!string.IsNullOrEmpty(nextPage) && pages < MaxEagerPages && !ContextResolve.IsInfinite(spec.Uri))
        {
            var more = await _transport.Request(PageChannel(nextPage!), PageRoute(nextPage!), default, ct).ConfigureAwait(false);
            if (!more.Ok || more.Body is null || more.Body.Length == 0) break;
            string? pageNext = null, _s = null;
            ContextJson.Parse(more.Body, refs, ref _s, ref pageNext);
            nextPage = pageNext;
            pages++;
        }

        if (refs.Count == 0) { _log.Warn("context-resolve: 0 tracks for " + spec.Uri); return ResolvedContext.Empty; }

        var tracks = await HydrateAsync(refs, ct).ConfigureAwait(false);
        int start = ContextResolve.ResolveStartIndex(tracks, spec);
        return new ResolvedContext(tracks, start, sorting, nextPage, ContextResolve.IsInfinite(spec.Uri),
            jsonInfo.Metadata, string.IsNullOrEmpty(jsonInfo.ContextUri) ? null : jsonInfo.ContextUri);
    }

    public async Task<ContextPage> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(nextPageUrl)) return ContextPage.Empty;
        var resp = await _transport.Request(PageChannel(nextPageUrl), PageRoute(nextPageUrl), default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return ContextPage.Empty;
        var refs = new List<QueuedRef>();
        string? _s = null, _n = null;
        ContextJson.Parse(resp.Body, refs, ref _s, ref _n);
        if (refs.Count == 0) return new ContextPage(Array.Empty<QueuedTrack>(), _n);
        return new ContextPage(await HydrateAsync(refs, ct).ConfigureAwait(false), _n);
    }

    public async Task<ResolvedContext> ResolveAutoplayAsync(string contextUri, IReadOnlyList<string> recentTrackUris,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contextUri)) return ResolvedContext.Empty;
        // Spotify has no station for a uri it does not know (see ResolveAsync §0) — refuse locally instead of trading a
        // 400 for the same empty answer. A module/local session simply ends when its context does.
        if (!ContextResolve.IsSpotifyContext(contextUri))
        {
            _log.Info("autoplay skipped (not a Spotify context): " + contextUri);
            return ResolvedContext.Empty;
        }
        try
        {
            // Three wires, one per SEED SHAPE — this fork is deliberately NOT `IsPlayable`.
            //   Track   → radio-apollo. Its route is `spotify:station:track:<id>`: a MUSIC-radio seed. An episode has
            //             no station of that shape, so widening this arm would send podcasts at a music endpoint.
            //   Episode → /context-resolve/v1/autopodcast. The episode-shaped tail, same AutoplayContextRequest body.
            //             Before P4 an episode fell through to the music `autoplay` route and a finished podcast
            //             simply stopped.
            //   anything else (a playlist, an album, a show, a station) → the generic /context-resolve/v1/autoplay.
            return EntityUri.KindOf(contextUri) switch
            {
                EntityKind.Track => await ResolveRadioApolloAsync(contextUri, recentTrackUris, ct).ConfigureAwait(false),
                EntityKind.Episode => await ResolveAutopodcastAsync(contextUri, recentTrackUris, ct).ConfigureAwait(false),
                _ => await ResolveContextAutoplayAsync(contextUri, recentTrackUris, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn("autoplay resolve failed for " + contextUri + ": " + ex.Message, ex);
            return ResolvedContext.Empty;
        }
    }

    public async Task<ResolvedContext> ResolveAutopodcastAsync(string contextUri, IReadOnlyList<string> recentEpisodeUris,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contextUri) || !ContextResolve.IsSpotifyContext(contextUri)) return ResolvedContext.Empty;
        var request = new AutoplayContextRequest { ContextUri = contextUri, IsVideo = false };
        for (int i = 0; i < recentEpisodeUris.Count; i++)
            if (!string.IsNullOrEmpty(recentEpisodeUris[i])) request.RecentTrackUri.Add(recentEpisodeUris[i]);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-protobuf",
            ["Accept"] = "application/json",
        };
        var resp = await _transport.Request(Channel.Spclient, "/context-resolve/v1/autopodcast",
            request.ToByteArray(), ct, "POST", headers).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log.Warn($"autopodcast failed ({resp.Status}): {contextUri}");
            return ResolvedContext.Empty;
        }

        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage, out var info);
        if (refs.Count == 0) return ResolvedContext.Empty;
        var tracks = Tag(await HydrateAsync(refs, ct).ConfigureAwait(false), "autoplay");
        return new ResolvedContext(tracks, 0, sorting, nextPage, true, info.Metadata, info.ContextUri ?? contextUri);
    }

    async Task<ResolvedContext> ResolveContextAutoplayAsync(string contextUri, IReadOnlyList<string> recentTrackUris, CancellationToken ct)
    {
        var request = new AutoplayContextRequest { ContextUri = contextUri, IsVideo = false };
        for (int i = 0; i < recentTrackUris.Count; i++)
            if (!string.IsNullOrEmpty(recentTrackUris[i])) request.RecentTrackUri.Add(recentTrackUris[i]);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-protobuf",
            ["Accept"] = "application/json",
        };
        var resp = await _transport.Request(Channel.Spclient, "/context-resolve/v1/autoplay",
            request.ToByteArray(), ct, "POST", headers).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log.Warn($"autoplay endpoint failed ({resp.Status}): {contextUri}");
            return ResolvedContext.Empty;
        }

        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage, out var info);
        if (refs.Count == 0) return ResolvedContext.Empty;
        var tracks = Tag(await HydrateAsync(refs, ct).ConfigureAwait(false), "autoplay");
        var stationUri = string.IsNullOrEmpty(info.ContextUri) ? contextUri : info.ContextUri;
        return new ResolvedContext(tracks, 0, sorting, nextPage, true, info.Metadata, stationUri);
    }

    async Task<ResolvedContext> ResolveRadioApolloAsync(string seedTrackUri, IReadOnlyList<string> recentTrackUris, CancellationToken ct)
    {
        string seedId = EntityUri.IdOf(seedTrackUri);
        var prev = new List<string>(recentTrackUris.Count);
        for (int i = 0; i < recentTrackUris.Count; i++)
        {
            var id = EntityUri.IdOf(recentTrackUris[i]);
            if (!string.IsNullOrEmpty(id)) prev.Add(Uri.EscapeDataString(id));
        }

        int salt = Random.Shared.Next(100_000, 1_000_000);
        var route = "/radio-apollo/v3/tracks/spotify:station:track:" + seedId
            + "?salt=" + salt.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&autoplay=true&count=50&isVideo=false"
            + "&prev_tracks=" + string.Join(',', prev)
            + "&pageNum=2&minimal=true";
        var resp = await _transport.Request(Channel.Spclient, route, default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log.Warn($"radio-apollo failed ({resp.Status}): {seedTrackUri}");
            return ResolvedContext.Empty;
        }

        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage);
        if (refs.Count == 0) return ResolvedContext.Empty;
        var tracks = Tag(await HydrateAsync(refs, ct).ConfigureAwait(false), "autoplay");
        return new ResolvedContext(tracks, 0, null, nextPage, true, null, "spotify:station:track:" + seedId);
    }

    // Explicit "Start radio" (song/artist radio): resolve a seed uri → a concrete radio PLAYLIST uri via
    // GET /inspiredby-mix/v2/seed_to_playlist/{seedUri}?response-format=json. Distinct from ResolveRadioApolloAsync (the
    // autoplay-TAIL continuation): this is the user-visible "make me a radio" boundary. The seed rides the path segment
    // with literal colons (RFC 3986 allows ':' in a segment — same as the radio-apollo route). Bearer + Accept: JSON.
    // Response: { "total": N, "mediaItems": [ { "uri": "spotify:playlist:…" } ] } → the first non-empty mediaItems[].uri.
    // 404 / empty mediaItems → null (no radio available); the resolved playlist is then a normal /context-resolve context.
    public async Task<string?> ResolveRadioSeedAsync(string seedUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(seedUri)) return null;
        var route = "/inspiredby-mix/v2/seed_to_playlist/" + seedUri + "?response-format=json";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
        Resp resp;
        try { resp = await _transport.Request(Channel.Spclient, route, default, ct, headers: headers).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Warn("inspiredby-mix request failed for " + seedUri + ": " + ex.Message, ex); return null; }
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log.Warn($"inspiredby-mix failed ({resp.Status}): {seedUri}");   // 404 / server error → no radio
            return null;
        }

        var playlistUri = ParseFirstMediaItemUri(resp.Body);
        if (string.IsNullOrEmpty(playlistUri)) { _log.Warn("inspiredby-mix: no mediaItems for " + seedUri); return null; }
        return playlistUri;
    }

    // Streaming Utf8JsonReader (proto-free, no full-doc alloc — the ContextJson.Parse house style): find the "mediaItems"
    // array and return the first non-empty "uri" string inside it (matches WaveeMusic's FirstOrDefault(m => m.Uri != "")).
    static string? ParseFirstMediaItemUri(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        bool inMediaItems = false;
        int arrayDepth = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (!inMediaItems && reader.ValueTextEquals("mediaItems"))
                    {
                        if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                        {
                            inMediaItems = true;
                            arrayDepth = reader.CurrentDepth;   // StartArray + its matching EndArray share this depth
                        }
                    }
                    else if (inMediaItems && reader.ValueTextEquals("uri"))
                    {
                        if (reader.Read() && reader.TokenType == JsonTokenType.String)
                        {
                            var s = reader.GetString();
                            if (!string.IsNullOrEmpty(s)) return s;
                        }
                    }
                    break;
                case JsonTokenType.EndArray:
                    if (inMediaItems && reader.CurrentDepth == arrayDepth) return null;   // mediaItems closed with no uri
                    break;
            }
        }
        return null;
    }

    static bool IsArtistUri(string uri) => EntityUri.KindOf(uri) == EntityKind.Artist;

    static bool TryArtistIdFromListUri(string uri, out string artistId)
    {
        artistId = "";
        if (!uri.StartsWith("spotify:list:popular-release-segments", StringComparison.Ordinal)) return false;
        int marker = uri.LastIndexOf("artist_", StringComparison.Ordinal);
        if (marker < 0) return false;
        artistId = uri[(marker + "artist_".Length)..];
        int separator = artistId.IndexOfAny(':', '?', '#');
        if (separator >= 0) artistId = artistId[..separator];
        return artistId.Length > 0;
    }

    // Artist play: GET /playlist/v2/list/popular-release-segments-main-roles/artist_<id> → a zstd SelectedListContent
    // (playlist4_external.proto), NOT the JSON context-resolve shape. play_origin.feature_identifier stays "artist"
    // (handled by ConnectStateBuilder.FeatureOf, which keys off the ORIGINAL spec.Uri) — so ContextUri here stays null;
    // the caller falls back to spec.Uri (resolved.ContextUri ?? spec.Uri), never relabeling to this list uri.
    async Task<ResolvedContext> ResolveArtistAsync(ContextSpec spec, CancellationToken ct)
    {
        string id = EntityUri.IdOf(spec.Uri);
        var loaded = await _artistCache.GetAsync(id, ct).ConfigureAwait(false);
        if (!loaded.IsReady)
        {
            _log.Error($"artist context-resolve failed: {loaded.Error ?? "unknown"} ({spec.Uri})");
            return ResolvedContext.Empty;
        }
        var wire = loaded.Value!;
        var tracks = await HydrateAsync(wire.Refs, ct).ConfigureAwait(false);
        int start = ContextResolve.ResolveStartIndex(tracks, spec);
        return new ResolvedContext(tracks, start, null, null, false, wire.Metadata, null);
    }

    async Task<ArtistContextWire> FetchArtistWireAsync(string id, SessionContext ctx)
    {
        var path = "/playlist/v2/list/popular-release-segments-main-roles/artist_" + id;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        var resp = await _transport.Request(Channel.Spclient, path, default, CancellationToken.None, headers: headers).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
            throw new InvalidOperationException($"artist context-resolve failed ({resp.Status})");

        Pl.SelectedListContent slc;
        try { slc = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(resp.Body)); }
        catch (Exception ex) { throw new InvalidOperationException("artist context-resolve parse failed: " + ex.Message, ex); }

        var (members, _) = PlaylistWireMapper.ParseContents(slc);
        if (members.Count == 0) throw new InvalidOperationException("artist context-resolve: 0 tracks");

        var refs = new List<QueuedRef>(members.Count);
        foreach (var m in members) refs.Add(new QueuedRef(m.ItemUri, m.ItemId));   // uid = hex item_id (§6.1)

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format_list_type"] = "popular-release-segments-main-roles",
            ["reporting.uri"] = "spotify:artist:" + id,
            ["total_number_of_tracks"] = (slc.HasLength ? slc.Length : members.Count).ToString(CultureInfo.InvariantCulture),
        };
        if (slc.Attributes is { } attr)
            foreach (var fa in attr.FormatAttributes)
                if (!string.IsNullOrEmpty(fa.Key)) metadata[fa.Key] = fa.Value ?? "";

        return new ArtistContextWire(refs, metadata);
    }

    // Pull display + duration metadata for the resolved order — through THE façade, at Identity (a queue row needs a
    // title, a duration and an image; it is not a page open). The ledger dedupes across surfaces, so re-resolving the
    // same context is near-free. Misses become uri-only placeholders (preserving indices so skip_to-by-index stays valid).
    public async Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default)
    {
        var uris = new string[refs.Count];
        for (int i = 0; i < refs.Count; i++) uris[i] = refs[i].Uri;
        try { await _hydrator.EnsureManyAsync(uris, HydrationLevel.Identity, new HydrationOptions(Surface: TraitSurface.Context), ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn("context hydrate: " + ex.Message, ex); }   // best-effort: placeholders below

        var tracks = new QueuedTrack[refs.Count];
        for (int i = 0; i < refs.Count; i++)
        {
            var uri = refs[i].Uri;
            string provider = string.IsNullOrEmpty(refs[i].Provider) ? "context" : refs[i].Provider;
            // An EPISODE is a playable: a podcast context used to fall through to the uri-only placeholder because the
            // join asked for GetTrack alone (design §1.5 / plan §1.5).
            var row = _store.GetTrack(uri) ?? EpisodeAsTrack.From(_store.GetEpisode(uri)) ?? Placeholder(uri);
            tracks[i] = new QueuedTrack(row, refs[i].Uid, provider, refs[i].Metadata, RowKindOf(uri));
        }
        return tracks;
    }

    static Track Placeholder(string uri)
    {
        string id = EntityUri.IdOf(uri);
        return new Track(id, uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
    }

    // GET /context-resolve/v1/{escaped uri}. A collection's sort/filter rides on context.url's query string — forward it.
    static IReadOnlyList<QueuedTrack> Tag(IReadOnlyList<QueuedTrack> tracks, string provider)
    {
        if (tracks.Count == 0) return tracks;
        var tagged = new QueuedTrack[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) tagged[i] = tracks[i] with { Provider = provider };
        return tagged;
    }

    static QueueRowKind RowKindOf(string uri)
    {
        if (uri == "spotify:delimiter") return QueueRowKind.Delimiter;
        if (uri.StartsWith("spotify:meta:page:", StringComparison.Ordinal)) return QueueRowKind.PageMarker;
        return QueueRowKind.Playable;
    }

    static string ResolvePath(ContextSpec spec)
    {
        var path = "/context-resolve/v1/" + Uri.EscapeDataString(spec.Uri);
        if (spec.Url is { } url)
        {
            int q = url.IndexOf('?');
            if (q >= 0 && q + 1 < url.Length) path += url[q..];
        }
        return path;
    }

    // A page cursor is either an hm:// mercury ident or an spclient path/URL; route each on the right channel.
    static Channel PageChannel(string pageUrl) => pageUrl.StartsWith("hm://", StringComparison.Ordinal) ? Channel.ApMercury : Channel.Spclient;

    static string PageRoute(string pageUrl)
    {
        if (pageUrl.StartsWith("hm://", StringComparison.Ordinal)) return pageUrl;
        if (pageUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            int slash = pageUrl.IndexOf('/', "https://".Length);
            return slash >= 0 ? pageUrl[slash..] : pageUrl;   // strip host → spclient path+query
        }
        return pageUrl;
    }
}
